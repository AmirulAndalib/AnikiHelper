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
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoMetadataRecord
    {
        public string Title { get; set; } = string.Empty;
        public int Year { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string Overview { get; set; } = string.Empty;
        public string Genres { get; set; } = string.Empty;
        public double Rating { get; set; }
        public int VoteCount { get; set; }
        public int RuntimeMinutes { get; set; }
        public string Tagline { get; set; } = string.Empty;
        public string Credits { get; set; } = string.Empty;
        public string Cast { get; set; } = string.Empty;
        public string OriginalTitle { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public int CollectionId { get; set; }
        public string CollectionName { get; set; } = string.Empty;
        public string CollectionPosterPath { get; set; } = string.Empty;
        public string CollectionBackdropPath { get; set; } = string.Empty;
        public bool CollectionLookupComplete { get; set; }
        public bool IsManual { get; set; }
        public bool IsFavorite { get; set; }
        public DateTime FavoriteUpdatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public AnikiVideoMetadataRecord Clone()
        {
            return new AnikiVideoMetadataRecord
            {
                Title = Title ?? string.Empty,
                Year = Year,
                MediaType = MediaType ?? string.Empty,
                Overview = Overview ?? string.Empty,
                Genres = Genres ?? string.Empty,
                Rating = Rating,
                VoteCount = VoteCount,
                RuntimeMinutes = RuntimeMinutes,
                Tagline = Tagline ?? string.Empty,
                Credits = Credits ?? string.Empty,
                Cast = Cast ?? string.Empty,
                OriginalTitle = OriginalTitle ?? string.Empty,
                Provider = Provider ?? string.Empty,
                ProviderId = ProviderId ?? string.Empty,
                CollectionId = CollectionId,
                CollectionName = CollectionName ?? string.Empty,
                CollectionPosterPath = CollectionPosterPath ?? string.Empty,
                CollectionBackdropPath = CollectionBackdropPath ?? string.Empty,
                CollectionLookupComplete = CollectionLookupComplete,
                IsManual = IsManual,
                IsFavorite = IsFavorite,
                FavoriteUpdatedUtc = FavoriteUpdatedUtc,
                UpdatedUtc = UpdatedUtc
            };
        }
    }

    /// <summary>
    /// Persistent metadata cache for Video Center media. The JSON never contains a media path:
    /// records are addressed by a SHA-256 hash of the normalized path.
    /// </summary>
    internal sealed class AnikiVideoMetadataStore
    {
        private sealed class MetadataState
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, AnikiVideoMetadataRecord> Entries { get; set; }
                = new Dictionary<string, AnikiVideoMetadataRecord>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Regex YearRegex =
            new Regex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);

        private readonly object sync = new object();
        private readonly ILogger logger;
        private readonly string filePath;
        private Dictionary<string, AnikiVideoMetadataRecord> entries =
            new Dictionary<string, AnikiVideoMetadataRecord>(StringComparer.OrdinalIgnoreCase);
        private Task lastSaveTask = Task.CompletedTask;

        public AnikiVideoMetadataStore(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            var root = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? Path.Combine(Path.GetTempPath(), "AnikiHelper", "VideoCenter")
                : Path.Combine(pluginUserDataPath, "VideoCenter");
            filePath = Path.Combine(root, "metadata.json");
            Load();
        }

        public AnikiVideoMetadataRecord Get(string mediaPath)
        {
            var key = BuildKey(mediaPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            lock (sync)
            {
                if (!entries.TryGetValue(key, out var value) || value == null)
                {
                    return null;
                }

                var clone = value.Clone();
                if (!clone.IsManual)
                {
                    clone.Title = CleanFallbackTitle(clone.Title);
                }
                return clone;
            }
        }

        public AnikiVideoMetadataRecord GetOrCreateFallback(string mediaPath, string mediaType, string displayName)
        {
            var existing = Get(mediaPath);
            if (existing != null)
            {
                return existing;
            }

            var title = (displayName ?? string.Empty).Trim();
            var year = 0;
            var match = YearRegex.Match(title);
            if (match.Success)
            {
                int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
                title = (title.Substring(0, match.Index) + " " + title.Substring(match.Index + match.Length)).Trim();
            }
            title = CleanFallbackTitle(title);

            var fallback = new AnikiVideoMetadataRecord
            {
                Title = title,
                Year = year,
                MediaType = NormalizeType(mediaType),
                UpdatedUtc = DateTime.UtcNow
            };
            UpsertInternal(mediaPath, fallback, preserveManual: true);
            return fallback.Clone();
        }

        private static string CleanFallbackTitle(string value)
        {
            var title = (value ?? string.Empty).Trim();
            title = Regex.Replace(title, @"\(\s*\)|\[\s*\]|\{\s*\}", " ");
            title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '.', '–', '—');
            return title;
        }

        public void UpsertProvider(
            string mediaPath,
            string title,
            int year,
            string mediaType,
            string overview,
            string genres,
            double rating,
            string provider,
            string providerId,
            int runtimeMinutes = 0,
            int voteCount = 0,
            string tagline = null,
            string credits = null,
            string originalTitle = null,
            string cast = null,
            int collectionId = 0,
            string collectionName = null,
            string collectionPosterPath = null,
            string collectionBackdropPath = null,
            bool collectionLookupComplete = false)
        {
            var record = new AnikiVideoMetadataRecord
            {
                Title = title ?? string.Empty,
                Year = year,
                MediaType = NormalizeType(mediaType),
                Overview = overview ?? string.Empty,
                Genres = genres ?? string.Empty,
                Rating = rating,
                RuntimeMinutes = Math.Max(0, runtimeMinutes),
                VoteCount = Math.Max(0, voteCount),
                Tagline = tagline ?? string.Empty,
                Credits = credits ?? string.Empty,
                Cast = cast ?? string.Empty,
                OriginalTitle = originalTitle ?? string.Empty,
                Provider = provider ?? string.Empty,
                ProviderId = providerId ?? string.Empty,
                CollectionId = Math.Max(0, collectionId),
                CollectionName = collectionName ?? string.Empty,
                CollectionPosterPath = collectionPosterPath ?? string.Empty,
                CollectionBackdropPath = collectionBackdropPath ?? string.Empty,
                CollectionLookupComplete = collectionLookupComplete,
                IsManual = false,
                UpdatedUtc = DateTime.UtcNow
            };
            UpsertInternal(mediaPath, record, preserveManual: true);
        }

        public void SetProviderMatch(string mediaPath, AnikiVideoMetadataRecord metadata)
        {
            if (metadata == null)
            {
                return;
            }

            // Choosing a provider match is an explicit user action. Unlike a background scraper
            // refresh it is allowed to replace previously cached/manual metadata, while user
            // state such as Favorite is still preserved by UpsertInternal.
            var record = metadata.Clone();
            record.MediaType = NormalizeType(record.MediaType);
            // A provider chosen by the user is a locked/manual association. Background scrapers
            // may refresh automatic records, but must never silently replace this TMDb match.
            // Change match calls this method again with preserveManual:false, so the user can
            // still deliberately replace the association at any time.
            record.IsManual = true;
            record.UpdatedUtc = DateTime.UtcNow;
            UpsertInternal(mediaPath, record, preserveManual: false);
        }

        public void SetManual(string mediaPath, AnikiVideoMetadataRecord metadata)
        {
            if (metadata == null)
            {
                return;
            }

            var record = metadata.Clone();
            record.MediaType = NormalizeType(record.MediaType);
            record.IsManual = true;
            record.Provider = string.IsNullOrWhiteSpace(record.Provider) ? "MANUAL" : record.Provider;
            record.UpdatedUtc = DateTime.UtcNow;
            UpsertInternal(mediaPath, record, preserveManual: false);
        }

        private void UpsertInternal(string mediaPath, AnikiVideoMetadataRecord record, bool preserveManual)
        {
            var key = BuildKey(mediaPath);
            if (string.IsNullOrWhiteSpace(key) || record == null)
            {
                return;
            }

            lock (sync)
            {
                entries.TryGetValue(key, out var existing);
                if (preserveManual && existing?.IsManual == true)
                {
                    return;
                }

                // User state is independent from provider/manual metadata refreshes. Never let a
                // scraper refresh silently remove a movie the user explicitly favorited.
                if (existing?.IsFavorite == true)
                {
                    record.IsFavorite = true;
                    record.FavoriteUpdatedUtc = existing.FavoriteUpdatedUtc;
                }
                if (string.IsNullOrWhiteSpace(record.Cast) && !string.IsNullOrWhiteSpace(existing?.Cast))
                {
                    record.Cast = existing.Cast;
                }
                // Preserve collection metadata only when this provider refresh did not perform a
                // collection lookup itself. A completed lookup with CollectionId=0 is authoritative
                // (the movie is not in a TMDb collection) and must be allowed to clear stale data.
                if (!record.CollectionLookupComplete)
                {
                    if (record.CollectionId <= 0 && existing?.CollectionId > 0)
                    {
                        record.CollectionId = existing.CollectionId;
                        record.CollectionName = existing.CollectionName ?? string.Empty;
                        record.CollectionPosterPath = existing.CollectionPosterPath ?? string.Empty;
                        record.CollectionBackdropPath = existing.CollectionBackdropPath ?? string.Empty;
                    }
                    if (existing?.CollectionLookupComplete == true)
                    {
                        record.CollectionLookupComplete = true;
                    }
                }

                entries[key] = record.Clone();
            }
            _ = SaveAsync();
        }

        public void SetCollectionMetadata(
            string mediaPath,
            int collectionId,
            string collectionName,
            string posterPath,
            string backdropPath,
            bool lookupComplete,
            bool persist = true)
        {
            var key = BuildKey(mediaPath);
            if (string.IsNullOrWhiteSpace(key)) return;

            lock (sync)
            {
                entries.TryGetValue(key, out var existing);
                var record = existing?.Clone() ?? new AnikiVideoMetadataRecord
                {
                    MediaType = "movies",
                    UpdatedUtc = DateTime.UtcNow
                };
                record.CollectionId = Math.Max(0, collectionId);
                record.CollectionName = collectionName ?? string.Empty;
                record.CollectionPosterPath = posterPath ?? string.Empty;
                record.CollectionBackdropPath = backdropPath ?? string.Empty;
                record.CollectionLookupComplete = lookupComplete;
                record.UpdatedUtc = DateTime.UtcNow;
                entries[key] = record;
            }
            if (persist) _ = SaveAsync();
        }

        public bool IsFavorite(string mediaPath)
        {
            var key = BuildKey(mediaPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (sync)
            {
                return entries.TryGetValue(key, out var value) && value?.IsFavorite == true;
            }
        }

        public DateTime GetFavoriteUpdatedUtc(string mediaPath)
        {
            var key = BuildKey(mediaPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return DateTime.MinValue;
            }

            lock (sync)
            {
                if (!entries.TryGetValue(key, out var value) || value?.IsFavorite != true)
                {
                    return DateTime.MinValue;
                }
                return value.FavoriteUpdatedUtc > DateTime.MinValue ? value.FavoriteUpdatedUtc : value.UpdatedUtc;
            }
        }

        public void SetFavorite(string mediaPath, bool isFavorite)
        {
            var key = BuildKey(mediaPath);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (sync)
            {
                if (!entries.TryGetValue(key, out var existing) || existing == null)
                {
                    return;
                }

                var updated = existing.Clone();
                updated.IsFavorite = isFavorite;
                updated.FavoriteUpdatedUtc = isFavorite ? DateTime.UtcNow : DateTime.MinValue;
                updated.UpdatedUtc = DateTime.UtcNow;
                entries[key] = updated;
            }
            _ = SaveAsync();
        }

        public Task SaveAsync()
        {
            Dictionary<string, AnikiVideoMetadataRecord> snapshot;
            lock (sync)
            {
                snapshot = entries.ToDictionary(
                    x => x.Key,
                    x => x.Value?.Clone(),
                    StringComparer.OrdinalIgnoreCase);
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

                    var state = new MetadataState { Entries = snapshot };
                    var json = JsonConvert.SerializeObject(state, Formatting.Indented);
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
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to save metadata cache.");
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

                var state = JsonConvert.DeserializeObject<MetadataState>(File.ReadAllText(filePath));
                var loaded = state?.Entries ?? new Dictionary<string, AnikiVideoMetadataRecord>();
                entries = new Dictionary<string, AnikiVideoMetadataRecord>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to load metadata cache.");
                entries = new Dictionary<string, AnikiVideoMetadataRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeType(string mediaType)
        {
            switch ((mediaType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "movie":
                case "movies": return "movies";
                case "tv":
                case "show":
                case "series": return "series";
                case "anime": return "anime";
                default: return (mediaType ?? string.Empty).Trim().ToLowerInvariant();
            }
        }

        private static string BuildKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalized;
            try { normalized = Path.GetFullPath(path).Trim().ToUpperInvariant(); }
            catch { normalized = path.Trim().ToUpperInvariant(); }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }
    }
}
