using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AnikiHelper.Services.VideoPlayer
{
    public sealed class AnikiVideoSeriesArtworkChoice
    {
        public string PreviewPath { get; set; } = string.Empty;
        public string ProviderText { get; set; } = string.Empty;
        public string MatchText { get; set; } = string.Empty;
        public string LanguageText { get; set; } = string.Empty;
        public string SizeText { get; set; } = string.Empty;
        public string MetadataTitle { get; set; } = string.Empty;
        public int MetadataYear { get; set; }
        public string MetadataOverview { get; set; } = string.Empty;
        public string MetadataGenres { get; set; } = string.Empty;
        public double MetadataRating { get; set; }
        public int MetadataRuntimeMinutes { get; set; }
        public int MetadataVoteCount { get; set; }
        public string MetadataTagline { get; set; } = string.Empty;
        public string MetadataCredits { get; set; } = string.Empty;
        public string MetadataOriginalTitle { get; set; } = string.Empty;

        internal string ProviderId { get; set; } = string.Empty;
        internal int RemoteId { get; set; }
        internal string SeriesLookupKey { get; set; } = string.Empty;
        internal string PosterRemotePath { get; set; } = string.Empty;
        internal string BackdropRemotePath { get; set; } = string.Empty;
        internal string LogoRemotePath { get; set; } = string.Empty;
    }

    /// <summary>Episodic artwork resolver using TMDb, TVmaze and AniList with local caching.</summary>
    internal sealed class AnikiVideoSeriesArtworkService : IDisposable
    {
        private sealed class SeriesIdentity
        {
            public string Title { get; set; } = string.Empty;
            public int Year { get; set; }
            public int Season { get; set; }
            public int Episode { get; set; }
            public bool HasAnimeHint { get; set; }
        }

        private sealed class SeriesCacheEntry
        {
            public int MatcherVersion { get; set; }
            public string ProviderId { get; set; } = string.Empty;
            public int RemoteId { get; set; }
            public string PosterFileName { get; set; } = string.Empty;
            public string BackdropFileName { get; set; } = string.Empty;
            public string LogoFileName { get; set; } = string.Empty;
            public bool IsManual { get; set; }
            public int HeroBackdropVersion { get; set; }
            public bool NoMatch { get; set; }
            public DateTime LastAttemptUtc { get; set; }
        }

        private sealed class TvmazeMatch
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public int Year { get; set; }
            public string Type { get; set; } = string.Empty;
            public string CountryCode { get; set; } = string.Empty;
            public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();
            public string Overview { get; set; } = string.Empty;
            public double Rating { get; set; }
            public string PrimaryPosterUrl { get; set; } = string.Empty;
            public double Score { get; set; }
            public bool IsExactTitle { get; set; }
        }

        private sealed class AniListMatch
        {
            public int Id { get; set; }
            public string DisplayTitle { get; set; } = string.Empty;
            public int Year { get; set; }
            public string CoverUrl { get; set; } = string.Empty;
            public string BannerUrl { get; set; } = string.Empty;
            public string Overview { get; set; } = string.Empty;
            public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();
            public double Rating { get; set; }
            public int Popularity { get; set; }
            public bool IsExactTitle { get; set; }
        }

        private sealed class TmdbTvMatch
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public int Year { get; set; }
            public string OriginalLanguage { get; set; } = string.Empty;
            public string SearchPosterPath { get; set; } = string.Empty;
            public string SearchBackdropPath { get; set; } = string.Empty;
            public string Overview { get; set; } = string.Empty;
            public double Rating { get; set; }
            public double Score { get; set; }
            public bool IsExactTitle { get; set; }
        }

        private const int MatcherVersion = 2;
        private const int PosterMaxDimension = 1000;
        private const int BackdropMaxDimension = 1920;
        private const int CurrentHeroBackdropVersion = 1;
        private const int PickerMaxDimension = 520;
        private const int JpegQuality = 88;
        private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromHours(24);

        private static readonly Regex SxxExxRegex =
            new Regex(@"(?<![A-Za-z0-9])S(?<s>\d{1,2})[ ._-]*E(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex XEpisodeRegex =
            new Regex(@"(?<!\d)(?<s>\d{1,2})x(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EpisodeWordRegex =
            new Regex(@"(?<![A-Za-z0-9])(?:episode|ep|e)[ ._-]*(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnimeDashEpisodeRegex =
            new Regex(@"\s[-–—]\s*(?<e>\d{1,3})(?=\s|$|[\[\(])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeasonFolderRegex =
            new Regex(@"^(?:season|saison|temporada|staffel|stagione|serie)[ ._-]*\d{1,2}$|^s\d{1,2}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeriesSeasonEpisodeSuffixRegex =
            new Regex(@"(?:[ ._-]+(?:S\d{1,2}(?:[ ._-]*E\d{1,3})?|(?:season|saison|temporada|staffel|stagione|serie)[ ._-]*\d{1,2}))+$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YearRegex =
            new Regex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex BracketRegex =
            new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex TechnicalTokenRegex =
            new Regex(
                @"\b(?:4320p?|2160p?|1080p?|1080i|720p?|576p?|480p?|4k|8k|uhd|hdr10\+?|hdr10|hdr|dolby[\s._-]*vision|dovi|dv|sdr|" +
                @"blu[\s._-]*ray|bluray|brrip|bdrip|bdremux|web[\s._-]*dl|webdl|webrip|web|hdtv|remux|dvdrip|dvd|" +
                @"x264|x265|h264|h265|h\.264|h\.265|hevc|av1|vc1|mpeg2|" +
                @"aac|ac3|eac3|ddp?|dts|truehd|atmos|flac|mp3|" +
                @"multi|multilang|french|truefrench|vff|vfq|vf2|vf|vostfr|vost|subfrench|subbed|dubbed|dual|" +
                @"proper|repack|internal|limited|complete|10bit|12bit|8bit|yify|rarbg|" +
                @"amzn|amazon|nf|netflix|dsnp|disney\+?|atvp|apple[\s._-]*tv|hmax|max)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StrongReleaseTokenRegex =
            new Regex(
                @"\b(?:4320p?|2160p?|1080p?|1080i|720p?|576p?|480p?|4k|8k|uhd|hdr10\+?|hdr10|hdr|dolby[\s._-]*vision|dovi|dv|" +
                @"blu[\s._-]*ray|bluray|brrip|bdrip|bdremux|web[\s._-]*dl|webdl|webrip|hdtv|remux|dvdrip|" +
                @"x264|x265|h264|h265|h\.264|h\.265|hevc|av1|" +
                @"aac|ac3|eac3|ddp?|dts|truehd|atmos|" +
                @"multi|multilang|french|truefrench|vff|vfq|vf2|vostfr|subfrench|10bit|12bit|8bit)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly HashSet<string> SupportedVideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv",
            ".mpg", ".mpeg", ".m2v", ".ts", ".mts", ".m2ts", ".vob",
            ".flv", ".f4v", ".3gp", ".3g2", ".ogv", ".asf", ".divx"
        };

        private readonly global::AnikiHelper.AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly HttpClient http;
        private readonly string cacheRoot;
        private readonly string indexPath;
        private readonly object indexSync = new object();
        private readonly object saveSync = new object();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> cacheLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim networkGate = new SemaphoreSlim(2, 2);
        private Dictionary<string, SeriesCacheEntry> cacheIndex =
            new Dictionary<string, SeriesCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> providerIdentityCacheKeys;
        private string blockedUnauthorizedTmdbToken = string.Empty;
        private int unauthorizedTmdbLogged;

        public AnikiVideoSeriesArtworkService(
            global::AnikiHelper.AnikiHelperSettings settings,
            string pluginUserDataPath,
            ILogger logger)
        {
            this.settings = settings;
            this.logger = logger ?? LogManager.GetLogger();
            cacheRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "VideoCenter", "SeriesArtworkCache");
            indexPath = Path.Combine(cacheRoot, "index.json");

            http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnikiHelper-VideoCenter/2.0");

            EnsureCacheDirectory();
            LoadIndex();
        }

        public bool IsEnabled =>
            settings != null && settings.VideoOnlineArtworkEnabled;

        private bool CanUseTmdb =>
            IsEnabled &&
            !string.IsNullOrWhiteSpace(settings?.VideoTmdbReadAccessToken) &&
            !IsTmdbAuthorizationBlocked();

        private bool IsTmdbAuthorizationBlocked()
        {
            var token = (settings?.VideoTmdbReadAccessToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(blockedUnauthorizedTmdbToken)) return false;
            if (string.Equals(blockedUnauthorizedTmdbToken, token, StringComparison.Ordinal)) return true;

            blockedUnauthorizedTmdbToken = string.Empty;
            Interlocked.Exchange(ref unauthorizedTmdbLogged, 0);
            return false;
        }

        private void MarkTmdbUnauthorized(string token)
        {
            blockedUnauthorizedTmdbToken = (token ?? string.Empty).Trim();
            if (Interlocked.Exchange(ref unauthorizedTmdbLogged, 1) == 0)
            {
                logger?.Warn("[AnikiHelper][VideoCenter][SeriesArtwork][TMDb] HTTP 401. TMDb requests are paused for this session/token; TVMaze/AniList fallbacks remain available.");
            }
        }

        public bool CanHandlePath(string videoPath)
        {
            return TryParseSeriesIdentity(videoPath, out _);
        }

        public AnikiVideoArtworkInfo GetCachedArtwork(string videoPath, bool preferPoster)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity))
            {
                return null;
            }

            try
            {
                return TryResolveCached(BuildSeriesLookupKey(identity), preferPoster, out _);
            }
            catch
            {
                return null;
            }
        }

        public AnikiVideoArtworkInfo GetCachedArtworkByProviderIdentity(
            string metadataProvider,
            int remoteId,
            bool preferPoster)
        {
            if (remoteId <= 0) return null;
            var provider = NormalizeCacheProviderId(metadataProvider);
            if (string.IsNullOrWhiteSpace(provider)) return null;

            try
            {
                var identityKey = provider + "|" + remoteId.ToString(CultureInfo.InvariantCulture);
                List<string> keys;
                lock (indexSync)
                {
                    if (providerIdentityCacheKeys == null)
                    {
                        providerIdentityCacheKeys = cacheIndex
                            .Where(x => x.Value != null && !x.Value.NoMatch && x.Value.RemoteId > 0 && !string.IsNullOrWhiteSpace(x.Value.ProviderId))
                            .GroupBy(x => (x.Value.ProviderId ?? string.Empty).ToLowerInvariant() + "|" + x.Value.RemoteId.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                g => g.Key,
                                g => g.OrderByDescending(x => x.Value.IsManual).Select(x => x.Key).ToList(),
                                StringComparer.OrdinalIgnoreCase);
                    }
                    keys = providerIdentityCacheKeys.TryGetValue(identityKey, out var cachedKeys)
                        ? cachedKeys.ToList()
                        : new List<string>();
                }

                foreach (var key in keys)
                {
                    var cached = TryResolveCached(key, preferPoster, out _);
                    if (cached != null && !string.IsNullOrWhiteSpace(cached.Path)) return cached;
                }
            }
            catch { }
            return null;
        }

        public string GetCachedLogoPathByProviderIdentity(string metadataProvider, int remoteId)
        {
            if (remoteId <= 0) return string.Empty;
            var provider = NormalizeCacheProviderId(metadataProvider);
            if (string.IsNullOrWhiteSpace(provider)) return string.Empty;
            try
            {
                var identityKey = provider + "|" + remoteId.ToString(CultureInfo.InvariantCulture);
                List<string> keys;
                lock (indexSync)
                {
                    if (providerIdentityCacheKeys == null)
                    {
                        providerIdentityCacheKeys = cacheIndex
                            .Where(x => x.Value != null && !x.Value.NoMatch && x.Value.RemoteId > 0 && !string.IsNullOrWhiteSpace(x.Value.ProviderId))
                            .GroupBy(x => (x.Value.ProviderId ?? string.Empty).ToLowerInvariant() + "|" + x.Value.RemoteId.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                g => g.Key,
                                g => g.OrderByDescending(x => x.Value.IsManual).Select(x => x.Key).ToList(),
                                StringComparer.OrdinalIgnoreCase);
                    }
                    keys = providerIdentityCacheKeys.TryGetValue(identityKey, out var cachedKeys)
                        ? cachedKeys.ToList()
                        : new List<string>();
                }
                foreach (var key in keys)
                {
                    var entry = GetEntrySnapshot(key);
                    var path = entry == null ? string.Empty : GetCachedPath(entry.LogoFileName);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
            }
            catch { }
            return string.Empty;
        }

        private static string NormalizeCacheProviderId(string metadataProvider)
        {
            var provider = (metadataProvider ?? string.Empty).Trim();
            if (string.Equals(provider, "TMDB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "tmdb-tv", StringComparison.OrdinalIgnoreCase)) return "tmdb-tv";
            if (string.Equals(provider, "TVMAZE", StringComparison.OrdinalIgnoreCase)) return "tvmaze";
            if (string.Equals(provider, "ANILIST", StringComparison.OrdinalIgnoreCase)) return "anilist";
            return provider.ToLowerInvariant();
        }

        public AnikiVideoArtworkInfo GetCachedFolderArtwork(string folderPath, bool preferPoster)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            try
            {
                return TryResolveCached(BuildFolderLookupKey(folderPath), preferPoster, out _);
            }
            catch
            {
                return null;
            }
        }

        public string GetCachedLogoPath(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity)) return string.Empty;
            try
            {
                var entry = GetEntrySnapshot(BuildSeriesLookupKey(identity));
                return entry == null ? string.Empty : GetCachedPath(entry.LogoFileName);
            }
            catch { return string.Empty; }
        }

        public string GetCachedFolderLogoPath(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return string.Empty;
            try
            {
                var entry = GetEntrySnapshot(BuildFolderLookupKey(folderPath));
                return entry == null ? string.Empty : GetCachedPath(entry.LogoFileName);
            }
            catch { return string.Empty; }
        }


        public bool TryGetCachedProviderIdentity(string videoPath, out string providerId, out int remoteId)
        {
            providerId = string.Empty;
            remoteId = 0;
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity) || identity == null)
            {
                return false;
            }

            try
            {
                return TryGetCachedProviderIdentityByKey(BuildSeriesLookupKey(identity), out providerId, out remoteId);
            }
            catch
            {
                providerId = string.Empty;
                remoteId = 0;
                return false;
            }
        }

        public bool TryGetCachedFolderProviderIdentity(string folderPath, out string providerId, out int remoteId)
        {
            providerId = string.Empty;
            remoteId = 0;
            if (string.IsNullOrWhiteSpace(folderPath)) return false;

            try
            {
                return TryGetCachedProviderIdentityByKey(BuildFolderLookupKey(folderPath), out providerId, out remoteId);
            }
            catch
            {
                providerId = string.Empty;
                remoteId = 0;
                return false;
            }
        }

        private bool TryGetCachedProviderIdentityByKey(string cacheKey, out string providerId, out int remoteId)
        {
            providerId = string.Empty;
            remoteId = 0;
            if (string.IsNullOrWhiteSpace(cacheKey)) return false;

            var entry = GetEntrySnapshot(cacheKey);
            if (entry == null || entry.NoMatch || entry.RemoteId <= 0 || string.IsNullOrWhiteSpace(entry.ProviderId))
            {
                return false;
            }

            providerId = entry.ProviderId.Trim();
            remoteId = entry.RemoteId;
            return true;
        }

        public Task<string> ResolveFolderLogoAsync(string folderPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return Task.FromResult(string.Empty);
            SeriesIdentity identity;
            try
            {
                if (!TryParseSeriesFolderIdentity(folderPath, out identity) || identity == null) return Task.FromResult(string.Empty);
            }
            catch { return Task.FromResult(string.Empty); }
            return ResolveLogoForCacheKeyAsync(BuildFolderLookupKey(folderPath), identity, cancellationToken);
        }

        public Task<string> ResolveLogoAsync(string videoPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity) || identity == null)
                return Task.FromResult(string.Empty);
            return ResolveLogoForCacheKeyAsync(BuildSeriesLookupKey(identity), identity, cancellationToken);
        }

        public Task<AnikiVideoMetadataRecord> ResolveFolderMetadataAsync(string folderPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return Task.FromResult<AnikiVideoMetadataRecord>(null);
            SeriesIdentity identity;
            try
            {
                if (!TryParseSeriesFolderIdentity(folderPath, out identity) || identity == null) return Task.FromResult<AnikiVideoMetadataRecord>(null);
            }
            catch { return Task.FromResult<AnikiVideoMetadataRecord>(null); }
            return ResolveMetadataForIdentityAsync(identity, cancellationToken);
        }

        public Task<AnikiVideoMetadataRecord> ResolveMetadataAsync(string videoPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity) || identity == null)
                return Task.FromResult<AnikiVideoMetadataRecord>(null);
            return ResolveMetadataForIdentityAsync(identity, cancellationToken);
        }

        public bool HasCachedArtwork(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity))
            {
                return false;
            }

            try
            {
                var cacheKey = BuildSeriesLookupKey(identity);
                return TryResolveCached(cacheKey, preferPoster: true, out _) != null ||
                       TryResolveCached(cacheKey, preferPoster: false, out _) != null;
            }
            catch
            {
                return false;
            }
        }

        public bool HasCachedFolderArtwork(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                var cacheKey = BuildFolderLookupKey(folderPath);
                return TryResolveCached(cacheKey, preferPoster: true, out _) != null ||
                       TryResolveCached(cacheKey, preferPoster: false, out _) != null;
            }
            catch
            {
                return false;
            }
        }

        public string GetSeriesFolderPath(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out _))
            {
                return string.Empty;
            }

            try
            {
                var directory = Directory.GetParent(videoPath);
                if (directory == null)
                {
                    return string.Empty;
                }

                // Episodes can live directly in the show folder or one level below in
                // Season 01 / Saison 01 / S01. Artwork belongs to the show folder.
                if (SeasonFolderRegex.IsMatch(directory.Name ?? string.Empty) && directory.Parent != null)
                {
                    directory = directory.Parent;
                }

                return directory.FullName;
            }
            catch
            {
                return string.Empty;
            }
        }

        public AnikiVideoArtworkInfo GetCachedManualArtwork(string videoPath, bool preferPoster)
        {
            if (string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity))
            {
                return null;
            }

            try
            {
                var cacheKey = BuildSeriesLookupKey(identity);
                SeriesCacheEntry entry;
                lock (indexSync)
                {
                    cacheIndex.TryGetValue(cacheKey, out entry);
                }

                if (!IsManualEntry(entry))
                {
                    entry = TryRecoverManualEntry(cacheKey, entry);
                }

                return IsManualEntry(entry)
                    ? TryResolveCached(cacheKey, preferPoster, out _)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public bool HasManualCachedArtwork(string videoPath)
        {
            return GetCachedManualArtwork(videoPath, preferPoster: true) != null ||
                   GetCachedManualArtwork(videoPath, preferPoster: false) != null;
        }

        public AnikiVideoArtworkInfo GetCachedManualFolderArtwork(string folderPath, bool preferPoster)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            try
            {
                var cacheKey = BuildFolderLookupKey(folderPath);
                SeriesCacheEntry entry;
                lock (indexSync)
                {
                    cacheIndex.TryGetValue(cacheKey, out entry);
                }

                if (!IsManualEntry(entry))
                {
                    entry = TryRecoverManualEntry(cacheKey, entry);
                }

                return IsManualEntry(entry)
                    ? TryResolveCached(cacheKey, preferPoster, out _)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public bool HasManualCachedFolderArtwork(string folderPath)
        {
            return GetCachedManualFolderArtwork(folderPath, preferPoster: true) != null ||
                   GetCachedManualFolderArtwork(folderPath, preferPoster: false) != null;
        }

        public async Task<bool> EnsureAutomaticFolderArtworkAsync(
            string folderPath,
            bool requirePoster,
            bool requireBackdrop,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            SeriesIdentity identity = null;
            try
            {
                identity = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryParseSeriesFolderIdentity(folderPath, out var parsed) ? parsed : null;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { identity = null; }

            if (identity == null) return false;
            return await EnsureAutomaticArtworkForCacheKeyAsync(
                BuildFolderLookupKey(folderPath), identity, requirePoster, requireBackdrop, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> EnsureAutomaticArtworkAsync(
            string videoPath,
            bool requirePoster,
            bool requireBackdrop,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath) || !TryParseSeriesIdentity(videoPath, out var identity) || identity == null)
            {
                return false;
            }

            return await EnsureAutomaticArtworkForCacheKeyAsync(
                BuildSeriesLookupKey(identity), identity, requirePoster, requireBackdrop, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> EnsureAutomaticArtworkForCacheKeyAsync(
            string cacheKey,
            SeriesIdentity identity,
            bool requirePoster,
            bool requireBackdrop,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || identity == null)
            {
                return false;
            }

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = GetEntrySnapshot(cacheKey) ?? new SeriesCacheEntry
                {
                    MatcherVersion = MatcherVersion,
                    LastAttemptUtc = DateTime.UtcNow
                };

                var hasPoster = !string.IsNullOrWhiteSpace(GetCachedPath(existing.PosterFileName));
                var hasBackdrop = !string.IsNullOrWhiteSpace(GetCachedPath(existing.BackdropFileName));
                if ((!requirePoster || hasPoster) && (!requireBackdrop || hasBackdrop))
                {
                    return true;
                }

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var scraped = await ScrapeAutomaticAsync(cacheKey, identity, cancellationToken).ConfigureAwait(false);
                    if (scraped == null)
                    {
                        return (!requirePoster || hasPoster) && (!requireBackdrop || hasBackdrop);
                    }

                    if (requirePoster && !hasPoster && !string.IsNullOrWhiteSpace(scraped.PosterFileName))
                    {
                        existing.PosterFileName = scraped.PosterFileName;
                        hasPoster = !string.IsNullOrWhiteSpace(GetCachedPath(existing.PosterFileName));
                    }
                    if (requireBackdrop && !hasBackdrop && !string.IsNullOrWhiteSpace(scraped.BackdropFileName))
                    {
                        existing.BackdropFileName = scraped.BackdropFileName;
                        hasBackdrop = !string.IsNullOrWhiteSpace(GetCachedPath(existing.BackdropFileName));
                    }

                    existing.MatcherVersion = MatcherVersion;
                    existing.NoMatch = false;
                    existing.LastAttemptUtc = DateTime.UtcNow;
                    existing.HeroBackdropVersion = Math.Max(existing.HeroBackdropVersion, scraped.HeroBackdropVersion);
                    if (!existing.IsManual)
                    {
                        existing.ProviderId = scraped.ProviderId ?? existing.ProviderId;
                        existing.RemoteId = scraped.RemoteId > 0 ? scraped.RemoteId : existing.RemoteId;
                    }
                    if (hasPoster || hasBackdrop)
                    {
                        StoreEntry(cacheKey, existing);
                    }

                    return (!requirePoster || hasPoster) && (!requireBackdrop || hasBackdrop);
                }
                finally
                {
                    networkGate.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<AnikiVideoArtworkInfo> ResolveHomeArtworkAsync(string videoPath, CancellationToken cancellationToken)
        {
            return ResolveAsync(videoPath, preferPoster: false, cancellationToken);
        }

        public Task<AnikiVideoArtworkInfo> ResolvePreviewArtworkAsync(string videoPath, CancellationToken cancellationToken)
        {
            return ResolveAsync(videoPath, preferPoster: true, cancellationToken);
        }

        public Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetArtworkChoicesAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            return GetArtworkChoicesAsync(videoPath, null, cancellationToken);
        }

        public async Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetArtworkChoicesAsync(
            string videoPath,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath))
            {
                return Array.Empty<AnikiVideoSeriesArtworkChoice>();
            }

            SeriesIdentity identity;
            var query = (searchText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var title = CleanSeriesTitle(query, out var year);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return Array.Empty<AnikiVideoSeriesArtworkChoice>();
                }

                identity = new SeriesIdentity
                {
                    Title = title,
                    Year = year,
                    Season = 1,
                    Episode = 1,
                    HasAnimeHint = HasAnimePathHint((videoPath ?? string.Empty).ToLowerInvariant())
                };
            }
            else if (!TryParseSeriesIdentity(videoPath, out identity))
            {
                return Array.Empty<AnikiVideoSeriesArtworkChoice>();
            }

            return await GetArtworkChoicesForIdentityAsync(
                identity,
                BuildSeriesLookupKey(identity),
                cancellationToken).ConfigureAwait(false);
        }

        internal string GetPosterRemoteUrl(AnikiVideoSeriesArtworkChoice choice)
        {
            return choice?.PosterRemotePath ?? string.Empty;
        }

        internal string GetBackdropRemoteUrl(AnikiVideoSeriesArtworkChoice choice)
        {
            return choice?.BackdropRemotePath ?? string.Empty;
        }

        internal Task<string> GetBackdropPickerPreviewAsync(
            AnikiVideoSeriesArtworkChoice choice,
            CancellationToken cancellationToken)
        {
            var url = choice?.BackdropRemotePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return Task.FromResult(string.Empty);
            return DownloadPickerPreviewAsync("backdrop", url, cancellationToken);
        }

        public string GetDefaultSearchText(string videoPath)
        {
            if (!TryParseSeriesIdentity(videoPath, out var identity) || identity == null)
            {
                return string.Empty;
            }

            return identity.Title + (identity.Year > 0
                ? " " + identity.Year.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
        }

        public async Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetFolderArtworkChoicesAsync(
            string folderPath,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath))
            {
                return Array.Empty<AnikiVideoSeriesArtworkChoice>();
            }

            var query = (searchText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                query = GetDefaultFolderSearchText(folderPath);
            }

            var title = CleanSeriesTitle(query, out var year);
            if (string.IsNullOrWhiteSpace(title))
            {
                return Array.Empty<AnikiVideoSeriesArtworkChoice>();
            }

            var lowerPath = folderPath.ToLowerInvariant();
            var identity = new SeriesIdentity
            {
                Title = title,
                Year = year,
                Season = 1,
                Episode = 1,
                HasAnimeHint = HasAnimePathHint(lowerPath)
            };

            global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter][Scraper] Manual folder artwork search: '{identity.Title}' ({identity.Year}).");

            return await GetArtworkChoicesForIdentityAsync(
                identity,
                BuildFolderLookupKey(folderPath),
                cancellationToken).ConfigureAwait(false);
        }

        public string GetDefaultFolderSearchText(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            try
            {
                var directory = new DirectoryInfo(folderPath);
                if (SeasonFolderRegex.IsMatch(directory.Name ?? string.Empty) && directory.Parent != null)
                {
                    directory = directory.Parent;
                }

                var title = CleanSeriesTitle(directory.Name ?? string.Empty, out var year);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return directory.Name ?? string.Empty;
                }

                return year > 0
                    ? title + " " + year.ToString(CultureInfo.InvariantCulture)
                    : title;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<AnikiVideoArtworkInfo> ResolveBestFolderBackdropAsync(
            string folderPath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            var cacheKey = BuildFolderLookupKey(folderPath);
            var existing = GetEntrySnapshot(cacheKey);
            var existingBackdrop = TryGetCachedBackdropOnly(cacheKey);

            // A backdrop explicitly attached to a manual artwork choice is an override and must
            // never be replaced by an automatic provider refresh.
            if (IsManualEntry(existing) && existingBackdrop != null)
            {
                return existingBackdrop;
            }

            if (existing != null &&
                existing.HeroBackdropVersion >= CurrentHeroBackdropVersion &&
                existingBackdrop != null)
            {
                return existingBackdrop;
            }

            SeriesIdentity identity = null;
            try
            {
                identity = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryParseSeriesFolderIdentity(folderPath, out var parsed) ? parsed : null;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                identity = null;
            }

            if (identity == null)
            {
                return existingBackdrop;
            }

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = GetEntrySnapshot(cacheKey);
                existingBackdrop = TryGetCachedBackdropOnly(cacheKey);
                if (IsManualEntry(existing) && existingBackdrop != null)
                {
                    return existingBackdrop;
                }
                if (existing != null && existing.HeroBackdropVersion >= CurrentHeroBackdropVersion && existingBackdrop != null)
                {
                    return existingBackdrop;
                }

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = existing ?? new SeriesCacheEntry
                    {
                        MatcherVersion = MatcherVersion,
                        LastAttemptUtc = DateTime.UtcNow
                    };

                    string heroFile = string.Empty;
                    string providerId = string.Empty;
                    int remoteId = 0;

                    // 1) TMDb: prefer a textless 16:9 backdrop, then votes/resolution.
                    if (CanUseTmdb)
                    {
                        var tmdb = await SearchTmdbTvStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                        if (tmdb != null)
                        {
                            var images = await GetTmdbTvImagesAsync(
                                tmdb.Id,
                                tmdb.OriginalLanguage,
                                cancellationToken).ConfigureAwait(false);
                            var backdropPath = SelectTmdbBackdropPath(
                                images?["backdrops"] as JArray,
                                ResolveTmdbLanguageCode(),
                                tmdb.OriginalLanguage);
                            if (string.IsNullOrWhiteSpace(backdropPath))
                            {
                                backdropPath = tmdb.SearchBackdropPath;
                            }

                            var url = BuildTmdbImageUrl(backdropPath, "original");
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                heroFile = await DownloadAndCacheImageAsync(
                                    BuildHeroBackdropStem(cacheKey, "tmdb", url),
                                    url,
                                    BackdropMaxDimension,
                                    cancellationToken).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(heroFile))
                                {
                                    providerId = "tmdb-tv";
                                    remoteId = tmdb.Id;
                                }
                            }
                        }
                    }

                    // 2) TVMaze background/banner fallback.
                    if (string.IsNullOrWhiteSpace(heroFile) && settings.VideoOnlineArtworkEnabled)
                    {
                        var tv = await SearchTvmazeStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                        if (tv != null)
                        {
                            var images = await GetTvmazeImagesAsync(tv.Id, cancellationToken).ConfigureAwait(false);
                            var url = SelectTvmazeBackdrop(images);
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                heroFile = await DownloadAndCacheImageAsync(
                                    BuildHeroBackdropStem(cacheKey, "tvmaze", url),
                                    url,
                                    BackdropMaxDimension,
                                    cancellationToken).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(heroFile))
                                {
                                    providerId = "tvmaze";
                                    remoteId = tv.Id;
                                }
                            }
                        }
                    }

                    // 3) AniList banner is useful for anime when neither TV provider has a Hero.
                    if (string.IsNullOrWhiteSpace(heroFile) && identity.HasAnimeHint && settings.VideoOnlineArtworkEnabled)
                    {
                        var anime = await SearchAniListStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                        if (anime != null && !string.IsNullOrWhiteSpace(anime.BannerUrl))
                        {
                            heroFile = await DownloadAndCacheImageAsync(
                                BuildHeroBackdropStem(cacheKey, "anilist", anime.BannerUrl),
                                anime.BannerUrl,
                                BackdropMaxDimension,
                                cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(heroFile))
                            {
                                providerId = "anilist";
                                remoteId = anime.Id;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(heroFile))
                    {
                        entry.BackdropFileName = heroFile;
                        entry.HeroBackdropVersion = CurrentHeroBackdropVersion;
                        entry.NoMatch = false;
                        entry.LastAttemptUtc = DateTime.UtcNow;
                        // Preserve the provider identity of a manual poster. Otherwise the Hero
                        // provider is also a good identity source for episode metadata.
                        if (!entry.IsManual)
                        {
                            entry.ProviderId = providerId;
                            entry.RemoteId = remoteId;
                        }
                        StoreEntry(cacheKey, entry);
                        return TryGetCachedBackdropOnly(cacheKey);
                    }

                    // Do not destroy a usable old landscape just because all online providers failed.
                    // Mark this pass as completed only on the existing positive entry so we do not
                    // hammer providers every time the series opens.
                    if (entry != null && existingBackdrop != null)
                    {
                        entry.HeroBackdropVersion = CurrentHeroBackdropVersion;
                        StoreEntry(cacheKey, entry);
                    }
                    return existingBackdrop;
                }
                finally
                {
                    networkGate.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<AnikiVideoArtworkInfo> ResolveFolderArtworkAsync(
            string folderPath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            var cacheKey = BuildFolderLookupKey(folderPath);
            var cached = TryResolveCached(cacheKey, preferPoster: true, out var freshNegative);
            if (cached != null || freshNegative)
            {
                return cached;
            }

            SeriesIdentity identity = null;
            try
            {
                identity = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryParseSeriesFolderIdentity(folderPath, out var parsed) ? parsed : null;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                identity = null;
            }

            if (identity == null)
            {
                return null;
            }

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = TryResolveCached(cacheKey, preferPoster: true, out freshNegative);
                if (cached != null || freshNegative)
                {
                    return cached;
                }

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = await ScrapeAutomaticAsync(cacheKey, identity, cancellationToken).ConfigureAwait(false);
                    if (entry == null)
                    {
                        RememberNoMatch(cacheKey);
                        return null;
                    }

                    StoreEntry(cacheKey, entry);
                    return TryResolveCached(cacheKey, preferPoster: true, out _);
                }
                finally
                {
                    networkGate.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<AnikiVideoArtworkInfo> ApplyFolderArtworkChoiceAsync(
            string folderPath,
            AnikiVideoSeriesArtworkChoice choice,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || choice == null || string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            return await ApplyArtworkChoiceToCacheKeyAsync(
                BuildFolderLookupKey(folderPath),
                choice,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<AnikiVideoArtworkInfo> ImportLocalFolderArtworkAsync(
            string folderPath,
            string imagePath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            return await ImportLocalArtworkToCacheKeyAsync(
                BuildFolderLookupKey(folderPath),
                imagePath,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<AnikiVideoArtworkInfo> ImportLocalArtworkAsync(
            string videoPath,
            string imagePath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !TryParseSeriesIdentity(videoPath, out var identity))
            {
                return null;
            }

            return await ImportLocalArtworkToCacheKeyAsync(
                BuildSeriesLookupKey(identity),
                imagePath,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<AnikiVideoArtworkInfo> ImportLocalArtworkToCacheKeyAsync(
            string cacheKey,
            string imagePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = GetEntrySnapshot(cacheKey);
                var entry = existing ?? new SeriesCacheEntry();
                entry.MatcherVersion = MatcherVersion;
                entry.IsManual = true;
                entry.LastAttemptUtc = DateTime.UtcNow;

                var manualVersion = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
                entry.PosterFileName = ImportLocalImageToCache(
                    BuildManualArtworkStem(cacheKey, "poster", imagePath, manualVersion),
                    imagePath,
                    PosterMaxDimension,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(entry.PosterFileName))
                {
                    return null;
                }

                StoreEntry(cacheKey, entry);
                return TryResolveCached(cacheKey, preferPoster: true, out _);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<IReadOnlyDictionary<string, string>> GetEpisodeTitlesAsync(
            string folderPath,
            IEnumerable<int> seasonNumbers,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath))
            {
                return Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            return GetEpisodeTitlesByCacheKeyAsync(
                BuildFolderLookupKey(folderPath),
                seasonNumbers,
                cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, string>> GetEpisodeTitlesForVideoAsync(
            string videoPath,
            IEnumerable<int> seasonNumbers,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath) ||
                !TryParseSeriesIdentity(videoPath, out var identity) || identity == null)
            {
                return Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            return GetEpisodeTitlesByCacheKeyAsync(
                BuildSeriesLookupKey(identity),
                seasonNumbers,
                cancellationToken);
        }

        private async Task<IReadOnlyDictionary<string, string>> GetEpisodeTitlesByCacheKeyAsync(
            string cacheKey,
            IEnumerable<int> seasonNumbers,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!IsEnabled || string.IsNullOrWhiteSpace(cacheKey))
            {
                return result;
            }

            SeriesCacheEntry entry = null;
            lock (indexSync)
            {
                if (cacheIndex.TryGetValue(cacheKey, out var cached) && cached != null && !cached.NoMatch)
                {
                    entry = new SeriesCacheEntry
                    {
                        MatcherVersion = cached.MatcherVersion,
                        ProviderId = cached.ProviderId ?? string.Empty,
                        RemoteId = cached.RemoteId,
                        PosterFileName = cached.PosterFileName ?? string.Empty,
                        BackdropFileName = cached.BackdropFileName ?? string.Empty,
                        LogoFileName = cached.LogoFileName ?? string.Empty,
                        IsManual = cached.IsManual,
                        NoMatch = cached.NoMatch,
                        LastAttemptUtc = cached.LastAttemptUtc
                    };
                }
            }

            if (entry == null || entry.RemoteId <= 0 || string.IsNullOrWhiteSpace(entry.ProviderId))
            {
                return result;
            }

            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (string.Equals(entry.ProviderId, "tmdb-tv", StringComparison.OrdinalIgnoreCase))
                {
                    var locale = ToTmdbLocale(ResolveTmdbLanguageCode());
                    var seasons = (seasonNumbers ?? Enumerable.Empty<int>())
                        .Where(x => x >= 0)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();

                    foreach (var season in seasons)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "https://api.themoviedb.org/3/tv/{0}/season/{1}?language={2}",
                            entry.RemoteId,
                            season,
                            Uri.EscapeDataString(locale));
                        var json = await GetTmdbJsonAsync(url, cancellationToken).ConfigureAwait(false);
                        var seasonTitle = (json?["name"]?.ToString() ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(seasonTitle))
                        {
                            result[BuildSeasonTitleKey(season)] = seasonTitle;
                        }

                        var episodes = json?["episodes"] as JArray;
                        if (episodes == null)
                        {
                            continue;
                        }

                        foreach (var episode in episodes.OfType<JObject>())
                        {
                            var seasonNumber = ParseInt(episode["season_number"]?.ToString());
                            var episodeNumber = ParseInt(episode["episode_number"]?.ToString());
                            var title = (episode["name"]?.ToString() ?? string.Empty).Trim();
                            if (seasonNumber >= 0 && episodeNumber > 0 && !string.IsNullOrWhiteSpace(title))
                            {
                                result[BuildEpisodeTitleKey(seasonNumber, episodeNumber)] = title;
                            }
                        }
                    }
                }
                else if (string.Equals(entry.ProviderId, "tvmaze", StringComparison.OrdinalIgnoreCase))
                {
                    var url = "https://api.tvmaze.com/shows/" +
                              entry.RemoteId.ToString(CultureInfo.InvariantCulture) +
                              "/episodes";
                    var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false) as JArray;
                    if (json != null)
                    {
                        foreach (var episode in json.OfType<JObject>())
                        {
                            var seasonNumber = ParseInt(episode["season"]?.ToString());
                            var episodeNumber = ParseInt(episode["number"]?.ToString());
                            var title = (episode["name"]?.ToString() ?? string.Empty).Trim();
                            if (seasonNumber >= 0 && episodeNumber > 0 && !string.IsNullOrWhiteSpace(title))
                            {
                                result[BuildEpisodeTitleKey(seasonNumber, episodeNumber)] = title;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Failed to resolve episode titles.");
            }
            finally
            {
                networkGate.Release();
            }

            return result;
        }

        public static string BuildSeasonTitleKey(int seasonNumber)
        {
            return "season:" + seasonNumber.ToString(CultureInfo.InvariantCulture);
        }

        public static string BuildEpisodeTitleKey(int seasonNumber, int episodeNumber)
        {
            return seasonNumber.ToString(CultureInfo.InvariantCulture) + ":" +
                   episodeNumber.ToString(CultureInfo.InvariantCulture);
        }

        public Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetFolderLogoChoicesAsync(
            string folderPath,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(folderPath))
                return Task.FromResult<IReadOnlyList<AnikiVideoSeriesArtworkChoice>>(Array.Empty<AnikiVideoSeriesArtworkChoice>());

            SeriesIdentity identity;
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var raw = searchText.Trim();
                var year = 0;
                var yearMatch = YearRegex.Match(raw);
                if (yearMatch.Success)
                {
                    int.TryParse(yearMatch.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
                    raw = (raw.Substring(0, yearMatch.Index) + " " + raw.Substring(yearMatch.Index + yearMatch.Length)).Trim();
                }
                identity = new SeriesIdentity { Title = CleanSeriesTitle(raw, out _), Year = year, HasAnimeHint = HasAnimePathHint(folderPath.ToLowerInvariant()) };
            }
            else if (!TryParseSeriesFolderIdentity(folderPath, out identity) || identity == null)
            {
                return Task.FromResult<IReadOnlyList<AnikiVideoSeriesArtworkChoice>>(Array.Empty<AnikiVideoSeriesArtworkChoice>());
            }
            return GetLogoChoicesForIdentityAsync(identity, cancellationToken);
        }

        public Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetLogoChoicesAsync(
            string videoPath,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath))
                return Task.FromResult<IReadOnlyList<AnikiVideoSeriesArtworkChoice>>(Array.Empty<AnikiVideoSeriesArtworkChoice>());
            SeriesIdentity identity;
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var raw = searchText.Trim();
                var year = 0;
                var yearMatch = YearRegex.Match(raw);
                if (yearMatch.Success)
                {
                    int.TryParse(yearMatch.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
                    raw = (raw.Substring(0, yearMatch.Index) + " " + raw.Substring(yearMatch.Index + yearMatch.Length)).Trim();
                }
                identity = new SeriesIdentity { Title = CleanSeriesTitle(raw, out _), Year = year, HasAnimeHint = HasAnimePathHint(videoPath.ToLowerInvariant()) };
            }
            else if (!TryParseSeriesIdentity(videoPath, out identity) || identity == null)
            {
                return Task.FromResult<IReadOnlyList<AnikiVideoSeriesArtworkChoice>>(Array.Empty<AnikiVideoSeriesArtworkChoice>());
            }
            return GetLogoChoicesForIdentityAsync(identity, cancellationToken);
        }

        public string GetLogoRemoteUrl(AnikiVideoSeriesArtworkChoice choice)
        {
            return choice?.LogoRemotePath ?? string.Empty;
        }

        private async Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetLogoChoicesForIdentityAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title) || !CanUseTmdb)
                return Array.Empty<AnikiVideoSeriesArtworkChoice>();

            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var matches = await SearchTmdbTvCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
                var match = matches.FirstOrDefault();
                if (match == null || match.Id <= 0) return Array.Empty<AnikiVideoSeriesArtworkChoice>();
                var details = await GetTmdbTvDetailsAsync(match.Id, cancellationToken).ConfigureAwait(false);
                var originalLanguage = details?["original_language"]?.ToString() ?? match.OriginalLanguage;
                var images = await GetTmdbTvImagesAsync(match.Id, originalLanguage, cancellationToken).ConfigureAwait(false);
                var logos = BuildTmdbLogoChoices(images?["logos"] as JArray, ResolveTmdbLanguageCode(), originalLanguage, 6);
                if (logos.Count == 0) return Array.Empty<AnikiVideoSeriesArtworkChoice>();

                var metadata = BuildTmdbMetadataRecord(details, match, identity.HasAnimeHint ? "anime" : "series");
                var result = new List<AnikiVideoSeriesArtworkChoice>();
                foreach (var logo in logos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remotePath = logo?["file_path"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(remotePath)) continue;
                    var url = BuildTmdbImageUrl(remotePath, "original");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var preview = await DownloadLogoPreviewAsync("tmdb-logo", url, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(preview)) continue;
                    var language = GetTmdbImageLanguage(logo);
                    var width = ParseInt(logo?["width"]?.ToString());
                    var height = ParseInt(logo?["height"]?.ToString());
                    result.Add(new AnikiVideoSeriesArtworkChoice
                    {
                        PreviewPath = preview,
                        ProviderText = "TMDB",
                        MatchText = metadata?.Title ?? match.Name,
                        LanguageText = string.IsNullOrWhiteSpace(language) ? "NO TEXT" : language.ToUpperInvariant(),
                        SizeText = width > 0 && height > 0 ? width.ToString(CultureInfo.InvariantCulture) + " × " + height.ToString(CultureInfo.InvariantCulture) : string.Empty,
                        MetadataTitle = metadata?.Title ?? match.Name,
                        MetadataYear = metadata?.Year ?? match.Year,
                        MetadataOverview = metadata?.Overview ?? string.Empty,
                        MetadataGenres = metadata?.Genres ?? string.Empty,
                        MetadataRating = metadata?.Rating ?? 0.0,
                        MetadataRuntimeMinutes = metadata?.RuntimeMinutes ?? 0,
                        MetadataVoteCount = metadata?.VoteCount ?? 0,
                        MetadataTagline = metadata?.Tagline ?? string.Empty,
                        MetadataCredits = metadata?.Credits ?? string.Empty,
                        MetadataOriginalTitle = metadata?.OriginalTitle ?? string.Empty,
                        ProviderId = "tmdb-tv",
                        RemoteId = match.Id,
                        LogoRemotePath = url
                    });
                }
                return result.Take(6).ToArray();
            }
            finally
            {
                networkGate.Release();
            }
        }

        private async Task<IReadOnlyList<AnikiVideoSeriesArtworkChoice>> GetArtworkChoicesForIdentityAsync(
            SeriesIdentity identity,
            string lookupKey,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return Array.Empty<AnikiVideoSeriesArtworkChoice>();
            }

            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = new List<AnikiVideoSeriesArtworkChoice>();
                ClearPickerPreviews();

                // When the user configured TMDb, use it first for TV as well. TMDb gives us
                // translated/alternate title search plus proper poster and backdrop sets.
                if (CanUseTmdb)
                {
                    var tmdbMatches = await SearchTmdbTvCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
                    foreach (var match in tmdbMatches.Take(2))
                    {
                        var details = await GetTmdbTvDetailsAsync(match.Id, cancellationToken).ConfigureAwait(false);
                        var metadataTitle = details?["name"]?.ToString() ?? match.Name;
                        var metadataYear = ParseYear(details?["first_air_date"]?.ToString());
                        if (metadataYear <= 0) metadataYear = match.Year;
                        var metadataOverview = details?["overview"]?.ToString() ?? match.Overview;
                        var metadataGenres = JoinGenreNames(details?["genres"] as JArray);
                        var metadataRating = ParseDouble(details?["vote_average"]?.ToString());
                        if (metadataRating <= 0.0) metadataRating = match.Rating;

                        var images = await GetTmdbTvImagesAsync(
                            match.Id,
                            match.OriginalLanguage,
                            cancellationToken).ConfigureAwait(false);

                        var posters = SelectTmdbPosterCandidates(
                            images?["posters"] as JArray,
                            ResolveTmdbLanguageCode(),
                            match.OriginalLanguage,
                            4);

                        if (posters.Count == 0 && !string.IsNullOrWhiteSpace(match.SearchPosterPath))
                        {
                            posters.Add(new JObject
                            {
                                ["file_path"] = match.SearchPosterPath,
                                ["iso_639_1"] = ResolveTmdbLanguageCode()
                            });
                        }

                        var backdropPath = SelectTmdbBackdropPath(
                            images?["backdrops"] as JArray,
                            ResolveTmdbLanguageCode(),
                            match.OriginalLanguage);
                        if (string.IsNullOrWhiteSpace(backdropPath))
                        {
                            backdropPath = match.SearchBackdropPath;
                        }

                        foreach (var poster in posters)
                        {
                            var remotePath = poster?["file_path"]?.ToString() ?? string.Empty;
                            var remoteUrl = BuildTmdbImageUrl(remotePath, "w780");
                            if (string.IsNullOrWhiteSpace(remoteUrl))
                            {
                                continue;
                            }

                            var preview = await DownloadPickerPreviewAsync("tmdbtv", remoteUrl, cancellationToken).ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(preview))
                            {
                                continue;
                            }

                            var imageLanguage = GetTmdbImageLanguage(poster);
                            result.Add(new AnikiVideoSeriesArtworkChoice
                            {
                                PreviewPath = preview,
                                ProviderText = "TMDB",
                                MatchText = FormatMatchText(match.Name, match.Year),
                                LanguageText = string.IsNullOrWhiteSpace(imageLanguage)
                                    ? "NO TEXT"
                                    : imageLanguage.ToUpperInvariant(),
                                SizeText = string.Empty,
                                MetadataTitle = metadataTitle,
                                MetadataYear = metadataYear,
                                MetadataOverview = metadataOverview,
                                MetadataGenres = metadataGenres,
                                MetadataRating = metadataRating,
                                ProviderId = "tmdb-tv",
                                RemoteId = match.Id,
                                SeriesLookupKey = lookupKey ?? string.Empty,
                                PosterRemotePath = remoteUrl,
                                BackdropRemotePath = BuildTmdbImageUrl(backdropPath, "w1280")
                            });

                            if (result.Count >= 4)
                            {
                                break;
                            }
                        }

                        if (result.Count >= 4)
                        {
                            break;
                        }
                    }
                }

                // Anime keeps AniList as the specialized fallback. Ordinary series use TVmaze
                // first. Both remain keyless fallbacks when TMDb is not configured or misses.
                if (identity.HasAnimeHint && result.Count < 6)
                {
                    await AppendAniListChoicesAsync(result, identity, lookupKey, cancellationToken).ConfigureAwait(false);
                }

                if (result.Count < 6)
                {
                    await AppendTvmazeChoicesAsync(result, identity, lookupKey, cancellationToken).ConfigureAwait(false);
                }

                if (!identity.HasAnimeHint && result.Count < 6)
                {
                    await AppendAniListChoicesAsync(result, identity, lookupKey, cancellationToken).ConfigureAwait(false);
                }

                return result.Take(6).ToArray();
            }
            finally
            {
                networkGate.Release();
            }
        }

        private async Task AppendTvmazeChoicesAsync(
            List<AnikiVideoSeriesArtworkChoice> result,
            SeriesIdentity identity,
            string lookupKey,
            CancellationToken cancellationToken)
        {
            if (!settings.VideoOnlineArtworkEnabled || result == null || result.Count >= 6)
            {
                return;
            }

            var tvMatches = await SearchTvmazeCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
            foreach (var match in tvMatches.Take(2))
            {
                var images = await GetTvmazeImagesAsync(match.Id, cancellationToken).ConfigureAwait(false);
                var posters = SelectTvmazePosterCandidates(images, match.PrimaryPosterUrl, 2);
                var backdrop = SelectTvmazeBackdrop(images);

                foreach (var poster in posters)
                {
                    var preview = await DownloadPickerPreviewAsync("tvmaze", poster, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(preview))
                    {
                        continue;
                    }

                    result.Add(new AnikiVideoSeriesArtworkChoice
                    {
                        PreviewPath = preview,
                        ProviderText = "TVMAZE",
                        MatchText = FormatMatchText(match.Name, match.Year),
                        LanguageText = "TV",
                        SizeText = string.Empty,
                        MetadataTitle = match.Name,
                        MetadataYear = match.Year,
                        MetadataOverview = match.Overview,
                        MetadataGenres = string.Join(", ", match.Genres ?? Array.Empty<string>()),
                        MetadataRating = match.Rating,
                        ProviderId = "tvmaze",
                        RemoteId = match.Id,
                        SeriesLookupKey = lookupKey ?? string.Empty,
                        PosterRemotePath = poster,
                        BackdropRemotePath = backdrop
                    });

                    if (result.Count >= 6)
                    {
                        return;
                    }
                }
            }
        }

        private async Task AppendAniListChoicesAsync(
            List<AnikiVideoSeriesArtworkChoice> result,
            SeriesIdentity identity,
            string lookupKey,
            CancellationToken cancellationToken)
        {
            if (!settings.VideoOnlineArtworkEnabled || result == null || result.Count >= 6)
            {
                return;
            }

            var animeMatches = await SearchAniListCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
            foreach (var match in animeMatches.Take(4))
            {
                if (string.IsNullOrWhiteSpace(match.CoverUrl))
                {
                    continue;
                }

                var preview = await DownloadPickerPreviewAsync("anilist", match.CoverUrl, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    continue;
                }

                result.Add(new AnikiVideoSeriesArtworkChoice
                {
                    PreviewPath = preview,
                    ProviderText = "ANILIST",
                    MatchText = FormatMatchText(match.DisplayTitle, match.Year),
                    LanguageText = "ANIME",
                    SizeText = string.Empty,
                    MetadataTitle = match.DisplayTitle,
                    MetadataYear = match.Year,
                    MetadataOverview = match.Overview,
                    MetadataGenres = string.Join(", ", match.Genres ?? Array.Empty<string>()),
                    MetadataRating = match.Rating,
                    ProviderId = "anilist",
                    RemoteId = match.Id,
                    SeriesLookupKey = lookupKey ?? string.Empty,
                    PosterRemotePath = match.CoverUrl,
                    BackdropRemotePath = match.BannerUrl
                });

                if (result.Count >= 6)
                {
                    return;
                }
            }
        }

        public async Task<AnikiVideoArtworkInfo> ApplyArtworkChoiceAsync(
            string videoPath,
            AnikiVideoSeriesArtworkChoice choice,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || choice == null || !TryParseSeriesIdentity(videoPath, out var identity))
            {
                return null;
            }

            return await ApplyArtworkChoiceToCacheKeyAsync(
                BuildSeriesLookupKey(identity),
                choice,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<AnikiVideoArtworkInfo> ApplyArtworkChoiceToCacheKeyAsync(
            string cacheKey,
            AnikiVideoSeriesArtworkChoice choice,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || choice == null)
            {
                return null;
            }

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = new SeriesCacheEntry
                    {
                        MatcherVersion = MatcherVersion,
                        ProviderId = choice.ProviderId ?? string.Empty,
                        RemoteId = choice.RemoteId,
                        IsManual = true,
                        LastAttemptUtc = DateTime.UtcNow
                    };

                    // Same rationale as the TMDb movie picker: every manual Apply must create fresh
                    // cache file names so replacing an existing artwork never collides with a file that
                    // is still displayed by WPF.
                    var manualVersion = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);

                    if (!string.IsNullOrWhiteSpace(choice.PosterRemotePath))
                    {
                        var posterStem = BuildManualArtworkStem(cacheKey, "poster", choice.PosterRemotePath, manualVersion);
                        // The selected preview is already local and valid. Promote it first so
                        // replacing an existing artwork never depends on another provider request.
                        entry.PosterFileName = PromotePickerPreviewToCache(
                            posterStem,
                            choice.PreviewPath);

                        if (string.IsNullOrWhiteSpace(entry.PosterFileName))
                        {
                            entry.PosterFileName = await DownloadAndCacheImageAsync(
                                posterStem,
                                choice.PosterRemotePath,
                                PosterMaxDimension,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(choice.BackdropRemotePath))
                    {
                        entry.HeroBackdropVersion = CurrentHeroBackdropVersion;
                        entry.BackdropFileName = await DownloadAndCacheImageAsync(
                            BuildManualArtworkStem(cacheKey, "backdrop", choice.BackdropRemotePath, manualVersion),
                            choice.BackdropRemotePath,
                            BackdropMaxDimension,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                        string.IsNullOrWhiteSpace(entry.BackdropFileName))
                    {
                        return null;
                    }

                    StoreEntry(cacheKey, entry);
                    return TryResolveCached(cacheKey, preferPoster: true, out _);
                }
                finally
                {
                    networkGate.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public long GetCacheSizeBytes()
        {
            try
            {
                if (!Directory.Exists(cacheRoot))
                {
                    return 0L;
                }

                long total = 0L;
                foreach (var file in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
                return total;
            }
            catch
            {
                return 0L;
            }
        }

        public void ClearCache()
        {
            try
            {
                lock (indexSync)
                {
                    cacheIndex.Clear();
                    providerIdentityCacheKeys = null;
                }

                if (Directory.Exists(cacheRoot))
                {
                    foreach (var file in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.TopDirectoryOnly))
                    {
                        TryDelete(file);
                    }
                }

                EnsureCacheDirectory();
                SaveIndex();
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to clear series artwork cache.");
            }
        }

        private async Task<AnikiVideoArtworkInfo> ResolveAsync(
            string videoPath,
            bool preferPoster,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || !TryParseSeriesIdentity(videoPath, out var identity))
            {
                return null;
            }

            global::AnikiHelper.AnikiLog.Debug(logger, 
                $"[AnikiHelper][VideoCenter][Scraper] Episodic media detected: title='{identity.Title}', year={identity.Year}, S{identity.Season:00}E{identity.Episode:00}, animeHint={identity.HasAnimeHint}.");

            var cacheKey = BuildSeriesLookupKey(identity);
            var cached = TryResolveCached(cacheKey, preferPoster, out var freshNegative);
            if (cached != null || freshNegative)
            {
                return cached;
            }

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = TryResolveCached(cacheKey, preferPoster, out freshNegative);
                if (cached != null || freshNegative)
                {
                    return cached;
                }

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = await ScrapeAutomaticAsync(cacheKey, identity, cancellationToken).ConfigureAwait(false);
                    if (entry == null)
                    {
                        RememberNoMatch(cacheKey);
                        return null;
                    }

                    StoreEntry(cacheKey, entry);
                    return TryResolveCached(cacheKey, preferPoster, out _);
                }
                finally
                {
                    networkGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Automatic scraping failed.");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<SeriesCacheEntry> ScrapeAutomaticAsync(
            string cacheKey,
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            // TMDb is the primary provider whenever its token is configured. It searches TV
            // shows by original, translated and alternate names and gives us localized posters
            // plus real 16:9 backdrops.
            if (CanUseTmdb)
            {
                var tmdb = await SearchTmdbTvStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                if (tmdb != null)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, 
                        $"[AnikiHelper][VideoCenter][Scraper] TMDb TV match: '{tmdb.Name}' ({tmdb.Year}), score={tmdb.Score:0.0}.");
                    var tmdbEntry = await CacheTmdbTvMatchAsync(cacheKey, tmdb, cancellationToken).ConfigureAwait(false);
                    if (tmdbEntry != null)
                    {
                        return tmdbEntry;
                    }
                }
            }

            // Anime uses AniList as its specialized fallback.
            if (identity.HasAnimeHint && settings.VideoOnlineArtworkEnabled)
            {
                var anime = await SearchAniListStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                if (anime != null)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter][Scraper] AniList exact match: '{anime.DisplayTitle}' ({anime.Year}).");
                    return await CacheAniListMatchAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
                }
            }

            TvmazeMatch tv = null;
            if (settings.VideoOnlineArtworkEnabled)
            {
                tv = await SearchTvmazeStrictAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            if (tv != null)
            {
                // TVmaze itself can identify animation. If AniList also has a strict exact match,
                // prefer AniList for this fallback case so anime gets AniList's artwork.
                if (settings.VideoOnlineArtworkEnabled && IsLikelyAnimation(tv))
                {
                    var anime = await SearchAniListStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (anime != null)
                    {
                        return await CacheAniListMatchAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
                    }
                }

                global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter][Scraper] TVmaze fallback match: '{tv.Name}' ({tv.Year}).");
                return await CacheTvmazeMatchAsync(cacheKey, tv, cancellationToken).ConfigureAwait(false);
            }

            // Last chance for ordinary series that TVmaze did not safely identify.
            if (settings.VideoOnlineArtworkEnabled)
            {
                var anime = await SearchAniListStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                if (anime != null)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter][Scraper] AniList final fallback: '{anime.DisplayTitle}' ({anime.Year}).");
                    return await CacheAniListMatchAsync(cacheKey, anime, cancellationToken).ConfigureAwait(false);
                }
            }

            global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter][Scraper] No safe TMDb/TVmaze/AniList match for '{identity.Title}' ({identity.Year}).");
            return null;
        }

        private async Task<SeriesCacheEntry> CacheTmdbTvMatchAsync(
            string cacheKey,
            TmdbTvMatch match,
            CancellationToken cancellationToken)
        {
            var images = await GetTmdbTvImagesAsync(
                match.Id,
                match.OriginalLanguage,
                cancellationToken).ConfigureAwait(false);

            var posterPath = SelectTmdbPosterCandidates(
                images?["posters"] as JArray,
                ResolveTmdbLanguageCode(),
                match.OriginalLanguage,
                1).FirstOrDefault()?["file_path"]?.ToString();

            var backdropPath = SelectTmdbBackdropPath(
                images?["backdrops"] as JArray,
                ResolveTmdbLanguageCode(),
                match.OriginalLanguage);

            if (string.IsNullOrWhiteSpace(posterPath))
            {
                posterPath = match.SearchPosterPath;
            }
            if (string.IsNullOrWhiteSpace(backdropPath))
            {
                backdropPath = match.SearchBackdropPath;
            }

            var entry = new SeriesCacheEntry
            {
                MatcherVersion = MatcherVersion,
                ProviderId = "tmdb-tv",
                RemoteId = match.Id,
                HeroBackdropVersion = CurrentHeroBackdropVersion,
                LastAttemptUtc = DateTime.UtcNow
            };

            var posterUrl = BuildTmdbImageUrl(posterPath, "w780");
            if (!string.IsNullOrWhiteSpace(posterUrl))
            {
                entry.PosterFileName = await DownloadAndCacheImageAsync(
                    cacheKey + ".poster",
                    posterUrl,
                    PosterMaxDimension,
                    cancellationToken).ConfigureAwait(false);
            }

            var backdropUrl = BuildTmdbImageUrl(backdropPath, "original");
            if (!string.IsNullOrWhiteSpace(backdropUrl))
            {
                entry.BackdropFileName = await DownloadAndCacheImageAsync(
                    cacheKey + ".backdrop.hero.v1",
                    backdropUrl,
                    BackdropMaxDimension,
                    cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                   string.IsNullOrWhiteSpace(entry.BackdropFileName)
                ? null
                : entry;
        }

        private async Task<SeriesCacheEntry> CacheTvmazeMatchAsync(
            string cacheKey,
            TvmazeMatch match,
            CancellationToken cancellationToken)
        {
            var images = await GetTvmazeImagesAsync(match.Id, cancellationToken).ConfigureAwait(false);
            var poster = SelectTvmazePosterCandidates(images, match.PrimaryPosterUrl, 1).FirstOrDefault();
            var backdrop = SelectTvmazeBackdrop(images);

            var entry = new SeriesCacheEntry
            {
                MatcherVersion = MatcherVersion,
                ProviderId = "tvmaze",
                RemoteId = match.Id,
                HeroBackdropVersion = CurrentHeroBackdropVersion,
                LastAttemptUtc = DateTime.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(poster))
            {
                entry.PosterFileName = await DownloadAndCacheImageAsync(
                    cacheKey + ".poster",
                    poster,
                    PosterMaxDimension,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(backdrop))
            {
                entry.BackdropFileName = await DownloadAndCacheImageAsync(
                    cacheKey + ".backdrop",
                    backdrop,
                    BackdropMaxDimension,
                    cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                   string.IsNullOrWhiteSpace(entry.BackdropFileName)
                ? null
                : entry;
        }

        private async Task<SeriesCacheEntry> CacheAniListMatchAsync(
            string cacheKey,
            AniListMatch match,
            CancellationToken cancellationToken)
        {
            var entry = new SeriesCacheEntry
            {
                MatcherVersion = MatcherVersion,
                ProviderId = "anilist",
                RemoteId = match.Id,
                HeroBackdropVersion = CurrentHeroBackdropVersion,
                LastAttemptUtc = DateTime.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(match.CoverUrl))
            {
                entry.PosterFileName = await DownloadAndCacheImageAsync(
                    cacheKey + ".poster",
                    match.CoverUrl,
                    PosterMaxDimension,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(match.BannerUrl))
            {
                entry.BackdropFileName = await DownloadAndCacheImageAsync(
                    cacheKey + ".backdrop",
                    match.BannerUrl,
                    BackdropMaxDimension,
                    cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                   string.IsNullOrWhiteSpace(entry.BackdropFileName)
                ? null
                : entry;
        }

        private async Task<string> ResolveLogoForCacheKeyAsync(
            string cacheKey,
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(cacheKey) || identity == null) return string.Empty;
            var existing = GetEntrySnapshot(cacheKey);
            var cached = existing == null ? string.Empty : GetCachedPath(existing.LogoFileName);
            if (!string.IsNullOrWhiteSpace(cached)) return cached;

            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                existing = GetEntrySnapshot(cacheKey) ?? new SeriesCacheEntry { MatcherVersion = MatcherVersion };
                cached = GetCachedPath(existing.LogoFileName);
                if (!string.IsNullOrWhiteSpace(cached)) return cached;

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string logoUrl = string.Empty;
                    string provider = string.Empty;
                    int remoteId = 0;

                    if (CanUseTmdb)
                    {
                        var tmdb = await SearchTmdbTvStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                        if (tmdb != null)
                        {
                            var details = await GetTmdbTvDetailsAsync(tmdb.Id, cancellationToken).ConfigureAwait(false);
                            var originalLanguage = details?["original_language"]?.ToString() ?? tmdb.OriginalLanguage;
                            var images = await GetTmdbTvImagesAsync(tmdb.Id, originalLanguage, cancellationToken).ConfigureAwait(false);
                            var logoPath = BuildTmdbLogoChoices(images?["logos"] as JArray, ResolveTmdbLanguageCode(), originalLanguage, 1)
                                .FirstOrDefault()?["file_path"]?.ToString() ?? string.Empty;
                            logoUrl = BuildTmdbImageUrl(logoPath, "original");
                            if (!string.IsNullOrWhiteSpace(logoUrl))
                            {
                                provider = "tmdb-tv";
                                remoteId = tmdb.Id;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(logoUrl) && settings.VideoOnlineArtworkEnabled)
                    {
                        var tv = await SearchTvmazeStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                        if (tv != null)
                        {
                            var images = await GetTvmazeImagesAsync(tv.Id, cancellationToken).ConfigureAwait(false);
                            logoUrl = SelectTvmazeLogo(images);
                            if (!string.IsNullOrWhiteSpace(logoUrl))
                            {
                                provider = "tvmaze";
                                remoteId = tv.Id;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(logoUrl)) return string.Empty;
                    var fileName = await DownloadLogoToCacheAsync(cacheKey + ".logo.v1", logoUrl, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
                    existing.LogoFileName = fileName;
                    existing.NoMatch = false;
                    existing.LastAttemptUtc = DateTime.UtcNow;
                    if (!existing.IsManual)
                    {
                        existing.ProviderId = provider;
                        existing.RemoteId = remoteId;
                    }
                    StoreEntry(cacheKey, existing);
                    return GetCachedPath(fileName);
                }
                finally
                {
                    networkGate.Release();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<AnikiVideoMetadataRecord> ResolveMetadataForIdentityAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || identity == null || string.IsNullOrWhiteSpace(identity.Title)) return null;
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (CanUseTmdb)
                {
                    var tmdb = await SearchTmdbTvStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (tmdb != null)
                    {
                        var details = await GetTmdbTvDetailsAsync(tmdb.Id, cancellationToken).ConfigureAwait(false);
                        var record = BuildTmdbMetadataRecord(details, tmdb, identity.HasAnimeHint ? "anime" : "series");
                        if (record != null) return record;
                    }
                }

                if (settings.VideoOnlineArtworkEnabled)
                {
                    var tv = await SearchTvmazeStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (tv != null)
                    {
                        var show = await GetTvmazeShowAsync(tv.Id, cancellationToken).ConfigureAwait(false);
                        if (show != null)
                        {
                            var rating = ParseDouble(show?["rating"]?["average"]?.ToString());
                            var runtime = ParseInt(show?["averageRuntime"]?.ToString());
                            if (runtime <= 0) runtime = ParseInt(show?["runtime"]?.ToString());
                            var tvmazeCast = string.Join(" • ", (show?["_embedded"]?["cast"] as JArray)?.OfType<JObject>()
                                .Select(x => x["person"]?["name"]?.ToString() ?? string.Empty)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                                .Take(8) ?? Enumerable.Empty<string>());
                            return new AnikiVideoMetadataRecord
                            {
                                Title = show?["name"]?.ToString() ?? tv.Name,
                                Year = ParseYear(show?["premiered"]?.ToString()) > 0 ? ParseYear(show?["premiered"]?.ToString()) : tv.Year,
                                MediaType = identity.HasAnimeHint ? "anime" : "series",
                                Overview = StripHtml(show?["summary"]?.ToString() ?? tv.Overview),
                                Genres = string.Join(", ", (show?["genres"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()),
                                Rating = rating > 0.0 ? rating : tv.Rating,
                                RuntimeMinutes = Math.Max(0, runtime),
                                Cast = tvmazeCast,
                                Provider = "TVMAZE",
                                ProviderId = tv.Id.ToString(CultureInfo.InvariantCulture),
                                UpdatedUtc = DateTime.UtcNow
                            };
                        }
                    }
                }

                if (identity.HasAnimeHint && settings.VideoOnlineArtworkEnabled)
                {
                    var anime = await SearchAniListStrictAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (anime != null)
                    {
                        return new AnikiVideoMetadataRecord
                        {
                            Title = anime.DisplayTitle,
                            Year = anime.Year,
                            MediaType = "anime",
                            Overview = anime.Overview,
                            Genres = string.Join(", ", anime.Genres ?? Array.Empty<string>()),
                            Rating = anime.Rating,
                            Provider = "ANILIST",
                            ProviderId = anime.Id.ToString(CultureInfo.InvariantCulture),
                            UpdatedUtc = DateTime.UtcNow
                        };
                    }
                }
                return null;
            }
            finally
            {
                networkGate.Release();
            }
        }

        private static AnikiVideoMetadataRecord BuildTmdbMetadataRecord(JObject details, TmdbTvMatch match, string mediaType)
        {
            if (details == null && match == null) return null;
            var runtime = 0;
            var episodeRunTime = details?["episode_run_time"] as JArray;
            if (episodeRunTime != null && episodeRunTime.Count > 0) runtime = ParseInt(episodeRunTime[0]?.ToString());
            if (runtime <= 0) runtime = ParseInt(details?["last_episode_to_air"]?["runtime"]?.ToString());
            var creators = details?["created_by"] as JArray;
            var creatorText = creators == null ? string.Empty : string.Join(", ", creators.OfType<JObject>()
                .Select(x => x["name"]?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).Take(3));
            var tmdbCast = string.Join(" • ", (details?["credits"]?["cast"] as JArray)?.OfType<JObject>()
                .Select(x => x["name"]?.ToString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(8) ?? Enumerable.Empty<string>());
            var year = ParseYear(details?["first_air_date"]?.ToString());
            if (year <= 0) year = match?.Year ?? 0;
            return new AnikiVideoMetadataRecord
            {
                Title = details?["name"]?.ToString() ?? match?.Name ?? string.Empty,
                OriginalTitle = details?["original_name"]?.ToString() ?? string.Empty,
                Year = year,
                MediaType = mediaType ?? "series",
                Overview = details?["overview"]?.ToString() ?? match?.Overview ?? string.Empty,
                Genres = JoinGenreNames(details?["genres"] as JArray),
                Rating = ParseDouble(details?["vote_average"]?.ToString()) > 0.0 ? ParseDouble(details?["vote_average"]?.ToString()) : (match?.Rating ?? 0.0),
                VoteCount = ParseInt(details?["vote_count"]?.ToString()),
                RuntimeMinutes = Math.Max(0, runtime),
                Tagline = details?["tagline"]?.ToString() ?? string.Empty,
                Credits = string.IsNullOrWhiteSpace(creatorText) ? string.Empty : "Created by: " + creatorText,
                Cast = tmdbCast,
                Provider = "TMDB",
                ProviderId = (match?.Id ?? 0).ToString(CultureInfo.InvariantCulture),
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static List<JObject> BuildTmdbLogoChoices(JArray logos, string preferredLanguage, string originalLanguage, int maxCount)
        {
            if (logos == null) return new List<JObject>();
            var preferred = NormalizeLanguage(preferredLanguage);
            var original = NormalizeLanguage(originalLanguage);
            Func<JObject, int> rank = logo =>
            {
                var lang = GetTmdbImageLanguage(logo);
                if (!string.IsNullOrWhiteSpace(preferred) && string.Equals(lang, preferred, StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.IsNullOrWhiteSpace(lang)) return 1;
                if (!string.IsNullOrWhiteSpace(original) && string.Equals(lang, original, StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) return 3;
                return 4;
            };
            return logos.OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace(x["file_path"]?.ToString()))
                .Where(x => !x["file_path"].ToString().EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(rank)
                .ThenByDescending(x => ParseDouble(x["vote_average"]?.ToString()))
                .ThenByDescending(x => ParseInt(x["width"]?.ToString()) * ParseInt(x["height"]?.ToString()))
                .Take(Math.Max(1, maxCount)).ToList();
        }

        private static string SelectTvmazeLogo(JArray images)
        {
            if (images == null) return string.Empty;
            var candidate = images.OfType<JObject>()
                .Where(x => string.Equals(x["type"]?.ToString(), "typography", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x["main"]?.Value<bool>() == true)
                .FirstOrDefault();
            return GetTvmazeImageUrl(candidate);
        }

        private async Task<JObject> GetTvmazeShowAsync(int showId, CancellationToken cancellationToken)
        {
            if (showId <= 0) return null;
            return await GetJsonAsync("https://api.tvmaze.com/shows/" + showId.ToString(CultureInfo.InvariantCulture) + "?embed=cast", cancellationToken).ConfigureAwait(false) as JObject;
        }

        private async Task<string> DownloadLogoPreviewAsync(string prefix, string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            EnsureCacheDirectory();
            var path = Path.Combine(cacheRoot, "logo_picker_" + (prefix ?? "logo") + "_" + Sha256Hex(url) + ".png");
            if (File.Exists(path)) return path;
            return await DownloadPngUrlAsync(url, path, 900, cancellationToken).ConfigureAwait(false) ? path : string.Empty;
        }

        private async Task<string> DownloadLogoToCacheAsync(string stem, string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(url)) return string.Empty;
            EnsureCacheDirectory();
            var fileName = stem + ".png";
            var path = Path.Combine(cacheRoot, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return fileName;
            return await DownloadPngUrlAsync(url, path, 1200, cancellationToken).ConfigureAwait(false) ? fileName : string.Empty;
        }

        private async Task<bool> DownloadPngUrlAsync(string url, string outputPath, int maxDimension, CancellationToken cancellationToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return false;
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytes == null || bytes.Length == 0) return false;
                    var temp = outputPath + ".tmp";
                    TryDelete(temp);
                    CreateOptimizedPng(bytes, temp, maxDimension, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(temp)) return false;
                    TryDelete(outputPath);
                    File.Move(temp, outputPath);
                    return File.Exists(outputPath);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Logo download failed: " + url);
                return false;
            }
        }

        private static void CreateOptimizedPng(byte[] imageBytes, string outputPath, int maxDimension, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapImage bitmap;
            using (var stream = new MemoryStream(imageBytes, false))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder?.Frames != null && decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
                var width = frame?.PixelWidth ?? 0;
                var height = frame?.PixelHeight ?? 0;
                stream.Position = 0;
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                if (width > 0 && height > 0 && maxDimension > 0)
                {
                    if (width >= height && width > maxDimension) bitmap.DecodePixelWidth = maxDimension;
                    else if (height > width && height > maxDimension) bitmap.DecodePixelHeight = maxDimension;
                }
                bitmap.EndInit();
                bitmap.Freeze();
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None)) encoder.Save(output);
        }

        private async Task<TmdbTvMatch> SearchTmdbTvStrictAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            var candidates = await SearchTmdbTvCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
            return candidates.FirstOrDefault();
        }

        private async Task<IReadOnlyList<TmdbTvMatch>> SearchTmdbTvCandidatesAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            if (!CanUseTmdb || identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return Array.Empty<TmdbTvMatch>();
            }

            var bestById = new Dictionary<int, TmdbTvMatch>();
            var variants = BuildSearchTitleVariants(identity.Title);

            foreach (var query in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var useYearFilter in new[] { true, false })
                {
                    if (!useYearFilter && identity.Year <= 0)
                    {
                        continue;
                    }

                    var locale = ToTmdbLocale(ResolveTmdbLanguageCode());
                    var url = new StringBuilder("https://api.themoviedb.org/3/search/tv");
                    url.Append("?include_adult=false&page=1");
                    url.Append("&language=").Append(Uri.EscapeDataString(locale));
                    url.Append("&query=").Append(Uri.EscapeDataString(query));
                    if (useYearFilter && identity.Year > 0)
                    {
                        url.Append("&first_air_date_year=").Append(identity.Year.ToString(CultureInfo.InvariantCulture));
                    }

                    var root = await GetTmdbJsonAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
                    var results = root?["results"] as JArray;
                    if (results == null || results.Count == 0)
                    {
                        continue;
                    }

                    foreach (var result in results.OfType<JObject>().Take(15))
                    {
                        var id = ParseInt(result["id"]?.ToString());
                        if (id <= 0)
                        {
                            continue;
                        }

                        var name = result["name"]?.ToString() ?? string.Empty;
                        var originalName = result["original_name"]?.ToString() ?? string.Empty;
                        var similarity = Math.Max(
                            CalculateTitleSimilarity(identity.Title, name),
                            CalculateTitleSimilarity(identity.Title, originalName));
                        var year = ParseYear(result["first_air_date"]?.ToString());

                        var minimumSimilarity = identity.Year > 0 ? 0.80 : 0.92;
                        if (similarity < minimumSimilarity)
                        {
                            continue;
                        }

                        if (identity.Year > 0 && year > 0 && Math.Abs(year - identity.Year) > 1)
                        {
                            continue;
                        }

                        var yearBonus = 0.0;
                        if (identity.Year > 0)
                        {
                            if (year == identity.Year) yearBonus = 18.0;
                            else if (year > 0 && Math.Abs(year - identity.Year) == 1) yearBonus = 3.0;
                        }

                        var score = (similarity * 100.0) +
                                    yearBonus +
                                    Math.Min(4.0, ParseDouble(result["popularity"]?.ToString()) / 25.0);

                        TmdbTvMatch previous;
                        if (bestById.TryGetValue(id, out previous) && previous.Score >= score)
                        {
                            continue;
                        }

                        bestById[id] = new TmdbTvMatch
                        {
                            Id = id,
                            Name = string.IsNullOrWhiteSpace(name) ? identity.Title : name,
                            Year = year,
                            OriginalLanguage = NormalizeLanguage(result["original_language"]?.ToString()),
                            SearchPosterPath = result["poster_path"]?.ToString() ?? string.Empty,
                            SearchBackdropPath = result["backdrop_path"]?.ToString() ?? string.Empty,
                            Overview = result["overview"]?.ToString() ?? string.Empty,
                            Rating = ParseDouble(result["vote_average"]?.ToString()),
                            Score = score,
                            IsExactTitle =
                                string.Equals(NormalizeTitle(identity.Title), NormalizeTitle(name), StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(NormalizeTitle(identity.Title), NormalizeTitle(originalName), StringComparison.OrdinalIgnoreCase)
                        };
                    }
                }
            }

            return bestById.Values
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.IsExactTitle)
                .ThenByDescending(x => x.Year)
                .Take(8)
                .ToArray();
        }

        private async Task<JObject> GetTmdbTvDetailsAsync(
            int seriesId,
            CancellationToken cancellationToken)
        {
            if (!CanUseTmdb || seriesId <= 0) return null;
            var locale = ToTmdbLocale(ResolveTmdbLanguageCode());
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/tv/{0}?language={1}&append_to_response=credits",
                seriesId, Uri.EscapeDataString(locale));
            return await GetTmdbJsonAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private static string JoinGenreNames(JArray genres)
        {
            if (genres == null) return string.Empty;
            return string.Join(", ", genres.OfType<JObject>()
                .Select(x => x["name"]?.ToString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
        }

        private static string StripHtml(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var stripped = Regex.Replace(value, "<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(Regex.Replace(stripped, @"\s+", " ")).Trim();
        }

        private async Task<JObject> GetTmdbTvImagesAsync(
            int seriesId,
            string originalLanguage,
            CancellationToken cancellationToken)
        {
            if (!CanUseTmdb || seriesId <= 0)
            {
                return null;
            }

            var preferred = ResolveTmdbLanguageCode();
            var include = new List<string>();
            Action<string> add = value =>
            {
                value = NormalizeLanguage(value);
                if (!string.IsNullOrWhiteSpace(value) &&
                    !include.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                {
                    include.Add(value);
                }
            };

            add(preferred);
            add(originalLanguage);
            add("en");

            var locale = ToTmdbLocale(preferred);
            var includeImageLanguage = string.Join(",", include.Concat(new[] { "null" }).Distinct(StringComparer.OrdinalIgnoreCase));
            var url = "https://api.themoviedb.org/3/tv/" +
                      seriesId.ToString(CultureInfo.InvariantCulture) +
                      "/images?language=" + Uri.EscapeDataString(locale) +
                      "&include_image_language=" + Uri.EscapeDataString(includeImageLanguage);

            return await GetTmdbJsonAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private static List<JObject> SelectTmdbPosterCandidates(
            JArray posters,
            string preferredLanguage,
            string originalLanguage,
            int limit)
        {
            var result = new List<JObject>();
            if (posters == null || limit <= 0)
            {
                return result;
            }

            var preferred = NormalizeLanguage(preferredLanguage);
            var original = NormalizeLanguage(originalLanguage);

            Func<JObject, int> languageRank = image =>
            {
                var language = GetTmdbImageLanguage(image);
                if (!string.IsNullOrWhiteSpace(preferred) &&
                    string.Equals(language, preferred, StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.IsNullOrWhiteSpace(language)) return 1;
                if (!string.IsNullOrWhiteSpace(original) &&
                    string.Equals(language, original, StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) return 3;
                return 4;
            };

            return posters.OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace(x["file_path"]?.ToString()))
                .OrderBy(languageRank)
                .ThenByDescending(x => ParseDouble(x["vote_average"]?.ToString()))
                .ThenByDescending(x => ParseInt(x["vote_count"]?.ToString()))
                .Take(limit)
                .ToList();
        }

        private static string SelectTmdbBackdropPath(
            JArray backdrops,
            string preferredLanguage,
            string originalLanguage)
        {
            if (backdrops == null)
            {
                return string.Empty;
            }

            var preferred = NormalizeLanguage(preferredLanguage);
            var original = NormalizeLanguage(originalLanguage);

            Func<JObject, int> languageRank = image =>
            {
                var language = GetTmdbImageLanguage(image);
                // Hero wallpapers without embedded text are ideal and therefore always first.
                if (string.IsNullOrWhiteSpace(language)) return 0;
                if (!string.IsNullOrWhiteSpace(preferred) &&
                    string.Equals(language, preferred, StringComparison.OrdinalIgnoreCase)) return 1;
                if (!string.IsNullOrWhiteSpace(original) &&
                    string.Equals(language, original, StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) return 3;
                return 4;
            };

            Func<JObject, double> ratioPenalty = image =>
            {
                var ratio = ParseDouble(image?["aspect_ratio"]?.ToString());
                if (ratio <= 0.0)
                {
                    var width = ParseDouble(image?["width"]?.ToString());
                    var height = ParseDouble(image?["height"]?.ToString());
                    ratio = height > 0.0 ? width / height : 0.0;
                }
                return ratio > 0.0 ? Math.Abs(ratio - (16.0 / 9.0)) : 10.0;
            };

            Func<JObject, long> pixelArea = image =>
            {
                var width = ParseInt(image?["width"]?.ToString());
                var height = ParseInt(image?["height"]?.ToString());
                return (long)Math.Max(0, width) * Math.Max(0, height);
            };

            return backdrops.OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace(x["file_path"]?.ToString()))
                .OrderBy(languageRank)
                .ThenBy(ratioPenalty)
                .ThenByDescending(x => ParseDouble(x["vote_average"]?.ToString()))
                .ThenByDescending(x => ParseInt(x["vote_count"]?.ToString()))
                .ThenByDescending(pixelArea)
                .Select(x => x["file_path"]?.ToString() ?? string.Empty)
                .FirstOrDefault() ?? string.Empty;
        }

        private async Task<JObject> GetTmdbJsonAsync(string url, CancellationToken cancellationToken)
        {
            var token = (settings?.VideoTmdbReadAccessToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token) || IsTmdbAuthorizationBlocked())
            {
                return null;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 401) MarkTmdbUnauthorized(token);
                        else
                        {
                            global::AnikiHelper.AnikiLog.Debug(logger, 
                                "[AnikiHelper][VideoCenter][SeriesArtwork][TMDb] HTTP " +
                                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                        }
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
                }
            }
        }

        private string ResolveTmdbLanguageCode()
        {
            var configured = NormalizeLanguage(settings?.VideoTmdbArtworkLanguage);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            try
            {
                var language = NormalizeLanguage(CultureInfo.CurrentUICulture?.TwoLetterISOLanguageName);
                return string.IsNullOrWhiteSpace(language) ? "en" : language;
            }
            catch
            {
                return "en";
            }
        }

        private static string ToTmdbLocale(string language)
        {
            switch (NormalizeLanguage(language))
            {
                case "fr": return "fr-FR";
                case "es": return "es-ES";
                case "de": return "de-DE";
                case "it": return "it-IT";
                case "pt": return "pt-PT";
                case "ja": return "ja-JP";
                case "ko": return "ko-KR";
                case "zh": return "zh-CN";
                case "ru": return "ru-RU";
                case "nl": return "nl-NL";
                case "pl": return "pl-PL";
                case "cs": return "cs-CZ";
                case "tr": return "tr-TR";
                case "bg": return "bg-BG";
                default: return "en-US";
            }
        }

        private static string NormalizeLanguage(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            var dash = normalized.IndexOf('-');
            if (dash > 0)
            {
                normalized = normalized.Substring(0, dash);
            }
            return normalized;
        }

        private static string GetTmdbImageLanguage(JObject image)
        {
            var token = image?["iso_639_1"];
            return token == null || token.Type == JTokenType.Null
                ? string.Empty
                : NormalizeLanguage(token.ToString());
        }

        private static string BuildTmdbImageUrl(string filePath, string size)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            return "https://image.tmdb.org/t/p/" +
                   (string.IsNullOrWhiteSpace(size) ? "original" : size.Trim()) +
                   (filePath.StartsWith("/", StringComparison.Ordinal) ? filePath : "/" + filePath);
        }

        private static IReadOnlyList<string> BuildSearchTitleVariants(string title)
        {
            var result = new List<string>();
            Action<string> add = value =>
            {
                value = SpaceRegex.Replace((value ?? string.Empty).Trim(), " ");
                if (!string.IsNullOrWhiteSpace(value) &&
                    !result.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(value);
                }
            };

            add(title);
            add(Regex.Replace(
                title ?? string.Empty,
                @"\b(?:II|III|IV|V|VI|VII|VIII|IX|X)\b",
                match => RomanNumeralToArabic(match.Value),
                RegexOptions.IgnoreCase));

            return result;
        }

        private static string RomanNumeralToArabic(string value)
        {
            switch ((value ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "II": return "2";
                case "III": return "3";
                case "IV": return "4";
                case "V": return "5";
                case "VI": return "6";
                case "VII": return "7";
                case "VIII": return "8";
                case "IX": return "9";
                case "X": return "10";
                default: return value ?? string.Empty;
            }
        }

        private static double CalculateTitleSimilarity(string expected, string candidate)
        {
            var left = NormalizeTitle(expected);
            var right = NormalizeTitle(candidate);
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return 0.0;
            }

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }

            var leftTokens = new HashSet<string>(
                left.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            var rightTokens = new HashSet<string>(
                right.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            var intersection = leftTokens.Count(token => rightTokens.Contains(token));
            var union = leftTokens.Union(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
            var tokenScore = union <= 0 ? 0.0 : (double)intersection / union;

            var distance = LevenshteinDistance(left, right);
            var maxLength = Math.Max(left.Length, right.Length);
            var editScore = maxLength <= 0 ? 0.0 : 1.0 - ((double)distance / maxLength);
            return Math.Max(tokenScore, editScore);
        }

        private static int LevenshteinDistance(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;

            if (left.Length == 0) return right.Length;
            if (right.Length == 0) return left.Length;

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var j = 0; j <= right.Length; j++) previous[j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }

        private async Task<TvmazeMatch> SearchTvmazeStrictAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            var candidates = await SearchTvmazeCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return null;
            }

            var exact = candidates
                .Where(x => x.IsExactTitle)
                .Where(x => identity.Year <= 0 || x.Year == identity.Year)
                .ToList();

            if (identity.Year <= 0 && exact.Select(x => x.Id).Distinct().Count() != 1)
            {
                return null;
            }

            return exact
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Year)
                .FirstOrDefault();
        }

        private async Task<IReadOnlyList<TvmazeMatch>> SearchTvmazeCandidatesAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return Array.Empty<TvmazeMatch>();
            }

            var url = "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(identity.Title);
            var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            var array = json as JArray;
            if (array == null)
            {
                return Array.Empty<TvmazeMatch>();
            }

            var wanted = NormalizeTitle(identity.Title);
            var result = new List<TvmazeMatch>();

            foreach (var item in array.OfType<JObject>().Take(12))
            {
                var show = item["show"] as JObject;
                if (show == null)
                {
                    continue;
                }

                var name = show["name"]?.ToString() ?? string.Empty;
                var normalized = NormalizeTitle(name);
                var isExactTitle = string.Equals(normalized, wanted, StringComparison.OrdinalIgnoreCase);

                var year = ParseYear(show["premiered"]?.ToString());
                if (identity.Year > 0 && year > 0 && year != identity.Year)
                {
                    continue;
                }

                // TVmaze can return explicit JSON null values for optional objects.
                // Newtonsoft represents those as JValue(null), and indexing a child on that
                // JValue throws InvalidOperationException. Cast optional objects first.
                var network = show["network"] as JObject;
                var networkCountry = network?["country"] as JObject;
                var country = networkCountry?["code"]?.ToString();

                if (string.IsNullOrWhiteSpace(country))
                {
                    var webChannel = show["webChannel"] as JObject;
                    var webCountry = webChannel?["country"] as JObject;
                    country = webCountry?["code"]?.ToString();
                }

                var image = show["image"] as JObject;
                var genres = (show["genres"] as JArray)?.Values<string>()
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray() ?? Array.Empty<string>();

                result.Add(new TvmazeMatch
                {
                    Id = ParseInt(show["id"]?.ToString()),
                    Name = name,
                    Year = year,
                    Type = show["type"]?.ToString() ?? string.Empty,
                    CountryCode = country ?? string.Empty,
                    Genres = genres,
                    Overview = StripHtml(show["summary"]?.ToString()),
                    Rating = ParseDouble((show["rating"] as JObject)?["average"]?.ToString()),
                    PrimaryPosterUrl = image?["original"]?.ToString()
                                       ?? image?["medium"]?.ToString()
                                       ?? string.Empty,
                    Score = ParseDouble(item["score"]?.ToString()),
                    IsExactTitle = isExactTitle
                });
            }

            return result;
        }

        private async Task<JArray> GetTvmazeImagesAsync(int showId, CancellationToken cancellationToken)
        {
            if (showId <= 0)
            {
                return new JArray();
            }

            var json = await GetJsonAsync(
                "https://api.tvmaze.com/shows/" + showId.ToString(CultureInfo.InvariantCulture) + "/images",
                cancellationToken).ConfigureAwait(false);
            return json as JArray ?? new JArray();
        }

        private static IReadOnlyList<string> SelectTvmazePosterCandidates(
            JArray images,
            string primaryPosterUrl,
            int limit)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (images != null)
            {
                foreach (var image in images.OfType<JObject>()
                    .Where(x => string.Equals(x["type"]?.ToString(), "poster", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x["main"]?.Value<bool>() == true))
                {
                    var url = GetTvmazeImageUrl(image);
                    if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
                    {
                        result.Add(url);
                        if (result.Count >= limit)
                        {
                            return result;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(primaryPosterUrl) && seen.Add(primaryPosterUrl))
            {
                result.Add(primaryPosterUrl);
            }

            return result.Take(limit).ToArray();
        }

        private static string SelectTvmazeBackdrop(JArray images)
        {
            if (images == null)
            {
                return string.Empty;
            }

            Func<JObject, int> typeRank = image =>
            {
                var type = image?["type"]?.ToString() ?? string.Empty;
                if (string.Equals(type, "background", StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.Equals(type, "banner", StringComparison.OrdinalIgnoreCase)) return 1;
                return 2;
            };

            Func<JObject, double> ratioPenalty = image =>
            {
                var resolution = (image?["resolutions"] as JObject)?["original"] as JObject;
                var width = ParseDouble(resolution?["width"]?.ToString());
                var height = ParseDouble(resolution?["height"]?.ToString());
                if (width <= 0.0 || height <= 0.0) return 10.0;
                return Math.Abs((width / height) - (16.0 / 9.0));
            };

            Func<JObject, long> pixelArea = image =>
            {
                var resolution = (image?["resolutions"] as JObject)?["original"] as JObject;
                var width = ParseInt(resolution?["width"]?.ToString());
                var height = ParseInt(resolution?["height"]?.ToString());
                return (long)Math.Max(0, width) * Math.Max(0, height);
            };

            var candidate = images.OfType<JObject>()
                .Where(x =>
                    string.Equals(x["type"]?.ToString(), "background", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x["type"]?.ToString(), "banner", StringComparison.OrdinalIgnoreCase))
                .OrderBy(typeRank)
                .ThenByDescending(x => x["main"]?.Value<bool>() == true)
                .ThenBy(ratioPenalty)
                .ThenByDescending(pixelArea)
                .FirstOrDefault();

            return GetTvmazeImageUrl(candidate);
        }

        private static string GetTvmazeImageUrl(JObject image)
        {
            var resolutions = image?["resolutions"] as JObject;
            var original = resolutions?["original"] as JObject;
            var medium = resolutions?["medium"] as JObject;

            return original?["url"]?.ToString()
                   ?? medium?["url"]?.ToString()
                   ?? string.Empty;
        }

        private async Task<AniListMatch> SearchAniListStrictAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            var candidates = await SearchAniListCandidatesAsync(identity, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return null;
            }

            var exact = candidates
                .Where(x => x.IsExactTitle)
                .Where(x => identity.Year <= 0 || x.Year == identity.Year)
                .ToList();

            if (exact.Count == 0)
            {
                return null;
            }

            if (identity.Year <= 0 && exact.Select(x => x.Id).Distinct().Count() != 1)
            {
                return null;
            }

            return exact
                .OrderByDescending(x => x.Popularity)
                .FirstOrDefault();
        }

        private async Task<IReadOnlyList<AniListMatch>> SearchAniListCandidatesAsync(
            SeriesIdentity identity,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return Array.Empty<AniListMatch>();
            }

            const string query = @"query ($search: String) {
  Page(page: 1, perPage: 10) {
    media(search: $search, type: ANIME, sort: SEARCH_MATCH) {
      id
      title { romaji english native }
      synonyms
      startDate { year }
      countryOfOrigin
      coverImage { extraLarge large medium }
      bannerImage
      description(asHtml: false)
      genres
      averageScore
      popularity
      isAdult
    }
  }
}";

            var payload = new JObject
            {
                ["query"] = query,
                ["variables"] = new JObject { ["search"] = identity.Title }
            };

            var root = await PostJsonAsync("https://graphql.anilist.co", payload, cancellationToken).ConfigureAwait(false);
            var data = root?["data"] as JObject;
            var page = data?["Page"] as JObject;
            var media = page?["media"] as JArray;
            if (media == null)
            {
                return Array.Empty<AniListMatch>();
            }

            var wanted = NormalizeTitle(identity.Title);
            var result = new List<AniListMatch>();

            foreach (var item in media.OfType<JObject>())
            {
                if (item["isAdult"]?.Value<bool>() == true)
                {
                    continue;
                }

                var titleObject = item["title"] as JObject;
                var titles = new List<string>
                {
                    titleObject?["english"]?.ToString(),
                    titleObject?["romaji"]?.ToString(),
                    titleObject?["native"]?.ToString()
                };
                if (item["synonyms"] is JArray synonyms)
                {
                    titles.AddRange(synonyms.Values<string>());
                }

                var isExactTitle = titles.Any(title =>
                    !string.IsNullOrWhiteSpace(title) &&
                    string.Equals(NormalizeTitle(title), wanted, StringComparison.OrdinalIgnoreCase));

                var startDate = item["startDate"] as JObject;
                var year = startDate?["year"]?.Value<int?>() ?? 0;
                if (identity.Year > 0 && year > 0 && year != identity.Year)
                {
                    continue;
                }

                var displayTitle = titleObject?["english"]?.ToString();
                if (string.IsNullOrWhiteSpace(displayTitle))
                {
                    displayTitle = titleObject?["romaji"]?.ToString();
                }
                if (string.IsNullOrWhiteSpace(displayTitle))
                {
                    displayTitle = identity.Title;
                }

                result.Add(new AniListMatch
                {
                    Id = ParseInt(item["id"]?.ToString()),
                    DisplayTitle = displayTitle ?? identity.Title,
                    Year = year,
                    CoverUrl = GetAniListCoverUrl(item),
                    BannerUrl = item["bannerImage"]?.Type == JTokenType.String
                        ? item["bannerImage"]?.ToString() ?? string.Empty
                        : string.Empty,
                    Overview = StripHtml(item["description"]?.ToString()),
                    Genres = (item["genres"] as JArray)?.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>(),
                    Rating = ParseDouble(item["averageScore"]?.ToString()) / 10.0,
                    Popularity = ParseInt(item["popularity"]?.ToString()),
                    IsExactTitle = isExactTitle
                });
            }

            return result;
        }

        private static string GetAniListCoverUrl(JObject item)
        {
            var cover = item?["coverImage"] as JObject;
            return cover?["extraLarge"]?.ToString()
                   ?? cover?["large"]?.ToString()
                   ?? cover?["medium"]?.ToString()
                   ?? string.Empty;
        }

        private static bool IsLikelyAnimation(TvmazeMatch match)
        {
            if (match == null)
            {
                return false;
            }

            if (match.Type.IndexOf("anim", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (match.Genres != null && match.Genres.Any(x =>
                x.IndexOf("anime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                x.IndexOf("animation", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            // Japan/Korea/China alone is not enough to call a live-action series anime.
            return false;
        }

        private AnikiVideoArtworkInfo TryResolveCached(
            string cacheKey,
            bool preferPoster,
            out bool freshNegative)
        {
            freshNegative = false;
            SeriesCacheEntry entry;
            lock (indexSync)
            {
                cacheIndex.TryGetValue(cacheKey, out entry);
            }

            if (entry == null)
            {
                return null;
            }

            if (entry.NoMatch)
            {
                // Retry negative results created by the old matcher/provider order. Positive
                // cache entries stay valid and are reused without downloading artwork again.
                if (entry.MatcherVersion < MatcherVersion)
                {
                    RemoveEntry(cacheKey);
                    return null;
                }

                freshNegative = entry.LastAttemptUtc > DateTime.MinValue &&
                                DateTime.UtcNow - entry.LastAttemptUtc < NegativeCacheDuration;
                if (!freshNegative)
                {
                    RemoveEntry(cacheKey);
                }
                return null;
            }

            var poster = GetCachedPath(entry.PosterFileName);
            var backdrop = GetCachedPath(entry.BackdropFileName);

            if (preferPoster)
            {
                if (!string.IsNullOrWhiteSpace(poster)) return new AnikiVideoArtworkInfo { Path = poster, IsPortrait = true };
                if (!string.IsNullOrWhiteSpace(backdrop)) return new AnikiVideoArtworkInfo { Path = backdrop, IsPortrait = false };
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(backdrop)) return new AnikiVideoArtworkInfo { Path = backdrop, IsPortrait = false };
                if (!string.IsNullOrWhiteSpace(poster)) return new AnikiVideoArtworkInfo { Path = poster, IsPortrait = true };
            }

            RemoveEntry(cacheKey);
            return null;
        }

        private static bool TryParseSeriesFolderIdentity(string folderPath, out SeriesIdentity identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                var current = new DirectoryInfo(folderPath);
                if (!current.Exists)
                {
                    return false;
                }

                var seriesDirectory = current;
                if (SeasonFolderRegex.IsMatch(current.Name ?? string.Empty) && current.Parent != null)
                {
                    seriesDirectory = current.Parent;
                }

                string samplePath = FindEpisodeSamplePath(current.FullName);
                if (string.IsNullOrWhiteSpace(samplePath) &&
                    !DirectoryPathsEqual(current.FullName, seriesDirectory.FullName))
                {
                    samplePath = FindEpisodeSamplePath(seriesDirectory.FullName);
                }

                if (string.IsNullOrWhiteSpace(samplePath) ||
                    !TryParseSeriesIdentity(samplePath, out var sampleIdentity))
                {
                    return false;
                }

                var folderTitle = CleanSeriesTitle(seriesDirectory.Name ?? string.Empty, out var folderYear);
                if (string.IsNullOrWhiteSpace(folderTitle))
                {
                    folderTitle = sampleIdentity.Title;
                }

                identity = new SeriesIdentity
                {
                    Title = folderTitle,
                    Year = folderYear > 0 ? folderYear : sampleIdentity.Year,
                    Season = sampleIdentity.Season,
                    Episode = sampleIdentity.Episode,
                    HasAnimeHint = sampleIdentity.HasAnimeHint || HasAnimePathHint(folderPath.ToLowerInvariant())
                };

                return !string.IsNullOrWhiteSpace(identity.Title);
            }
            catch
            {
                return false;
            }
        }

        private static string FindEpisodeSamplePath(string folderPath)
        {
            try
            {
                var direct = FindEpisodeInDirectory(folderPath, 48);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return direct;
                }

                var checkedDirectories = 0;
                foreach (var childPath in Directory.EnumerateDirectories(folderPath))
                {
                    if (checkedDirectories++ >= 12)
                    {
                        break;
                    }

                    string name = string.Empty;
                    try { name = Path.GetFileName(childPath) ?? string.Empty; } catch { }
                    if (!SeasonFolderRegex.IsMatch(name) && checkedDirectories > 4)
                    {
                        continue;
                    }

                    var nested = FindEpisodeInDirectory(childPath, 36);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string FindEpisodeInDirectory(string directoryPath, int maxFiles)
        {
            try
            {
                var count = 0;
                foreach (var filePath in Directory.EnumerateFiles(directoryPath))
                {
                    if (count++ >= maxFiles)
                    {
                        break;
                    }

                    string extension;
                    try { extension = Path.GetExtension(filePath) ?? string.Empty; }
                    catch { continue; }

                    if (!SupportedVideoExtensions.Contains(extension))
                    {
                        continue;
                    }

                    if (TryParseSeriesIdentity(filePath, out _))
                    {
                        return filePath;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool HasAnimePathHint(string lowerPath)
        {
            lowerPath = lowerPath ?? string.Empty;
            return lowerPath.Contains("\\anime\\") ||
                   lowerPath.Contains("/anime/") ||
                   lowerPath.Contains("\\animes\\") ||
                   lowerPath.Contains("/animes/") ||
                   lowerPath.Contains("[anime]") ||
                   lowerPath.Contains("film animé") ||
                   lowerPath.Contains("film anime");
        }

        private static bool DirectoryPathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left ?? string.Empty).TrimEnd('\\', '/'),
                    Path.GetFullPath(right ?? string.Empty).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool TryParseSeriesIdentity(string videoPath, out SeriesIdentity identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return false;
            }

            string fileName;
            try { fileName = Path.GetFileNameWithoutExtension(videoPath) ?? string.Empty; }
            catch { return false; }

            var match = SxxExxRegex.Match(fileName);
            var season = 0;
            var dashEpisodePattern = false;
            if (!match.Success)
            {
                match = XEpisodeRegex.Match(fileName);
            }
            if (!match.Success)
            {
                match = EpisodeWordRegex.Match(fileName);
                season = 1;
            }
            if (!match.Success)
            {
                match = AnimeDashEpisodeRegex.Match(fileName);
                season = 1;
                dashEpisodePattern = match.Success;
            }
            if (!match.Success)
            {
                return false;
            }

            if (dashEpisodePattern && !IsSafeDashEpisodePattern(videoPath, fileName, match))
            {
                return false;
            }

            if (season <= 0)
            {
                season = ParseInt(match.Groups["s"].Value);
            }
            if (season <= 0) season = 1;
            var episode = ParseInt(match.Groups["e"].Value);
            var prefix = fileName.Substring(0, match.Index).Trim(' ', '.', '-', '_');
            var title = CleanSeriesTitle(prefix, out var year);

            if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
            {
                try
                {
                    var parent = Directory.GetParent(videoPath);
                    if (parent != null && SeasonFolderRegex.IsMatch(parent.Name ?? string.Empty))
                    {
                        parent = parent.Parent;
                    }
                    if (parent != null)
                    {
                        title = CleanSeriesTitle(parent.Name ?? string.Empty, out var parentYear);
                        if (year <= 0) year = parentYear;
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
            {
                return false;
            }

            var lowerPath = videoPath.ToLowerInvariant();
            var animeHint = HasAnimePathHint(lowerPath);

            identity = new SeriesIdentity
            {
                Title = title,
                Year = year,
                Season = season,
                Episode = episode,
                HasAnimeHint = animeHint
            };
            return true;
        }

        private static bool IsSafeDashEpisodePattern(string videoPath, string fileName, Match match)
        {
            try
            {
                var lowerPath = (videoPath ?? string.Empty).ToLowerInvariant();
                if (lowerPath.Contains("\\anime\\") ||
                    lowerPath.Contains("/anime/") ||
                    lowerPath.Contains("\\animes\\") ||
                    lowerPath.Contains("/animes/") ||
                    lowerPath.Contains("[anime]") ||
                    BracketRegex.IsMatch(fileName ?? string.Empty))
                {
                    return true;
                }

                var prefix = (fileName ?? string.Empty).Substring(0, match.Index).Trim(' ', '.', '-', '_');
                var prefixTitle = CleanSeriesTitle(prefix, out _);
                if (string.IsNullOrWhiteSpace(prefixTitle))
                {
                    return false;
                }

                var parent = Directory.GetParent(videoPath);
                if (parent != null && SeasonFolderRegex.IsMatch(parent.Name ?? string.Empty))
                {
                    parent = parent.Parent;
                }

                var parentTitle = parent == null ? string.Empty : CleanSeriesTitle(parent.Name ?? string.Empty, out _);
                return !string.IsNullOrWhiteSpace(parentTitle) &&
                       string.Equals(
                           NormalizeTitle(parentTitle),
                           NormalizeTitle(prefixTitle),
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string CleanSeriesTitle(string raw, out int year)
        {
            year = 0;
            var input = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            // Library folders/files often include the season/episode directly in the media name
            // (for example "Shrinking S02" or "Widows Bay S01E03"). Keep the show title
            // canonical so TMDb/TVMaze/AniList are searched for the series, not for the episode token.
            input = SeriesSeasonEpisodeSuffixRegex.Replace(input, string.Empty).Trim(' ', '-', '.', '_');

            Match releaseYearMatch = null;
            var yearMatches = YearRegex.Matches(input);
            if (yearMatches != null && yearMatches.Count > 0)
            {
                releaseYearMatch = yearMatches[yearMatches.Count - 1];
            }

            var titleSource = input;
            if (releaseYearMatch != null)
            {
                var beforeYear = releaseYearMatch.Index > 0
                    ? input.Substring(0, releaseYearMatch.Index)
                    : string.Empty;
                var beforeClean = CleanSeriesReleaseTitle(beforeYear, stripTechnicalTokens: false);

                if (!string.IsNullOrWhiteSpace(beforeClean))
                {
                    year = ParseInt(releaseYearMatch.Value);
                    titleSource = beforeYear;
                }
                else
                {
                    // A show can itself be named with a year (for example "1923"). If there is
                    // no other title text, do not treat that number as release metadata.
                    year = 0;
                    titleSource = input;
                }
            }

            var cleaned = CleanSeriesReleaseTitle(titleSource, stripTechnicalTokens: releaseYearMatch == null);
            cleaned = SeriesSeasonEpisodeSuffixRegex.Replace(cleaned ?? string.Empty, string.Empty).Trim(' ', '-', '.', '_');
            if (string.IsNullOrWhiteSpace(cleaned) && releaseYearMatch != null)
            {
                cleaned = releaseYearMatch.Value;
            }

            return cleaned;
        }

        private static string CleanSeriesReleaseTitle(string raw, bool stripTechnicalTokens)
        {
            var input = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            if (stripTechnicalTokens)
            {
                var releaseStart = StrongReleaseTokenRegex.Match(input);
                if (releaseStart.Success && releaseStart.Index > 1)
                {
                    input = input.Substring(0, releaseStart.Index);
                }
            }

            if (stripTechnicalTokens && TechnicalTokenRegex.IsMatch(input))
            {
                input = Regex.Replace(input, @"-(?:[A-Za-z0-9][A-Za-z0-9._-]{1,24})$", " ");
            }

            var cleaned = input.Replace('.', ' ').Replace('_', ' ');
            cleaned = BracketRegex.Replace(cleaned, " ");
            if (stripTechnicalTokens)
            {
                cleaned = TechnicalTokenRegex.Replace(cleaned, " ");
                cleaned = Regex.Replace(cleaned, @"(?<!\d)(?:1\s+0|2\s+0|5\s+1|7\s+1)(?!\d)", " ");
            }
            cleaned = cleaned.Replace("(", " ").Replace(")", " ").Replace("{", " ").Replace("}", " ");
            cleaned = SpaceRegex.Replace(cleaned, " ").Trim(' ', '-', '.', '_');
            return cleaned;
        }

        private string BuildFolderLookupKey(string folderPath)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(folderPath ?? string.Empty)
                    .TrimEnd('\\', '/')
                    .ToUpperInvariant();
            }
            catch
            {
                normalized = (folderPath ?? string.Empty).Trim().ToUpperInvariant();
            }

            return Sha256Hex("series-folder-v1|" + normalized);
        }

        private string BuildSeriesLookupKey(SeriesIdentity identity)
        {
            return Sha256Hex(
                "series-v2|" +
                NormalizeTitle(identity?.Title) + "|" +
                (identity?.Year ?? 0).ToString(CultureInfo.InvariantCulture) + "|online=" +
                (settings?.VideoOnlineArtworkEnabled == true ? "1" : "0"));
        }

        private async Task<JToken> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            if ((url ?? string.Empty).IndexOf("api.themoviedb.org", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return await GetTmdbJsonAsync(url, cancellationToken).ConfigureAwait(false);
            }

            using (var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][SeriesArtwork] HTTP " +
                                  ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " for " +
                                  SafeProviderFromUrl(url) + ".");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(json) ? null : JToken.Parse(json);
            }
        }

        private async Task<JObject> PostJsonAsync(
            string url,
            JObject payload,
            CancellationToken cancellationToken)
        {
            using (var content = new StringContent(
                payload?.ToString(Formatting.None) ?? "{}",
                Encoding.UTF8,
                "application/json"))
            using (var response = await http.PostAsync(url, content, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][SeriesArtwork] HTTP " +
                                  ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " from AniList.");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
            }
        }

        private static string SafeProviderFromUrl(string url)
        {
            if ((url ?? string.Empty).IndexOf("themoviedb", StringComparison.OrdinalIgnoreCase) >= 0) return "TMDb";
            if ((url ?? string.Empty).IndexOf("tvmaze", StringComparison.OrdinalIgnoreCase) >= 0) return "TVmaze";
            if ((url ?? string.Empty).IndexOf("anilist", StringComparison.OrdinalIgnoreCase) >= 0) return "AniList";
            return "online artwork provider";
        }

        private async Task<string> DownloadPickerPreviewAsync(
            string provider,
            string remoteUrl,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return string.Empty;
            }

            var fileName = "picker_" + provider + "_" + Sha256Hex(remoteUrl) + ".jpg";
            var path = Path.Combine(cacheRoot, fileName);
            if (File.Exists(path))
            {
                return path;
            }

            var bytes = await DownloadBytesAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            EnsureCacheDirectory();
            var temp = path + ".tmp";
            TryDelete(temp);
            CreateOptimizedJpeg(bytes, temp, PickerMaxDimension, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temp)) return string.Empty;
            TryDelete(path);
            File.Move(temp, path);
            return path;
        }

        private async Task<string> DownloadAndCacheImageAsync(
            string fileStem,
            string remoteUrl,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(fileStem) || string.IsNullOrWhiteSpace(remoteUrl))
            {
                return string.Empty;
            }

            EnsureCacheDirectory();
            var fileName = fileStem + ".jpg";
            var path = Path.Combine(cacheRoot, fileName);

            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return fileName;
                }
            }
            catch
            {
            }

            var bytes = await DownloadBytesAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var temp = path + ".tmp";
            TryDelete(temp);
            CreateOptimizedJpeg(bytes, temp, maxDimension, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temp)) return string.Empty;
            TryDelete(path);
            if (File.Exists(path)) return string.Empty;
            File.Move(temp, path);
            return fileName;
        }

        private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using (var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Image download failed.");
                return null;
            }
        }

        private static void CreateOptimizedJpeg(
            byte[] imageBytes,
            string outputPath,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapImage bitmap;
            using (var stream = new MemoryStream(imageBytes, false))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                var frame = decoder?.Frames != null && decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
                var width = frame?.PixelWidth ?? 0;
                var height = frame?.PixelHeight ?? 0;

                stream.Position = 0;
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;

                if (width > 0 && height > 0 && maxDimension > 0)
                {
                    if (width >= height && width > maxDimension) bitmap.DecodePixelWidth = maxDimension;
                    else if (height > width && height > maxDimension) bitmap.DecodePixelHeight = maxDimension;
                }

                bitmap.EndInit();
                bitmap.Freeze();
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
        }

        private void ClearPickerPreviews()
        {
            try
            {
                if (!Directory.Exists(cacheRoot)) return;
                foreach (var file in Directory.EnumerateFiles(cacheRoot, "picker_*.jpg", SearchOption.TopDirectoryOnly))
                {
                    TryDelete(file);
                }
            }
            catch { }
        }

        private void RememberNoMatch(string cacheKey)
        {
            StoreEntry(cacheKey, new SeriesCacheEntry { MatcherVersion = MatcherVersion, NoMatch = true, LastAttemptUtc = DateTime.UtcNow });
        }

        private void StoreEntry(string cacheKey, SeriesCacheEntry entry)
        {
            lock (indexSync)
            {
                cacheIndex[cacheKey] = entry;
                providerIdentityCacheKeys = null;
            }
            SaveIndex();
        }

        private void RemoveEntry(string cacheKey)
        {
            lock (indexSync)
            {
                cacheIndex.Remove(cacheKey);
                providerIdentityCacheKeys = null;
            }
            SaveIndex();
        }

        private AnikiVideoArtworkInfo TryGetCachedBackdropOnly(string cacheKey)
        {
            SeriesCacheEntry entry;
            lock (indexSync)
            {
                cacheIndex.TryGetValue(cacheKey, out entry);
            }

            if (entry == null || entry.NoMatch)
            {
                return null;
            }

            var backdrop = GetCachedPath(entry.BackdropFileName);
            return string.IsNullOrWhiteSpace(backdrop)
                ? null
                : new AnikiVideoArtworkInfo { Path = backdrop, IsPortrait = false };
        }

        private static string BuildHeroBackdropStem(string cacheKey, string provider, string source)
        {
            var sourceHash = Sha256Hex((source ?? string.Empty).Trim());
            if (sourceHash.Length > 16)
            {
                sourceHash = sourceHash.Substring(0, 16);
            }
            return (cacheKey ?? string.Empty) + ".hero." + (provider ?? "art") + "." + sourceHash;
        }

        private string GetCachedPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
            var path = Path.Combine(cacheRoot, fileName);
            return File.Exists(path) ? path : string.Empty;
        }

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(indexPath)) return;
                var json = File.ReadAllText(indexPath);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, SeriesCacheEntry>>(json);
                if (loaded != null)
                {
                    lock (indexSync)
                    {
                        cacheIndex = new Dictionary<string, SeriesCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                        providerIdentityCacheKeys = null;
                    }
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Failed to load cache index.");
                lock (indexSync)
                {
                    cacheIndex = new Dictionary<string, SeriesCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    providerIdentityCacheKeys = null;
                }
            }
        }

        private void SaveIndex()
        {
            lock (saveSync)
            {
                try
                {
                    EnsureCacheDirectory();
                    Dictionary<string, SeriesCacheEntry> snapshot;
                    lock (indexSync)
                    {
                        snapshot = new Dictionary<string, SeriesCacheEntry>(cacheIndex, StringComparer.OrdinalIgnoreCase);
                    }

                    var temp = indexPath + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                    TryDelete(indexPath);
                    File.Move(temp, indexPath);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Failed to save cache index.");
                }
            }
        }

        private void EnsureCacheDirectory()
        {
            try { Directory.CreateDirectory(cacheRoot); } catch { }
        }

        private static string FormatMatchText(string title, int year)
        {
            return (title ?? string.Empty) + (year > 0 ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty);
        }

        private static string NormalizeTitle(string value)
        {
            var input = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var normalized = input.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            return SpaceRegex.Replace(builder.ToString(), " ").Trim();
        }

        private static int ParseYear(string date)
        {
            return !string.IsNullOrWhiteSpace(date) && date.Length >= 4 ? ParseInt(date.Substring(0, 4)) : 0;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
        }

        private SeriesCacheEntry TryRecoverManualEntry(string cacheKey, SeriesCacheEntry previousEntry)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cacheKey) || !Directory.Exists(cacheRoot))
                {
                    return previousEntry;
                }

                var poster = Directory.EnumerateFiles(
                        cacheRoot,
                        cacheKey + ".poster.manual.*.jpg",
                        SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path =>
                    {
                        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
                    })
                    .FirstOrDefault();

                var backdrop = Directory.EnumerateFiles(
                        cacheRoot,
                        cacheKey + ".backdrop.manual.*.jpg",
                        SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path =>
                    {
                        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
                    })
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(poster) && string.IsNullOrWhiteSpace(backdrop))
                {
                    return previousEntry;
                }

                var recovered = new SeriesCacheEntry
                {
                    MatcherVersion = MatcherVersion,
                    ProviderId = previousEntry?.ProviderId ?? string.Empty,
                    RemoteId = previousEntry?.RemoteId ?? 0,
                    PosterFileName = string.IsNullOrWhiteSpace(poster) ? string.Empty : Path.GetFileName(poster),
                    BackdropFileName = string.IsNullOrWhiteSpace(backdrop) ? string.Empty : Path.GetFileName(backdrop),
                    IsManual = true,
                    NoMatch = false,
                    LastAttemptUtc = DateTime.UtcNow
                };

                StoreEntry(cacheKey, recovered);
                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][Series] Recovered manual artwork cache entry: " + cacheKey);
                return recovered;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][Series] Failed to recover manual artwork cache entry.");
                return previousEntry;
            }
        }

        private static bool IsManualEntry(SeriesCacheEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            if (entry.IsManual)
            {
                return true;
            }

            return (!string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                    entry.PosterFileName.IndexOf(".manual.", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(entry.BackdropFileName) &&
                    entry.BackdropFileName.IndexOf(".manual.", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private SeriesCacheEntry GetEntrySnapshot(string cacheKey)
        {
            lock (indexSync)
            {
                if (!cacheIndex.TryGetValue(cacheKey, out var entry) || entry == null)
                {
                    return null;
                }

                return new SeriesCacheEntry
                {
                    MatcherVersion = entry.MatcherVersion,
                    ProviderId = entry.ProviderId ?? string.Empty,
                    RemoteId = entry.RemoteId,
                    PosterFileName = entry.PosterFileName ?? string.Empty,
                    BackdropFileName = entry.BackdropFileName ?? string.Empty,
                    LogoFileName = entry.LogoFileName ?? string.Empty,
                    IsManual = entry.IsManual,
                    HeroBackdropVersion = entry.HeroBackdropVersion,
                    LastAttemptUtc = entry.LastAttemptUtc,
                    NoMatch = entry.NoMatch
                };
            }
        }

        private string ImportLocalImageToCache(
            string fileStem,
            string sourcePath,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileStem) || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    return string.Empty;
                }

                EnsureCacheDirectory();
                var fileName = fileStem + ".jpg";
                var path = Path.Combine(cacheRoot, fileName);
                var temp = path + ".tmp";
                TryDelete(temp);
                var bytes = File.ReadAllBytes(sourcePath);
                if (bytes == null || bytes.Length == 0)
                {
                    return string.Empty;
                }

                CreateOptimizedJpeg(bytes, temp, maxDimension, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(temp))
                {
                    return string.Empty;
                }

                TryDelete(path);
                if (File.Exists(path))
                {
                    return string.Empty;
                }

                File.Move(temp, path);
                return File.Exists(path) ? fileName : string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Failed to import local artwork.");
                return string.Empty;
            }
        }

        private static string BuildManualArtworkStem(string cacheKey, string kind, string source, string version = null)
        {
            var sourceHash = Sha256Hex(source ?? string.Empty);
            if (sourceHash.Length > 16)
            {
                sourceHash = sourceHash.Substring(0, 16);
            }

            var stem = (cacheKey ?? string.Empty) + "." + (kind ?? "art") + ".manual." + sourceHash;
            if (!string.IsNullOrWhiteSpace(version))
            {
                stem += "." + version.Trim();
            }

            return stem;
        }

        private string PromotePickerPreviewToCache(string fileStem, string previewPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileStem) ||
                    string.IsNullOrWhiteSpace(previewPath) ||
                    !File.Exists(previewPath))
                {
                    return string.Empty;
                }

                EnsureCacheDirectory();
                var fileName = fileStem + ".jpg";
                var destination = Path.Combine(cacheRoot, fileName);
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                {
                    return fileName;
                }

                using (var input = new FileStream(
                    previewPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read))
                {
                    input.CopyTo(output);
                }

                return File.Exists(destination) && new FileInfo(destination).Length > 0
                    ? fileName
                    : string.Empty;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][SeriesArtwork] Failed to promote picker preview.");
                return string.Empty;
            }
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose()
        {
            try { http?.Dispose(); } catch { }
            try { networkGate?.Dispose(); } catch { }
            foreach (var gate in cacheLocks.Values)
            {
                try { gate?.Dispose(); } catch { }
            }
            cacheLocks.Clear();
        }
    }
}
