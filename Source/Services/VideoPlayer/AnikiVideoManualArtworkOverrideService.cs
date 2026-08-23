using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoManualArtworkOverrideService : IDisposable
    {
        private sealed class OverrideEntry
        {
            public string CoverFileName { get; set; } = string.Empty;
            public string LandscapeFileName { get; set; } = string.Empty;
            public string HeroFileName { get; set; } = string.Empty;
            public string LogoFileName { get; set; } = string.Empty;
            public string CoverSource { get; set; } = string.Empty;
            public string LandscapeSource { get; set; } = string.Empty;
            public string HeroSource { get; set; } = string.Empty;
            public string LogoSource { get; set; } = string.Empty;
            public DateTime UpdatedUtc { get; set; }
        }

        public const string Cover = "cover";
        public const string Landscape = "landscape";
        public const string Hero = "hero";
        public const string Logo = "logo";

        private const int CoverMaxDimension = 1200;
        private const int LandscapeMaxDimension = 1600;
        private const int HeroMaxDimension = 1920;
        private const int LogoMaxDimension = 1200;
        private const int JpegQuality = 90;

        private readonly ILogger logger;
        private readonly HttpClient http;
        private readonly string cacheRoot;
        private readonly string indexPath;
        private readonly object sync = new object();
        private Dictionary<string, OverrideEntry> index = new Dictionary<string, OverrideEntry>(StringComparer.OrdinalIgnoreCase);

        public AnikiVideoManualArtworkOverrideService(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            cacheRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "VideoCenter", "ManualArtworkOverrides");
            indexPath = Path.Combine(cacheRoot, "index.json");
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnikiHelper-VideoCenter/1.0");
            EnsureDirectory();
            LoadIndex();
        }

        public AnikiVideoArtworkInfo GetArtwork(string mediaPath, string target)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return null;
            var key = BuildKey(mediaPath);
            OverrideEntry entry;
            lock (sync)
            {
                index.TryGetValue(key, out entry);
            }
            if (entry == null) return null;
            var fileName = GetFileName(entry, target);
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var path = Path.Combine(cacheRoot, fileName);
            if (!File.Exists(path)) return null;
            return new AnikiVideoArtworkInfo
            {
                Path = path,
                IsPortrait = string.Equals(NormalizeTarget(target), Cover, StringComparison.OrdinalIgnoreCase)
            };
        }

        public bool HasArtwork(string mediaPath, string target)
        {
            return !string.IsNullOrWhiteSpace(GetArtwork(mediaPath, target)?.Path);
        }

        public string GetSource(string mediaPath, string target)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return string.Empty;
            var key = BuildKey(mediaPath);
            OverrideEntry entry;
            lock (sync)
            {
                index.TryGetValue(key, out entry);
            }
            return GetSource(entry, target);
        }

        public bool RemoveArtwork(string mediaPath, string target)
        {
            if (string.IsNullOrWhiteSpace(mediaPath)) return false;
            var key = BuildKey(mediaPath);
            string fileName = string.Empty;
            var changed = false;
            lock (sync)
            {
                if (!index.TryGetValue(key, out var entry) || entry == null) return false;
                fileName = GetFileName(entry, target);
                if (string.IsNullOrWhiteSpace(fileName)) return false;
                SetFileName(entry, target, string.Empty);
                SetSource(entry, target, string.Empty);
                entry.UpdatedUtc = DateTime.UtcNow;
                if (HasAnyArtwork(entry)) index[key] = entry;
                else index.Remove(key);
                SaveIndexLocked();
                changed = true;
            }
            if (changed && !string.IsNullOrWhiteSpace(fileName)) TryDelete(Path.Combine(cacheRoot, fileName));
            return changed;
        }

        public async Task<AnikiVideoArtworkInfo> ImportLocalAsync(
            string mediaPath,
            string target,
            string sourcePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
            byte[] bytes;
            try
            {
                bytes = await Task.Run(() => File.ReadAllBytes(sourcePath), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][ManualArtwork] Failed to read local image.");
                return null;
            }
            return await ImportBytesAsync(mediaPath, target, bytes, "local", cancellationToken).ConfigureAwait(false);
        }

        public async Task<AnikiVideoArtworkInfo> ImportProviderPreviewAsync(
            string mediaPath,
            string target,
            string previewPath,
            string remoteUrl,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(previewPath) || !File.Exists(previewPath)) return null;
            byte[] bytes;
            try
            {
                bytes = await Task.Run(() => File.ReadAllBytes(previewPath), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][ManualArtwork] Failed to read provider preview.");
                return null;
            }
            var source = string.IsNullOrWhiteSpace(remoteUrl) ? string.Empty : "remote:" + remoteUrl;
            return await ImportBytesAsync(mediaPath, target, bytes, source, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AnikiVideoArtworkInfo> ImportRemoteAsync(
            string mediaPath,
            string target,
            string remoteUrl,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, remoteUrl))
                using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return await ImportBytesAsync(mediaPath, target, bytes, "remote:" + remoteUrl, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][ManualArtwork] Failed to download selected image.");
                return null;
            }
        }

        private async Task<AnikiVideoArtworkInfo> ImportBytesAsync(
            string mediaPath,
            string target,
            byte[] bytes,
            string source,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || bytes == null || bytes.Length == 0) return null;
            var normalizedTarget = NormalizeTarget(target);
            var key = BuildKey(mediaPath);
            var version = DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
            var isLogo = string.Equals(normalizedTarget, Logo, StringComparison.OrdinalIgnoreCase);
            var fileName = key + "." + normalizedTarget + ".manual." + version + (isLogo ? ".png" : ".jpg");
            var destination = Path.Combine(cacheRoot, fileName);
            var temp = destination + ".tmp";
            try
            {
                EnsureDirectory();
                TryDelete(temp);
                var maxDimension = normalizedTarget == Cover
                    ? CoverMaxDimension
                    : (normalizedTarget == Hero ? HeroMaxDimension : (normalizedTarget == Logo ? LogoMaxDimension : LandscapeMaxDimension));
                if (isLogo)
                {
                    await Task.Run(() => CreateOptimizedPng(bytes, temp, maxDimension, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => CreateOptimizedJpeg(bytes, temp, maxDimension, cancellationToken), cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(temp)) return null;
                File.Move(temp, destination);

                OverrideEntry entry;
                lock (sync)
                {
                    if (!index.TryGetValue(key, out entry) || entry == null)
                    {
                        entry = new OverrideEntry();
                    }
                    SetFileName(entry, normalizedTarget, fileName);
                    SetSource(entry, normalizedTarget, source ?? string.Empty);
                    entry.UpdatedUtc = DateTime.UtcNow;
                    index[key] = entry;
                    SaveIndexLocked();
                }

                return new AnikiVideoArtworkInfo
                {
                    Path = destination,
                    IsPortrait = normalizedTarget == Cover
                };
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][ManualArtwork] Failed to import selected image.");
                return null;
            }
        }

        private static string NormalizeTarget(string target)
        {
            var value = (target ?? string.Empty).Trim().ToLowerInvariant();
            if (value == Landscape || value == Hero || value == Logo) return value;
            return Cover;
        }

        private static string GetFileName(OverrideEntry entry, string target)
        {
            if (entry == null) return string.Empty;
            switch (NormalizeTarget(target))
            {
                case Landscape: return entry.LandscapeFileName ?? string.Empty;
                case Hero: return entry.HeroFileName ?? string.Empty;
                case Logo: return entry.LogoFileName ?? string.Empty;
                default: return entry.CoverFileName ?? string.Empty;
            }
        }

        private static void SetFileName(OverrideEntry entry, string target, string fileName)
        {
            switch (NormalizeTarget(target))
            {
                case Landscape: entry.LandscapeFileName = fileName ?? string.Empty; break;
                case Hero: entry.HeroFileName = fileName ?? string.Empty; break;
                case Logo: entry.LogoFileName = fileName ?? string.Empty; break;
                default: entry.CoverFileName = fileName ?? string.Empty; break;
            }
        }

        private static string GetSource(OverrideEntry entry, string target)
        {
            if (entry == null) return string.Empty;
            switch (NormalizeTarget(target))
            {
                case Landscape: return entry.LandscapeSource ?? string.Empty;
                case Hero: return entry.HeroSource ?? string.Empty;
                case Logo: return entry.LogoSource ?? string.Empty;
                default: return entry.CoverSource ?? string.Empty;
            }
        }

        private static void SetSource(OverrideEntry entry, string target, string source)
        {
            switch (NormalizeTarget(target))
            {
                case Landscape: entry.LandscapeSource = source ?? string.Empty; break;
                case Hero: entry.HeroSource = source ?? string.Empty; break;
                case Logo: entry.LogoSource = source ?? string.Empty; break;
                default: entry.CoverSource = source ?? string.Empty; break;
            }
        }

        private static bool HasAnyArtwork(OverrideEntry entry)
        {
            return entry != null &&
                   (!string.IsNullOrWhiteSpace(entry.CoverFileName) ||
                    !string.IsNullOrWhiteSpace(entry.LandscapeFileName) ||
                    !string.IsNullOrWhiteSpace(entry.HeroFileName) ||
                    !string.IsNullOrWhiteSpace(entry.LogoFileName));
        }

        private static string BuildKey(string mediaPath)
        {
            string normalized;
            try { normalized = Path.GetFullPath(mediaPath).Trim().ToUpperInvariant(); }
            catch { normalized = (mediaPath ?? string.Empty).Trim().ToUpperInvariant(); }
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes("manual-art|" + normalized));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private void EnsureDirectory()
        {
            try { Directory.CreateDirectory(cacheRoot); } catch { }
        }

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(indexPath)) return;
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, OverrideEntry>>(File.ReadAllText(indexPath));
                if (loaded == null) return;
                lock (sync)
                {
                    index = new Dictionary<string, OverrideEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][ManualArtwork] Failed to load index.");
            }
        }

        private void SaveIndexLocked()
        {
            try
            {
                EnsureDirectory();
                var temp = indexPath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(index, Formatting.Indented));
                if (File.Exists(indexPath)) File.Delete(indexPath);
                File.Move(temp, indexPath);
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][ManualArtwork] Failed to save index.");
            }
        }

        private static void CreateOptimizedJpeg(byte[] imageBytes, string outputPath, int maxDimension, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
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
            cancellationToken.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose()
        {
            try { http.Dispose(); } catch { }
        }
    }
}
