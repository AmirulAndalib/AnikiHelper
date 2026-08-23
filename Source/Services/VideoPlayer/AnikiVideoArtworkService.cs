using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoArtworkInfo
    {
        public string Path { get; set; } = string.Empty;
        public bool IsPortrait { get; set; }
    }

    /// <summary>Resolves and caches user-provided local Video Center artwork.</summary>
    internal sealed class AnikiVideoArtworkService
    {
        private sealed class ArtworkCacheEntry
        {
            public string CachedFileName { get; set; } = string.Empty;
            public bool IsPortrait { get; set; }
            public string SourceFingerprint { get; set; } = string.Empty;
            public DateTime LastValidatedUtc { get; set; }
        }

        private const int HomeMaxDimension = 960;
        private const int PreviewMaxDimension = 1000;
        private const int FolderMaxDimension = 800;
        private const int FolderLandscapeMaxDimension = 1920;
        private const int JpegQuality = 88;
        private static readonly TimeSpan ValidationInterval = TimeSpan.FromHours(12);
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };

        private readonly ILogger logger;
        private readonly string cacheRoot;
        private readonly string indexPath;
        private readonly object indexSync = new object();
        private readonly object saveSync = new object();
        private readonly ConcurrentDictionary<string, byte> refreshInFlight =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> cacheLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ArtworkCacheEntry> cacheIndex =
            new Dictionary<string, ArtworkCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public AnikiVideoArtworkService(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            cacheRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "VideoCenter", "ArtworkCache");
            indexPath = Path.Combine(cacheRoot, "index.json");
            EnsureCacheDirectory();
            LoadIndex();
        }

        public Task<AnikiVideoArtworkInfo> ResolveHomeVideoArtworkAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            return ResolveVideoArtworkAsync(
                videoPath,
                scope: "home-v2",
                homeMode: true,
                maxDimension: HomeMaxDimension,
                cancellationToken);
        }

        public Task<AnikiVideoArtworkInfo> ResolveExplorerVideoArtworkAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            return ResolveVideoArtworkAsync(
                videoPath,
                scope: "preview",
                homeMode: false,
                maxDimension: PreviewMaxDimension,
                cancellationToken);
        }

        public AnikiVideoArtworkInfo GetCachedHomeVideoArtwork(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return null;
            try
            {
                var cacheKey = BuildLookupKey("home-v2", videoPath);
                return TryResolveCached(cacheKey, out var cached, out _, out _) ? cached : null;
            }
            catch { return null; }
        }

        public AnikiVideoArtworkInfo GetCachedExplorerVideoArtwork(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return null;
            try
            {
                var cacheKey = BuildLookupKey("preview", videoPath);
                return TryResolveCached(cacheKey, out var cached, out _, out _) ? cached : null;
            }
            catch { return null; }
        }

        public AnikiVideoArtworkInfo GetCachedFolderArtwork(string folderPath, bool preferLandscape)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return null;
            try
            {
                var cacheKey = BuildLookupKey(preferLandscape ? "folder-landscape-v1" : "folder-v2", folderPath);
                if (!TryResolveCached(cacheKey, out var cached, out _, out _) || cached == null) return null;
                if (preferLandscape && cached.IsPortrait) return null;
                if (!preferLandscape && !cached.IsPortrait) return null;
                return cached;
            }
            catch { return null; }
        }

        public Task<AnikiVideoArtworkInfo> ResolveFavoriteFolderArtworkAsync(
            string folderPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return Task.FromResult<AnikiVideoArtworkInfo>(null);
            }

            // v2 invalidates older folder-cache entries whose lookup order preferred landscape
            // before cover/poster. Movie/library folders now resolve portrait artwork first.
            var cacheKey = BuildLookupKey("folder-v2", folderPath);
            if (TryResolveCached(cacheKey, out var cached, out var shouldValidate, out var knownNegative))
            {
                if (shouldValidate)
                {
                    StartBackgroundFolderValidation(cacheKey, folderPath);
                }

                return Task.FromResult(cached);
            }

            if (knownNegative)
            {
                if (shouldValidate)
                {
                    StartBackgroundFolderValidation(cacheKey, folderPath);
                }
                return Task.FromResult<AnikiVideoArtworkInfo>(null);
            }

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RefreshFolderArtworkWithGate(cacheKey, folderPath, FolderMaxDimension, cancellationToken);
            }, cancellationToken);
        }

        public Task<AnikiVideoArtworkInfo> ResolveFavoriteFolderLandscapeArtworkAsync(
            string folderPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return Task.FromResult<AnikiVideoArtworkInfo>(null);
            }

            // Separate cache key from the portrait folder artwork. This lets a series keep both
            // cover/poster artwork and a full-width Hero wallpaper at the same time.
            var cacheKey = BuildLookupKey("folder-landscape-v1", folderPath);
            if (TryResolveCached(cacheKey, out var cached, out var shouldValidate, out var knownNegative))
            {
                if (shouldValidate)
                {
                    StartBackgroundFolderLandscapeValidation(cacheKey, folderPath);
                }

                return Task.FromResult(cached != null && !cached.IsPortrait ? cached : null);
            }

            if (knownNegative)
            {
                if (shouldValidate)
                {
                    StartBackgroundFolderLandscapeValidation(cacheKey, folderPath);
                }
                return Task.FromResult<AnikiVideoArtworkInfo>(null);
            }

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RefreshFolderLandscapeArtworkWithGate(
                    cacheKey,
                    folderPath,
                    FolderLandscapeMaxDimension,
                    cancellationToken);
            }, cancellationToken);
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
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
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
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to clear artwork cache.");
            }
        }

        private Task<AnikiVideoArtworkInfo> ResolveVideoArtworkAsync(
            string videoPath,
            string scope,
            bool homeMode,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return Task.FromResult<AnikiVideoArtworkInfo>(null);
            }

            var cacheKey = BuildLookupKey(scope, videoPath);
            if (TryResolveCached(cacheKey, out var cached, out var shouldValidate, out var knownNegative))
            {
                if (shouldValidate)
                {
                    StartBackgroundVideoValidation(cacheKey, videoPath, homeMode, maxDimension);
                }

                // This is the fast path after first use: no NAS/source artwork access is needed.
                return Task.FromResult(cached);
            }

            if (knownNegative)
            {
                if (shouldValidate)
                {
                    StartBackgroundVideoValidation(cacheKey, videoPath, homeMode, maxDimension);
                }
                return Task.FromResult<AnikiVideoArtworkInfo>(null);
            }

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RefreshVideoArtworkWithGate(cacheKey, videoPath, homeMode, maxDimension, cancellationToken);
            }, cancellationToken);
        }

        /// <summary>
        /// Returns true when a positive cache entry can be used immediately. knownNegative is true
        /// for a fresh negative cache entry so callers can immediately fall back to FFmpeg/folder UI.
        /// </summary>
        private bool TryResolveCached(
            string cacheKey,
            out AnikiVideoArtworkInfo info,
            out bool shouldValidate,
            out bool knownNegative)
        {
            info = null;
            shouldValidate = false;
            knownNegative = false;

            ArtworkCacheEntry entry;
            lock (indexSync)
            {
                cacheIndex.TryGetValue(cacheKey, out entry);
            }

            if (entry == null)
            {
                return false;
            }

            var stale = entry.LastValidatedUtc <= DateTime.MinValue ||
                        DateTime.UtcNow - entry.LastValidatedUtc >= ValidationInterval;

            if (string.IsNullOrWhiteSpace(entry.CachedFileName))
            {
                // A negative entry avoids repeating up to 24 network File.Exists calls on every
                // Home opening when a video has no sidecar artwork at all.
                if (!stale)
                {
                    knownNegative = true;
                    return false;
                }

                // Let the caller fall back immediately while validation runs silently.
                knownNegative = true;
                shouldValidate = true;
                return false;
            }

            var localPath = Path.Combine(cacheRoot, entry.CachedFileName);
            if (!File.Exists(localPath))
            {
                return false;
            }

            info = new AnikiVideoArtworkInfo
            {
                Path = localPath,
                IsPortrait = entry.IsPortrait
            };
            shouldValidate = stale;
            return true;
        }

        private AnikiVideoArtworkInfo RefreshVideoArtworkWithGate(
            string cacheKey,
            string videoPath,
            bool homeMode,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            gate.Wait(cancellationToken);
            try
            {
                // Another Home card may have populated the same cache while this request waited.
                if (TryResolveCached(cacheKey, out var cached, out _, out var negative) && cached != null)
                {
                    return cached;
                }
                if (negative)
                {
                    return null;
                }

                return RefreshVideoArtwork(cacheKey, videoPath, homeMode, maxDimension, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        private AnikiVideoArtworkInfo RefreshFolderLandscapeArtworkWithGate(
            string cacheKey,
            string folderPath,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            gate.Wait(cancellationToken);
            try
            {
                if (TryResolveCached(cacheKey, out var cached, out _, out var negative) && cached != null)
                {
                    return cached.IsPortrait ? null : cached;
                }
                if (negative)
                {
                    return null;
                }

                return RefreshFolderLandscapeArtwork(cacheKey, folderPath, maxDimension, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        private AnikiVideoArtworkInfo RefreshFolderArtworkWithGate(
            string cacheKey,
            string folderPath,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            gate.Wait(cancellationToken);
            try
            {
                if (TryResolveCached(cacheKey, out var cached, out _, out var negative) && cached != null)
                {
                    return cached;
                }
                if (negative)
                {
                    return null;
                }

                return RefreshFolderArtwork(cacheKey, folderPath, maxDimension, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        private AnikiVideoArtworkInfo RefreshVideoArtwork(
            string cacheKey,
            string videoPath,
            bool homeMode,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = FindVideoArtwork(videoPath, homeMode, cancellationToken, out var portraitHint);
                if (string.IsNullOrWhiteSpace(source))
                {
                    RememberNegative(cacheKey);
                    return null;
                }

                return CacheSourceArtwork(cacheKey, source, portraitHint, maxDimension, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Local artwork lookup/cache failed.");
                return TryGetPositiveCache(cacheKey);
            }
        }

        private AnikiVideoArtworkInfo RefreshFolderLandscapeArtwork(
            string cacheKey,
            string folderPath,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = FindFolderLandscapeArtwork(folderPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(source))
                {
                    RememberNegative(cacheKey);
                    return null;
                }

                var result = CacheSourceArtwork(cacheKey, source, false, maxDimension, cancellationToken);
                return result != null && !result.IsPortrait ? result : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Local folder landscape lookup/cache failed.");
                var cached = TryGetPositiveCache(cacheKey);
                return cached != null && !cached.IsPortrait ? cached : null;
            }
        }

        private AnikiVideoArtworkInfo RefreshFolderArtwork(
            string cacheKey,
            string folderPath,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = FindFolderArtwork(folderPath, cancellationToken, out var portraitHint);
                if (string.IsNullOrWhiteSpace(source))
                {
                    RememberNegative(cacheKey);
                    return null;
                }

                return CacheSourceArtwork(cacheKey, source, portraitHint, maxDimension, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Favorite-folder artwork lookup/cache failed.");
                return TryGetPositiveCache(cacheKey);
            }
        }

        private AnikiVideoArtworkInfo CacheSourceArtwork(
            string cacheKey,
            string sourcePath,
            bool portraitHint,
            int maxDimension,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo sourceInfo;
            try
            {
                sourceInfo = new FileInfo(sourcePath);
                if (!sourceInfo.Exists)
                {
                    return TryGetPositiveCache(cacheKey);
                }
            }
            catch
            {
                return TryGetPositiveCache(cacheKey);
            }

            var sourceFingerprint = BuildSourceFingerprint(sourceInfo);
            ArtworkCacheEntry existing;
            lock (indexSync)
            {
                cacheIndex.TryGetValue(cacheKey, out existing);
            }

            if (existing != null &&
                !string.IsNullOrWhiteSpace(existing.CachedFileName) &&
                string.Equals(existing.SourceFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                var existingPath = Path.Combine(cacheRoot, existing.CachedFileName);
                if (File.Exists(existingPath))
                {
                    existing.LastValidatedUtc = DateTime.UtcNow;
                    StoreEntry(cacheKey, existing);
                    return new AnikiVideoArtworkInfo
                    {
                        Path = existingPath,
                        IsPortrait = existing.IsPortrait
                    };
                }
            }

            EnsureCacheDirectory();
            var sourceExtension = (sourceInfo.Extension ?? string.Empty).ToLowerInvariant();
            var outputExtension = sourceExtension == ".png" ? ".png" : ".jpg";
            var cachedFileName = cacheKey + outputExtension;
            var cachePath = Path.Combine(cacheRoot, cachedFileName);
            var tempPath = cachePath + ".tmp";

            TryDelete(tempPath);
            var isPortrait = CreateOptimizedCopy(
                sourceInfo.FullName,
                tempPath,
                outputExtension,
                maxDimension,
                portraitHint,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(tempPath))
            {
                return TryGetPositiveCache(cacheKey);
            }

            // Delete an older cached file if the source format changed PNG <-> JPEG.
            if (existing != null &&
                !string.IsNullOrWhiteSpace(existing.CachedFileName) &&
                !string.Equals(existing.CachedFileName, cachedFileName, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(Path.Combine(cacheRoot, existing.CachedFileName));
            }

            TryDelete(cachePath);
            File.Move(tempPath, cachePath);

            var entry = new ArtworkCacheEntry
            {
                CachedFileName = cachedFileName,
                IsPortrait = isPortrait,
                SourceFingerprint = sourceFingerprint,
                LastValidatedUtc = DateTime.UtcNow
            };
            StoreEntry(cacheKey, entry);

            return new AnikiVideoArtworkInfo
            {
                Path = cachePath,
                IsPortrait = isPortrait
            };
        }

        private bool CreateOptimizedCopy(
            string sourcePath,
            string outputPath,
            string outputExtension,
            int maxDimension,
            bool portraitHint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int sourceWidth = 0;
            int sourceHeight = 0;
            try
            {
                using (var probeStream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    var decoder = BitmapDecoder.Create(
                        probeStream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.None);
                    var frame = decoder?.Frames != null && decoder.Frames.Count > 0
                        ? decoder.Frames[0]
                        : null;
                    if (frame != null)
                    {
                        sourceWidth = frame.PixelWidth;
                        sourceHeight = frame.PixelHeight;
                    }
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Could not inspect artwork dimensions.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            BitmapImage bitmap;
            using (var sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = sourceStream;

                if (sourceWidth > 0 && sourceHeight > 0 && maxDimension > 0)
                {
                    if (sourceWidth >= sourceHeight && sourceWidth > maxDimension)
                    {
                        bitmap.DecodePixelWidth = maxDimension;
                    }
                    else if (sourceHeight > sourceWidth && sourceHeight > maxDimension)
                    {
                        bitmap.DecodePixelHeight = maxDimension;
                    }
                }

                bitmap.EndInit();
                bitmap.Freeze();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var finalWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : sourceWidth;
            var finalHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : sourceHeight;
            var isPortrait = finalWidth > 0 && finalHeight > 0
                ? finalHeight > finalWidth * 1.08
                : portraitHint;

            BitmapEncoder encoder;
            if (string.Equals(outputExtension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                encoder = new PngBitmapEncoder();
            }
            else
            {
                encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            }

            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }

            return isPortrait;
        }

        private string FindVideoArtwork(
            string videoPath,
            bool homeMode,
            CancellationToken cancellationToken,
            out bool portraitHint)
        {
            portraitHint = false;
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return string.Empty;
            }

            string directory;
            string baseName;
            try
            {
                directory = Path.GetDirectoryName(videoPath);
                baseName = Path.GetFileNameWithoutExtension(videoPath);
            }
            catch
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
            {
                return string.Empty;
            }

            var stems = homeMode
                ? new[]
                {
                    // Home cards are 16:9: exhaust landscape/backdrop sidecars before any
                    // ambiguous or portrait artwork. Generic files are useful when each movie
                    // lives in its own folder (landscape.jpg/backdrop.jpg).
                    baseName + "-landscape",
                    baseName + "-backdrop",
                    "landscape",
                    "backdrop",
                    baseName + "-fanart1",
                    baseName + "-fanart2",
                    baseName + "-fanart3",
                    "fanart1",
                    "fanart2",
                    "fanart3",
                    baseName + "-banner",
                    "banner",
                    baseName
                }
                : new[]
                {
                    baseName + "-poster",
                    baseName + "-cover",
                    baseName,
                    baseName + "-landscape",
                    baseName + "-fanart1",
                    baseName + "-fanart2",
                    baseName + "-fanart3",
                    baseName + "-banner"
                };

            foreach (var stem in stems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = FindImage(directory, stem);
                if (string.IsNullOrWhiteSpace(found))
                {
                    continue;
                }

                portraitHint =
                    stem.EndsWith("-poster", StringComparison.OrdinalIgnoreCase) ||
                    stem.EndsWith("-cover", StringComparison.OrdinalIgnoreCase);
                return found;
            }

            return string.Empty;
        }

        private string FindFolderLandscapeArtwork(
            string folderPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            // Explicit Hero/landscape sidecars always win over online providers. Do not use
            // folder.jpg/cover.jpg/poster.jpg here because a portrait image must never become
            // the full-screen series Hero.
            var candidates = new[]
            {
                "landscape",
                "backdrop",
                "background",
                "fanart",
                "fanart1",
                "fanart2",
                "fanart3",
                "banner"
            };

            foreach (var stem in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = FindImage(folderPath, stem);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }

            return string.Empty;
        }

        private string FindFolderArtwork(
            string folderPath,
            CancellationToken cancellationToken,
            out bool portraitHint)
        {
            portraitHint = false;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return string.Empty;
            }

            // Explorer/library folders prefer a real vertical cover/poster. folder.jpg remains
            // the generic fallback, while landscape.jpg is intentionally last for 16:9 Home use.
            var candidates = new[] { "cover", "poster", "folder", "landscape" };
            foreach (var stem in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = FindImage(folderPath, stem);
                if (string.IsNullOrWhiteSpace(found))
                {
                    continue;
                }

                portraitHint = string.Equals(stem, "poster", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase);
                return found;
            }

            return string.Empty;
        }

        private static string FindImage(string directory, string stem)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
            {
                return string.Empty;
            }

            foreach (var extension in ImageExtensions)
            {
                try
                {
                    var path = Path.Combine(directory, stem + extension);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private void StartBackgroundVideoValidation(
            string cacheKey,
            string videoPath,
            bool homeMode,
            int maxDimension)
        {
            if (!refreshInFlight.TryAdd(cacheKey, 0))
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    gate.Wait();
                    try
                    {
                        RefreshVideoArtwork(cacheKey, videoPath, homeMode, maxDimension, CancellationToken.None);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Background artwork validation failed.");
                }
                finally
                {
                    refreshInFlight.TryRemove(cacheKey, out _);
                }
            });
        }

        private void StartBackgroundFolderLandscapeValidation(string cacheKey, string folderPath)
        {
            if (!refreshInFlight.TryAdd(cacheKey, 0))
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    gate.Wait();
                    try
                    {
                        RefreshFolderLandscapeArtwork(
                            cacheKey,
                            folderPath,
                            FolderLandscapeMaxDimension,
                            CancellationToken.None);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Background folder landscape validation failed.");
                }
                finally
                {
                    refreshInFlight.TryRemove(cacheKey, out _);
                }
            });
        }

        private void StartBackgroundFolderValidation(string cacheKey, string folderPath)
        {
            if (!refreshInFlight.TryAdd(cacheKey, 0))
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var gate = cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                    gate.Wait();
                    try
                    {
                        RefreshFolderArtwork(cacheKey, folderPath, FolderMaxDimension, CancellationToken.None);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Background folder-art validation failed.");
                }
                finally
                {
                    refreshInFlight.TryRemove(cacheKey, out _);
                }
            });
        }

        private AnikiVideoArtworkInfo TryGetPositiveCache(string cacheKey)
        {
            ArtworkCacheEntry entry;
            lock (indexSync)
            {
                cacheIndex.TryGetValue(cacheKey, out entry);
            }

            if (entry == null || string.IsNullOrWhiteSpace(entry.CachedFileName))
            {
                return null;
            }

            var path = Path.Combine(cacheRoot, entry.CachedFileName);
            return File.Exists(path)
                ? new AnikiVideoArtworkInfo { Path = path, IsPortrait = entry.IsPortrait }
                : null;
        }

        private void RememberNegative(string cacheKey)
        {
            // Never replace a usable positive cache just because a NAS/share is temporarily offline.
            ArtworkCacheEntry existing;
            lock (indexSync)
            {
                cacheIndex.TryGetValue(cacheKey, out existing);
            }

            if (existing != null && !string.IsNullOrWhiteSpace(existing.CachedFileName))
            {
                return;
            }

            StoreEntry(cacheKey, new ArtworkCacheEntry
            {
                CachedFileName = string.Empty,
                IsPortrait = false,
                SourceFingerprint = string.Empty,
                LastValidatedUtc = DateTime.UtcNow
            });
        }

        private void StoreEntry(string cacheKey, ArtworkCacheEntry entry)
        {
            lock (indexSync)
            {
                cacheIndex[cacheKey] = entry;
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
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, ArtworkCacheEntry>>(json);
                if (loaded == null)
                {
                    return;
                }

                lock (indexSync)
                {
                    cacheIndex = new Dictionary<string, ArtworkCacheEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to load artwork-cache index.");
                lock (indexSync)
                {
                    cacheIndex = new Dictionary<string, ArtworkCacheEntry>(StringComparer.OrdinalIgnoreCase);
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
                    Dictionary<string, ArtworkCacheEntry> snapshot;
                    lock (indexSync)
                    {
                        snapshot = new Dictionary<string, ArtworkCacheEntry>(cacheIndex, StringComparer.OrdinalIgnoreCase);
                    }

                    var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                    var temp = indexPath + ".tmp";
                    File.WriteAllText(temp, json);
                    TryDelete(indexPath);
                    File.Move(temp, indexPath);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to save artwork-cache index.");
                }
            }
        }

        private void EnsureCacheDirectory()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(cacheRoot))
                {
                    Directory.CreateDirectory(cacheRoot);
                }
            }
            catch
            {
            }
        }

        private static string BuildLookupKey(string scope, string sourceIdentity)
        {
            var normalized = NormalizePath(sourceIdentity);
            return Sha256Hex((scope ?? string.Empty) + "|" + normalized);
        }

        private static string BuildSourceFingerprint(FileInfo file)
        {
            try
            {
                var raw = string.Concat(
                    NormalizePath(file.FullName),
                    "|",
                    file.Length.ToString(CultureInfo.InvariantCulture),
                    "|",
                    file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                return Sha256Hex(raw);
            }
            catch
            {
                return Sha256Hex(NormalizePath(file?.FullName));
            }
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
    }
}
