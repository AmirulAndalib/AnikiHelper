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
    public sealed class AnikiVideoTmdbArtworkChoice
    {
        public string PreviewPath { get; set; } = string.Empty;
        public string ProviderText { get; set; } = "TMDB";
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

        internal int MovieId { get; set; }
        internal string PosterRemotePath { get; set; } = string.Empty;
        internal string BackdropRemotePath { get; set; } = string.Empty;
        internal string LogoRemotePath { get; set; } = string.Empty;
    }

    public sealed class AnikiVideoTmdbMovieMatchChoice
    {
        public string PreviewPath { get; set; } = string.Empty;
        public string ProviderText { get; set; } = "TMDB";
        public string MatchText { get; set; } = string.Empty;
        public string LanguageText { get; set; } = string.Empty;
        public string SizeText { get; set; } = string.Empty;
        public string ArtworkTarget { get; set; } = "match";
        public string Overview { get; set; } = string.Empty;
        public string OriginalTitle { get; set; } = string.Empty;
        public int Year { get; set; }
        public int MovieId { get; set; }
    }

    /// <summary>TMDb movie artwork resolver with strict automatic matching and local hashed caching.</summary>
    internal sealed class AnikiVideoTmdbArtworkService : IDisposable
    {
        private sealed class TmdbCacheEntry
        {
            public int MatcherVersion { get; set; }
            public int MovieId { get; set; }
            public string PosterFileName { get; set; } = string.Empty;
            public string BackdropFileName { get; set; } = string.Empty;
            public string LogoFileName { get; set; } = string.Empty;
            public bool IsManual { get; set; }
            public bool NoMatch { get; set; }
            public DateTime LastAttemptUtc { get; set; }
        }

        private sealed class MovieIdentity
        {
            public string Title { get; set; } = string.Empty;
            public int Year { get; set; }
        }

        private sealed class MovieMatch
        {
            public int Id { get; set; }
            public string OriginalLanguage { get; set; } = string.Empty;
            public string SearchPosterPath { get; set; } = string.Empty;
            public string SearchBackdropPath { get; set; } = string.Empty;
        }

        private const int MatcherVersion = 2;
        private const int PosterMaxDimension = 1000;
        private const int BackdropMaxDimension = 960;
        private const int JpegQuality = 88;
        private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromHours(24);

        private static readonly Regex YearRegex =
            new Regex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);

        private static readonly Regex BracketRegex =
            new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);

        private static readonly Regex ParenthesisTechRegex =
            new Regex(@"\((?=[^)]*(?:1080|2160|720|bluray|web|x26|h26|hevc|hdr|remux|multi|french|vost))[^)]*\)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TechnicalTokenRegex =
            new Regex(
                @"\b(?:4320p?|2160p?|1080p?|1080i|720p?|576p?|480p?|4k|8k|uhd|hdr10\+?|hdr10|hdr|dolby[\s._-]*vision|dovi|dv|sdr|" +
                @"blu[\s._-]*ray|bluray|brrip|bdrip|bdremux|web[\s._-]*dl|webdl|webrip|web|hdtv|remux|dvdrip|dvd|" +
                @"x264|x265|h264|h265|h\.264|h\.265|hevc|av1|vc1|mpeg2|" +
                @"aac|aac2?\.0|ac3|eac3|eac3\.?5\.?1|ddp?|ddp5\.?1|ddp7\.?1|dts(?:[\s._-]*hd(?:[\s._-]*(?:ma|hra))?)?|truehd|atmos|flac|mp3|" +
                @"multi|multilang|french|truefrench|vff|vfq|vf2|vf|vostfr|vost|subfrench|subbed|dubbed|dual|" +
                @"proper|repack|extended|unrated|internal|limited|complete|criterion|imax|" +
                @"final[\s._-]*cut|directors?[\s._-]*cut|director['’]?s[\s._-]*cut|ultimate[\s._-]*cut|" +
                @"theatrical[\s._-]*cut|special[\s._-]*edition|extended[\s._-]*edition|remastered|" +
                @"10bit|12bit|8bit|yify|rarbg|amzn|amazon|nf|netflix|dsnp|disney\+?|atvp|apple[\s._-]*tv|hmax|max)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StrongReleaseTokenRegex =
            new Regex(
                @"\b(?:4320p?|2160p?|1080p?|1080i|720p?|576p?|480p?|4k|8k|uhd|hdr10\+?|hdr10|hdr|dolby[\s._-]*vision|dovi|dv|" +
                @"blu[\s._-]*ray|bluray|brrip|bdrip|bdremux|web[\s._-]*dl|webdl|webrip|hdtv|remux|dvdrip|" +
                @"x264|x265|h264|h265|h\.264|h\.265|hevc|av1|" +
                @"aac|ac3|eac3|ddp?|dts|truehd|atmos|" +
                @"multi|multilang|french|truefrench|vff|vfq|vf2|vostfr|subfrench|10bit|12bit|8bit)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ChannelTokenRegex =
            new Regex(@"(?<!\d)(?:1\.0|2\.0|5\.1|7\.1)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SpaceRegex =
            new Regex(@"\s+", RegexOptions.Compiled);

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
        private Dictionary<string, TmdbCacheEntry> cacheIndex =
            new Dictionary<string, TmdbCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<int, List<string>> movieIdCacheKeys;
        private string blockedUnauthorizedTmdbToken = string.Empty;
        private int unauthorizedTmdbLogged;

        public AnikiVideoTmdbArtworkService(
            global::AnikiHelper.AnikiHelperSettings settings,
            string pluginUserDataPath,
            ILogger logger)
        {
            this.settings = settings;
            this.logger = logger ?? LogManager.GetLogger();

            cacheRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "VideoCenter", "TmdbArtworkCache");
            indexPath = Path.Combine(cacheRoot, "index.json");

            http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnikiHelper-VideoCenter/1.0");

            EnsureCacheDirectory();
            LoadIndex();
        }

        public bool IsEnabled
        {
            get
            {
                return settings != null &&
                       settings.VideoOnlineArtworkEnabled &&
                       !string.IsNullOrWhiteSpace(settings.VideoTmdbReadAccessToken) &&
                       !IsTmdbAuthorizationBlocked();
            }
        }

        private bool IsTmdbAuthorizationBlocked()
        {
            var token = (settings?.VideoTmdbReadAccessToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(blockedUnauthorizedTmdbToken)) return false;
            if (string.Equals(blockedUnauthorizedTmdbToken, token, StringComparison.Ordinal)) return true;

            // A changed token gets a fresh attempt without requiring a Playnite restart.
            blockedUnauthorizedTmdbToken = string.Empty;
            Interlocked.Exchange(ref unauthorizedTmdbLogged, 0);
            return false;
        }

        private void MarkTmdbUnauthorized(string token)
        {
            blockedUnauthorizedTmdbToken = (token ?? string.Empty).Trim();
            if (Interlocked.Exchange(ref unauthorizedTmdbLogged, 1) == 0)
            {
                logger?.Warn("[AnikiHelper][VideoCenter][TMDb] HTTP 401. TMDb requests are paused for this session/token to avoid repeated failed calls.");
            }
        }

        public AnikiVideoArtworkInfo GetCachedArtwork(string videoPath, bool preferPoster)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return null;
            }

            try
            {
                var cacheKey = BuildLookupKey(videoPath, ResolveLanguageCode());
                return TryResolveCached(cacheKey, preferPoster, out _);
            }
            catch
            {
                return null;
            }
        }

        public AnikiVideoArtworkInfo GetCachedArtworkByMovieId(int movieId, bool preferPoster)
        {
            if (movieId <= 0) return null;
            try
            {
                List<string> keys;
                lock (indexSync)
                {
                    if (movieIdCacheKeys == null)
                    {
                        movieIdCacheKeys = cacheIndex
                            .Where(x => x.Value != null && !x.Value.NoMatch && x.Value.MovieId > 0)
                            .GroupBy(x => x.Value.MovieId)
                            .ToDictionary(
                                g => g.Key,
                                g => g.OrderByDescending(x => x.Value.IsManual).Select(x => x.Key).ToList());
                    }
                    keys = movieIdCacheKeys.TryGetValue(movieId, out var cachedKeys)
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

        public string GetCachedLogoPathByMovieId(int movieId)
        {
            if (movieId <= 0) return string.Empty;
            try
            {
                List<string> keys;
                lock (indexSync)
                {
                    if (movieIdCacheKeys == null)
                    {
                        movieIdCacheKeys = cacheIndex
                            .Where(x => x.Value != null && !x.Value.NoMatch && x.Value.MovieId > 0)
                            .GroupBy(x => x.Value.MovieId)
                            .ToDictionary(
                                g => g.Key,
                                g => g.OrderByDescending(x => x.Value.IsManual).Select(x => x.Key).ToList());
                    }
                    keys = movieIdCacheKeys.TryGetValue(movieId, out var cachedKeys)
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

        public bool HasCachedArtwork(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return false;
            }

            try
            {
                var cacheKey = BuildLookupKey(videoPath, ResolveLanguageCode());
                return TryResolveCached(cacheKey, preferPoster: true, out _) != null ||
                       TryResolveCached(cacheKey, preferPoster: false, out _) != null;
            }
            catch
            {
                return false;
            }
        }

        public string GetCachedLogoPath(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return string.Empty;
            try
            {
                var cacheKey = BuildLookupKey(videoPath, ResolveLanguageCode());
                var entry = GetEntrySnapshot(cacheKey);
                return entry == null ? string.Empty : GetCachedPath(entry.LogoFileName);
            }
            catch { return string.Empty; }
        }

        public async Task<string> ResolveLogoAsync(string videoPath, CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath)) return string.Empty;
            var cached = GetCachedLogoPath(videoPath);
            if (!string.IsNullOrWhiteSpace(cached)) return cached;
            if (!TryParseMovieIdentity(videoPath, out var identity) || identity == null) return string.Empty;

            var language = ResolveLanguageCode();
            var cacheKey = BuildLookupKey(videoPath, language);
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = GetEntrySnapshot(cacheKey) ?? new TmdbCacheEntry { MatcherVersion = MatcherVersion };
                var already = GetCachedPath(entry.LogoFileName);
                if (!string.IsNullOrWhiteSpace(already)) return already;

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    MovieMatch match;
                    if (entry.MovieId > 0)
                    {
                        match = new MovieMatch { Id = entry.MovieId };
                    }
                    else
                    {
                        match = await SearchMovieStrictAsync(identity, language, cancellationToken).ConfigureAwait(false);
                    }
                    if (match == null || match.Id <= 0) return string.Empty;

                    var details = await GetMovieDetailsAsync(match.Id, language, cancellationToken).ConfigureAwait(false);
                    var originalLanguage = details?["original_language"]?.ToString() ?? match.OriginalLanguage;
                    var images = await GetMovieImagesAsync(match.Id, language, originalLanguage, cancellationToken).ConfigureAwait(false);
                    var logoPath = SelectLogoPath(images?["logos"] as JArray, language, originalLanguage);
                    if (string.IsNullOrWhiteSpace(logoPath)) return string.Empty;

                    var fileName = await DownloadAndCacheLogoAsync(
                        cacheKey + ".logo.v1",
                        logoPath,
                        cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

                    entry.MovieId = match.Id;
                    entry.LogoFileName = fileName;
                    entry.NoMatch = false;
                    entry.LastAttemptUtc = DateTime.UtcNow;
                    StoreEntry(cacheKey, entry);
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

        public async Task<AnikiVideoMetadataRecord> ResolveMetadataAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath) ||
                !TryParseMovieIdentity(videoPath, out var identity) || identity == null)
            {
                return null;
            }

            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var match = await SearchMovieStrictAsync(identity, language, cancellationToken).ConfigureAwait(false);
                if (match == null || match.Id <= 0) return null;
                var details = await GetMovieDetailsAsync(match.Id, language, cancellationToken).ConfigureAwait(false);
                if (details == null) return null;

                var credits = details["credits"] as JObject;
                var crew = credits?["crew"] as JArray;
                var director = crew?.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["job"]?.ToString(), "Director", StringComparison.OrdinalIgnoreCase))?["name"]?.ToString() ?? string.Empty;
                var castNames = string.Join(" • ", (credits?["cast"] as JArray)?.OfType<JObject>()
                    .Select(x => x["name"]?.ToString() ?? string.Empty)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Take(8) ?? Enumerable.Empty<string>());
                var runtime = ParseInt(details["runtime"]?.ToString());
                var title = details["title"]?.ToString() ?? identity.Title;
                var year = ParseYear(details["release_date"]?.ToString());
                if (year <= 0) year = identity.Year;

                var record = new AnikiVideoMetadataRecord
                {
                    Title = title,
                    OriginalTitle = details["original_title"]?.ToString() ?? string.Empty,
                    Year = year,
                    MediaType = "movies",
                    Overview = details["overview"]?.ToString() ?? string.Empty,
                    Genres = JoinGenreNames(details["genres"] as JArray),
                    Rating = ParseDouble(details["vote_average"]?.ToString()),
                    VoteCount = ParseInt(details["vote_count"]?.ToString()),
                    RuntimeMinutes = Math.Max(0, runtime),
                    Tagline = details["tagline"]?.ToString() ?? string.Empty,
                    Credits = string.IsNullOrWhiteSpace(director) ? string.Empty : "Director: " + director,
                    Cast = castNames,
                    Provider = "TMDB",
                    ProviderId = match.Id.ToString(CultureInfo.InvariantCulture),
                    UpdatedUtc = DateTime.UtcNow
                };

                ApplyCollectionMetadata(record, details);

                var cacheKey = BuildLookupKey(videoPath, language);
                var entry = GetEntrySnapshot(cacheKey) ?? new TmdbCacheEntry { MatcherVersion = MatcherVersion };
                if (!entry.IsManual || entry.MovieId <= 0) entry.MovieId = match.Id;
                entry.LastAttemptUtc = DateTime.UtcNow;
                StoreEntry(cacheKey, entry);
                return record;
            }
            finally
            {
                networkGate.Release();
            }
        }

        public async Task<AnikiVideoMetadataRecord> ResolveCollectionMetadataAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath)) return null;

            var movieId = GetCachedMovieId(videoPath);
            if (movieId <= 0)
            {
                // Normal metadata resolution also stores the resolved movie id in the artwork
                // cache, so use it as the safe fallback for media that has never been matched.
                var full = await ResolveMetadataAsync(videoPath, cancellationToken).ConfigureAwait(false);
                if (full != null) return full;
                return null;
            }

            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var details = await GetMovieDetailsAsync(movieId, language, cancellationToken).ConfigureAwait(false);
                if (details == null) return null;
                var record = new AnikiVideoMetadataRecord
                {
                    MediaType = "movies",
                    Provider = "TMDB",
                    ProviderId = movieId.ToString(CultureInfo.InvariantCulture),
                    UpdatedUtc = DateTime.UtcNow
                };
                ApplyCollectionMetadata(record, details);
                return record;
            }
            finally
            {
                networkGate.Release();
            }
        }

        public async Task<AnikiVideoMetadataRecord> ResolveCollectionMetadataByMovieIdAsync(
            int movieId,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || movieId <= 0) return null;

            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var details = await GetMovieDetailsAsync(movieId, language, cancellationToken).ConfigureAwait(false);
                if (details == null) return null;
                var record = new AnikiVideoMetadataRecord
                {
                    MediaType = "movies",
                    Provider = "TMDB",
                    ProviderId = movieId.ToString(CultureInfo.InvariantCulture),
                    UpdatedUtc = DateTime.UtcNow
                };
                ApplyCollectionMetadata(record, details);
                return record;
            }
            finally
            {
                networkGate.Release();
            }
        }

        public AnikiVideoArtworkInfo GetCachedCollectionArtwork(
            int collectionId,
            string posterRemotePath,
            string backdropRemotePath,
            bool preferPoster)
        {
            if (collectionId <= 0) return null;
            var remotePath = preferPoster ? posterRemotePath : backdropRemotePath;
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                remotePath = preferPoster ? backdropRemotePath : posterRemotePath;
                preferPoster = !string.Equals(remotePath, backdropRemotePath, StringComparison.Ordinal);
            }
            if (string.IsNullOrWhiteSpace(remotePath)) return null;
            var kind = preferPoster ? "poster" : "backdrop";
            var fileName = "collection_v1_" + collectionId.ToString(CultureInfo.InvariantCulture) + "_" + kind + "_" + Sha256Hex(remotePath) + ".jpg";
            var path = GetCachedPath(fileName);
            return string.IsNullOrWhiteSpace(path)
                ? null
                : new AnikiVideoArtworkInfo { Path = path, IsPortrait = preferPoster };
        }

        public async Task<AnikiVideoArtworkInfo> ResolveCollectionArtworkAsync(
            int collectionId,
            string posterRemotePath,
            string backdropRemotePath,
            bool preferPoster,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || collectionId <= 0) return null;
            var remotePath = preferPoster ? posterRemotePath : backdropRemotePath;
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                remotePath = preferPoster ? backdropRemotePath : posterRemotePath;
                preferPoster = !string.Equals(remotePath, backdropRemotePath, StringComparison.Ordinal);
            }
            if (string.IsNullOrWhiteSpace(remotePath)) return null;

            var kind = preferPoster ? "poster" : "backdrop";
            var fileName = "collection_v1_" + collectionId.ToString(CultureInfo.InvariantCulture) + "_" + kind + "_" + Sha256Hex(remotePath) + ".jpg";
            var cachedPath = GetCachedPath(fileName);
            if (!string.IsNullOrWhiteSpace(cachedPath))
            {
                return new AnikiVideoArtworkInfo { Path = cachedPath, IsPortrait = preferPoster };
            }

            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var downloaded = await DownloadAndCacheImageAsync(
                    Path.GetFileNameWithoutExtension(fileName),
                    remotePath,
                    preferPoster ? "w780" : "w1280",
                    preferPoster ? PosterMaxDimension : BackdropMaxDimension,
                    cancellationToken).ConfigureAwait(false);
                var path = GetCachedPath(downloaded);
                return string.IsNullOrWhiteSpace(path)
                    ? null
                    : new AnikiVideoArtworkInfo { Path = path, IsPortrait = preferPoster };
            }
            finally
            {
                networkGate.Release();
            }
        }

        public AnikiVideoArtworkInfo GetCachedManualArtwork(string videoPath, bool preferPoster)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return null;
            }

            try
            {
                var cacheKey = BuildLookupKey(videoPath, ResolveLanguageCode());
                TmdbCacheEntry entry;
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

        public Task<AnikiVideoArtworkInfo> ResolveHomeArtworkAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            return ResolveAsync(videoPath, preferPoster: false, cancellationToken);
        }

        public Task<AnikiVideoArtworkInfo> ResolvePreviewArtworkAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            return ResolveAsync(videoPath, preferPoster: true, cancellationToken);
        }

        public async Task<bool> EnsureAutomaticArtworkAsync(
            string videoPath,
            bool requirePoster,
            bool requireBackdrop,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath))
            {
                return false;
            }

            var language = ResolveLanguageCode();
            var cacheKey = BuildLookupKey(videoPath, language);
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = GetEntrySnapshot(cacheKey) ?? new TmdbCacheEntry
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

                if (!TryParseMovieIdentity(videoPath, out var identity) || identity == null)
                {
                    return false;
                }

                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    MovieMatch match = null;

                    // Reuse the already established TMDb identity whenever possible. This is both
                    // faster (the poster pass after the Hero does not search the title again) and
                    // preserves a manual Artwork Manager association exactly.
                    if (existing.MovieId > 0)
                    {
                        var details = await GetMovieDetailsAsync(existing.MovieId, language, cancellationToken).ConfigureAwait(false);
                        if (details != null)
                        {
                            match = new MovieMatch
                            {
                                Id = existing.MovieId,
                                OriginalLanguage = NormalizeLanguage(details["original_language"]?.ToString()),
                                SearchPosterPath = details["poster_path"]?.ToString() ?? string.Empty,
                                SearchBackdropPath = details["backdrop_path"]?.ToString() ?? string.Empty
                            };
                        }
                    }

                    if (match == null)
                    {
                        match = await SearchMovieStrictAsync(identity, language, cancellationToken).ConfigureAwait(false);
                    }

                    if (match == null || match.Id <= 0)
                    {
                        return (!requirePoster || hasPoster) && (!requireBackdrop || hasBackdrop);
                    }

                    var images = await GetMovieImagesAsync(match.Id, language, match.OriginalLanguage, cancellationToken).ConfigureAwait(false);
                    var posterRemote = SelectPosterPath(images, language, match.OriginalLanguage);
                    var backdropRemote = SelectBackdropPath(images, language, match.OriginalLanguage);
                    if (string.IsNullOrWhiteSpace(posterRemote)) posterRemote = match.SearchPosterPath;
                    if (string.IsNullOrWhiteSpace(backdropRemote)) backdropRemote = match.SearchBackdropPath;

                    if (requirePoster && !hasPoster && !string.IsNullOrWhiteSpace(posterRemote))
                    {
                        var file = await DownloadAndCacheImageAsync(
                            cacheKey + ".poster.ensure.v1",
                            posterRemote,
                            "w780",
                            PosterMaxDimension,
                            cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            existing.PosterFileName = file;
                            hasPoster = true;
                        }
                    }

                    if (requireBackdrop && !hasBackdrop && !string.IsNullOrWhiteSpace(backdropRemote))
                    {
                        var file = await DownloadAndCacheImageAsync(
                            cacheKey + ".backdrop.ensure.v1",
                            backdropRemote,
                            "w1280",
                            BackdropMaxDimension,
                            cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            existing.BackdropFileName = file;
                            hasBackdrop = true;
                        }
                    }

                    existing.MatcherVersion = MatcherVersion;
                    existing.NoMatch = false;
                    existing.LastAttemptUtc = DateTime.UtcNow;
                    if (!existing.IsManual || existing.MovieId <= 0)
                    {
                        existing.MovieId = match.Id;
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

        public int GetCachedMovieId(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return 0;
            try
            {
                var cacheKey = BuildLookupKey(videoPath, ResolveLanguageCode());
                return Math.Max(0, GetEntrySnapshot(cacheKey)?.MovieId ?? 0);
            }
            catch
            {
                return 0;
            }
        }

        public async Task<int> ResolveMovieIdForMarkersFallbackAsync(
            string videoPath,
            string fallbackTitle,
            int fallbackYear,
            CancellationToken cancellationToken)
        {
            var cachedMovieId = GetCachedMovieId(videoPath);
            if (cachedMovieId > 0)
            {
                return cachedMovieId;
            }

            if (!IsEnabled)
            {
                return 0;
            }

            MovieIdentity identity = null;
            TryParseMovieIdentity(videoPath, out identity);

            MovieIdentity fallbackIdentity = null;
            TryParseManualSearchIdentity(fallbackTitle ?? string.Empty, out fallbackIdentity);
            var cleanedFallbackTitle = CleanReleaseTitle(
                fallbackIdentity?.Title ?? fallbackTitle ?? string.Empty,
                stripReleaseGroup: false,
                stripTechnicalTokens: true);

            if (!string.IsNullOrWhiteSpace(cleanedFallbackTitle) && cleanedFallbackTitle.Length >= 2)
            {
                if (identity == null)
                {
                    identity = new MovieIdentity();
                }

                // Metadata already resolved by Video Center is generally cleaner and more useful
                // than a release filename, especially for localized titles.
                identity.Title = cleanedFallbackTitle;
            }

            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return 0;
            }

            if (fallbackYear > 0)
            {
                identity.Year = fallbackYear;
            }
            else if (fallbackIdentity?.Year > 0)
            {
                identity.Year = fallbackIdentity.Year;
            }

            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var match = await SearchMovieMarkerFallbackAsync(identity, language, cancellationToken).ConfigureAwait(false);
                if (match == null || match.Id <= 0)
                {
                    return 0;
                }

                try
                {
                    var cacheKey = BuildLookupKey(videoPath, language);
                    var entry = GetEntrySnapshot(cacheKey) ?? new TmdbCacheEntry { MatcherVersion = MatcherVersion };
                    if (!entry.IsManual || entry.MovieId <= 0)
                    {
                        entry.MovieId = match.Id;
                        entry.NoMatch = false;
                        entry.LastAttemptUtc = DateTime.UtcNow;
                        StoreEntry(cacheKey, entry);
                    }
                }
                catch
                {
                    // Marker identity resolution must still succeed even if the artwork cache cannot
                    // be updated for some reason.
                }

                global::AnikiHelper.AnikiLog.Debug(logger, 
                    $"[AnikiHelper][VideoCenter][TMDb] Marker movie fallback match: '{identity.Title}' ({identity.Year}) -> TMDb {match.Id}.");

                return match.Id;
            }
            finally
            {
                networkGate.Release();
            }
        }

        public async Task<IReadOnlyList<AnikiVideoTmdbMovieMatchChoice>> SearchMovieMatchesAsync(
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(searchText))
            {
                return Array.Empty<AnikiVideoTmdbMovieMatchChoice>();
            }

            MovieIdentity identity;
            if (!TryParseManualSearchIdentity(searchText, out identity) || identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return Array.Empty<AnikiVideoTmdbMovieMatchChoice>();
            }

            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Manual matching is intentionally broader than the automatic scraper. The user is
                // choosing the result, so showing plausible remakes/alternate years is preferable to
                // returning nothing because the filename did not pass the strict matcher.
                var root = await SearchMovieRawAsync(identity.Title, 0, language, cancellationToken).ConfigureAwait(false);
                var results = root?["results"] as JArray;
                if (results == null || results.Count == 0)
                {
                    return Array.Empty<AnikiVideoTmdbMovieMatchChoice>();
                }

                var ranked = results.OfType<JObject>()
                    .Where(x => ParseInt(x["id"]?.ToString()) > 0)
                    .Select(x => new
                    {
                        Item = x,
                        Title = x["title"]?.ToString() ?? string.Empty,
                        OriginalTitle = x["original_title"]?.ToString() ?? string.Empty,
                        Year = ParseYear(x["release_date"]?.ToString()),
                        Similarity = Math.Max(
                            CalculateTitleSimilarity(identity.Title, x["title"]?.ToString() ?? string.Empty),
                            CalculateTitleSimilarity(identity.Title, x["original_title"]?.ToString() ?? string.Empty)),
                        Popularity = ParseDouble(x["popularity"]?.ToString()),
                        Votes = ParseInt(x["vote_count"]?.ToString())
                    })
                    .OrderByDescending(x => x.Similarity * 100.0 +
                        (identity.Year > 0 && x.Year == identity.Year ? 24.0 : 0.0) +
                        Math.Min(8.0, x.Popularity / 20.0))
                    .ThenByDescending(x => x.Votes)
                    .Take(8)
                    .ToArray();

                var choices = new List<AnikiVideoTmdbMovieMatchChoice>();
                foreach (var candidate in ranked)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = candidate.Item;
                    var posterRemote = item["poster_path"]?.ToString() ?? string.Empty;
                    var preview = string.IsNullOrWhiteSpace(posterRemote)
                        ? string.Empty
                        : await DownloadPickerPreviewAsync(posterRemote, cancellationToken).ConfigureAwait(false);
                    var rating = ParseDouble(item["vote_average"]?.ToString());
                    var id = ParseInt(item["id"]?.ToString());
                    var suffix = rating > 0.0
                        ? "★ " + rating.ToString("0.0", CultureInfo.CurrentCulture) + "  •  TMDb #" + id.ToString(CultureInfo.InvariantCulture)
                        : "TMDb #" + id.ToString(CultureInfo.InvariantCulture);

                    choices.Add(new AnikiVideoTmdbMovieMatchChoice
                    {
                        PreviewPath = preview,
                        ProviderText = "TMDB",
                        MatchText = candidate.Title + (candidate.Year > 0 ? " (" + candidate.Year.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty),
                        LanguageText = NormalizeLanguage(item["original_language"]?.ToString()).ToUpperInvariant(),
                        SizeText = suffix,
                        Overview = item["overview"]?.ToString() ?? string.Empty,
                        OriginalTitle = candidate.OriginalTitle,
                        Year = candidate.Year,
                        MovieId = id
                    });
                }

                return choices;
            }
            finally
            {
                networkGate.Release();
            }
        }

        public async Task<IReadOnlyList<AnikiVideoTmdbArtworkChoice>> GetArtworkChoicesByMovieIdAsync(
            int movieId,
            string artworkTarget,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || movieId <= 0)
            {
                return Array.Empty<AnikiVideoTmdbArtworkChoice>();
            }

            var target = NormalizeArtworkTarget(artworkTarget);
            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var details = await GetMovieDetailsAsync(movieId, language, cancellationToken).ConfigureAwait(false);
                if (details == null) return Array.Empty<AnikiVideoTmdbArtworkChoice>();

                var originalLanguage = details["original_language"]?.ToString() ?? string.Empty;
                var images = await GetMovieImagesAsync(movieId, language, originalLanguage, cancellationToken).ConfigureAwait(false);
                var title = details["title"]?.ToString() ?? string.Empty;
                var year = ParseYear(details["release_date"]?.ToString());
                var overview = details["overview"]?.ToString() ?? string.Empty;
                var genres = JoinGenreNames(details["genres"] as JArray);
                var rating = ParseDouble(details["vote_average"]?.ToString());
                var runtime = ParseInt(details["runtime"]?.ToString());
                var voteCount = ParseInt(details["vote_count"]?.ToString());
                var tagline = details["tagline"]?.ToString() ?? string.Empty;
                var originalTitle = details["original_title"]?.ToString() ?? string.Empty;
                var crew = details?["credits"]?["crew"] as JArray;
                var director = crew?.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["job"]?.ToString(), "Director", StringComparison.OrdinalIgnoreCase))?["name"]?.ToString();
                var credits = string.IsNullOrWhiteSpace(director) ? string.Empty : "Director: " + director;

                List<JObject> candidates;
                if (string.Equals(target, AnikiVideoManualArtworkOverrideService.Logo, StringComparison.OrdinalIgnoreCase))
                {
                    candidates = BuildLogoChoices(images?["logos"] as JArray, language, originalLanguage, 8);
                }
                else if (string.Equals(target, AnikiVideoManualArtworkOverrideService.Landscape, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(target, AnikiVideoManualArtworkOverrideService.Hero, StringComparison.OrdinalIgnoreCase))
                {
                    candidates = BuildBackdropChoices(images?["backdrops"] as JArray, language, originalLanguage, 8);
                    if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(details["backdrop_path"]?.ToString()))
                    {
                        candidates.Add(new JObject
                        {
                            ["file_path"] = details["backdrop_path"]?.ToString() ?? string.Empty,
                            ["iso_639_1"] = null,
                            ["width"] = 0,
                            ["height"] = 0
                        });
                    }
                }
                else
                {
                    candidates = BuildPosterChoices(images?["posters"] as JArray, language, originalLanguage, 8);
                    if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(details["poster_path"]?.ToString()))
                    {
                        candidates.Add(new JObject
                        {
                            ["file_path"] = details["poster_path"]?.ToString() ?? string.Empty,
                            ["iso_639_1"] = language,
                            ["width"] = 0,
                            ["height"] = 0
                        });
                    }
                }

                var result = new List<AnikiVideoTmdbArtworkChoice>();
                foreach (var image in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remotePath = image?["file_path"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(remotePath)) continue;

                    string preview;
                    if (string.Equals(target, AnikiVideoManualArtworkOverrideService.Logo, StringComparison.OrdinalIgnoreCase))
                    {
                        preview = await DownloadLogoPickerPreviewAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        preview = await DownloadPickerPreviewAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    }
                    if (string.IsNullOrWhiteSpace(preview)) continue;

                    var token = image?["iso_639_1"];
                    var imageLanguage = token == null || token.Type == JTokenType.Null
                        ? string.Empty
                        : NormalizeLanguage(token.ToString());
                    var width = ParseInt(image?["width"]?.ToString());
                    var height = ParseInt(image?["height"]?.ToString());

                    var choice = new AnikiVideoTmdbArtworkChoice
                    {
                        PreviewPath = preview,
                        ProviderText = "TMDB",
                        MatchText = title + (year > 0 ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty),
                        LanguageText = string.Equals(target, AnikiVideoManualArtworkOverrideService.Landscape, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(target, AnikiVideoManualArtworkOverrideService.Hero, StringComparison.OrdinalIgnoreCase)
                            ? "16:9"
                            : (string.IsNullOrWhiteSpace(imageLanguage) ? "NO TEXT" : imageLanguage.ToUpperInvariant()),
                        SizeText = width > 0 && height > 0
                            ? width.ToString(CultureInfo.InvariantCulture) + " × " + height.ToString(CultureInfo.InvariantCulture)
                            : string.Empty,
                        MetadataTitle = title,
                        MetadataYear = year,
                        MetadataOverview = overview,
                        MetadataGenres = genres,
                        MetadataRating = rating,
                        MetadataRuntimeMinutes = runtime,
                        MetadataVoteCount = voteCount,
                        MetadataTagline = tagline,
                        MetadataCredits = credits,
                        MetadataOriginalTitle = originalTitle,
                        MovieId = movieId
                    };

                    if (string.Equals(target, AnikiVideoManualArtworkOverrideService.Logo, StringComparison.OrdinalIgnoreCase))
                    {
                        choice.LogoRemotePath = remotePath;
                    }
                    else if (string.Equals(target, AnikiVideoManualArtworkOverrideService.Landscape, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(target, AnikiVideoManualArtworkOverrideService.Hero, StringComparison.OrdinalIgnoreCase))
                    {
                        choice.BackdropRemotePath = remotePath;
                    }
                    else
                    {
                        choice.PosterRemotePath = remotePath;
                    }

                    result.Add(choice);
                }
                return result.Take(8).ToArray();
            }
            finally
            {
                networkGate.Release();
            }
        }

        public async Task<AnikiVideoMetadataRecord> ApplyManualMovieMatchAsync(
            string videoPath,
            int movieId,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath) || movieId <= 0)
            {
                return null;
            }

            var language = ResolveLanguageCode();
            var cacheKey = BuildLookupKey(videoPath, language);
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var details = await GetMovieDetailsAsync(movieId, language, cancellationToken).ConfigureAwait(false);
                    if (details == null) return null;

                    var originalLanguage = details["original_language"]?.ToString() ?? string.Empty;
                    var images = await GetMovieImagesAsync(movieId, language, originalLanguage, cancellationToken).ConfigureAwait(false);
                    var posterRemote = SelectPosterPath(images, language, originalLanguage);
                    var backdropRemote = SelectBackdropPath(images, language, originalLanguage);
                    var logoRemote = SelectLogoPath(images?["logos"] as JArray, language, originalLanguage);
                    if (string.IsNullOrWhiteSpace(posterRemote)) posterRemote = details["poster_path"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(backdropRemote)) backdropRemote = details["backdrop_path"]?.ToString() ?? string.Empty;

                    var version = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
                    var entry = new TmdbCacheEntry
                    {
                        MatcherVersion = MatcherVersion,
                        MovieId = movieId,
                        IsManual = true,
                        NoMatch = false,
                        LastAttemptUtc = DateTime.UtcNow
                    };

                    if (!string.IsNullOrWhiteSpace(posterRemote))
                    {
                        entry.PosterFileName = await DownloadAndCacheImageAsync(
                            BuildManualArtworkStem(cacheKey, "poster", posterRemote, version),
                            posterRemote,
                            "w780",
                            PosterMaxDimension,
                            cancellationToken).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrWhiteSpace(backdropRemote))
                    {
                        entry.BackdropFileName = await DownloadAndCacheImageAsync(
                            BuildManualArtworkStem(cacheKey, "backdrop", backdropRemote, version),
                            backdropRemote,
                            "w1280",
                            BackdropMaxDimension,
                            cancellationToken).ConfigureAwait(false);
                    }
                    if (!string.IsNullOrWhiteSpace(logoRemote))
                    {
                        entry.LogoFileName = await DownloadAndCacheLogoAsync(
                            BuildManualArtworkStem(cacheKey, "logo", logoRemote, version),
                            logoRemote,
                            cancellationToken).ConfigureAwait(false);
                    }
                    StoreEntry(cacheKey, entry);

                    var creditsObject = details["credits"] as JObject;
                    var crew = creditsObject?["crew"] as JArray;
                    var director = crew?.OfType<JObject>()
                        .FirstOrDefault(x => string.Equals(x["job"]?.ToString(), "Director", StringComparison.OrdinalIgnoreCase))?["name"]?.ToString() ?? string.Empty;
                    var castNames = string.Join(" • ", (creditsObject?["cast"] as JArray)?.OfType<JObject>()
                        .Select(x => x["name"]?.ToString() ?? string.Empty)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .Take(8) ?? Enumerable.Empty<string>());

                    var record = new AnikiVideoMetadataRecord
                    {
                        Title = details["title"]?.ToString() ?? string.Empty,
                        OriginalTitle = details["original_title"]?.ToString() ?? string.Empty,
                        Year = ParseYear(details["release_date"]?.ToString()),
                        MediaType = "movies",
                        Overview = details["overview"]?.ToString() ?? string.Empty,
                        Genres = JoinGenreNames(details["genres"] as JArray),
                        Rating = ParseDouble(details["vote_average"]?.ToString()),
                        VoteCount = ParseInt(details["vote_count"]?.ToString()),
                        RuntimeMinutes = Math.Max(0, ParseInt(details["runtime"]?.ToString())),
                        Tagline = details["tagline"]?.ToString() ?? string.Empty,
                        Credits = string.IsNullOrWhiteSpace(director) ? string.Empty : "Director: " + director,
                        Cast = castNames,
                        Provider = "TMDB",
                        ProviderId = movieId.ToString(CultureInfo.InvariantCulture),
                        UpdatedUtc = DateTime.UtcNow
                    };
                    ApplyCollectionMetadata(record, details);
                    return record;
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

        private static string NormalizeArtworkTarget(string target)
        {
            var value = (target ?? string.Empty).Trim().ToLowerInvariant();
            if (value == AnikiVideoManualArtworkOverrideService.Landscape ||
                value == AnikiVideoManualArtworkOverrideService.Hero ||
                value == AnikiVideoManualArtworkOverrideService.Logo)
            {
                return value;
            }
            return AnikiVideoManualArtworkOverrideService.Cover;
        }

        public Task<IReadOnlyList<AnikiVideoTmdbArtworkChoice>> GetArtworkChoicesAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            return GetArtworkChoicesAsync(videoPath, null, cancellationToken);
        }

        internal string GetPosterRemoteUrl(AnikiVideoTmdbArtworkChoice choice)
        {
            return BuildTmdbPickerUrl(choice?.PosterRemotePath, "original");
        }

        internal string GetBackdropRemoteUrl(AnikiVideoTmdbArtworkChoice choice)
        {
            return BuildTmdbPickerUrl(choice?.BackdropRemotePath, "original");
        }

        internal async Task<string> GetBackdropPickerPreviewAsync(
            AnikiVideoTmdbArtworkChoice choice,
            CancellationToken cancellationToken)
        {
            var remotePath = choice?.BackdropRemotePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(remotePath)) return string.Empty;
            var fileName = "picker_bg_v1_" + Sha256Hex(remotePath) + ".jpg";
            var path = Path.Combine(cacheRoot, fileName);
            if (File.Exists(path)) return path;
            var url = BuildTmdbPickerUrl(remotePath, "w1280");
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return string.Empty;
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytes == null || bytes.Length == 0) return string.Empty;
                    EnsureCacheDirectory();
                    var temp = path + ".tmp";
                    TryDelete(temp);
                    CreateOptimizedJpeg(bytes, temp, 1280, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(temp)) return string.Empty;
                    TryDelete(path);
                    File.Move(temp, path);
                    return path;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return string.Empty; }
        }

        private static string BuildTmdbPickerUrl(string remotePath, string size)
        {
            if (string.IsNullOrWhiteSpace(remotePath)) return string.Empty;
            var normalized = remotePath.StartsWith("/", StringComparison.Ordinal) ? remotePath : "/" + remotePath;
            return "https://image.tmdb.org/t/p/" + (string.IsNullOrWhiteSpace(size) ? "original" : size) + normalized;
        }

        public string GetDefaultSearchText(string videoPath)
        {
            if (!TryParseMovieIdentity(videoPath, out var identity) || identity == null)
            {
                return string.Empty;
            }

            return identity.Title + (identity.Year > 0
                ? " " + identity.Year.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
        }

        public async Task<IReadOnlyList<AnikiVideoTmdbArtworkChoice>> GetArtworkChoicesAsync(
            string videoPath,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath))
            {
                return Array.Empty<AnikiVideoTmdbArtworkChoice>();
            }

            MovieIdentity identity;
            var manualSearch = !string.IsNullOrWhiteSpace(searchText);
            if (manualSearch)
            {
                if (!TryParseManualSearchIdentity(searchText, out identity))
                {
                    return Array.Empty<AnikiVideoTmdbArtworkChoice>();
                }
            }
            else if (!TryParseMovieIdentity(videoPath, out identity))
            {
                return Array.Empty<AnikiVideoTmdbArtworkChoice>();
            }

            var language = ResolveLanguageCode();

            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var match = manualSearch
                    ? await SearchMovieBestAsync(identity, language, cancellationToken).ConfigureAwait(false)
                    : await SearchMovieStrictAsync(identity, language, cancellationToken).ConfigureAwait(false);

                if (match == null || match.Id <= 0)
                {
                    return Array.Empty<AnikiVideoTmdbArtworkChoice>();
                }

                var details = await GetMovieDetailsAsync(match.Id, language, cancellationToken).ConfigureAwait(false);
                var metadataTitle = details?["title"]?.ToString() ?? identity.Title;
                var metadataYear = ParseYear(details?["release_date"]?.ToString());
                if (metadataYear <= 0) metadataYear = identity.Year;
                var metadataOverview = details?["overview"]?.ToString() ?? string.Empty;
                var metadataRating = ParseDouble(details?["vote_average"]?.ToString());
                var metadataGenres = JoinGenreNames(details?["genres"] as JArray);
                var metadataRuntime = ParseInt(details?["runtime"]?.ToString());
                var metadataVoteCount = ParseInt(details?["vote_count"]?.ToString());
                var metadataTagline = details?["tagline"]?.ToString() ?? string.Empty;
                var metadataOriginalTitle = details?["original_title"]?.ToString() ?? string.Empty;
                var metadataCredits = string.Empty;
                var metadataCrew = details?["credits"]?["crew"] as JArray;
                var metadataDirector = metadataCrew?.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["job"]?.ToString(), "Director", StringComparison.OrdinalIgnoreCase))?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(metadataDirector)) metadataCredits = "Director: " + metadataDirector;

                var images = await GetMovieImagesAsync(
                    match.Id,
                    language,
                    match.OriginalLanguage,
                    cancellationToken).ConfigureAwait(false);

                var posters = BuildPosterChoices(
                    images?["posters"] as JArray,
                    language,
                    match.OriginalLanguage,
                    6);

                if (posters.Count == 0 && !string.IsNullOrWhiteSpace(match.SearchPosterPath))
                {
                    posters.Add(new JObject
                    {
                        ["file_path"] = match.SearchPosterPath,
                        ["iso_639_1"] = language,
                        ["width"] = 0,
                        ["height"] = 0
                    });
                }

                if (posters.Count == 0)
                {
                    return Array.Empty<AnikiVideoTmdbArtworkChoice>();
                }

                var backdropPath = SelectBackdropPath(images, language, match.OriginalLanguage);
                if (string.IsNullOrWhiteSpace(backdropPath))
                {
                    backdropPath = match.SearchBackdropPath;
                }

                ClearPickerPreviews();

                var tasks = posters.Select(async poster =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var remotePath = poster["file_path"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(remotePath))
                        {
                            return null;
                        }

                        var previewPath = await DownloadPickerPreviewAsync(
                            remotePath,
                            cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(previewPath))
                        {
                            return null;
                        }

                        var languageCode = poster["iso_639_1"] == null ||
                                           poster["iso_639_1"].Type == JTokenType.Null
                            ? string.Empty
                            : NormalizeLanguage(poster["iso_639_1"].ToString());

                        var width = ParseInt(poster["width"]?.ToString());
                        var height = ParseInt(poster["height"]?.ToString());

                        return new AnikiVideoTmdbArtworkChoice
                        {
                            PreviewPath = previewPath,
                            ProviderText = "TMDB",
                            MatchText = identity.Title + (identity.Year > 0
                                ? " (" + identity.Year.ToString(CultureInfo.InvariantCulture) + ")"
                                : string.Empty),
                            LanguageText = string.IsNullOrWhiteSpace(languageCode)
                                ? "NO TEXT"
                                : languageCode.ToUpperInvariant(),
                            SizeText = width > 0 && height > 0
                                ? width.ToString(CultureInfo.InvariantCulture) + " × " +
                                  height.ToString(CultureInfo.InvariantCulture)
                                : string.Empty,
                            MetadataTitle = metadataTitle,
                            MetadataYear = metadataYear,
                            MetadataOverview = metadataOverview,
                            MetadataGenres = metadataGenres,
                            MetadataRating = metadataRating,
                            MetadataRuntimeMinutes = metadataRuntime,
                            MetadataVoteCount = metadataVoteCount,
                            MetadataTagline = metadataTagline,
                            MetadataCredits = metadataCredits,
                            MetadataOriginalTitle = metadataOriginalTitle,
                            MovieId = match.Id,
                            PosterRemotePath = remotePath,
                            BackdropRemotePath = backdropPath ?? string.Empty
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Poster preview download failed.");
                        return null;
                    }
                }).ToArray();

                var choices = await Task.WhenAll(tasks).ConfigureAwait(false);
                return choices
                    .Where(choice => choice != null)
                    .Take(6)
                    .ToArray();
            }
            finally
            {
                networkGate.Release();
            }
        }

        public async Task<IReadOnlyList<AnikiVideoTmdbArtworkChoice>> GetLogoChoicesAsync(
            string videoPath,
            string searchText,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath))
            {
                return Array.Empty<AnikiVideoTmdbArtworkChoice>();
            }

            MovieIdentity identity;
            var manualSearch = !string.IsNullOrWhiteSpace(searchText);
            if (manualSearch)
            {
                if (!TryParseManualSearchIdentity(searchText, out identity)) return Array.Empty<AnikiVideoTmdbArtworkChoice>();
            }
            else if (!TryParseMovieIdentity(videoPath, out identity))
            {
                return Array.Empty<AnikiVideoTmdbArtworkChoice>();
            }

            var language = ResolveLanguageCode();
            await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var match = manualSearch
                    ? await SearchMovieBestAsync(identity, language, cancellationToken).ConfigureAwait(false)
                    : await SearchMovieStrictAsync(identity, language, cancellationToken).ConfigureAwait(false);
                if (match == null || match.Id <= 0) return Array.Empty<AnikiVideoTmdbArtworkChoice>();

                var details = await GetMovieDetailsAsync(match.Id, language, cancellationToken).ConfigureAwait(false);
                var originalLanguage = details?["original_language"]?.ToString() ?? match.OriginalLanguage;
                var images = await GetMovieImagesAsync(match.Id, language, originalLanguage, cancellationToken).ConfigureAwait(false);
                var logos = BuildLogoChoices(images?["logos"] as JArray, language, originalLanguage, 6);
                if (logos.Count == 0) return Array.Empty<AnikiVideoTmdbArtworkChoice>();

                var title = details?["title"]?.ToString() ?? identity.Title;
                var year = ParseYear(details?["release_date"]?.ToString());
                if (year <= 0) year = identity.Year;
                var overview = details?["overview"]?.ToString() ?? string.Empty;
                var genres = JoinGenreNames(details?["genres"] as JArray);
                var rating = ParseDouble(details?["vote_average"]?.ToString());
                var runtime = ParseInt(details?["runtime"]?.ToString());
                var voteCount = ParseInt(details?["vote_count"]?.ToString());
                var tagline = details?["tagline"]?.ToString() ?? string.Empty;
                var originalTitle = details?["original_title"]?.ToString() ?? string.Empty;
                var credits = string.Empty;
                var crew = details?["credits"]?["crew"] as JArray;
                var director = crew?.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(x["job"]?.ToString(), "Director", StringComparison.OrdinalIgnoreCase))?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(director)) credits = "Director: " + director;

                var result = new List<AnikiVideoTmdbArtworkChoice>();
                foreach (var logo in logos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remotePath = logo?["file_path"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(remotePath)) continue;
                    var preview = await DownloadLogoPickerPreviewAsync(remotePath, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(preview)) continue;
                    var lang = logo?["iso_639_1"] == null || logo["iso_639_1"].Type == JTokenType.Null
                        ? string.Empty : NormalizeLanguage(logo["iso_639_1"].ToString());
                    var width = ParseInt(logo?["width"]?.ToString());
                    var height = ParseInt(logo?["height"]?.ToString());
                    result.Add(new AnikiVideoTmdbArtworkChoice
                    {
                        PreviewPath = preview,
                        ProviderText = "TMDB",
                        MatchText = title + (year > 0 ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty),
                        LanguageText = string.IsNullOrWhiteSpace(lang) ? "NO TEXT" : lang.ToUpperInvariant(),
                        SizeText = width > 0 && height > 0 ? width.ToString(CultureInfo.InvariantCulture) + " × " + height.ToString(CultureInfo.InvariantCulture) : string.Empty,
                        MetadataTitle = title,
                        MetadataYear = year,
                        MetadataOverview = overview,
                        MetadataGenres = genres,
                        MetadataRating = rating,
                        MetadataRuntimeMinutes = runtime,
                        MetadataVoteCount = voteCount,
                        MetadataTagline = tagline,
                        MetadataCredits = credits,
                        MetadataOriginalTitle = originalTitle,
                        MovieId = match.Id,
                        LogoRemotePath = remotePath
                    });
                }
                return result.Take(6).ToArray();
            }
            finally
            {
                networkGate.Release();
            }
        }

        public string GetLogoRemoteUrl(AnikiVideoTmdbArtworkChoice choice)
        {
            if (choice == null || string.IsNullOrWhiteSpace(choice.LogoRemotePath)) return string.Empty;
            var path = choice.LogoRemotePath.StartsWith("/", StringComparison.Ordinal) ? choice.LogoRemotePath : "/" + choice.LogoRemotePath;
            return "https://image.tmdb.org/t/p/original" + path;
        }

        public async Task<AnikiVideoArtworkInfo> ApplyArtworkChoiceAsync(
            string videoPath,
            AnikiVideoTmdbArtworkChoice choice,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled ||
                string.IsNullOrWhiteSpace(videoPath) ||
                choice == null ||
                string.IsNullOrWhiteSpace(choice.PosterRemotePath))
            {
                return null;
            }

            var language = ResolveLanguageCode();
            var cacheKey = BuildLookupKey(videoPath, language);
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var entry = new TmdbCacheEntry
                    {
                        MatcherVersion = MatcherVersion,
                        MovieId = choice.MovieId,
                        IsManual = true,
                        NoMatch = false,
                        LastAttemptUtc = DateTime.UtcNow
                    };

                    // Manual artwork replacement must never reuse the same cache file when an artwork
                    // already exists. The previous file may still be held open by WPF, and reusing the
                    // same path also makes it impossible to force a visible refresh when the user picks
                    // another image. Every Apply therefore gets its own version suffix.
                    var manualVersion = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
                    var posterStem = BuildManualArtworkStem(cacheKey, "poster", choice.PosterRemotePath, manualVersion);
                    // The picker preview was already downloaded successfully (w780) and is exactly
                    // the poster selected by the user. Promote it first so Apply does not depend on a
                    // second network request. Only fall back to downloading again if the local promotion
                    // unexpectedly fails.
                    entry.PosterFileName = PromotePickerPreviewToCache(
                        posterStem,
                        choice.PreviewPath);

                    if (string.IsNullOrWhiteSpace(entry.PosterFileName))
                    {
                        entry.PosterFileName = await DownloadAndCacheImageAsync(
                            posterStem,
                            choice.PosterRemotePath,
                            "w780",
                            PosterMaxDimension,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(choice.BackdropRemotePath))
                    {
                        entry.BackdropFileName = await DownloadAndCacheImageAsync(
                            BuildManualArtworkStem(cacheKey, "backdrop", choice.BackdropRemotePath, manualVersion),
                            choice.BackdropRemotePath,
                            "w1280",
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

        public async Task<AnikiVideoArtworkInfo> ImportLocalArtworkAsync(
            string videoPath,
            string imagePath,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return null;
            }

            var language = ResolveLanguageCode();
            var cacheKey = BuildLookupKey(videoPath, language);
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = GetEntrySnapshot(cacheKey);
                var entry = existing ?? new TmdbCacheEntry();
                entry.MatcherVersion = MatcherVersion;
                entry.IsManual = true;
                entry.NoMatch = false;
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
                    movieIdCacheKeys = null;
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
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to clear TMDb artwork cache.");
            }
        }

        private async Task<AnikiVideoArtworkInfo> ResolveAsync(
            string videoPath,
            bool preferPoster,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(videoPath))
            {
                return null;
            }

            var language = ResolveLanguageCode();
            var cacheKey = BuildLookupKey(videoPath, language);

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
                    return await ScrapeAndCacheAsync(
                        cacheKey,
                        videoPath,
                        language,
                        preferPoster,
                        cancellationToken).ConfigureAwait(false);
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

        private AnikiVideoArtworkInfo TryResolveCached(
            string cacheKey,
            bool preferPoster,
            out bool freshNegative)
        {
            freshNegative = false;

            TmdbCacheEntry entry;
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
                // Matcher v2 understands release-style filenames much better. Old negative
                // results must be retried, while old positive artwork remains perfectly valid.
                if (entry.MatcherVersion < MatcherVersion)
                {
                    RemoveEntry(cacheKey);
                    return null;
                }

                freshNegative =
                    entry.LastAttemptUtc > DateTime.MinValue &&
                    DateTime.UtcNow - entry.LastAttemptUtc < NegativeCacheDuration;

                if (!freshNegative)
                {
                    RemoveEntry(cacheKey);
                }

                return null;
            }

            var posterPath = GetCachedPath(entry.PosterFileName);
            var backdropPath = GetCachedPath(entry.BackdropFileName);

            if (preferPoster)
            {
                if (!string.IsNullOrWhiteSpace(posterPath))
                {
                    return new AnikiVideoArtworkInfo { Path = posterPath, IsPortrait = true };
                }

                if (!string.IsNullOrWhiteSpace(backdropPath))
                {
                    return new AnikiVideoArtworkInfo { Path = backdropPath, IsPortrait = false };
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(backdropPath))
                {
                    return new AnikiVideoArtworkInfo { Path = backdropPath, IsPortrait = false };
                }

                if (!string.IsNullOrWhiteSpace(posterPath))
                {
                    return new AnikiVideoArtworkInfo { Path = posterPath, IsPortrait = true };
                }
            }

            // Keep an explicit user association even if its cached image files were deleted.
            // ScrapeAndCacheAsync will rebuild the assets from the stored TMDb id instead of
            // falling back to filename matching and potentially losing the user's correction.
            if (entry.IsManual && entry.MovieId > 0)
            {
                return null;
            }

            // Cache index survived but image files were removed externally.
            RemoveEntry(cacheKey);
            return null;
        }

        private async Task<AnikiVideoArtworkInfo> ScrapeAndCacheAsync(
            string cacheKey,
            string videoPath,
            string language,
            bool preferPoster,
            CancellationToken cancellationToken)
        {
            try
            {
                MovieMatch match = null;
                var explicitEntry = GetEntrySnapshot(cacheKey);
                if (explicitEntry?.IsManual == true && explicitEntry.MovieId > 0)
                {
                    var explicitDetails = await GetMovieDetailsAsync(explicitEntry.MovieId, language, cancellationToken).ConfigureAwait(false);
                    if (explicitDetails == null)
                    {
                        // A manual match is authoritative. A temporary TMDb/network failure must not
                        // make us fall back to filename guessing or erase the association.
                        return null;
                    }

                    match = new MovieMatch
                    {
                        Id = explicitEntry.MovieId,
                        OriginalLanguage = NormalizeLanguage(explicitDetails["original_language"]?.ToString()),
                        SearchPosterPath = explicitDetails["poster_path"]?.ToString() ?? string.Empty,
                        SearchBackdropPath = explicitDetails["backdrop_path"]?.ToString() ?? string.Empty
                    };
                }

                if (match == null)
                {
                    MovieIdentity identity;
                    if (!TryParseMovieIdentity(videoPath, out identity))
                    {
                        RememberNoMatch(cacheKey);
                        return null;
                    }

                    match = await SearchMovieStrictAsync(identity, language, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (match == null || match.Id <= 0)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, 
                        "[AnikiHelper][VideoCenter][TMDb] No strict movie match for hashed media key " +
                        cacheKey.Substring(0, Math.Min(12, cacheKey.Length)) + ".");
                    RememberNoMatch(cacheKey);
                    return null;
                }

                var images = await GetMovieImagesAsync(
                    match.Id,
                    language,
                    match.OriginalLanguage,
                    cancellationToken).ConfigureAwait(false);

                var posterRemote = SelectPosterPath(images, language, match.OriginalLanguage);
                var backdropRemote = SelectBackdropPath(images, language, match.OriginalLanguage);

                if (string.IsNullOrWhiteSpace(posterRemote))
                {
                    posterRemote = match.SearchPosterPath;
                }

                if (string.IsNullOrWhiteSpace(backdropRemote))
                {
                    backdropRemote = match.SearchBackdropPath;
                }

                var entry = new TmdbCacheEntry
                {
                    MatcherVersion = MatcherVersion,
                    MovieId = match.Id,
                    IsManual = explicitEntry?.IsManual == true && explicitEntry.MovieId == match.Id,
                    NoMatch = false,
                    LastAttemptUtc = DateTime.UtcNow
                };

                // Respect the caller's visual priority. Hero/Home callers ask for landscape first,
                // while card/preview callers ask for the portrait poster first. Both assets are still
                // cached during a normal scrape; only the download order changes here.
                if (!preferPoster && !string.IsNullOrWhiteSpace(backdropRemote))
                {
                    entry.BackdropFileName = await DownloadAndCacheImageAsync(
                        cacheKey + ".backdrop",
                        backdropRemote,
                        "w1280",
                        BackdropMaxDimension,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(posterRemote))
                {
                    entry.PosterFileName = await DownloadAndCacheImageAsync(
                        cacheKey + ".poster",
                        posterRemote,
                        "w780",
                        PosterMaxDimension,
                        cancellationToken).ConfigureAwait(false);
                }

                if (preferPoster && !string.IsNullOrWhiteSpace(backdropRemote))
                {
                    entry.BackdropFileName = await DownloadAndCacheImageAsync(
                        cacheKey + ".backdrop",
                        backdropRemote,
                        "w1280",
                        BackdropMaxDimension,
                        cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                    string.IsNullOrWhiteSpace(entry.BackdropFileName))
                {
                    if (entry.IsManual && entry.MovieId > 0)
                    {
                        // Keep the explicit provider id even when this title currently has no usable
                        // poster/backdrop (or an image download failed). The Artwork manager can still
                        // reopen the correct TMDb title and the user can choose another asset later.
                        StoreEntry(cacheKey, entry);
                    }
                    else
                    {
                        RememberNoMatch(cacheKey);
                    }
                    return null;
                }

                StoreEntry(cacheKey, entry);

                return TryResolveCached(cacheKey, preferPoster, out _);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Do not poison the negative cache on temporary HTTP/network failures.
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Artwork scraping failed.");
                return null;
            }
        }

        private async Task<MovieMatch> SearchMovieBestAsync(
            MovieIdentity identity,
            string language,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return null;
            }

            var locale = ToTmdbLocale(language);
            var url = new StringBuilder("https://api.themoviedb.org/3/search/movie");
            url.Append("?include_adult=false&page=1");
            url.Append("&language=").Append(Uri.EscapeDataString(locale));
            url.Append("&query=").Append(Uri.EscapeDataString(identity.Title));
            if (identity.Year > 0)
            {
                url.Append("&year=").Append(identity.Year.ToString(CultureInfo.InvariantCulture));
            }

            var root = await GetJsonAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
            var results = root?["results"] as JArray;
            if (results == null || results.Count == 0)
            {
                return null;
            }

            var selected = results
                .OfType<JObject>()
                .Where(x => ParseInt(x["id"]?.ToString()) > 0)
                .OrderByDescending(x => ParseDouble(x["popularity"]?.ToString()))
                .ThenByDescending(x => ParseDouble(x["vote_count"]?.ToString()))
                .FirstOrDefault();

            if (selected == null)
            {
                return null;
            }

            return new MovieMatch
            {
                Id = ParseInt(selected["id"]?.ToString()),
                OriginalLanguage = NormalizeLanguage(selected["original_language"]?.ToString()),
                SearchPosterPath = selected["poster_path"]?.ToString() ?? string.Empty,
                SearchBackdropPath = selected["backdrop_path"]?.ToString() ?? string.Empty
            };
        }

        private async Task<MovieMatch> SearchMovieStrictAsync(
            MovieIdentity identity,
            string language,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return null;
            }

            var variants = BuildSearchTitleVariants(identity.Title);
            MovieMatch bestMatch = null;
            double bestScore = double.MinValue;

            foreach (var query in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // First try the year-filtered search. If TMDb has incomplete release-date
                // metadata, retry without the server-side year filter but keep the year in our
                // own scoring so we still reject unrelated remakes.
                foreach (var useYearFilter in new[] { true, false })
                {
                    if (!useYearFilter && identity.Year <= 0)
                    {
                        continue;
                    }

                    var root = await SearchMovieRawAsync(
                        query,
                        useYearFilter ? identity.Year : 0,
                        language,
                        cancellationToken).ConfigureAwait(false);

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

                        var title = result["title"]?.ToString() ?? string.Empty;
                        var originalTitle = result["original_title"]?.ToString() ?? string.Empty;
                        var similarity = Math.Max(
                            CalculateTitleSimilarity(identity.Title, title),
                            CalculateTitleSimilarity(identity.Title, originalTitle));

                        var releaseYear = ParseYear(result["release_date"]?.ToString());
                        var yearScore = 0.0;
                        if (identity.Year > 0)
                        {
                            if (releaseYear == identity.Year)
                            {
                                yearScore = 18.0;
                            }
                            else if (releaseYear > 0 && Math.Abs(releaseYear - identity.Year) == 1)
                            {
                                yearScore = 3.0;
                            }
                            else if (releaseYear > 0)
                            {
                                yearScore = -28.0;
                            }
                        }

                        // Require a strong title match. With a confirmed year we can accept a
                        // slightly fuzzier localized/alternate title; without a year be stricter.
                        var minimumSimilarity = identity.Year > 0 ? 0.82 : 0.93;
                        if (similarity < minimumSimilarity)
                        {
                            continue;
                        }

                        if (identity.Year > 0 && releaseYear > 0 &&
                            Math.Abs(releaseYear - identity.Year) > 1)
                        {
                            continue;
                        }

                        var score = (similarity * 100.0) +
                                    yearScore +
                                    Math.Min(4.0, ParseDouble(result["popularity"]?.ToString()) / 25.0);

                        if (score <= bestScore)
                        {
                            continue;
                        }

                        bestScore = score;
                        bestMatch = new MovieMatch
                        {
                            Id = id,
                            OriginalLanguage = NormalizeLanguage(result["original_language"]?.ToString()),
                            SearchPosterPath = result["poster_path"]?.ToString() ?? string.Empty,
                            SearchBackdropPath = result["backdrop_path"]?.ToString() ?? string.Empty
                        };
                    }

                    if (bestMatch != null && bestScore >= 112.0)
                    {
                        break;
                    }
                }

                if (bestMatch != null && bestScore >= 112.0)
                {
                    break;
                }
            }

            if (bestMatch != null)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, 
                    $"[AnikiHelper][VideoCenter][TMDb] Smart movie match: '{identity.Title}' ({identity.Year}), score={bestScore:0.0}.");
            }

            return bestMatch;
        }

        private async Task<MovieMatch> SearchMovieMarkerFallbackAsync(
            MovieIdentity identity,
            string language,
            CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title))
            {
                return null;
            }

            MovieMatch bestMatch = null;
            double bestScore = double.MinValue;

            foreach (var query in BuildSearchTitleVariants(identity.Title))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var useYearFilter in new[] { true, false })
                {
                    if (useYearFilter && identity.Year <= 0)
                    {
                        continue;
                    }

                    var root = await SearchMovieRawAsync(
                        query,
                        useYearFilter ? identity.Year : 0,
                        language,
                        cancellationToken).ConfigureAwait(false);

                    var results = root?["results"] as JArray;
                    if (results == null || results.Count == 0)
                    {
                        continue;
                    }

                    var candidates = results.OfType<JObject>()
                        .Where(x => ParseInt(x["id"]?.ToString()) > 0)
                        .Take(20)
                        .ToArray();

                    var yearCompatibleCount = identity.Year > 0
                        ? candidates.Count(x =>
                        {
                            var candidateYear = ParseYear(x["release_date"]?.ToString());
                            return candidateYear > 0 && Math.Abs(candidateYear - identity.Year) <= 1;
                        })
                        : candidates.Length;

                    foreach (var result in candidates)
                    {
                        var id = ParseInt(result["id"]?.ToString());
                        if (id <= 0)
                        {
                            continue;
                        }

                        var title = result["title"]?.ToString() ?? string.Empty;
                        var originalTitle = result["original_title"]?.ToString() ?? string.Empty;
                        var similarity = Math.Max(
                            CalculateTitleSimilarity(identity.Title, title),
                            CalculateTitleSimilarity(identity.Title, originalTitle));

                        var releaseYear = ParseYear(result["release_date"]?.ToString());
                        var exactYear = identity.Year > 0 && releaseYear == identity.Year;
                        var nearYear = identity.Year > 0 && releaseYear > 0 &&
                            Math.Abs(releaseYear - identity.Year) == 1;

                        if (identity.Year > 0 && releaseYear > 0 &&
                            Math.Abs(releaseYear - identity.Year) > 1)
                        {
                            continue;
                        }

                        // Manual matching is more tolerant, but still needs a year or a strong unique title match.
                        var acceptable = identity.Year > 0
                            ? similarity >= 0.55 || (exactYear && yearCompatibleCount == 1)
                            : similarity >= 0.90;

                        if (!acceptable)
                        {
                            continue;
                        }

                        var score = (similarity * 100.0) +
                                    (exactYear ? 32.0 : nearYear ? 8.0 : 0.0) +
                                    Math.Min(6.0, ParseDouble(result["popularity"]?.ToString()) / 20.0) +
                                    Math.Min(4.0, ParseInt(result["vote_count"]?.ToString()) / 5000.0);

                        if (score <= bestScore)
                        {
                            continue;
                        }

                        bestScore = score;
                        bestMatch = new MovieMatch
                        {
                            Id = id,
                            OriginalLanguage = NormalizeLanguage(result["original_language"]?.ToString()),
                            SearchPosterPath = result["poster_path"]?.ToString() ?? string.Empty,
                            SearchBackdropPath = result["backdrop_path"]?.ToString() ?? string.Empty
                        };
                    }

                    if (bestMatch != null && bestScore >= 110.0)
                    {
                        break;
                    }
                }

                if (bestMatch != null && bestScore >= 110.0)
                {
                    break;
                }
            }

            return bestMatch;
        }

        private async Task<JObject> SearchMovieRawAsync(
            string query,
            int year,
            string language,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var locale = ToTmdbLocale(language);
            var url = new StringBuilder("https://api.themoviedb.org/3/search/movie");
            url.Append("?include_adult=false&page=1");
            url.Append("&language=").Append(Uri.EscapeDataString(locale));
            url.Append("&query=").Append(Uri.EscapeDataString(query));
            if (year > 0)
            {
                url.Append("&primary_release_year=").Append(year.ToString(CultureInfo.InvariantCulture));
            }

            return await GetJsonAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
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

            var romanVariant = Regex.Replace(
                title ?? string.Empty,
                @"\b(?:II|III|IV|V|VI|VII|VIII|IX|X)\b",
                match => RomanNumeralToArabic(match.Value),
                RegexOptions.IgnoreCase);

            add(romanVariant);
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

            var intersection = leftTokens.Count == 0
                ? 0
                : leftTokens.Count(token => rightTokens.Contains(token));
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

            for (var j = 0; j <= right.Length; j++)
            {
                previous[j] = j;
            }

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

        private async Task<JObject> GetMovieDetailsAsync(
            int movieId,
            string language,
            CancellationToken cancellationToken)
        {
            var locale = ToTmdbLocale(language);
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/movie/{0}?language={1}&append_to_response=credits",
                movieId,
                Uri.EscapeDataString(locale));
            return await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private static string JoinGenreNames(JArray genres)
        {
            if (genres == null) return string.Empty;
            return string.Join(", ", genres.OfType<JObject>()
                .Select(x => x["name"]?.ToString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
        }

        private static void ApplyCollectionMetadata(AnikiVideoMetadataRecord record, JObject details)
        {
            if (record == null || details == null) return;
            record.CollectionLookupComplete = true;
            var collection = details["belongs_to_collection"] as JObject;
            if (collection == null) return;
            record.CollectionId = Math.Max(0, ParseInt(collection["id"]?.ToString()));
            record.CollectionName = collection["name"]?.ToString() ?? string.Empty;
            record.CollectionPosterPath = collection["poster_path"]?.ToString() ?? string.Empty;
            record.CollectionBackdropPath = collection["backdrop_path"]?.ToString() ?? string.Empty;
        }

        private async Task<JObject> GetMovieImagesAsync(
            int movieId,
            string preferredLanguage,
            string originalLanguage,
            CancellationToken cancellationToken)
        {
            var includeLanguages = new List<string>();

            Action<string> addLanguage = value =>
            {
                value = NormalizeLanguage(value);
                if (!string.IsNullOrWhiteSpace(value) &&
                    !includeLanguages.Any(existing =>
                        string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                {
                    includeLanguages.Add(value);
                }
            };

            addLanguage(preferredLanguage);
            includeLanguages.Add("null");
            addLanguage(originalLanguage);
            addLanguage("en");

            var locale = ToTmdbLocale(preferredLanguage);
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/movie/{0}/images?language={1}&include_image_language={2}",
                movieId,
                Uri.EscapeDataString(locale),
                Uri.EscapeDataString(string.Join(",", includeLanguages)));

            return await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private static List<JObject> BuildPosterChoices(
            JArray posters,
            string preferredLanguage,
            string originalLanguage,
            int limit)
        {
            var result = new List<JObject>();
            if (posters == null || posters.Count == 0 || limit <= 0)
            {
                return result;
            }

            var all = posters.OfType<JObject>().ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string, int> addForLanguage = (language, maxForLanguage) =>
            {
                if (result.Count >= limit || maxForLanguage <= 0)
                {
                    return;
                }

                var candidates = all
                    .Where(image => LanguageEquals(image["iso_639_1"], language))
                    .OrderByDescending(image => ParseDouble(image["vote_average"]?.ToString()))
                    .ThenByDescending(image => ParseDouble(image["vote_count"]?.ToString()))
                    .ThenByDescending(image => ParseInt(image["width"]?.ToString()));

                var added = 0;
                foreach (var candidate in candidates)
                {
                    var path = candidate["file_path"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                    {
                        continue;
                    }

                    result.Add(candidate);
                    added++;
                    if (result.Count >= limit || added >= maxForLanguage)
                    {
                        break;
                    }
                }
            };

            var preferred = NormalizeLanguage(preferredLanguage);
            var original = NormalizeLanguage(originalLanguage);

            // Give the user several choices in the preferred language, while reserving a couple
            // of slots for textless/original-language alternatives when they exist.
            addForLanguage(preferred, 4);
            addForLanguage(null, 1);

            if (!string.IsNullOrWhiteSpace(original) &&
                !string.Equals(original, preferred, StringComparison.OrdinalIgnoreCase))
            {
                addForLanguage(original, 1);
            }

            if (result.Count < limit &&
                !string.Equals("en", preferred, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals("en", original, StringComparison.OrdinalIgnoreCase))
            {
                addForLanguage("en", 1);
            }

            foreach (var candidate in all
                .OrderByDescending(image => ParseDouble(image["vote_average"]?.ToString()))
                .ThenByDescending(image => ParseDouble(image["vote_count"]?.ToString()))
                .ThenByDescending(image => ParseInt(image["width"]?.ToString())))
            {
                if (result.Count >= limit)
                {
                    break;
                }

                var path = candidate["file_path"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                {
                    continue;
                }

                result.Add(candidate);
            }

            return result;
        }

        private static List<JObject> BuildBackdropChoices(
            JArray backdrops,
            string preferredLanguage,
            string originalLanguage,
            int limit)
        {
            var result = new List<JObject>();
            if (backdrops == null || backdrops.Count == 0 || limit <= 0)
            {
                return result;
            }

            var preferred = NormalizeLanguage(preferredLanguage);
            var original = NormalizeLanguage(originalLanguage);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Func<JObject, int> languageRank = image =>
            {
                var token = image?["iso_639_1"];
                var lang = token == null || token.Type == JTokenType.Null ? string.Empty : NormalizeLanguage(token.ToString());
                if (string.IsNullOrWhiteSpace(lang)) return 0;
                if (!string.IsNullOrWhiteSpace(preferred) && string.Equals(lang, preferred, StringComparison.OrdinalIgnoreCase)) return 1;
                if (!string.IsNullOrWhiteSpace(original) && string.Equals(lang, original, StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) return 3;
                return 4;
            };

            foreach (var candidate in backdrops.OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace(x["file_path"]?.ToString()))
                .OrderBy(languageRank)
                .ThenByDescending(x => ParseDouble(x["vote_average"]?.ToString()))
                .ThenByDescending(x => ParseInt(x["width"]?.ToString()) * ParseInt(x["height"]?.ToString())))
            {
                var path = candidate["file_path"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
                result.Add(candidate);
                if (result.Count >= limit) break;
            }
            return result;
        }

        private static List<JObject> BuildLogoChoices(
            JArray logos,
            string preferredLanguage,
            string originalLanguage,
            int maxCount)
        {
            if (logos == null) return new List<JObject>();
            var preferred = NormalizeLanguage(preferredLanguage);
            var original = NormalizeLanguage(originalLanguage);
            Func<JObject, int> languageRank = logo =>
            {
                var token = logo?["iso_639_1"];
                var lang = token == null || token.Type == JTokenType.Null ? string.Empty : NormalizeLanguage(token.ToString());
                if (!string.IsNullOrWhiteSpace(preferred) && string.Equals(lang, preferred, StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.IsNullOrWhiteSpace(lang)) return 1;
                if (!string.IsNullOrWhiteSpace(original) && string.Equals(lang, original, StringComparison.OrdinalIgnoreCase)) return 2;
                if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) return 3;
                return 4;
            };
            return logos.OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace(x["file_path"]?.ToString()))
                .Where(x => !x["file_path"].ToString().EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(languageRank)
                .ThenByDescending(x => ParseDouble(x["vote_average"]?.ToString()))
                .ThenByDescending(x => ParseInt(x["width"]?.ToString()) * ParseInt(x["height"]?.ToString()))
                .Take(Math.Max(1, maxCount))
                .ToList();
        }

        private static string SelectLogoPath(JArray logos, string preferredLanguage, string originalLanguage)
        {
            return BuildLogoChoices(logos, preferredLanguage, originalLanguage, 1)
                .FirstOrDefault()?["file_path"]?.ToString() ?? string.Empty;
        }

        private async Task<string> DownloadLogoPickerPreviewAsync(string tmdbFilePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tmdbFilePath)) return string.Empty;
            EnsureCacheDirectory();
            var fileName = "logo_picker_" + Sha256Hex(tmdbFilePath) + ".png";
            var path = Path.Combine(cacheRoot, fileName);
            if (File.Exists(path)) return path;
            var remotePath = tmdbFilePath.StartsWith("/", StringComparison.Ordinal) ? tmdbFilePath : "/" + tmdbFilePath;
            var url = "https://image.tmdb.org/t/p/w500" + remotePath;
            return await DownloadPngUrlAsync(url, path, 900, cancellationToken).ConfigureAwait(false) ? path : string.Empty;
        }

        private async Task<string> DownloadAndCacheLogoAsync(string fileStem, string tmdbFilePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(fileStem) || string.IsNullOrWhiteSpace(tmdbFilePath)) return string.Empty;
            EnsureCacheDirectory();
            var fileName = fileStem + ".png";
            var path = Path.Combine(cacheRoot, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return fileName;
            var remotePath = tmdbFilePath.StartsWith("/", StringComparison.Ordinal) ? tmdbFilePath : "/" + tmdbFilePath;
            var url = "https://image.tmdb.org/t/p/original" + remotePath;
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
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Logo download failed: " + url);
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

        private async Task<string> DownloadPickerPreviewAsync(
            string tmdbFilePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tmdbFilePath))
            {
                return string.Empty;
            }

            EnsureCacheDirectory();

            // V2 picker previews are also suitable as the final manually selected poster.
            // Use a new cache prefix so older w342 previews are not reused.
            var fileName = "picker_v2_" + Sha256Hex(tmdbFilePath) + ".jpg";
            var path = Path.Combine(cacheRoot, fileName);
            if (File.Exists(path))
            {
                return path;
            }

            var remotePath = tmdbFilePath.StartsWith("/", StringComparison.Ordinal)
                ? tmdbFilePath
                : "/" + tmdbFilePath;
            var url = "https://image.tmdb.org/t/p/w780" + remotePath;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes == null || bytes.Length == 0)
                {
                    return string.Empty;
                }

                var temp = path + ".tmp";
                TryDelete(temp);
                CreateOptimizedJpeg(bytes, temp, PosterMaxDimension, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(temp))
                {
                    return string.Empty;
                }

                TryDelete(path);
                File.Move(temp, path);
                return path;
            }
        }

        private void ClearPickerPreviews()
        {
            try
            {
                if (!Directory.Exists(cacheRoot))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(cacheRoot, "picker_*.jpg", SearchOption.TopDirectoryOnly))
                {
                    TryDelete(file);
                }
                foreach (var file in Directory.EnumerateFiles(cacheRoot, "logo_picker_*.png", SearchOption.TopDirectoryOnly))
                {
                    TryDelete(file);
                }
            }
            catch
            {
            }
        }

        private string SelectPosterPath(JObject images, string preferredLanguage, string originalLanguage)
        {
            var posters = images?["posters"] as JArray;
            return SelectImagePath(
                posters,
                new[] { NormalizeLanguage(preferredLanguage), null, NormalizeLanguage(originalLanguage), "en" });
        }

        private string SelectBackdropPath(JObject images, string preferredLanguage, string originalLanguage)
        {
            var backdrops = images?["backdrops"] as JArray;
            return SelectImagePath(
                backdrops,
                new[] { null, NormalizeLanguage(preferredLanguage), NormalizeLanguage(originalLanguage), "en" });
        }

        private static string SelectImagePath(JArray images, IEnumerable<string> languagePriority)
        {
            if (images == null || images.Count == 0)
            {
                return string.Empty;
            }

            var objects = images.OfType<JObject>().ToList();

            foreach (var language in languagePriority ?? Enumerable.Empty<string>())
            {
                var candidates = objects
                    .Where(image => LanguageEquals(image["iso_639_1"], language))
                    .OrderByDescending(image => ParseDouble(image["vote_average"]?.ToString()))
                    .ThenByDescending(image => ParseDouble(image["vote_count"]?.ToString()))
                    .ThenByDescending(image => ParseInt(image["width"]?.ToString()))
                    .ToList();

                var chosen = candidates.FirstOrDefault();
                var filePath = chosen?["file_path"]?.ToString();
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    return filePath;
                }
            }

            var fallback = objects
                .OrderByDescending(image => ParseDouble(image["vote_average"]?.ToString()))
                .ThenByDescending(image => ParseDouble(image["vote_count"]?.ToString()))
                .ThenByDescending(image => ParseInt(image["width"]?.ToString()))
                .FirstOrDefault();

            return fallback?["file_path"]?.ToString() ?? string.Empty;
        }

        private static bool LanguageEquals(JToken token, string expected)
        {
            if (expected == null)
            {
                return token == null ||
                       token.Type == JTokenType.Null ||
                       string.IsNullOrWhiteSpace(token.ToString());
            }

            var actual = token == null || token.Type == JTokenType.Null
                ? string.Empty
                : NormalizeLanguage(token.ToString());

            return string.Equals(actual, NormalizeLanguage(expected), StringComparison.OrdinalIgnoreCase);
        }

        private async Task<JObject> GetJsonAsync(string url, CancellationToken cancellationToken)
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
                                "[AnikiHelper][VideoCenter][TMDb] HTTP " +
                                ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                                " from TMDb.");
                        }
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
                }
            }
        }

        private async Task<string> DownloadAndCacheImageAsync(
            string fileStem,
            string tmdbFilePath,
            string tmdbSize,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(fileStem) || string.IsNullOrWhiteSpace(tmdbFilePath))
            {
                return string.Empty;
            }

            EnsureCacheDirectory();
            var fileName = fileStem + ".jpg";
            var path = Path.Combine(cacheRoot, fileName);

            // Manual cache files are immutable. Re-selecting the same artwork can reuse the file
            // without touching a path that may currently be held open by a WPF Image control.
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

            var remotePath = tmdbFilePath.StartsWith("/", StringComparison.Ordinal)
                ? tmdbFilePath
                : "/" + tmdbFilePath;

            var url = "https://image.tmdb.org/t/p/" + tmdbSize + remotePath;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        global::AnikiHelper.AnikiLog.Debug(logger, 
                            "[AnikiHelper][VideoCenter][TMDb] Artwork download HTTP " +
                            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) +
                            " for " + url);
                        return string.Empty;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (bytes == null || bytes.Length == 0)
                    {
                        return string.Empty;
                    }

                    var temp = path + ".tmp";
                    TryDelete(temp);
                    CreateOptimizedJpeg(bytes, temp, maxDimension, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(temp))
                    {
                        return string.Empty;
                    }

                    // This path should normally be new. If a stale zero-byte file is present,
                    // remove it before moving the completed temp file into place.
                    TryDelete(path);
                    if (File.Exists(path))
                    {
                        return string.Empty;
                    }
                    File.Move(temp, path);
                    return fileName;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Artwork download failed: " + url);
                return string.Empty;
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
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.None);

                var frame = decoder?.Frames != null && decoder.Frames.Count > 0
                    ? decoder.Frames[0]
                    : null;

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
                    if (width >= height && width > maxDimension)
                    {
                        bitmap.DecodePixelWidth = maxDimension;
                    }
                    else if (height > width && height > maxDimension)
                    {
                        bitmap.DecodePixelHeight = maxDimension;
                    }
                }

                bitmap.EndInit();
                bitmap.Freeze();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
        }

        private static bool TryParseManualSearchIdentity(string searchText, out MovieIdentity identity)
        {
            identity = null;
            var raw = (searchText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var yearMatch = YearRegex.Match(raw);
            var year = yearMatch.Success ? ParseInt(yearMatch.Value) : 0;
            var title = raw;
            if (year > 0)
            {
                title = Regex.Replace(
                    title,
                    @"[\(\[\{]?\s*" + year.ToString(CultureInfo.InvariantCulture) + @"\s*[\)\]\}]?",
                    " ",
                    RegexOptions.IgnoreCase);
            }

            title = SpaceRegex.Replace(title.Replace('_', ' ').Replace('.', ' '), " ")
                .Trim(' ', '-', '.', '_');
            if (title.Length < 2)
            {
                return false;
            }

            identity = new MovieIdentity { Title = title, Year = year };
            return true;
        }

        private bool TryParseMovieIdentity(string videoPath, out MovieIdentity identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return false;
            }

            string raw;
            try
            {
                raw = Path.GetFileNameWithoutExtension(videoPath) ?? string.Empty;
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            // Series-like filenames are intentionally skipped by the movie resolver.
            if (Regex.IsMatch(raw, @"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(raw, @"\b\d{1,2}x\d{1,3}\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            // Release names very often put the real release year immediately after the title.
            // Use the LAST plausible year so titles such as "2001 A Space Odyssey 1968" and
            // "Blade Runner 2049 2017" keep the year that belongs to the title.
            Match releaseYearMatch = null;
            var yearMatches = YearRegex.Matches(raw);
            if (yearMatches != null && yearMatches.Count > 0)
            {
                releaseYearMatch = yearMatches[yearMatches.Count - 1];
            }

            var year = releaseYearMatch == null ? 0 : ParseInt(releaseYearMatch.Value);
            var titleSource = raw;

            if (releaseYearMatch != null && releaseYearMatch.Index > 0)
            {
                var beforeYear = raw.Substring(0, releaseYearMatch.Index);
                var candidateBeforeYear = CleanReleaseTitle(beforeYear, stripReleaseGroup: false, stripTechnicalTokens: false);

                // If there is a real title before the last year, everything after that year is
                // considered release metadata. This safely removes strings such as
                // MULTI.VF2.2160p.WEBRip.DV.HDR10+.x265.EAC3.5.1-Amen in one go.
                if (!string.IsNullOrWhiteSpace(candidateBeforeYear))
                {
                    titleSource = beforeYear;
                }
            }

                        var cleaned = CleanReleaseTitle(
                titleSource,
                stripReleaseGroup: releaseYearMatch == null,
                stripTechnicalTokens: releaseYearMatch == null);

            // A title can itself be a year ("1984"). In that special case keep the year token as
            // the title instead of turning the identity into an empty string.
            if (string.IsNullOrWhiteSpace(cleaned) && releaseYearMatch != null)
            {
                cleaned = releaseYearMatch.Value;
            }

            if (cleaned.Length < 2)
            {
                return false;
            }

            identity = new MovieIdentity
            {
                Title = cleaned,
                Year = year
            };
            return true;
        }

        private static string CleanReleaseTitle(string raw, bool stripReleaseGroup, bool stripTechnicalTokens)
        {
            var input = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            if (stripTechnicalTokens)
            {
                // Once a strong release token begins, everything to its right is metadata. This
                // also removes unknown site/group tags that are impossible to enumerate safely.
                // Deliberately do not use ambiguous words such as "web" or "max" as cut points.
                var releaseStart = StrongReleaseTokenRegex.Match(input);
                if (releaseStart.Success && releaseStart.Index > 1)
                {
                    input = input.Substring(0, releaseStart.Index);
                }
            }

            // A release group suffix is only stripped when the filename also contains obvious
            // release metadata. This avoids damaging legitimate titles such as "Spider-Man".
            if (stripReleaseGroup && TechnicalTokenRegex.IsMatch(input))
            {
                input = Regex.Replace(input, @"-(?:[A-Za-z0-9][A-Za-z0-9._-]{1,24})$", " ");
            }

            var cleaned = ChannelTokenRegex.Replace(input, " ");
            cleaned = cleaned.Replace('.', ' ').Replace('_', ' ');
            cleaned = BracketRegex.Replace(cleaned, " ");
            if (stripTechnicalTokens)
            {
                cleaned = ParenthesisTechRegex.Replace(cleaned, " ");
                cleaned = TechnicalTokenRegex.Replace(cleaned, " ");

                // Audio channel tokens can become separated after dots are converted to spaces.
                cleaned = Regex.Replace(cleaned, @"(?<!\d)(?:1\s+0|2\s+0|5\s+1|7\s+1)(?!\d)", " ");
            }
            cleaned = cleaned.Replace("(", " ").Replace(")", " ").Replace("{", " ").Replace("}", " ");
            cleaned = SpaceRegex.Replace(cleaned, " ").Trim(' ', '-', '.', '_');
            return cleaned;
        }

        private string ResolveLanguageCode()
        {
            var configured = NormalizeLanguage(settings?.VideoTmdbArtworkLanguage);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            try
            {
                var culture = CultureInfo.CurrentUICulture;
                var language = NormalizeLanguage(culture?.TwoLetterISOLanguageName);
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
                case "ar": return "ar-SA";
                case "hi": return "hi-IN";
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

        private static string NormalizeTitle(string value)
        {
            var input = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var normalized = input.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(' ');
                }
            }

            return SpaceRegex.Replace(builder.ToString(), " ").Trim();
        }

        private static int ParseYear(string date)
        {
            if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            {
                return 0;
            }

            return ParseInt(date.Substring(0, 4));
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : 0;
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : 0.0;
        }

        private static string BuildLookupKey(string videoPath, string language)
        {
            return Sha256Hex(
                "tmdb-movie|" +
                NormalizePath(videoPath) +
                "|" +
                NormalizeLanguage(language));
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path)
                    ? string.Empty
                    : Path.GetFullPath(path).Trim().ToUpperInvariant();
            }
            catch
            {
                return (path ?? string.Empty).Trim().ToUpperInvariant();
            }
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private string GetCachedPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            var path = Path.Combine(cacheRoot, fileName);
            return File.Exists(path) ? path : string.Empty;
        }

        private void RememberNoMatch(string cacheKey)
        {
            StoreEntry(cacheKey, new TmdbCacheEntry
            {
                MatcherVersion = MatcherVersion,
                NoMatch = true,
                LastAttemptUtc = DateTime.UtcNow
            });
        }

        private void StoreEntry(string cacheKey, TmdbCacheEntry entry)
        {
            lock (indexSync)
            {
                cacheIndex[cacheKey] = entry;
                movieIdCacheKeys = null;
            }
            SaveIndex();
        }

        private void RemoveEntry(string cacheKey)
        {
            lock (indexSync)
            {
                cacheIndex.Remove(cacheKey);
                movieIdCacheKeys = null;
            }
            SaveIndex();
        }

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(indexPath))
                {
                    return;
                }

                var json = File.ReadAllText(indexPath);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, TmdbCacheEntry>>(json);
                if (loaded != null)
                {
                    lock (indexSync)
                    {
                        cacheIndex = new Dictionary<string, TmdbCacheEntry>(
                            loaded,
                            StringComparer.OrdinalIgnoreCase);
                        movieIdCacheKeys = null;
                    }
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Failed to load cache index.");
                lock (indexSync)
                {
                    cacheIndex = new Dictionary<string, TmdbCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    movieIdCacheKeys = null;
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

                    Dictionary<string, TmdbCacheEntry> snapshot;
                    lock (indexSync)
                    {
                        snapshot = new Dictionary<string, TmdbCacheEntry>(
                            cacheIndex,
                            StringComparer.OrdinalIgnoreCase);
                    }

                    var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                    var temp = indexPath + ".tmp";
                    File.WriteAllText(temp, json);
                    TryDelete(indexPath);
                    File.Move(temp, indexPath);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Failed to save cache index.");
                }
            }
        }

        private TmdbCacheEntry TryRecoverManualEntry(string cacheKey, TmdbCacheEntry previousEntry)
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

                var recovered = new TmdbCacheEntry
                {
                    MatcherVersion = MatcherVersion,
                    MovieId = previousEntry?.MovieId ?? 0,
                    PosterFileName = string.IsNullOrWhiteSpace(poster) ? string.Empty : Path.GetFileName(poster),
                    BackdropFileName = string.IsNullOrWhiteSpace(backdrop) ? string.Empty : Path.GetFileName(backdrop),
                    IsManual = true,
                    NoMatch = false,
                    LastAttemptUtc = DateTime.UtcNow
                };

                StoreEntry(cacheKey, recovered);
                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][TMDb] Recovered manual artwork cache entry: " + cacheKey);
                return recovered;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Failed to recover manual artwork cache entry.");
                return previousEntry;
            }
        }

        private static bool IsManualEntry(TmdbCacheEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            if (entry.IsManual)
            {
                return true;
            }

            // Backward compatibility with manual selections created before the IsManual flag existed.
            return (!string.IsNullOrWhiteSpace(entry.PosterFileName) &&
                    entry.PosterFileName.IndexOf(".manual.", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(entry.BackdropFileName) &&
                    entry.BackdropFileName.IndexOf(".manual.", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private TmdbCacheEntry GetEntrySnapshot(string cacheKey)
        {
            lock (indexSync)
            {
                if (!cacheIndex.TryGetValue(cacheKey, out var entry) || entry == null)
                {
                    return null;
                }

                return new TmdbCacheEntry
                {
                    MatcherVersion = entry.MatcherVersion,
                    MovieId = entry.MovieId,
                    PosterFileName = entry.PosterFileName ?? string.Empty,
                    BackdropFileName = entry.BackdropFileName ?? string.Empty,
                    LogoFileName = entry.LogoFileName ?? string.Empty,
                    IsManual = entry.IsManual,
                    NoMatch = entry.NoMatch,
                    LastAttemptUtc = entry.LastAttemptUtc
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
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Failed to import local artwork.");
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

                // The preview can currently be displayed by a WPF Image. Read it with sharing enabled
                // instead of File.Copy so an open image handle cannot prevent Apply.
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
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][TMDb] Failed to promote picker preview.");
                return string.Empty;
            }
        }

        private void EnsureCacheDirectory()
        {
            try
            {
                Directory.CreateDirectory(cacheRoot);
            }
            catch
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
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
