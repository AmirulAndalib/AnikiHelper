using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoLibraryIndexService
    {
        private sealed class IndexState
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, long> RootStamps { get; set; }
                = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public List<IndexEntry> Entries { get; set; } = new List<IndexEntry>();
        }

        private sealed class IndexEntry
        {
            public string Kind { get; set; } = string.Empty;
            public string RootKey { get; set; } = string.Empty;
            public string TopLevelKey { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty; // DPAPI encrypted.
            public string Name { get; set; } = string.Empty;
            public bool IsDirectory { get; set; }
            public bool IsVideo { get; set; }
            public int Depth { get; set; }
            public int SeasonNumber { get; set; }
            public int EpisodeNumber { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public DateTime AddedUtc { get; set; }
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AnikiHelper.VideoCenter.LibraryIndex.v1");
        private static readonly Regex SeasonEpisodeRegex = new Regex(
            @"(?<![A-Za-z0-9])S(?<s>\d{1,2})[ ._-]*E(?<e>\d{1,3})(?!\d)|(?<!\d)(?<s2>\d{1,2})x(?<e2>\d{1,3})(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeasonFolderRegex = new Regex(
            @"(?:season|saison|temporada|staffel|stagione|serie)[ ._-]*(?<s>\d{1,2})|^s(?<s2>\d{1,2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EpisodeRegex = new Regex(
            @"(?<![A-Za-z0-9])(?:episode|ep|e)[ ._-]*(?<e>\d{1,3})(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly object sync = new object();
        private readonly ILogger logger;
        private readonly string filePath;
        private IndexState state = new IndexState();
        private Task lastSaveTask = Task.CompletedTask;

        public AnikiVideoLibraryIndexService(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            var root = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? Path.Combine(Path.GetTempPath(), "AnikiHelper", "VideoCenter")
                : Path.Combine(pluginUserDataPath, "VideoCenter");
            filePath = Path.Combine(root, "library_index.json");
            Load();
        }

        public IReadOnlyList<AnikiVideoBrowserItem> GetTopLevelItems(string kind, IEnumerable<string> roots)
        {
            var rootKeys = new HashSet<string>(
                (roots ?? Enumerable.Empty<string>()).Select(BuildPathKey).Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            if (rootKeys.Count == 0)
            {
                return Array.Empty<AnikiVideoBrowserItem>();
            }

            List<IndexEntry> snapshot;
            lock (sync)
            {
                snapshot = state.Entries
                    .Where(x => x != null && x.Depth == 1 &&
                                string.Equals(x.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                                rootKeys.Contains(x.RootKey))
                    .ToList();
            }

            return snapshot
                .Select(ToBrowserItem)
                .Where(x => x != null)
                .OrderBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public IReadOnlyDictionary<string, string> GetRepresentativeVideoPaths(IEnumerable<string> topLevelPaths)
        {
            var requestedByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in topLevelPaths ?? Enumerable.Empty<string>())
            {
                var key = BuildPathKey(path);
                if (!string.IsNullOrWhiteSpace(key) && !requestedByKey.ContainsKey(key))
                {
                    requestedByKey[key] = path;
                }
            }
            if (requestedByKey.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            List<IndexEntry> snapshot;
            lock (sync)
            {
                snapshot = state.Entries
                    .Where(x => x != null && x.IsVideo && requestedByKey.ContainsKey(x.TopLevelKey))
                    // A trailer/bonus/sample must never become the identity of a movie/show just
                    // because it sorts before the main file alphabetically. This representative is
                    // reused by every Video Center view and therefore must be deterministic.
                    .OrderBy(x => IsLikelyExtraVideoName(x.Name) ? 1 : 0)
                    .ThenBy(x => x.SeasonNumber <= 0 ? int.MaxValue : x.SeasonNumber)
                    .ThenBy(x => x.EpisodeNumber <= 0 ? int.MaxValue : x.EpisodeNumber)
                    .ThenBy(x => x.Depth)
                    .ThenBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var resolvedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot)
            {
                if (!requestedByKey.TryGetValue(entry.TopLevelKey, out var requestedPath) ||
                    !resolvedKeys.Add(entry.TopLevelKey))
                {
                    continue;
                }

                var videoPath = Unprotect(entry.Path);
                if (!string.IsNullOrWhiteSpace(videoPath))
                {
                    result[requestedPath] = videoPath;
                }
            }
            return result;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetVideoPathsByTopLevel(IEnumerable<string> topLevelPaths)
        {
            var requestedByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in topLevelPaths ?? Enumerable.Empty<string>())
            {
                var key = BuildPathKey(path);
                if (!string.IsNullOrWhiteSpace(key) && !requestedByKey.ContainsKey(key))
                {
                    requestedByKey[key] = path;
                }
            }
            if (requestedByKey.Count == 0)
            {
                return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            }

            List<IndexEntry> snapshot;
            lock (sync)
            {
                snapshot = state.Entries
                    .Where(x => x != null && x.IsVideo && requestedByKey.ContainsKey(x.TopLevelKey))
                    .OrderBy(x => IsLikelyExtraVideoName(x.Name) ? 1 : 0)
                    .ThenBy(x => x.SeasonNumber <= 0 ? int.MaxValue : x.SeasonNumber)
                    .ThenBy(x => x.EpisodeNumber <= 0 ? int.MaxValue : x.EpisodeNumber)
                    .ThenBy(x => x.Depth)
                    .ThenBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in snapshot.GroupBy(x => x.TopLevelKey, StringComparer.OrdinalIgnoreCase))
            {
                if (!requestedByKey.TryGetValue(pair.Key, out var requestedPath)) continue;
                var paths = pair
                    .Select(x => Unprotect(x.Path))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (paths.Count > 0) result[requestedPath] = paths;
            }
            return result;
        }

        public IReadOnlyList<AnikiVideoBrowserItem> GetSeriesEpisodeItems(string seriesPath)
        {
            var topKey = BuildPathKey(seriesPath);
            if (string.IsNullOrWhiteSpace(topKey))
            {
                return Array.Empty<AnikiVideoBrowserItem>();
            }

            List<IndexEntry> snapshot;
            lock (sync)
            {
                snapshot = state.Entries
                    .Where(x => x != null && x.IsVideo && string.Equals(x.TopLevelKey, topKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return snapshot
                .Select(ToBrowserItem)
                .Where(x => x != null)
                .OrderBy(x => x.SeasonNumber)
                .ThenBy(x => x.EpisodeNumber <= 0 ? int.MaxValue : x.EpisodeNumber)
                .ThenBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task<bool> UpdateRootAsync(
            string kind,
            string rootPath,
            Func<string, bool> isSupportedVideo,
            CancellationToken cancellationToken,
            bool force = false)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || isSupportedVideo == null)
            {
                return false;
            }

            var normalizedRoot = NormalizePath(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
            {
                return false;
            }

            var rootKey = BuildPathKey(normalizedRoot);
            var stamp = await Task.Run(() => ComputeRootStamp(normalizedRoot), cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (!force && state.RootStamps.TryGetValue(rootKey, out var previous) && previous == stamp)
                {
                    return false;
                }
            }

            var existingAdded = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                foreach (var old in state.Entries.Where(x => x != null && string.Equals(x.RootKey, rootKey, StringComparison.OrdinalIgnoreCase)))
                {
                    var path = Unprotect(old.Path);
                    var key = BuildPathKey(path);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        existingAdded[key] = old.AddedUtc;
                    }
                }
            }

            var scanned = await Task.Run(() => ScanRoot(kind, normalizedRoot, rootKey, existingAdded, isSupportedVideo, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (sync)
            {
                state.Entries.RemoveAll(x => x != null && string.Equals(x.RootKey, rootKey, StringComparison.OrdinalIgnoreCase));
                state.Entries.AddRange(scanned);
                state.RootStamps[rootKey] = stamp;
            }
            _ = SaveAsync();
            return true;
        }

        private List<IndexEntry> ScanRoot(
            string kind,
            string rootPath,
            string rootKey,
            Dictionary<string, DateTime> existingAdded,
            Func<string, bool> isSupportedVideo,
            CancellationToken cancellationToken)
        {
            var result = new List<IndexEntry>();
            ScanDirectory(kind, rootPath, rootPath, rootKey, string.Empty, 0, existingAdded, isSupportedVideo, result, cancellationToken);
            return result;
        }

        private void ScanDirectory(
            string kind,
            string rootPath,
            string directory,
            string rootKey,
            string topLevelKey,
            int depth,
            Dictionary<string, DateTime> existingAdded,
            Func<string, bool> isSupportedVideo,
            List<IndexEntry> result,
            CancellationToken cancellationToken)
        {
            if (depth >= 5)
            {
                return;
            }

            IEnumerable<string> directories = Enumerable.Empty<string>();
            IEnumerable<string> files = Enumerable.Empty<string>();
            try
            {
                directories = Directory.EnumerateDirectories(directory)
                    .Where(x => !IsIgnoredDirectory(x))
                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch { }
            try
            {
                files = Directory.EnumerateFiles(directory)
                    .Where(isSupportedVideo)
                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch { }

            foreach (var child in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childDepth = depth + 1;
                var childTopKey = childDepth == 1 ? BuildPathKey(child) : topLevelKey;
                result.Add(CreateEntry(kind, child, rootKey, childTopKey, childDepth, true, false, existingAdded));
                ScanDirectory(kind, rootPath, child, rootKey, childTopKey, childDepth, existingAdded, isSupportedVideo, result, cancellationToken);
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileDepth = depth + 1;
                var fileTopKey = fileDepth == 1 ? BuildPathKey(file) : topLevelKey;
                var entry = CreateEntry(kind, file, rootKey, fileTopKey, fileDepth, false, true, existingAdded);
                ParseEpisodeIdentity(file, out var season, out var episode);
                if (season <= 0)
                {
                    season = TryParseSeasonFromParents(file, rootPath);
                }
                entry.SeasonNumber = season;
                entry.EpisodeNumber = episode;
                result.Add(entry);
            }
        }

        private IndexEntry CreateEntry(
            string kind,
            string path,
            string rootKey,
            string topKey,
            int depth,
            bool isDirectory,
            bool isVideo,
            Dictionary<string, DateTime> existingAdded)
        {
            var key = BuildPathKey(path);
            var added = existingAdded.TryGetValue(key, out var previousAdded) && previousAdded > DateTime.MinValue
                ? previousAdded
                : GetCreationUtc(path, isDirectory);
            var modified = GetLastWriteUtc(path, isDirectory);
            return new IndexEntry
            {
                Kind = kind ?? string.Empty,
                RootKey = rootKey ?? string.Empty,
                TopLevelKey = topKey ?? string.Empty,
                Path = Protect(path),
                Name = isVideo ? Path.GetFileNameWithoutExtension(path) ?? string.Empty : new DirectoryInfo(path).Name,
                IsDirectory = isDirectory,
                IsVideo = isVideo,
                Depth = depth,
                LastWriteUtc = modified,
                AddedUtc = added
            };
        }

        private AnikiVideoBrowserItem ToBrowserItem(IndexEntry entry)
        {
            if (entry == null)
            {
                return null;
            }
            var path = Unprotect(entry.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return new AnikiVideoBrowserItem
            {
                Name = entry.Name ?? string.Empty,
                FullPath = path,
                IsDirectory = entry.IsDirectory,
                IsVideo = entry.IsVideo,
                // The persistent index is deliberately a no-I/O fast path. Probing every cached
                // File.Exists/Directory.Exists here is surprisingly expensive on UNC/NAS roots and
                // this method is also called from the UI thread when a library/detail page opens.
                // Live enumeration validates availability later on a worker thread.
                IsAvailable = true,
                SeasonNumber = entry.SeasonNumber,
                EpisodeNumber = entry.EpisodeNumber,
                AddedUtc = entry.AddedUtc,
                LastWriteUtc = entry.LastWriteUtc
            };
        }

        private Task SaveAsync()
        {
            IndexState snapshot;
            lock (sync)
            {
                snapshot = new IndexState
                {
                    RootStamps = new Dictionary<string, long>(state.RootStamps, StringComparer.OrdinalIgnoreCase),
                    Entries = state.Entries.Select(CloneEntry).ToList()
                };
            }

            var previous = lastSaveTask ?? Task.CompletedTask;
            lastSaveTask = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                try
                {
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                    var temp = filePath + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                    if (File.Exists(filePath)) File.Delete(filePath);
                    File.Move(temp, filePath);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to save library index.");
                }
            });
            return lastSaveTask;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(filePath)) return;
                state = JsonConvert.DeserializeObject<IndexState>(File.ReadAllText(filePath)) ?? new IndexState();
                state.RootStamps = state.RootStamps ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                state.Entries = state.Entries ?? new List<IndexEntry>();
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to load library index.");
                state = new IndexState();
            }
        }

        private static IndexEntry CloneEntry(IndexEntry x)
        {
            return new IndexEntry
            {
                Kind = x.Kind ?? string.Empty,
                RootKey = x.RootKey ?? string.Empty,
                TopLevelKey = x.TopLevelKey ?? string.Empty,
                Path = x.Path ?? string.Empty,
                Name = x.Name ?? string.Empty,
                IsDirectory = x.IsDirectory,
                IsVideo = x.IsVideo,
                Depth = x.Depth,
                SeasonNumber = x.SeasonNumber,
                EpisodeNumber = x.EpisodeNumber,
                LastWriteUtc = x.LastWriteUtc,
                AddedUtc = x.AddedUtc
            };
        }

        private static long ComputeRootStamp(string root)
        {
            long stamp = 17;
            try
            {
                var info = new DirectoryInfo(root);
                stamp = unchecked((stamp * 31) + info.LastWriteTimeUtc.Ticks);

                // Fast change detector used by Fullscreen library pages. Inspect the root plus two
                // directory levels so Series/Anime layouts like Show\Season\Episode are detected
                // without doing the full recursive media scan on every visit to "All".
                foreach (var child in info.EnumerateDirectories()
                    .Where(x => !IsIgnoredDirectory(x.FullName))
                    .OrderBy(x => x.Name)
                    .Take(2048))
                {
                    stamp = unchecked((stamp * 31) + child.LastWriteTimeUtc.Ticks);
                    stamp = MixStampText(stamp, child.Name);

                    try
                    {
                        foreach (var grandChild in child.EnumerateDirectories()
                            .Where(x => !IsIgnoredDirectory(x.FullName))
                            .OrderBy(x => x.Name)
                            .Take(256))
                        {
                            stamp = unchecked((stamp * 31) + grandChild.LastWriteTimeUtc.Ticks);
                            stamp = MixStampText(stamp, grandChild.Name);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return stamp;
        }

        private static long MixStampText(long stamp, string value)
        {
            foreach (var ch in value ?? string.Empty)
            {
                stamp = unchecked((stamp * 31) + char.ToUpperInvariant(ch));
            }
            return stamp;
        }

        private static void ParseEpisodeIdentity(string path, out int season, out int episode)
        {
            season = 0;
            episode = 0;
            var text = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var match = SeasonEpisodeRegex.Match(text);
            if (match.Success)
            {
                var s = match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value;
                var e = match.Groups["e"].Success ? match.Groups["e"].Value : match.Groups["e2"].Value;
                int.TryParse(s, out season);
                int.TryParse(e, out episode);
                return;
            }
            match = EpisodeRegex.Match(text);
            if (match.Success)
            {
                int.TryParse(match.Groups["e"].Value, out episode);
            }
        }

        private static int TryParseSeasonFromParents(string filePath, string rootPath)
        {
            try
            {
                var parent = Directory.GetParent(filePath);
                while (parent != null && !string.Equals(NormalizePath(parent.FullName), NormalizePath(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    var match = SeasonFolderRegex.Match(parent.Name ?? string.Empty);
                    if (match.Success)
                    {
                        var value = match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value;
                        if (int.TryParse(value, out var season)) return season;
                    }
                    parent = parent.Parent;
                }
            }
            catch { }
            return 0;
        }

        private static DateTime GetCreationUtc(string path, bool directory)
        {
            try { return directory ? new DirectoryInfo(path).CreationTimeUtc : new FileInfo(path).CreationTimeUtc; }
            catch { return DateTime.UtcNow; }
        }

        private static DateTime GetLastWriteUtc(string path, bool directory)
        {
            try { return directory ? new DirectoryInfo(path).LastWriteTimeUtc : new FileInfo(path).LastWriteTimeUtc; }
            catch { return DateTime.MinValue; }
        }

        private static bool IsLikelyExtraVideoName(string value)
        {
            var text = (value ?? string.Empty).Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;

            return Regex.IsMatch(text,
                @"(?i)(^|\b)(trailer|teaser|sample|bonus|extras?|featurette|promo|preview|interview|clip|deleted\s+scenes?|behind\s+the\s+scenes|making\s+of|bande\s+annonce)(\b|$)");
        }

        private static bool IsIgnoredDirectory(string path)
        {
            var name = string.Empty;
            try { name = new DirectoryInfo(path).Name; } catch { }
            return string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(".", StringComparison.Ordinal);
        }

        private static string NormalizePath(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return (path ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        private static string BuildPathKey(string path)
        {
            var normalized = NormalizePath(path).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                var data = Encoding.UTF8.GetBytes(value);
                var protectedData = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return "dpapi:v1:" + Convert.ToBase64String(protectedData);
            }
            catch { return string.Empty; }
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                const string prefix = "dpapi:v1:";
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) return string.Empty;
                var encrypted = Convert.FromBase64String(value.Substring(prefix.Length));
                var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch { return string.Empty; }
        }
    }
}
