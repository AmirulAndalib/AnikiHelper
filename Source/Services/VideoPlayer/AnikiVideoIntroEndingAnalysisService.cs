using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    /// <summary>Online intro/ending marker resolver using AniSkip and TheIntroDB with local caching.</summary>
    internal sealed class AnikiVideoIntroEndingAnalysisService : IDisposable
    {
        private const string AniSkipBaseUrl = "https://api.aniskip.com/v2/skip-times";
        private const string AniSkipRelationRulesBaseUrl = "https://api.aniskip.com/v2/relation-rules";
        private const string AniListUrl = "https://graphql.anilist.co";
        private const string TheIntroDbUrl = "https://api.theintrodb.org/v3/media";
        private const string TvMazeBaseUrl = "https://api.tvmaze.com";
        private const string AnimeMappingUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";

        private static readonly TimeSpan MissingMarkersRetry = TimeSpan.FromDays(7);
        private static readonly TimeSpan MissingIdentityRetry = TimeSpan.FromHours(12);
        private static readonly TimeSpan TemporaryErrorRetry = TimeSpan.FromHours(2);
        private static readonly TimeSpan RateLimitedRetry = TimeSpan.FromHours(12);
        private static readonly TimeSpan AnimeMappingCacheLifetime = TimeSpan.FromDays(7);

        private static readonly Regex ImdbIdRegex = new Regex(@"(?<![A-Za-z0-9])(?<id>tt\d{7,9})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TmdbIdRegex = new Regex(@"(?:tmdb|themoviedb)[-_ .:\[\]()]*?(?<id>\d{2,10})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TvdbIdRegex = new Regex(@"(?:tvdb|thetvdb)[-_ .:\[\]()]*?(?<id>\d{2,10})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YearRegex = new Regex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex CleanupTitleRegex = new Regex(@"[\._]+", RegexOptions.Compiled);
        private static readonly Regex SeriesSeasonSuffixRegex = new Regex(
            @"(?:[\s._-]+(?:s(?:eason)?|saison)[\s._-]*\d{1,3})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class LookupIdentity
        {
            public string Title { get; set; } = string.Empty;
            public int Year { get; set; }
            public int TmdbId { get; set; }
            public int TvdbId { get; set; }
            public string ImdbId { get; set; } = string.Empty;
            public int TvMazeId { get; set; }
            public int AniListId { get; set; }
            public int MalId { get; set; }

            public bool HasTheIntroDbId => TmdbId > 0 || TvdbId > 0 || !string.IsNullOrWhiteSpace(ImdbId);
        }

        private sealed class AniListIdentity
        {
            public int AniListId { get; set; }
            public int MalId { get; set; }
            public string Title { get; set; } = string.Empty;
            public int Year { get; set; }
            public int EpisodeCount { get; set; }
        }

        private sealed class AniSkipRelationRule
        {
            public int FromStart { get; set; }
            public int FromEnd { get; set; }
            public int ToMalId { get; set; }
            public int ToStart { get; set; }
            public int ToEnd { get; set; }
        }

        private sealed class AniSkipEpisodeTarget
        {
            public int MalId { get; set; }
            public int EpisodeNumber { get; set; }
            public bool Remapped { get; set; }
        }

        private sealed class AnimeExternalMappingEntry
        {
            public string Type { get; set; } = string.Empty;
            public int AniListId { get; set; }
            public int MalId { get; set; }
            public int TvdbId { get; set; }
            public int TmdbTvId { get; set; }
            public int TvdbSeason { get; set; } = -1;
            public int TmdbSeason { get; set; } = -1;
            public int TvdbOffset { get; set; }
            public int TmdbOffset { get; set; }
        }

        private sealed class AnimeEpisodeMapping
        {
            public int MalId { get; set; }
            public int AniListId { get; set; }
            public int MalEpisode { get; set; }
            public int TvdbId { get; set; }
            public int TvdbSeason { get; set; }
            public int TvdbEpisode { get; set; }
            public int TmdbId { get; set; }
            public int TmdbSeason { get; set; }
            public int TmdbEpisode { get; set; }
            public string MappingSource { get; set; } = string.Empty;
        }

        private sealed class AniSkipRawMarker
        {
            public string Type { get; set; } = string.Empty;
            public double StartSeconds { get; set; }
            public double EndSeconds { get; set; }
            public double ReferenceEpisodeLengthSeconds { get; set; }
            public bool FromDurationMatchedQuery { get; set; }
        }

        private sealed class AniSkipFetchResult
        {
            public bool RequestSucceeded { get; set; }
            public bool RateLimited { get; set; }
            public bool NotFound { get; set; }
            public string Note { get; set; } = string.Empty;
            public List<AniSkipRawMarker> Markers { get; set; } = new List<AniSkipRawMarker>();
        }

        private sealed class MarkerLookupResult
        {
            public bool RequestSucceeded { get; set; }
            public bool RateLimited { get; set; }
            public bool NotFound { get; set; }
            public string Source { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
            public long IntroStartMs { get; set; } = -1L;
            public long IntroEndMs { get; set; } = -1L;
            public long EndingStartMs { get; set; } = -1L;
            public long EndingEndMs { get; set; } = -1L;

            public bool HasIntro => IntroStartMs >= 0L && IntroEndMs > IntroStartMs + 1000L;
            public bool HasEnding => EndingStartMs >= 0L;
            public bool HasAny => HasIntro || HasEnding;
        }

        private readonly global::AnikiHelper.AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly AnikiVideoIntroEndingStore store;
        private readonly AnikiVideoMetadataStore metadataStore;
        private readonly AnikiVideoSeriesArtworkService seriesArtworkService;
        private readonly AnikiVideoTmdbArtworkService tmdbArtworkService;
        private readonly HttpClient http;
        private readonly SemaphoreSlim networkGate = new SemaphoreSlim(3, 3);
        private readonly SemaphoreSlim requestPacingGate = new SemaphoreSlim(1, 1);
        private DateTime nextRequestUtc = DateTime.MinValue;
        private readonly object aniSkipRelationCacheSync = new object();
        private readonly Dictionary<int, IReadOnlyList<AniSkipRelationRule>> aniSkipRelationCache =
            new Dictionary<int, IReadOnlyList<AniSkipRelationRule>>();
        private readonly SemaphoreSlim aniSkipRelationGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim animeMappingGate = new SemaphoreSlim(1, 1);
        private readonly string animeMappingCachePath;
        private IReadOnlyList<AnimeExternalMappingEntry> animeMappings;
        private bool disposed;

        public AnikiVideoIntroEndingAnalysisService(
            global::AnikiHelper.AnikiHelperSettings settings,
            string pluginUserDataPath,
            ILogger logger,
            AnikiVideoMetadataStore metadataStore,
            AnikiVideoSeriesArtworkService seriesArtworkService,
            AnikiVideoTmdbArtworkService tmdbArtworkService)
        {
            this.settings = settings;
            this.logger = logger ?? LogManager.GetLogger();
            this.metadataStore = metadataStore;
            this.seriesArtworkService = seriesArtworkService;
            this.tmdbArtworkService = tmdbArtworkService;
            store = new AnikiVideoIntroEndingStore(pluginUserDataPath, this.logger);
            var mappingRoot = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? Path.Combine(Path.GetTempPath(), "AnikiHelper")
                : pluginUserDataPath;
            animeMappingCachePath = Path.Combine(mappingRoot, "VideoCenter", "IntroEnding", "anime-list-mini.json");

            http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnikiHelper-VideoCenter/3.0");
        }

        // Online marker lookups do not require FFmpeg/FFprobe. Availability is therefore not tied
        // to the user's Video Center tool paths anymore.
        public bool IsAvailable => !disposed;

        public AnikiVideoChapterAnalysis TryGetPlaybackAnalysis(string videoPath)
        {
            var record = store.GetValid(videoPath);
            if (record == null || (!record.HasIntro && !record.HasEnding)) return null;

            var skips = new List<AnikiVideoSkipChapter>();
            if (record.HasIntro)
            {
                skips.Add(new AnikiVideoSkipChapter
                {
                    StartMs = record.IntroStartMs,
                    EndMs = record.IntroEndMs,
                    Title = Loc("VideoIntroEnding_DetectedIntro", "Detected intro"),
                    Kind = "intro"
                });
            }

            return new AnikiVideoChapterAnalysis
            {
                SkipChapters = skips,
                EndingChapter = record.HasEnding
                    ? new AnikiVideoEndingChapter
                    {
                        StartMs = record.EndingStartMs,
                        Title = Loc("VideoIntroEnding_DetectedEnding", "Detected ending")
                    }
                    : null
            };
        }

        public void RefreshSeriesStatus(AnikiVideoIntroEndingSeriesItem series)
        {
            if (series == null) return;
            foreach (var season in series.Seasons ?? Array.Empty<AnikiVideoIntroEndingSeasonItem>())
            {
                foreach (var episode in season?.Episodes ?? Array.Empty<AnikiVideoIntroEndingEpisodeItem>())
                {
                    ApplyRecordToEpisode(episode, store.GetValid(episode?.Path));
                }
            }
            series.RefreshSummary();
        }

        public async Task AnalyzeSeriesAsync(
            AnikiVideoIntroEndingSeriesItem series,
            bool force,
            IProgress<AnikiVideoIntroEndingProgress> progress,
            CancellationToken cancellationToken)
        {
            if (series == null || disposed) return;

            var episodes = (series.Seasons ?? Array.Empty<AnikiVideoIntroEndingSeasonItem>())
                .Where(x => x != null)
                .SelectMany(x => x.Episodes ?? Array.Empty<AnikiVideoIntroEndingEpisodeItem>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
                .OrderBy(x => x.SeasonNumber)
                .ThenBy(x => x.EpisodeNumber <= 0 ? int.MaxValue : x.EpisodeNumber)
                .ThenBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (episodes.Count == 0) return;

            var pending = episodes.Where(x => ShouldRefresh(x.Path, force)).ToList();
            if (pending.Count == 0)
            {
                RefreshSeriesStatus(series);
                return;
            }

            ReportProgress(progress, 0, pending.Count,
                string.Format(CultureInfo.CurrentCulture,
                    Loc("VideoIntroEnding_Preparing", "Preparing marker lookup..."), series.Name));

            var identity = await ResolveLookupIdentityAsync(series, episodes, cancellationToken).ConfigureAwait(false);
            global::AnikiHelper.AnikiLog.Debug(logger, string.Format(
                CultureInfo.InvariantCulture,
                "[AnikiHelper][VideoCenter][IntroEnding] Identity '{0}' ({1}): TMDb={2}, TVDb={3}, IMDb={4}, AniList={5}, MAL={6}.",
                series.Name, series.Kind, identity?.TmdbId ?? 0, identity?.TvdbId ?? 0,
                identity?.ImdbId ?? string.Empty, identity?.AniListId ?? 0, identity?.MalId ?? 0));
            var records = new AnikiVideoIntroEndingRecord[pending.Count];
            var completed = 0;

            var tasks = pending.Select(async (episode, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await LookupEpisodeMarkersAsync(series, episode, identity, cancellationToken).ConfigureAwait(false);
                    records[index] = BuildRecord(episode.Path, result, identity);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] Marker lookup failed for: " + episode.Path);
                    records[index] = BuildTemporaryErrorRecord(episode.Path, ex.Message);
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    ReportProgress(progress, done, pending.Count,
                        string.Format(CultureInfo.CurrentCulture,
                            Loc("VideoIntroEnding_RefreshingEpisode", "Fetching markers {0}/{1}: {2}"),
                            done,
                            pending.Count,
                            episode.EpisodeCode));
                    networkGate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            store.UpsertRange(records.Where(x => x != null));
            RefreshSeriesStatus(series);

            global::AnikiHelper.AnikiLog.Debug(logger, string.Format(
                CultureInfo.InvariantCulture,
                "[AnikiHelper][VideoCenter][IntroEnding] Marker refresh '{0}' ({1}): checked={2}, intro={3}, ending={4}.",
                series.Name,
                series.Kind,
                pending.Count,
                records.Count(x => x?.HasIntro == true),
                records.Count(x => x?.HasEnding == true)));

            var sourceSummary = string.Join(", ", records.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Source))
                .GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key + "=" + g.Count().ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrWhiteSpace(sourceSummary))
            {
                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Sources for '" + series.Name + "': " + sourceSummary + ".");
            }
        }

        private bool ShouldRefresh(string path, bool force)
        {
            if (force) return true;
            var record = store.GetValid(path);
            if (record == null) return true;

            // Complete records are stable until the local file itself changes. Partial/negative
            // records are retried periodically so community databases can fill gaps later.
            if (record.HasIntro && record.HasEnding) return false;
            if (record.RetryAfterUtc <= DateTime.MinValue) return false;
            return DateTime.UtcNow >= record.RetryAfterUtc;
        }

        private async Task<MarkerLookupResult> LookupEpisodeMarkersAsync(
            AnikiVideoIntroEndingSeriesItem series,
            AnikiVideoIntroEndingEpisodeItem episode,
            LookupIdentity identity,
            CancellationToken cancellationToken)
        {
            var kind = (series?.Kind ?? string.Empty).Trim().ToLowerInvariant();
            MarkerLookupResult combined = null;

            if (string.Equals(kind, "anime", StringComparison.OrdinalIgnoreCase) && episode.EpisodeNumber > 0)
            {
                var animeMapping = await ResolveAnimeEpisodeMappingAsync(
                    series, episode, identity, cancellationToken).ConfigureAwait(false);

                if (animeMapping?.MalId > 0 && animeMapping.MalEpisode > 0)
                {
                    var localDurationSeconds = await ProbeLocalDurationSecondsAsync(episode.Path, cancellationToken).ConfigureAwait(false);
                    combined = await LookupAniSkipAsync(
                        animeMapping.MalId, animeMapping.MalEpisode, localDurationSeconds, cancellationToken).ConfigureAwait(false);
                }

                // TheIntroDB remains an online-only fallback, but use the SAME provider numbering
                // that was resolved by the anime mapping. Passing a TVDB-style local S17 to a
                // TMDb show that calls it S2 is exactly the mismatch this layer is meant to avoid.
                if (combined == null || !combined.HasIntro || !combined.HasEnding)
                {
                    LookupIdentity mappedIntroDbIdentity = null;
                    var mappedSeason = 0;
                    var mappedEpisode = 0;
                    if (animeMapping?.TvdbId > 0 && animeMapping.TvdbSeason >= 0 && animeMapping.TvdbEpisode > 0)
                    {
                        mappedIntroDbIdentity = new LookupIdentity { TvdbId = animeMapping.TvdbId, Title = identity?.Title ?? string.Empty };
                        mappedSeason = animeMapping.TvdbSeason;
                        mappedEpisode = animeMapping.TvdbEpisode;
                    }
                    else if (animeMapping?.TmdbId > 0 && animeMapping.TmdbSeason >= 0 && animeMapping.TmdbEpisode > 0)
                    {
                        mappedIntroDbIdentity = new LookupIdentity { TmdbId = animeMapping.TmdbId, Title = identity?.Title ?? string.Empty };
                        mappedSeason = animeMapping.TmdbSeason;
                        mappedEpisode = animeMapping.TmdbEpisode;
                    }
                    else if ((episode.SeasonNumber <= 1) && identity?.HasTheIntroDbId == true)
                    {
                        // Season 1 without a cross-provider mapping is still safe to try using the
                        // existing matched identity. For later local seasons, fail closed.
                        mappedIntroDbIdentity = identity;
                        mappedSeason = episode.SeasonNumber;
                        mappedEpisode = episode.EpisodeNumber;
                    }

                    if (mappedIntroDbIdentity?.HasTheIntroDbId == true)
                    {
                        var introDb = await LookupTheIntroDbAsync(
                            mappedIntroDbIdentity, false, mappedSeason, mappedEpisode, cancellationToken).ConfigureAwait(false);
                        combined = MergeResults(combined, introDb);
                    }
                }
            }
            else
            {
                // TheIntroDB is primary for ordinary TV and movies.
                if (identity?.HasTheIntroDbId == true)
                {
                    var localDurationSeconds = await ProbeLocalDurationSecondsAsync(episode.Path, cancellationToken).ConfigureAwait(false);
                    var introDb = await LookupTheIntroDbAsync(
                        identity,
                        string.Equals(kind, "movies", StringComparison.OrdinalIgnoreCase),
                        episode.SeasonNumber,
                        episode.EpisodeNumber,
                        localDurationSeconds,
                        cancellationToken).ConfigureAwait(false);
                    combined = MergeResults(combined, introDb);
                }
            }

            if (combined != null) return combined;

            return new MarkerLookupResult
            {
                RequestSucceeded = false,
                NotFound = false,
                Source = string.Empty,
                Note = Loc("VideoIntroEnding_MissingIdentity", "No compatible provider ID is available yet for this media.")
            };
        }

        private async Task<MarkerLookupResult> LookupAniSkipAsync(
            int malId,
            int episodeNumber,
            double localDurationSeconds,
            CancellationToken cancellationToken)
        {
            var target = await ResolveAniSkipEpisodeTargetAsync(malId, episodeNumber, cancellationToken).ConfigureAwait(false);
            var primary = await LookupAniSkipDirectAsync(
                target.MalId, target.EpisodeNumber, localDurationSeconds, cancellationToken).ConfigureAwait(false);

            // Relation rules can redirect absolute episode numbering to another MAL entry. If the
            // redirected target has no marker, try the original tuple as a compatibility fallback.
            if (target.Remapped && (primary == null || (!primary.HasAny && !primary.RateLimited)))
            {
                var original = await LookupAniSkipDirectAsync(
                    malId, episodeNumber, localDurationSeconds, cancellationToken).ConfigureAwait(false);
                primary = MergeResults(primary, original);
            }

            return primary;
        }

        private async Task<MarkerLookupResult> LookupAniSkipDirectAsync(
            int malId,
            int episodeNumber,
            double localDurationSeconds,
            CancellationToken cancellationToken)
        {
            try
            {
                // Match the behavior used by current AniSkip clients: request one duration-matched
                // result first, then a duration-agnostic result as fallback. Each returned marker
                // includes the submitter's episodeLength, allowing us to align it to the exact local
                // file duration without decoding any media.
                AniSkipFetchResult accurate = null;
                if (localDurationSeconds > 1.0)
                {
                    accurate = await FetchAniSkipAsync(
                        malId, episodeNumber, localDurationSeconds, cancellationToken).ConfigureAwait(false);
                    if (accurate?.RateLimited == true)
                    {
                        return new MarkerLookupResult { RateLimited = true, Source = "AniSkip", Note = "AniSkip rate limit" };
                    }
                }

                var rough = await FetchAniSkipAsync(
                    malId, episodeNumber, 0.0, cancellationToken).ConfigureAwait(false);
                if (rough?.RateLimited == true)
                {
                    return new MarkerLookupResult { RateLimited = true, Source = "AniSkip", Note = "AniSkip rate limit" };
                }

                var allMarkers = new List<AniSkipRawMarker>();
                if (accurate?.Markers != null) allMarkers.AddRange(accurate.Markers);
                if (rough?.Markers != null) allMarkers.AddRange(rough.Markers);

                // Accurate results come first. Keep the first marker for each skip type, exactly like
                // Hayase's AniSkip integration, so the rough query only fills a missing type.
                var selected = allMarkers
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Type))
                    .GroupBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (selected.Count == 0)
                {
                    var requestSucceeded = accurate?.RequestSucceeded == true || rough?.RequestSucceeded == true;
                    var notFound = accurate?.NotFound == true || rough?.NotFound == true;
                    return new MarkerLookupResult
                    {
                        RequestSucceeded = requestSucceeded,
                        NotFound = notFound,
                        Source = "AniSkip",
                        Note = accurate?.Note ?? rough?.Note ?? string.Empty
                    };
                }

                var output = new MarkerLookupResult { RequestSucceeded = true, Source = "AniSkip" };
                var intro = selected.FirstOrDefault(x => string.Equals(x.Type, "op", StringComparison.OrdinalIgnoreCase))
                    ?? selected.FirstOrDefault(x => string.Equals(x.Type, "mixed-op", StringComparison.OrdinalIgnoreCase));
                var ending = selected.FirstOrDefault(x => string.Equals(x.Type, "ed", StringComparison.OrdinalIgnoreCase))
                    ?? selected.FirstOrDefault(x => string.Equals(x.Type, "mixed-ed", StringComparison.OrdinalIgnoreCase));

                double introShift = 0.0;
                double endingShift = 0.0;
                if (intro != null)
                {
                    // Only duration-align OP markers returned by AniSkip's duration-matched query.
                    introShift = intro.FromDurationMatchedQuery
                        ? ComputeAniSkipDurationShift(localDurationSeconds, intro.ReferenceEpisodeLengthSeconds)
                        : 0.0;
                    var start = AlignAniSkipSeconds(intro.StartSeconds, introShift, localDurationSeconds);
                    var end = AlignAniSkipSeconds(intro.EndSeconds, introShift, localDurationSeconds);
                    if (end > start + 1.0)
                    {
                        output.IntroStartMs = SecondsToMilliseconds(start);
                        output.IntroEndMs = SecondsToMilliseconds(end);
                    }
                }

                if (ending != null)
                {
                    endingShift = ComputeAniSkipDurationShift(localDurationSeconds, ending.ReferenceEpisodeLengthSeconds);
                    var start = AlignAniSkipSeconds(ending.StartSeconds, endingShift, localDurationSeconds);
                    var end = AlignAniSkipSeconds(ending.EndSeconds, endingShift, localDurationSeconds);
                    if (end > start + 1.0)
                    {
                        output.EndingStartMs = SecondsToMilliseconds(start);
                        output.EndingEndMs = SecondsToMilliseconds(end);
                    }
                }

                if (!output.HasAny) output.NotFound = true;
                global::AnikiHelper.AnikiLog.Debug(logger, string.Format(
                    CultureInfo.InvariantCulture,
                    "[AnikiHelper][VideoCenter][IntroEnding] AniSkip result: MAL={0} episode={1} accurate={2} rough={3} local={4:0.###}s intro={5} shift={6:+0.###;-0.###;0}s ending={7} shift={8:+0.###;-0.###;0}s.",
                    malId,
                    episodeNumber,
                    accurate?.Markers?.Count ?? 0,
                    rough?.Markers?.Count ?? 0,
                    localDurationSeconds,
                    output.HasIntro ? "true" : "false",
                    introShift,
                    output.HasEnding ? "true" : "false",
                    endingShift));
                return output;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip request failed for MAL=" + malId + " episode=" + episodeNumber + ".");
                return new MarkerLookupResult { RequestSucceeded = false, Source = "AniSkip", Note = ex.Message };
            }
        }

        private async Task<AniSkipFetchResult> FetchAniSkipAsync(
            int malId,
            int episodeNumber,
            double requestedEpisodeLengthSeconds,
            CancellationToken cancellationToken)
        {
            var episodeLengthText = requestedEpisodeLengthSeconds > 1.0
                ? requestedEpisodeLengthSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                : "0";
            var url = AniSkipBaseUrl + "/" + malId.ToString(CultureInfo.InvariantCulture) + "/" +
                      episodeNumber.ToString(CultureInfo.InvariantCulture) +
                      "?types=op&types=ed&types=mixed-op&types=mixed-ed&episodeLength=" + episodeLengthText;

            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using (var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip rate limited: MAL=" + malId + " episode=" + episodeNumber + ".");
                    return new AniSkipFetchResult { RateLimited = true, Note = "AniSkip rate limit" };
                }

                if (!response.IsSuccessStatusCode)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip HTTP " + (int)response.StatusCode +
                        ": MAL=" + malId + " episode=" + episodeNumber + " length=" + episodeLengthText +
                        " body=" + TruncateForLog(body, 220));
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new AniSkipFetchResult { RequestSucceeded = true, NotFound = true };
                    }
                    return new AniSkipFetchResult { RequestSucceeded = false, Note = "HTTP " + (int)response.StatusCode };
                }

                var root = string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
                var results = root?["results"] as JArray;
                if (results == null || results.Count == 0)
                {
                    return new AniSkipFetchResult { RequestSucceeded = true, NotFound = true };
                }

                var markers = results.OfType<JObject>()
                    .Select(token => new AniSkipRawMarker
                    {
                        Type = (token["skipType"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant(),
                        StartSeconds = ParseDoubleToken((token["interval"] as JObject)?["startTime"]),
                        EndSeconds = ParseDoubleToken((token["interval"] as JObject)?["endTime"]),
                        ReferenceEpisodeLengthSeconds = ParseDoubleToken(token["episodeLength"]),
                        FromDurationMatchedQuery = requestedEpisodeLengthSeconds > 1.0
                    })
                    .Where(x => x.EndSeconds > x.StartSeconds + 1.0)
                    .ToList();

                return new AniSkipFetchResult
                {
                    RequestSucceeded = true,
                    NotFound = markers.Count == 0,
                    Markers = markers
                };
            }
        }

        private static double ComputeAniSkipDurationShift(double localDurationSeconds, double referenceDurationSeconds)
        {
            if (localDurationSeconds <= 1.0 || referenceDurationSeconds <= 1.0) return 0.0;
            return localDurationSeconds - referenceDurationSeconds;
        }

        private static double AlignAniSkipSeconds(double seconds, double shiftSeconds, double localDurationSeconds)
        {
            var aligned = Math.Max(0.0, seconds + shiftSeconds);
            if (localDurationSeconds > 1.0)
            {
                aligned = Math.Min(localDurationSeconds, aligned);
            }
            return aligned;
        }

        private async Task<AniSkipEpisodeTarget> ResolveAniSkipEpisodeTargetAsync(int malId, int episodeNumber, CancellationToken cancellationToken)
        {
            var target = new AniSkipEpisodeTarget
            {
                MalId = malId,
                EpisodeNumber = Math.Max(1, episodeNumber),
                Remapped = false
            };

            var rules = await GetAniSkipRelationRulesAsync(malId, cancellationToken).ConfigureAwait(false);
            var rule = (rules ?? Array.Empty<AniSkipRelationRule>())
                .FirstOrDefault(x => x != null && target.EpisodeNumber >= x.FromStart && target.EpisodeNumber <= x.FromEnd && x.ToMalId > 0);
            if (rule == null) return target;

            var mappedEpisode = rule.ToStart + (target.EpisodeNumber - rule.FromStart);
            if (mappedEpisode <= 0) return target;
            if (rule.ToEnd > 0 && mappedEpisode > rule.ToEnd) return target;

            global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip relation: MAL=" + malId +
                " episode=" + target.EpisodeNumber + " -> MAL=" + rule.ToMalId + " episode=" + mappedEpisode + ".");
            return new AniSkipEpisodeTarget
            {
                MalId = rule.ToMalId,
                EpisodeNumber = mappedEpisode,
                Remapped = rule.ToMalId != malId || mappedEpisode != target.EpisodeNumber
            };
        }

        private async Task<IReadOnlyList<AniSkipRelationRule>> GetAniSkipRelationRulesAsync(int malId, CancellationToken cancellationToken)
        {
            if (malId <= 0) return Array.Empty<AniSkipRelationRule>();
            lock (aniSkipRelationCacheSync)
            {
                if (aniSkipRelationCache.TryGetValue(malId, out var cached)) return cached;
            }

            await aniSkipRelationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (aniSkipRelationCacheSync)
                {
                    if (aniSkipRelationCache.TryGetValue(malId, out var cached)) return cached;
                }

                var rules = new List<AniSkipRelationRule>();
                try
                {
                    await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
                    using (var response = await http.GetAsync(AniSkipRelationRulesBaseUrl + "/" + malId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false))
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            var root = string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
                            var items = root?["rules"] as JArray;
                            if (items != null)
                            {
                                foreach (var item in items.OfType<JObject>())
                                {
                                    var from = item["from"] as JObject;
                                    var to = item["to"] as JObject;
                                    var rule = new AniSkipRelationRule
                                    {
                                        FromStart = ParseInt(from?["start"]?.ToString()),
                                        FromEnd = ParseInt(from?["end"]?.ToString()),
                                        ToMalId = ParseInt(to?["malId"]?.ToString()),
                                        ToStart = ParseInt(to?["start"]?.ToString()),
                                        ToEnd = ParseInt(to?["end"]?.ToString())
                                    };
                                    if (rule.FromStart > 0 && rule.FromEnd >= rule.FromStart && rule.ToMalId > 0 && rule.ToStart > 0)
                                    {
                                        rules.Add(rule);
                                    }
                                }
                            }
                        }
                        else if (response.StatusCode != HttpStatusCode.NotFound)
                        {
                            global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip relation rules HTTP " +
                                (int)response.StatusCode + ": MAL=" + malId + " body=" + TruncateForLog(body, 220));
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Relation rules are an optional normalization layer. Direct MAL/episode lookup
                    // remains valid when this endpoint is temporarily unavailable.
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip relation-rule lookup failed for MAL=" + malId + ".");
                }

                var frozen = (IReadOnlyList<AniSkipRelationRule>)rules;
                lock (aniSkipRelationCacheSync) aniSkipRelationCache[malId] = frozen;
                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] AniSkip relation rules: MAL=" + malId + " count=" + rules.Count + ".");
                return frozen;
            }
            finally
            {
                aniSkipRelationGate.Release();
            }
        }

        private Task<MarkerLookupResult> LookupTheIntroDbAsync(
            LookupIdentity identity,
            bool isMovie,
            int season,
            int episode,
            CancellationToken cancellationToken)
        {
            // Keep anime fallback behavior unchanged. Movie/TV lookups use the overload below
            // with the local duration so TheIntroDB can pick the closest release.
            return LookupTheIntroDbAsync(identity, isMovie, season, episode, 0.0, cancellationToken);
        }

        private async Task<MarkerLookupResult> LookupTheIntroDbAsync(
            LookupIdentity identity,
            bool isMovie,
            int season,
            int episode,
            double localDurationSeconds,
            CancellationToken cancellationToken)
        {
            var query = new List<string>();
            if (identity.TmdbId > 0)
                query.Add("tmdb_id=" + identity.TmdbId.ToString(CultureInfo.InvariantCulture));
            else if (identity.TvdbId > 0)
                query.Add("tvdb_id=" + identity.TvdbId.ToString(CultureInfo.InvariantCulture));
            else if (!string.IsNullOrWhiteSpace(identity.ImdbId))
                query.Add("imdb_id=" + Uri.EscapeDataString(identity.ImdbId));
            else
                return null;

            if (!isMovie)
            {
                if (season <= 0 || episode <= 0)
                {
                    return new MarkerLookupResult
                    {
                        RequestSucceeded = false,
                        Source = "TheIntroDB",
                        Note = Loc("VideoIntroEnding_MissingEpisodeNumber", "Season/episode number could not be determined.")
                    };
                }
                query.Add("season=" + season.ToString(CultureInfo.InvariantCulture));
                query.Add("episode=" + episode.ToString(CultureInfo.InvariantCulture));
            }

            if (localDurationSeconds > 1.0)
            {
                var durationMs = (long)Math.Round(localDurationSeconds * 1000.0, MidpointRounding.AwayFromZero);
                if (durationMs > 0L) query.Add("duration_ms=" + durationMs.ToString(CultureInfo.InvariantCulture));
            }

            var url = TheIntroDbUrl + "?" + string.Join("&", query);
            try
            {
                await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
                using (var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429)
                    {
                        return new MarkerLookupResult { RateLimited = true, Source = "TheIntroDB", Note = "TheIntroDB rate limit" };
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            return new MarkerLookupResult { RequestSucceeded = true, NotFound = true, Source = "TheIntroDB" };
                        }
                        return new MarkerLookupResult { RequestSucceeded = false, Source = "TheIntroDB", Note = "HTTP " + (int)response.StatusCode };
                    }

                    var root = JObject.Parse(body);
                    var output = new MarkerLookupResult { RequestSucceeded = true, Source = "TheIntroDB" };
                    ApplyIntroDbRange(root["intro"] as JArray, true, output);
                    ApplyIntroDbRange(root["credits"] as JArray, false, output);
                    if (!output.HasAny) output.NotFound = true;
                    return output;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] TheIntroDB request failed.");
                return new MarkerLookupResult { RequestSucceeded = false, Source = "TheIntroDB", Note = ex.Message };
            }
        }

        private static void ApplyIntroDbRange(JArray ranges, bool intro, MarkerLookupResult output)
        {
            if (ranges == null || output == null) return;
            foreach (var range in ranges.OfType<JObject>())
            {
                var startToken = range["start_ms"];
                var endToken = range["end_ms"];
                var start = startToken == null || startToken.Type == JTokenType.Null ? 0L : ParseLong(startToken.ToString());
                var end = endToken == null || endToken.Type == JTokenType.Null ? -1L : ParseLong(endToken.ToString());

                if (intro)
                {
                    if (end <= start + 1000L) continue;
                    output.IntroStartMs = Math.Max(0L, start);
                    output.IntroEndMs = end;
                    return;
                }

                if (start < 0L) continue;
                output.EndingStartMs = start;
                output.EndingEndMs = end;
                return;
            }
        }

        private async Task<LookupIdentity> ResolveLookupIdentityAsync(
            AnikiVideoIntroEndingSeriesItem series,
            IReadOnlyList<AnikiVideoIntroEndingEpisodeItem> episodes,
            CancellationToken cancellationToken)
        {
            var identity = new LookupIdentity
            {
                Title = series?.Name ?? string.Empty,
                Year = 0
            };

            ParseIdsFromText(series?.FullPath, identity);
            foreach (var episode in episodes.Take(3)) ParseIdsFromText(episode?.Path, identity);

            AnikiVideoMetadataRecord metadata = null;
            if (!string.IsNullOrWhiteSpace(series?.FullPath)) metadata = metadataStore?.Get(series.FullPath);
            var representativeVideo = episodes.FirstOrDefault()?.Path ?? string.Empty;
            if (metadata == null && !string.IsNullOrWhiteSpace(representativeVideo))
            {
                metadata = metadataStore?.Get(representativeVideo);
            }

            if (metadata != null)
            {
                if (!string.IsNullOrWhiteSpace(metadata.Title)) identity.Title = metadata.Title;
                if (metadata.Year > 0) identity.Year = metadata.Year;
                ApplyMetadataProviderIdentity(metadata, identity);
            }

            // Provider identity chosen by Video Center / Artwork Manager is authoritative. Read it
            // before any title-based network lookup so a user-selected match is never searched again.
            var kind = (series?.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (string.Equals(kind, "movies", StringComparison.OrdinalIgnoreCase) && identity.TmdbId <= 0)
            {
                var cachedMovieId = tmdbArtworkService?.GetCachedMovieId(representativeVideo) ?? 0;
                if (cachedMovieId > 0)
                {
                    identity.TmdbId = cachedMovieId;
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Reused cached movie TMDb id: " + cachedMovieId + " for '" + identity.Title + "'.");
                }
            }
            else if (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) &&
                     seriesArtworkService != null)
            {
                string cachedProvider = string.Empty;
                var cachedRemoteId = 0;
                var hasCachedProvider = series?.IsDirectory == true &&
                    seriesArtworkService.TryGetCachedFolderProviderIdentity(series.FullPath, out cachedProvider, out cachedRemoteId);
                if (!hasCachedProvider && !string.IsNullOrWhiteSpace(representativeVideo))
                {
                    hasCachedProvider = seriesArtworkService.TryGetCachedProviderIdentity(
                        representativeVideo, out cachedProvider, out cachedRemoteId);
                }

                if (hasCachedProvider && cachedRemoteId > 0)
                {
                    ApplyCachedSeriesProviderIdentity(cachedProvider, cachedRemoteId, identity);
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Reused cached series provider: " +
                        cachedProvider + "=" + cachedRemoteId + " for '" + identity.Title + "'.");
                }
            }

            var needsMetadataLookup =
                (string.Equals(kind, "anime", StringComparison.OrdinalIgnoreCase) && metadata == null) ||
                (string.Equals(kind, "movies", StringComparison.OrdinalIgnoreCase) && identity.TmdbId <= 0) ||
                (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) && metadata == null &&
                 identity.TmdbId <= 0 && identity.TvMazeId <= 0 && identity.TvdbId <= 0 && string.IsNullOrWhiteSpace(identity.ImdbId));

            if (needsMetadataLookup)
            {
                metadata = await ResolveMetadataForMarkersAsync(series, representativeVideo, cancellationToken).ConfigureAwait(false);
                if (metadata != null)
                {
                    if (!string.IsNullOrWhiteSpace(metadata.Title)) identity.Title = metadata.Title;
                    if (metadata.Year > 0) identity.Year = metadata.Year;
                    ApplyMetadataProviderIdentity(metadata, identity);

                    if (metadataStore != null && !string.IsNullOrWhiteSpace(series?.FullPath))
                    {
                        metadataStore.UpsertProvider(
                            series.FullPath,
                            metadata.Title,
                            metadata.Year,
                            series.Kind,
                            metadata.Overview,
                            metadata.Genres,
                            metadata.Rating,
                            metadata.Provider,
                            metadata.ProviderId,
                            metadata.RuntimeMinutes,
                            metadata.VoteCount,
                            metadata.Tagline,
                            metadata.Credits,
                            metadata.OriginalTitle,
                            metadata.Cast);
                    }
                }
            }

            if (string.Equals(kind, "movies", StringComparison.OrdinalIgnoreCase) &&
                identity.TmdbId <= 0 && tmdbArtworkService != null && !string.IsNullOrWhiteSpace(representativeVideo))
            {
                var fallbackMovieId = await tmdbArtworkService.ResolveMovieIdForMarkersFallbackAsync(
                    representativeVideo,
                    identity.Title,
                    identity.Year,
                    cancellationToken).ConfigureAwait(false);
                if (fallbackMovieId > 0)
                {
                    identity.TmdbId = fallbackMovieId;
                    global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Resolved movie TMDb id with marker fallback: " +
                        fallbackMovieId + " for '" + identity.Title + "'.");
                }
            }

            if (identity.TvMazeId > 0 && (identity.TmdbId <= 0 || identity.TvdbId <= 0 || string.IsNullOrWhiteSpace(identity.ImdbId)))
            {
                await EnrichFromTvMazeAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            // If there is no metadata association yet, TVMaze can safely provide external ids for
            // ordinary series without a user API key. Keep this as a fallback only; existing/manual
            // metadata always wins.
            if (string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase) && !identity.HasTheIntroDbId)
            {
                await TryResolveSeriesByTvMazeTitleAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            // Anime may have been matched through TMDb/TVMaze by the existing metadata pipeline.
            // Resolve a MAL id independently so AniSkip remains the preferred marker source.
            if (string.Equals(kind, "anime", StringComparison.OrdinalIgnoreCase) && identity.MalId <= 0)
            {
                var anime = identity.AniListId > 0
                    ? await ResolveAniListByIdAsync(identity.AniListId, cancellationToken).ConfigureAwait(false)
                    : await SearchAniListIdentityAsync(identity.Title, identity.Year, cancellationToken).ConfigureAwait(false);
                if (anime != null)
                {
                    identity.AniListId = anime.AniListId;
                    identity.MalId = anime.MalId;
                }
            }

            return identity;
        }

        private async Task<AnikiVideoMetadataRecord> ResolveMetadataForMarkersAsync(
            AnikiVideoIntroEndingSeriesItem series,
            string representativeVideo,
            CancellationToken cancellationToken)
        {
            try
            {
                var kind = (series?.Kind ?? string.Empty).Trim().ToLowerInvariant();
                if (string.Equals(kind, "movies", StringComparison.OrdinalIgnoreCase))
                {
                    if (tmdbArtworkService == null || string.IsNullOrWhiteSpace(representativeVideo)) return null;
                    return await tmdbArtworkService.ResolveMetadataAsync(representativeVideo, cancellationToken).ConfigureAwait(false);
                }

                if (seriesArtworkService == null) return null;
                if (series?.IsDirectory == true && !string.IsNullOrWhiteSpace(series.FullPath))
                {
                    return await seriesArtworkService.ResolveFolderMetadataAsync(series.FullPath, cancellationToken).ConfigureAwait(false);
                }
                if (!string.IsNullOrWhiteSpace(representativeVideo))
                {
                    return await seriesArtworkService.ResolveMetadataAsync(representativeVideo, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] Metadata resolution for markers failed.");
            }
            return null;
        }

        private static void ApplyMetadataProviderIdentity(AnikiVideoMetadataRecord metadata, LookupIdentity identity)
        {
            if (metadata == null || identity == null) return;
            var provider = (metadata.Provider ?? string.Empty).Trim();
            var id = ParseInt(metadata.ProviderId);
            if (id <= 0) return;

            if (provider.IndexOf("TMDB", StringComparison.OrdinalIgnoreCase) >= 0)
                identity.TmdbId = id;
            else if (provider.IndexOf("TVMAZE", StringComparison.OrdinalIgnoreCase) >= 0)
                identity.TvMazeId = id;
            else if (provider.IndexOf("ANILIST", StringComparison.OrdinalIgnoreCase) >= 0)
                identity.AniListId = id;
        }

        private static void ApplyCachedSeriesProviderIdentity(string provider, int remoteId, LookupIdentity identity)
        {
            if (identity == null || remoteId <= 0) return;
            provider = (provider ?? string.Empty).Trim();

            if (string.Equals(provider, "tmdb-tv", StringComparison.OrdinalIgnoreCase) ||
                provider.IndexOf("TMDB", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (identity.TmdbId <= 0) identity.TmdbId = remoteId;
            }
            else if (string.Equals(provider, "tvmaze", StringComparison.OrdinalIgnoreCase) ||
                     provider.IndexOf("TVMAZE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (identity.TvMazeId <= 0) identity.TvMazeId = remoteId;
            }
            else if (string.Equals(provider, "anilist", StringComparison.OrdinalIgnoreCase) ||
                     provider.IndexOf("ANILIST", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (identity.AniListId <= 0) identity.AniListId = remoteId;
            }
        }

        private async Task EnrichFromTvMazeAsync(LookupIdentity identity, CancellationToken cancellationToken)
        {
            if (identity == null || identity.TvMazeId <= 0) return;
            try
            {
                var root = await GetJsonAsync(TvMazeBaseUrl + "/shows/" + identity.TvMazeId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                ApplyTvMazeExternals(root, identity);
                if (identity.Year <= 0) identity.Year = ParseYear(root?["premiered"]?.ToString());
                if (string.IsNullOrWhiteSpace(identity.Title)) identity.Title = root?["name"]?.ToString() ?? string.Empty;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] TVMaze external-id lookup failed.");
            }
        }

        private async Task TryResolveSeriesByTvMazeTitleAsync(LookupIdentity identity, CancellationToken cancellationToken)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Title)) return;
            try
            {
                var lookupTitle = SeriesSeasonSuffixRegex.Replace(identity.Title ?? string.Empty, string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(lookupTitle)) lookupTitle = identity.Title;
                var url = TvMazeBaseUrl + "/singlesearch/shows?q=" + Uri.EscapeDataString(lookupTitle);
                var root = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
                if (root == null) return;

                var remoteTitle = root["name"]?.ToString() ?? string.Empty;
                var remoteYear = ParseYear(root["premiered"]?.ToString());
                var titleOkay = string.Equals(NormalizeTitle(remoteTitle), NormalizeTitle(lookupTitle), StringComparison.OrdinalIgnoreCase);
                var yearOkay = identity.Year <= 0 || remoteYear <= 0 || identity.Year == remoteYear;
                if (!titleOkay || !yearOkay) return;

                identity.TvMazeId = ParseInt(root["id"]?.ToString());
                if (identity.Year <= 0) identity.Year = remoteYear;
                ApplyTvMazeExternals(root, identity);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] TVMaze title lookup failed.");
            }
        }

        private static void ApplyTvMazeExternals(JObject root, LookupIdentity identity)
        {
            if (root == null || identity == null) return;
            var externals = root["externals"] as JObject;
            if (externals == null) return;

            if (identity.TvdbId <= 0) identity.TvdbId = ParseInt(externals["thetvdb"]?.ToString());
            if (identity.TmdbId <= 0)
            {
                identity.TmdbId = ParseInt(externals["themoviedb"]?.ToString());
                if (identity.TmdbId <= 0) identity.TmdbId = ParseInt(externals["tmdb"]?.ToString());
            }
            if (string.IsNullOrWhiteSpace(identity.ImdbId))
            {
                var imdb = externals["imdb"]?.ToString() ?? string.Empty;
                if (ImdbIdRegex.IsMatch(imdb)) identity.ImdbId = imdb.Trim();
            }
        }

        private async Task<AnimeEpisodeMapping> ResolveAnimeEpisodeMappingAsync(
            AnikiVideoIntroEndingSeriesItem series,
            AnikiVideoIntroEndingEpisodeItem episode,
            LookupIdentity identity,
            CancellationToken cancellationToken)
        {
            if (episode == null || episode.EpisodeNumber <= 0) return null;
            var localSeason = Math.Max(1, episode.SeasonNumber);
            var localEpisode = Math.Max(1, episode.EpisodeNumber);

            var mappings = await GetAnimeMappingsAsync(cancellationToken).ConfigureAwait(false);
            if (mappings != null && mappings.Count > 0)
            {
                // Resolve the franchise/show IDs from the exact anime match first. A shared TMDb
                // show can contain many MAL entries, so taking the first broad TMDb match could
                // accidentally pick another related title.
                var baseCandidates = mappings.Where(x => x != null && identity?.MalId > 0 && x.MalId == identity.MalId).ToList();
                if (baseCandidates.Count == 0)
                    baseCandidates = mappings.Where(x => x != null && identity?.AniListId > 0 && x.AniListId == identity.AniListId).ToList();
                if (baseCandidates.Count == 0 && identity?.TvdbId > 0)
                    baseCandidates = mappings.Where(x => x != null && x.TvdbId == identity.TvdbId).ToList();
                if (baseCandidates.Count == 0 && identity?.TmdbId > 0)
                    baseCandidates = mappings.Where(x => x != null && x.TmdbTvId == identity.TmdbId).ToList();

                var tvdbId = identity?.TvdbId > 0
                    ? identity.TvdbId
                    : baseCandidates.Select(x => x.TvdbId).FirstOrDefault(x => x > 0);
                var tmdbId = identity?.TmdbId > 0
                    ? identity.TmdbId
                    : baseCandidates.Select(x => x.TmdbTvId).FirstOrDefault(x => x > 0);

                AnimeExternalMappingEntry selected = null;
                var source = string.Empty;

                // Prefer TVDB numbering. Library season folders such as Bleach S17 commonly follow
                // TVDB, while TMDb may group the exact same anime under a completely different season.
                if (tvdbId > 0)
                {
                    selected = SelectAnimeMappingEntry(
                        mappings.Where(x => x != null && x.TvdbId == tvdbId && x.TvdbSeason == localSeason),
                        localEpisode,
                        true);
                    if (selected != null) source = "Fribb/TVDB";
                }

                // If the local season is not a TVDB season, try the TMDb season layout.
                if (selected == null && tmdbId > 0)
                {
                    selected = SelectAnimeMappingEntry(
                        mappings.Where(x => x != null && x.TmdbTvId == tmdbId && x.TmdbSeason == localSeason),
                        localEpisode,
                        false);
                    if (selected != null) source = "Fribb/TMDb";
                }

                if (selected != null && selected.MalId > 0)
                {
                    var animeEpisode = source == "Fribb/TVDB"
                        ? localEpisode - selected.TvdbOffset
                        : localEpisode - selected.TmdbOffset;
                    if (animeEpisode > 0)
                    {
                        var mapped = new AnimeEpisodeMapping
                        {
                            MalId = selected.MalId,
                            AniListId = selected.AniListId,
                            MalEpisode = animeEpisode,
                            MappingSource = source,
                            TvdbId = selected.TvdbId,
                            TvdbSeason = selected.TvdbSeason,
                            TvdbEpisode = selected.TvdbSeason >= 0 ? animeEpisode + selected.TvdbOffset : 0,
                            TmdbId = selected.TmdbTvId,
                            TmdbSeason = selected.TmdbSeason,
                            TmdbEpisode = selected.TmdbSeason >= 0 ? animeEpisode + selected.TmdbOffset : 0
                        };

                        global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Anime episode mapping: '" +
                            (series?.Name ?? identity?.Title ?? "") + "' local S" + localSeason + "E" + localEpisode +
                            " -> MAL=" + mapped.MalId + " E" + mapped.MalEpisode + " via " + source +
                            " (TVDB=" + mapped.TvdbId + " S" + mapped.TvdbSeason + "E" + mapped.TvdbEpisode +
                            ", TMDb=" + mapped.TmdbId + " S" + mapped.TmdbSeason + "E" + mapped.TmdbEpisode + ").");
                        return mapped;
                    }
                }
            }

            // Fail closed for later seasons. A local S17 is not necessarily the 17th AniList sequel
            // and using the base MAL id would silently attach markers from the wrong anime.
            if (localSeason > 1)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Anime episode mapping unavailable: '" +
                    (series?.Name ?? identity?.Title ?? "") + "' local S" + localSeason + "E" + localEpisode +
                    ". Skipping AniSkip rather than using an unsafe base-MAL guess.");
                return null;
            }

            var directMal = identity?.MalId ?? 0;
            if (directMal <= 0 && identity?.AniListId > 0)
            {
                var directIdentity = await ResolveAniListByIdAsync(identity.AniListId, cancellationToken).ConfigureAwait(false);
                directMal = directIdentity?.MalId ?? 0;
            }
            if (directMal <= 0) return null;

            return new AnimeEpisodeMapping
            {
                MalId = directMal,
                AniListId = identity?.AniListId ?? 0,
                MalEpisode = localEpisode,
                TvdbId = identity?.TvdbId ?? 0,
                TvdbSeason = localSeason,
                TvdbEpisode = localEpisode,
                TmdbId = identity?.TmdbId ?? 0,
                TmdbSeason = localSeason,
                TmdbEpisode = localEpisode,
                MappingSource = "Direct MAL"
            };
        }

        private static AnimeExternalMappingEntry SelectAnimeMappingEntry(
            IEnumerable<AnimeExternalMappingEntry> candidates,
            int localEpisode,
            bool useTvdbOffset)
        {
            return (candidates ?? Enumerable.Empty<AnimeExternalMappingEntry>())
                .Where(x => x != null && x.MalId > 0)
                .Where(x => localEpisode - (useTvdbOffset ? x.TvdbOffset : x.TmdbOffset) > 0)
                .OrderByDescending(x => useTvdbOffset ? x.TvdbOffset : x.TmdbOffset)
                .ThenByDescending(x => string.Equals(x.Type, "TV", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault();
        }

        private async Task<IReadOnlyList<AnimeExternalMappingEntry>> GetAnimeMappingsAsync(CancellationToken cancellationToken)
        {
            if (animeMappings != null) return animeMappings;

            await animeMappingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (animeMappings != null) return animeMappings;

                string json = null;
                var canUseDiskCache = false;
                try
                {
                    if (File.Exists(animeMappingCachePath))
                    {
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(animeMappingCachePath);
                        canUseDiskCache = age <= AnimeMappingCacheLifetime;
                        if (canUseDiskCache) json = File.ReadAllText(animeMappingCachePath);
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        using (var response = await http.GetAsync(AnimeMappingUrl, cancellationToken).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                try
                                {
                                    var folder = Path.GetDirectoryName(animeMappingCachePath);
                                    if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                                    File.WriteAllText(animeMappingCachePath, json ?? string.Empty);
                                }
                                catch (Exception ex)
                                {
                                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] Failed to cache anime mapping dataset.");
                                }
                            }
                            else
                            {
                                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Anime mapping download HTTP " +
                                    (int)response.StatusCode + ".");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] Anime mapping download failed.");
                    }

                    // A stale mapping file is still much safer than guessing the wrong MAL id.
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        try
                        {
                            if (File.Exists(animeMappingCachePath)) json = File.ReadAllText(animeMappingCachePath);
                        }
                        catch { }
                    }
                }

                var parsed = ParseAnimeMappings(json);
                animeMappings = parsed;
                global::AnikiHelper.AnikiLog.Debug(logger, "[AnikiHelper][VideoCenter][IntroEnding] Anime mapping dataset ready: " +
                    parsed.Count + " entries" + (canUseDiskCache ? " (cache)." : "."));
                return animeMappings;
            }
            finally
            {
                animeMappingGate.Release();
            }
        }

        private static IReadOnlyList<AnimeExternalMappingEntry> ParseAnimeMappings(string json)
        {
            var output = new List<AnimeExternalMappingEntry>();
            if (string.IsNullOrWhiteSpace(json)) return output;
            try
            {
                var array = JArray.Parse(json);
                foreach (var item in array.OfType<JObject>())
                {
                    var malId = ParseInt(item["mal_id"]?.ToString());
                    if (malId <= 0) continue;
                    var season = item["season"] as JObject;
                    var offset = item["episode_offset"] as JObject;
                    var tmdb = item["themoviedb_id"] as JObject;
                    output.Add(new AnimeExternalMappingEntry
                    {
                        Type = item["type"]?.ToString() ?? string.Empty,
                        AniListId = ParseInt(item["anilist_id"]?.ToString()),
                        MalId = malId,
                        TvdbId = ParseInt(item["tvdb_id"]?.ToString()),
                        TmdbTvId = ParseInt(tmdb?["tv"]?.ToString()),
                        TvdbSeason = season?["tvdb"] == null ? -1 : ParseInt(season["tvdb"]?.ToString()),
                        TmdbSeason = season?["tmdb"] == null ? -1 : ParseInt(season["tmdb"]?.ToString()),
                        TvdbOffset = ParseSignedInt(offset?["tvdb"]?.ToString()),
                        TmdbOffset = ParseSignedInt(offset?["tmdb"]?.ToString())
                    });
                }
            }
            catch
            {
                return new List<AnimeExternalMappingEntry>();
            }
            return output;
        }

        private static int ParseSignedInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        private async Task<AniListIdentity> ResolveAniListByIdAsync(int aniListId, CancellationToken cancellationToken)
        {
            if (aniListId <= 0) return null;
            const string query = @"query ($id: Int) {
  Media(id: $id, type: ANIME) {
    id
    idMal
    title { romaji english }
    startDate { year }
    episodes
  }
}";
            var root = await PostAniListAsync(query, new JObject { ["id"] = aniListId }, cancellationToken).ConfigureAwait(false);
            return ParseAniListIdentity(root?["data"]?["Media"] as JObject);
        }

        private async Task<AniListIdentity> SearchAniListIdentityAsync(string title, int year, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            const string query = @"query ($search: String) {
  Page(page: 1, perPage: 8) {
    media(search: $search, type: ANIME, sort: SEARCH_MATCH) {
      id
      idMal
      title { romaji english native }
      synonyms
      startDate { year }
      episodes
      isAdult
    }
  }
}";
            var root = await PostAniListAsync(query, new JObject { ["search"] = title }, cancellationToken).ConfigureAwait(false);
            var media = root?["data"]?["Page"]?["media"] as JArray;
            if (media == null) return null;

            var wanted = NormalizeTitle(title);
            var candidates = media.OfType<JObject>()
                .Where(x => x["isAdult"]?.Value<bool>() != true)
                .Select(x => new
                {
                    Json = x,
                    Identity = ParseAniListIdentity(x),
                    Titles = GetAniListTitles(x)
                })
                .Where(x => x.Identity != null && x.Identity.MalId > 0)
                .Where(x => x.Titles.Any(t => string.Equals(NormalizeTitle(t), wanted, StringComparison.OrdinalIgnoreCase)))
                .Where(x => year <= 0 || x.Identity.Year <= 0 || x.Identity.Year == year)
                .OrderBy(x => year > 0 && x.Identity.Year == year ? 0 : 1)
                .ToList();

            return candidates.FirstOrDefault()?.Identity;
        }

        private async Task<JObject> PostAniListAsync(string query, JObject variables, CancellationToken cancellationToken)
        {
            var payload = new JObject
            {
                ["query"] = query,
                ["variables"] = variables ?? new JObject()
            };
            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using (var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync(AniListUrl, content, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
            }
        }

        private async Task<JObject> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using (var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
            }
        }

        private static AniListIdentity ParseAniListIdentity(JObject item)
        {
            if (item == null) return null;
            var title = item["title"]?["english"]?.ToString();
            if (string.IsNullOrWhiteSpace(title)) title = item["title"]?["romaji"]?.ToString();
            return new AniListIdentity
            {
                AniListId = ParseInt(item["id"]?.ToString()),
                MalId = ParseInt(item["idMal"]?.ToString()),
                Title = title ?? string.Empty,
                Year = ParseInt(item["startDate"]?["year"]?.ToString()),
                EpisodeCount = ParseInt(item["episodes"]?.ToString())
            };
        }

        private static IReadOnlyList<string> GetAniListTitles(JObject item)
        {
            var result = new List<string>();
            var title = item?["title"] as JObject;
            result.Add(title?["english"]?.ToString());
            result.Add(title?["romaji"]?.ToString());
            result.Add(title?["native"]?.ToString());
            var synonyms = item?["synonyms"] as JArray;
            if (synonyms != null) result.AddRange(synonyms.Values<string>());
            return result.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        private static MarkerLookupResult MergeResults(MarkerLookupResult primary, MarkerLookupResult fallback)
        {
            if (primary == null) return fallback;
            if (fallback == null) return primary;

            var merged = new MarkerLookupResult
            {
                RequestSucceeded = primary.RequestSucceeded || fallback.RequestSucceeded,
                RateLimited = primary.RateLimited || fallback.RateLimited,
                NotFound = primary.NotFound && fallback.NotFound,
                Source = string.Join(" + ", new[]
                    {
                        primary.HasAny ? primary.Source : string.Empty,
                        fallback.HasAny ? fallback.Source : string.Empty
                    }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)),
                Note = !string.IsNullOrWhiteSpace(primary.Note) ? primary.Note : fallback.Note,
                IntroStartMs = primary.HasIntro ? primary.IntroStartMs : fallback.IntroStartMs,
                IntroEndMs = primary.HasIntro ? primary.IntroEndMs : fallback.IntroEndMs,
                EndingStartMs = primary.HasEnding ? primary.EndingStartMs : fallback.EndingStartMs,
                EndingEndMs = primary.HasEnding ? primary.EndingEndMs : fallback.EndingEndMs
            };
            if (string.IsNullOrWhiteSpace(merged.Source))
            {
                merged.Source = string.Join(" + ", new[] { primary.Source, fallback.Source }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            if (merged.HasAny) merged.NotFound = false;
            return merged;
        }

        private AnikiVideoIntroEndingRecord BuildRecord(string path, MarkerLookupResult result, LookupIdentity identity)
        {
            var record = AnikiVideoIntroEndingStore.CreateEmptyForFile(path);
            record.Source = result?.Source ?? string.Empty;
            record.SourceReference = BuildSourceReference(identity);
            record.AnalyzedUtc = DateTime.UtcNow;

            if (result != null)
            {
                if (result.HasIntro)
                {
                    record.IntroStartMs = result.IntroStartMs;
                    record.IntroEndMs = result.IntroEndMs;
                    record.IntroConfidence = 0.95;
                }
                if (result.HasEnding)
                {
                    record.EndingStartMs = result.EndingStartMs;
                    record.EndingEndMs = result.EndingEndMs;
                    record.EndingConfidence = 0.95;
                }
            }

            if (record.HasIntro || record.HasEnding)
            {
                record.LookupStatus = "found";
                record.Note = string.Empty;
                record.RetryAfterUtc = record.HasIntro && record.HasEnding
                    ? DateTime.MaxValue
                    : DateTime.UtcNow.Add(MissingMarkersRetry);
            }
            else if (result?.RateLimited == true)
            {
                record.LookupStatus = "error";
                record.Note = Loc("VideoIntroEnding_RateLimited", "Marker service rate limit reached. Aniki will retry later.");
                record.RetryAfterUtc = DateTime.UtcNow.Add(RateLimitedRetry);
            }
            else if (result?.RequestSucceeded == true || result?.NotFound == true)
            {
                record.LookupStatus = "not_available";
                record.Note = Loc("VideoIntroEnding_NoOnlineMarkers", "No online intro/ending markers are available for this item yet.");
                record.RetryAfterUtc = DateTime.UtcNow.Add(MissingMarkersRetry);
            }
            else if (identity == null || (!identity.HasTheIntroDbId && identity.MalId <= 0 && identity.AniListId <= 0))
            {
                record.LookupStatus = "missing_id";
                record.Note = result?.Note ?? Loc("VideoIntroEnding_MissingIdentity", "No compatible provider ID is available yet for this media.");
                record.RetryAfterUtc = DateTime.UtcNow.Add(MissingIdentityRetry);
            }
            else
            {
                record.LookupStatus = "error";
                record.Note = result?.Note ?? Loc("VideoIntroEnding_LookupError", "The marker lookup could not be completed.");
                record.RetryAfterUtc = DateTime.UtcNow.Add(TemporaryErrorRetry);
            }

            return record;
        }

        private AnikiVideoIntroEndingRecord BuildTemporaryErrorRecord(string path, string note)
        {
            var record = AnikiVideoIntroEndingStore.CreateEmptyForFile(path, note ?? string.Empty);
            record.LookupStatus = "error";
            record.RetryAfterUtc = DateTime.UtcNow.Add(TemporaryErrorRetry);
            return record;
        }

        private void ApplyRecordToEpisode(AnikiVideoIntroEndingEpisodeItem episode, AnikiVideoIntroEndingRecord record)
        {
            if (episode == null) return;
            episode.IsAnalyzed = record != null;
            episode.HasIntro = record?.HasIntro == true;
            episode.HasEnding = record?.HasEnding == true;
            episode.IntroStartMs = record?.IntroStartMs ?? -1L;
            episode.IntroEndMs = record?.IntroEndMs ?? -1L;
            episode.EndingStartMs = record?.EndingStartMs ?? -1L;
            episode.IntroConfidence = record?.IntroConfidence ?? 0.0;
            episode.EndingConfidence = record?.EndingConfidence ?? 0.0;
            episode.SourceText = record?.Source ?? string.Empty;

            if (record == null)
                episode.StatusText = Loc("VideoIntroEnding_StatusNotAnalyzed", "Not checked");
            else if (record.HasIntro && record.HasEnding)
                episode.StatusText = Loc("VideoIntroEnding_StatusIntroEnding", "Intro + Ending");
            else if (record.HasIntro)
                episode.StatusText = Loc("VideoIntroEnding_StatusIntroOnly", "Intro only");
            else if (record.HasEnding)
                episode.StatusText = Loc("VideoIntroEnding_StatusEndingOnly", "Ending only");
            else if (string.Equals(record.LookupStatus, "error", StringComparison.OrdinalIgnoreCase))
                episode.StatusText = Loc("VideoIntroEnding_StatusLookupError", "Lookup failed");
            else if (string.Equals(record.LookupStatus, "missing_id", StringComparison.OrdinalIgnoreCase))
                episode.StatusText = Loc("VideoIntroEnding_StatusMissingId", "Waiting for metadata");
            else
                episode.StatusText = Loc("VideoIntroEnding_StatusNoMatch", "Not available");
        }

        private static void ParseIdsFromText(string value, LookupIdentity identity)
        {
            if (identity == null || string.IsNullOrWhiteSpace(value)) return;
            var imdb = ImdbIdRegex.Match(value);
            if (imdb.Success && string.IsNullOrWhiteSpace(identity.ImdbId)) identity.ImdbId = imdb.Groups["id"].Value;
            var tmdb = TmdbIdRegex.Match(value);
            if (tmdb.Success && identity.TmdbId <= 0) identity.TmdbId = ParseInt(tmdb.Groups["id"].Value);
            var tvdb = TvdbIdRegex.Match(value);
            if (tvdb.Success && identity.TvdbId <= 0) identity.TvdbId = ParseInt(tvdb.Groups["id"].Value);
        }

        private static string BuildSourceReference(LookupIdentity identity)
        {
            if (identity == null) return string.Empty;
            if (identity.TmdbId > 0) return "tmdb:" + identity.TmdbId.ToString(CultureInfo.InvariantCulture);
            if (identity.TvdbId > 0) return "tvdb:" + identity.TvdbId.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(identity.ImdbId)) return "imdb:" + identity.ImdbId;
            if (identity.MalId > 0) return "mal:" + identity.MalId.ToString(CultureInfo.InvariantCulture);
            if (identity.AniListId > 0) return "anilist:" + identity.AniListId.ToString(CultureInfo.InvariantCulture);
            return string.Empty;
        }

        private static string TruncateForLog(string value, int maxLength)
        {
            var text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (maxLength <= 0 || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "…";
        }

        private static string NormalizeTitle(string value)
        {
            var text = CleanupTitleRegex.Replace(value ?? string.Empty, " ").ToLowerInvariant();
            text = YearRegex.Replace(text, " ");
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c)) builder.Append(c);
            }
            return builder.ToString();
        }

        private async Task<double> ProbeLocalDurationSecondsAsync(string videoPath, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return 0.0;
                var ffprobe = ResolveFfprobePath();
                if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe)) return 0.0;

                var args = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"" + videoPath + "\"";
                var output = await RunProcessCaptureAsync(ffprobe, args, cancellationToken).ConfigureAwait(false);
                return ParseDouble((output ?? string.Empty).Trim());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] FFprobe duration read failed for '" + videoPath + "'.");
                return 0.0;
            }
        }

        private string ResolveFfprobePath()
        {
            var configured = CleanExecutablePath(settings?.VideoFfprobePath);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            var ffmpeg = CleanExecutablePath(settings?.VideoThumbnailFfmpegPath);
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                try
                {
                    var folder = Path.GetDirectoryName(ffmpeg);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        var sibling = Path.Combine(folder, "ffprobe.exe");
                        if (File.Exists(sibling)) return sibling;
                    }
                }
                catch { }
            }

            return string.Empty;
        }

        private static string CleanExecutablePath(string value)
        {
            return (value ?? string.Empty).Trim().Trim('"');
        }

        private static async Task<string> RunProcessCaptureAsync(
            string exePath,
            string arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                var exitTcs = new TaskCompletionSource<int>();
                process.Exited += (s, e) =>
                {
                    try { exitTcs.TrySetResult(process.ExitCode); }
                    catch { exitTcs.TrySetResult(-1); }
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                using (cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch { }
                }))
                {
                    var exitCode = await exitTcs.Task.ConfigureAwait(false);
                    var output = await outputTask.ConfigureAwait(false);
                    await errorTask.ConfigureAwait(false);
                    return exitCode == 0 ? (output ?? string.Empty) : string.Empty;
                }
            }
        }

        private static int ParseYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var match = YearRegex.Match(value);
            return match.Success ? ParseInt(match.Value) : 0;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
        }

        private static long ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : -1L;
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
        }

        private static double ParseDoubleToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0.0;

            // Read numeric JSON values directly. Going through JToken.ToString() first can
            // format decimals with the current Windows culture (for example 66,824 on fr-FR),
            // which then fails when ParseDouble expects invariant 66.824.
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                try { return token.Value<double>(); }
                catch { return 0.0; }
            }

            return ParseDouble(token.ToString());
        }

        private static long SecondsToMilliseconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0) return -1L;
            return (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        }

        private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
        {
            await requestPacingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                var delay = nextRequestUtc - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                // TheIntroDB allows 30 requests per 10 seconds for anonymous reads. Pace all
                // marker/identity lookups slightly below that ceiling so a first library scan does
                // not burst into HTTP 429 responses.
                nextRequestUtc = DateTime.UtcNow.AddMilliseconds(360);
            }
            finally
            {
                requestPacingGate.Release();
            }
        }

        private static void ReportProgress(IProgress<AnikiVideoIntroEndingProgress> progress, int current, int total, string message)
        {
            progress?.Report(new AnikiVideoIntroEndingProgress
            {
                Current = current,
                Total = Math.Max(1, total),
                Message = message ?? string.Empty
            });
        }

        private static string Loc(string key, string fallback)
        {
            try
            {
                var value = System.Windows.Application.Current?.TryFindResource(key) as string;
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { http?.Dispose(); } catch { }
            try { animeMappingGate?.Dispose(); } catch { }
            // Do not dispose networkGate: in-flight continuations may still be unwinding during
            // Playnite shutdown; it is tiny and dies with the service.
        }
    }
}
