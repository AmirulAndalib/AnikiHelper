using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoThumbnailInfo
    {
        public string ThumbnailPath { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
    }

    internal sealed class AnikiVideoThumbnailService
    {
        private static readonly Regex DurationRegex = new Regex(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})(?:[\.,](\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly global::AnikiHelper.AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly string cacheRoot;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> generationLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        public AnikiVideoThumbnailService(global::AnikiHelper.AnikiHelperSettings settings, string pluginUserDataPath, ILogger logger)
        {
            this.settings = settings;
            this.logger = logger ?? LogManager.GetLogger();
            cacheRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "VideoCenter", "Thumbnails");
            EnsureCacheDirectory();
        }

        public bool IsEnabled => File.Exists(GetFfmpegPath());
        public string FfmpegPath => GetFfmpegPath();
        public string CacheRoot => cacheRoot;

        public async Task<string> GetOrCreateThumbnailAsync(string videoPath, CancellationToken cancellationToken)
        {
            var info = await GetOrCreateThumbnailInfoAsync(videoPath, cancellationToken).ConfigureAwait(false);
            return info?.ThumbnailPath ?? string.Empty;
        }

        public string GetCachedThumbnailPath(string videoPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                {
                    return string.Empty;
                }

                var cachePath = GetCachePath(videoPath);
                return File.Exists(cachePath) ? cachePath : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<AnikiVideoThumbnailInfo> GetOrCreateThumbnailInfoAsync(string videoPath, CancellationToken cancellationToken)
        {
            var resultInfo = new AnikiVideoThumbnailInfo();
            try
            {
                if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                {
                    return resultInfo;
                }

                var ffmpegPath = GetFfmpegPath();
                if (!File.Exists(ffmpegPath))
                {
                    return resultInfo;
                }

                EnsureCacheDirectory();
                var cachePath = GetCachePath(videoPath);
                var durationPath = cachePath + ".duration";

                if (File.Exists(cachePath))
                {
                    resultInfo.ThumbnailPath = cachePath;
                    resultInfo.DurationSeconds = ReadCachedDuration(durationPath);
                    if (resultInfo.DurationSeconds <= 0.0)
                    {
                        resultInfo.DurationSeconds = await TryGetDurationSecondsAsync(ffmpegPath, videoPath, cancellationToken).ConfigureAwait(false);
                        WriteCachedDuration(durationPath, resultInfo.DurationSeconds);
                    }
                    return resultInfo;
                }

                var gate = generationLocks.GetOrAdd(cachePath, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(cachePath))
                    {
                        resultInfo.ThumbnailPath = cachePath;
                        resultInfo.DurationSeconds = ReadCachedDuration(durationPath);
                        if (resultInfo.DurationSeconds <= 0.0)
                        {
                            resultInfo.DurationSeconds = await TryGetDurationSecondsAsync(ffmpegPath, videoPath, cancellationToken).ConfigureAwait(false);
                            WriteCachedDuration(durationPath, resultInfo.DurationSeconds);
                        }
                        return resultInfo;
                    }

                    var durationSeconds = await TryGetDurationSecondsAsync(ffmpegPath, videoPath, cancellationToken).ConfigureAwait(false);
                    var captureSeconds = ComputeCaptureSecond(durationSeconds);
                    var tempOutput = cachePath + ".tmp.jpg";
                    TryDelete(tempOutput);

                    var args = string.Format(CultureInfo.InvariantCulture,
                        "-y -ss {0:0.###} -i \"{1}\" -frames:v 1 -vf \"scale=640:-1:force_original_aspect_ratio=decrease\" -q:v 4 \"{2}\"",
                        captureSeconds, videoPath, tempOutput);

                    var processResult = await RunProcessCaptureAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
                    if (processResult.ExitCode == 0 && File.Exists(tempOutput))
                    {
                        File.Move(tempOutput, cachePath);
                        WriteCachedDuration(durationPath, durationSeconds);
                        resultInfo.ThumbnailPath = cachePath;
                        resultInfo.DurationSeconds = durationSeconds;
                        return resultInfo;
                    }

                    TryDelete(tempOutput);
                    global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter] Thumbnail generation failed for '{videoPath}'. Exit={processResult.ExitCode}. Error={processResult.Error}");
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to build thumbnail.");
            }

            return resultInfo;
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
                if (!Directory.Exists(cacheRoot))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    TryDelete(file);
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Failed to clear thumbnail cache.");
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

        private string GetFfmpegPath()
        {
            return (settings?.VideoThumbnailFfmpegPath ?? string.Empty).Trim().Trim('"');
        }

        private string GetCachePath(string videoPath)
        {
            var file = new FileInfo(videoPath);
            var input = string.Concat(file.FullName, "|", file.Length.ToString(CultureInfo.InvariantCulture), "|", file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return Path.Combine(cacheRoot, sb.ToString() + ".jpg");
            }
        }

        private static double ComputeCaptureSecond(double durationSeconds)
        {
            if (durationSeconds > 0.0)
            {
                var target = durationSeconds * 0.10;
                if (durationSeconds <= 12.0)
                {
                    return Math.Max(1.0, Math.Min(durationSeconds - 0.25, target));
                }

                return Math.Max(3.0, Math.Min(durationSeconds - 1.0, target));
            }

            return 10.0;
        }

        private async Task<double> TryGetDurationSecondsAsync(string ffmpegPath, string videoPath, CancellationToken cancellationToken)
        {
            var result = await RunProcessCaptureAsync(ffmpegPath, string.Format(CultureInfo.InvariantCulture, "-i \"{0}\"", videoPath), cancellationToken).ConfigureAwait(false);
            var match = DurationRegex.Match(result.Error ?? string.Empty);
            if (!match.Success)
            {
                match = DurationRegex.Match(result.Output ?? string.Empty);
            }

            if (!match.Success)
            {
                return 0.0;
            }

            var hours = SafeParseInt(match.Groups[1].Value);
            var minutes = SafeParseInt(match.Groups[2].Value);
            var seconds = SafeParseInt(match.Groups[3].Value);
            var fractionText = match.Groups[4].Value;
            var fraction = 0.0;
            if (!string.IsNullOrWhiteSpace(fractionText))
            {
                double.TryParse("0." + fractionText, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
            }

            return (hours * 3600.0) + (minutes * 60.0) + seconds + fraction;
        }

        private static double ReadCachedDuration(string path)
        {
            try
            {
                if (File.Exists(path) && double.TryParse(File.ReadAllText(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                {
                    return Math.Max(0.0, duration);
                }
            }
            catch
            {
            }
            return 0.0;
        }

        private static void WriteCachedDuration(string path, double durationSeconds)
        {
            if (durationSeconds <= 0.0)
            {
                return;
            }

            try
            {
                File.WriteAllText(path, durationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            catch
            {
            }
        }

        private static int SafeParseInt(string value)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static async Task<ProcessResult> RunProcessCaptureAsync(string exePath, string arguments, CancellationToken cancellationToken)
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
                var tcs = new TaskCompletionSource<int>();
                process.Exited += (s, e) =>
                {
                    try { tcs.TrySetResult(process.ExitCode); } catch { tcs.TrySetResult(-1); }
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                    }
                }))
                {
                    var exitCode = await tcs.Task.ConfigureAwait(false);
                    var output = await outputTask.ConfigureAwait(false);
                    var error = await errorTask.ConfigureAwait(false);
                    return new ProcessResult(exitCode, output, error);
                }
            }
        }

        private sealed class ProcessResult
        {
            public ProcessResult(int exitCode, string output, string error)
            {
                ExitCode = exitCode;
                Output = output ?? string.Empty;
                Error = error ?? string.Empty;
            }

            public int ExitCode { get; }
            public string Output { get; }
            public string Error { get; }
        }
    }
}
