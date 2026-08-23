using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    /// <summary>
    /// Persists lightweight Video Player home data (favorite folders and recently opened videos).
    /// Resume positions deliberately stay in AnikiVideoResumeStore so each concern remains small.
    /// </summary>
    internal sealed class AnikiVideoHomeStore
    {
        private const int MaxFavoriteFolders = 8;
        private sealed class FavoriteEntry
        {
            public string Path { get; set; }
            public DateTime AddedUtc { get; set; }
        }

        private sealed class RecentEntry
        {
            public string Path { get; set; }
            public DateTime OpenedUtc { get; set; }
        }

        private sealed class LibraryActivityEntry
        {
            // Hash only: local/NAS library paths are never written here in clear text.
            public string Key { get; set; }
            public long ContentStampTicks { get; set; }
            public DateTime ActivityUtc { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class HomeState
        {
            public List<FavoriteEntry> Favorites { get; set; } = new List<FavoriteEntry>();
            public List<RecentEntry> Recents { get; set; } = new List<RecentEntry>();
            public List<LibraryActivityEntry> LibraryActivity { get; set; } = new List<LibraryActivityEntry>();
        }

        private readonly object sync = new object();
        private readonly ILogger logger;
        private readonly string filePath;
        private HomeState state = new HomeState();
        private Task lastSaveTask = Task.CompletedTask;

        public AnikiVideoHomeStore(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            var root = AnikiVideoStoragePaths.GetPlayerStateRoot(pluginUserDataPath, this.logger);
            filePath = Path.Combine(root, "HomeState.json");
            Load();
        }

        public IReadOnlyList<string> GetFavoriteFolders(int limit = MaxFavoriteFolders)
        {
            lock (sync)
            {
                return state.Favorites
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                    .OrderByDescending(x => x.AddedUtc)
                    .Select(x => x.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(0, limit))
                    .ToList();
            }
        }

        public bool IsFavorite(string path)
        {
            var key = Normalize(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (sync)
            {
                return state.Favorites.Any(x =>
                    x != null && string.Equals(Normalize(x.Path), key, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool ToggleFavorite(string path)
        {
            var normalizedPath = NormalizePath(path);
            var key = Normalize(normalizedPath);
            if (string.IsNullOrWhiteSpace(key) || !Directory.Exists(normalizedPath))
            {
                return false;
            }

            lock (sync)
            {
                var existing = state.Favorites.FirstOrDefault(x =>
                    x != null && string.Equals(Normalize(x.Path), key, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    state.Favorites.Remove(existing);
                    return false;
                }

                state.Favorites.Add(new FavoriteEntry
                {
                    Path = normalizedPath,
                    AddedUtc = DateTime.UtcNow
                });

                // The dedicated Browse hub shows up to eight pinned folders in two rows.
                // Adding a ninth favorite replaces the oldest one so controller management
                // stays simple and the layout remains deterministic.
                if (state.Favorites.Count > MaxFavoriteFolders)
                {
                    state.Favorites = state.Favorites
                        .Where(x => x != null)
                        .OrderByDescending(x => x.AddedUtc)
                        .Take(MaxFavoriteFolders)
                        .ToList();
                }

                return true;
            }
        }

        public void RecordRecentVideo(string path)
        {
            var normalizedPath = NormalizePath(path);
            var key = Normalize(normalizedPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (sync)
            {
                state.Recents.RemoveAll(x =>
                    x == null || string.Equals(Normalize(x.Path), key, StringComparison.OrdinalIgnoreCase));

                state.Recents.Insert(0, new RecentEntry
                {
                    Path = normalizedPath,
                    OpenedUtc = DateTime.UtcNow
                });

                if (state.Recents.Count > 30)
                {
                    state.Recents = state.Recents.Take(30).ToList();
                }
            }
        }

        public IReadOnlyList<string> GetRecentVideoPaths(int limit = 10)
        {
            lock (sync)
            {
                return state.Recents
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                    .OrderByDescending(x => x.OpenedUtc)
                    .Select(x => x.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(0, limit))
                    .ToList();
            }
        }

        /// <summary>Removes confirmed-missing videos from persistent playback history.</summary>
        public bool RemoveRecentVideos(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return false;
            }

            var keys = new HashSet<string>(
                paths.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            if (keys.Count == 0)
            {
                return false;
            }

            lock (sync)
            {
                var before = state.Recents.Count;
                state.Recents.RemoveAll(x =>
                    x == null || keys.Contains(Normalize(x.Path)));
                return state.Recents.Count != before;
            }
        }

        /// <summary>Returns the persistent activity date used to order Video Center Home libraries.</summary>
        public DateTime GetOrUpdateLibraryActivityUtc(string kind, string path, long contentStampTicks, DateTime seedUtc)
        {
            var key = BuildLibraryKey(kind, path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return DateTime.MinValue;
            }

            var now = DateTime.UtcNow;
            if (seedUtc.Kind != DateTimeKind.Utc)
            {
                seedUtc = seedUtc.ToUniversalTime();
            }
            if (seedUtc <= new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) || seedUtc > now.AddDays(1))
            {
                seedUtc = now;
            }

            lock (sync)
            {
                state.LibraryActivity = state.LibraryActivity ?? new List<LibraryActivityEntry>();
                var entry = state.LibraryActivity.FirstOrDefault(x =>
                    x != null && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    entry = new LibraryActivityEntry
                    {
                        Key = key,
                        ContentStampTicks = contentStampTicks,
                        ActivityUtc = seedUtc,
                        LastSeenUtc = now
                    };
                    state.LibraryActivity.Add(entry);
                }
                else
                {
                    if (contentStampTicks != 0 && entry.ContentStampTicks != 0 && contentStampTicks != entry.ContentStampTicks)
                    {
                        // A new episode/file/folder appeared (or the directory contents changed).
                        entry.ActivityUtc = now;
                    }
                    else if (entry.ActivityUtc == default(DateTime))
                    {
                        entry.ActivityUtc = seedUtc;
                    }

                    if (contentStampTicks != 0)
                    {
                        entry.ContentStampTicks = contentStampTicks;
                    }
                    entry.LastSeenUtc = now;
                }

                // Keep the hash-only index bounded. Entries not seen for a long time are safe to forget.
                if (state.LibraryActivity.Count > 4000)
                {
                    state.LibraryActivity = state.LibraryActivity
                        .Where(x => x != null && x.LastSeenUtc >= now.AddDays(-365))
                        .OrderByDescending(x => x.LastSeenUtc)
                        .Take(3000)
                        .ToList();
                }

                return entry.ActivityUtc;
            }
        }

        public Task SaveAsync()
        {
            HomeState snapshot;
            lock (sync)
            {
                snapshot = new HomeState
                {
                    Favorites = state.Favorites
                        .Where(x => x != null)
                        .Select(x => new FavoriteEntry { Path = x.Path, AddedUtc = x.AddedUtc })
                        .ToList(),
                    Recents = state.Recents
                        .Where(x => x != null)
                        .Select(x => new RecentEntry { Path = x.Path, OpenedUtc = x.OpenedUtc })
                        .ToList(),
                    LibraryActivity = (state.LibraryActivity ?? new List<LibraryActivityEntry>())
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Key))
                        .Select(x => new LibraryActivityEntry
                        {
                            Key = x.Key,
                            ContentStampTicks = x.ContentStampTicks,
                            ActivityUtc = x.ActivityUtc,
                            LastSeenUtc = x.LastSeenUtc
                        })
                        .ToList()
                };
            }

            var previous = lastSaveTask ?? Task.CompletedTask;
            lastSaveTask = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }

                try
                {
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                    var temp = filePath + ".tmp";
                    File.WriteAllText(temp, json);

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    File.Move(temp, filePath);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to save Video Player home state.");
                }
            });

            return lastSaveTask;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                var json = File.ReadAllText(filePath);
                state = JsonConvert.DeserializeObject<HomeState>(json) ?? new HomeState();
                state.Favorites = state.Favorites ?? new List<FavoriteEntry>();
                state.Recents = state.Recents ?? new List<RecentEntry>();
                state.LibraryActivity = state.LibraryActivity ?? new List<LibraryActivityEntry>();
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to load Video Player home state.");
                state = new HomeState();
            }
        }

        private static string BuildLibraryKey(string kind, string path)
        {
            var normalizedPath = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            try
            {
                var raw = ((kind ?? string.Empty).Trim().ToUpperInvariant() + "|" + normalizedPath.ToUpperInvariant());
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    var builder = new StringBuilder(hash.Length * 2);
                    foreach (var value in hash)
                    {
                        builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    return builder.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).Trim();
            }
            catch
            {
                return (path ?? string.Empty).Trim();
            }
        }

        private static string Normalize(string path)
        {
            return NormalizePath(path).ToUpperInvariant();
        }
    }
}
