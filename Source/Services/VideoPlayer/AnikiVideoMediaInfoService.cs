using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoMediaInfo
    {
        public double DurationSeconds { get; set; }
        public string QualityText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lightweight ffprobe media-info reader used only for the selected Explorer preview.
    /// Results are cached by path/size/mtime so moving focus back to a file doesn't spawn ffprobe again.
    /// </summary>
    internal sealed class AnikiVideoMediaInfoService
    {
        private readonly Func<string> ffprobePathResolver;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<string, AnikiVideoMediaInfo> cache =
            new ConcurrentDictionary<string, AnikiVideoMediaInfo>(StringComparer.OrdinalIgnoreCase);

        public AnikiVideoMediaInfoService(Func<string> ffprobePathResolver, ILogger logger)
        {
            this.ffprobePathResolver = ffprobePathResolver;
            this.logger = logger ?? LogManager.GetLogger();
        }

        public async Task<AnikiVideoMediaInfo> ProbeAsync(string videoPath, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                {
                    return null;
                }

                var ffprobe = ffprobePathResolver?.Invoke() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(ffprobe) || !File.Exists(ffprobe))
                {
                    return null;
                }

                var cacheKey = BuildCacheKey(videoPath);
                if (cache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                var args = "-v error -print_format json -show_format -show_streams \"" + videoPath + "\"";
                var json = await RunProcessCaptureAsync(ffprobe, args, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                var root = JObject.Parse(json);
                var streams = root["streams"] as JArray;

                var video = streams?
                    .OfType<JObject>()
                    .FirstOrDefault(stream =>
                        string.Equals(stream["codec_type"]?.ToString(), "video", StringComparison.OrdinalIgnoreCase));

                var audio = streams?
                    .OfType<JObject>()
                    .FirstOrDefault(stream =>
                        string.Equals(stream["codec_type"]?.ToString(), "audio", StringComparison.OrdinalIgnoreCase));

                var info = new AnikiVideoMediaInfo
                {
                    DurationSeconds = ParseDouble(root["format"]?["duration"]?.ToString()),
                    QualityText = BuildQualityText(video, audio)
                };

                cache[cacheKey] = info;
                return info;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] FFprobe media-info read failed.");
                return null;
            }
        }

        private static string BuildQualityText(JObject video, JObject audio)
        {
            var parts = new List<string>();

            if (video != null)
            {
                var width = ParseInt(video["width"]?.ToString());
                var height = ParseInt(video["height"]?.ToString());

                var resolution = FormatResolution(width, height);
                if (!string.IsNullOrWhiteSpace(resolution))
                {
                    parts.Add(resolution);
                }

                var videoCodec = FormatVideoCodec(video["codec_name"]?.ToString());
                if (!string.IsNullOrWhiteSpace(videoCodec))
                {
                    parts.Add(videoCodec);
                }

                var hdr = DetectHdr(video);
                if (!string.IsNullOrWhiteSpace(hdr))
                {
                    parts.Add(hdr);
                }
            }

            if (audio != null)
            {
                var audioCodec = FormatAudioCodec(
                    audio["codec_name"]?.ToString(),
                    audio["profile"]?.ToString());
                var channels = FormatChannels(
                    ParseInt(audio["channels"]?.ToString()),
                    audio["channel_layout"]?.ToString());

                var audioPart = string.Join(" ", new[] { audioCodec, channels }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

                if (!string.IsNullOrWhiteSpace(audioPart))
                {
                    parts.Add(audioPart);
                }
            }

            return string.Join("  ·  ", parts);
        }

        private static string FormatResolution(int width, int height)
        {
            if (width <= 0 && height <= 0)
            {
                return string.Empty;
            }

            // Width is intentionally considered as well as height because cropped cinemascope
            // encodes commonly report 1920x800 / 3840x1600 while still being 1080p / 4K sources.
            if (width >= 3800 || height >= 2100)
            {
                return "4K";
            }

            if (width >= 2500 || height >= 1400)
            {
                return "1440p";
            }

            if (width >= 1900 || height >= 1000)
            {
                return "1080p";
            }

            if (width >= 1250 || height >= 700)
            {
                return "720p";
            }

            if (height >= 560)
            {
                return "576p";
            }

            if (height >= 450)
            {
                return "480p";
            }

            return height > 0
                ? height.ToString(CultureInfo.InvariantCulture) + "p"
                : width.ToString(CultureInfo.InvariantCulture) + "px";
        }

        private static string FormatVideoCodec(string codec)
        {
            switch ((codec ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "hevc":
                case "h265":
                    return "HEVC";
                case "h264":
                    return "H.264";
                case "av1":
                    return "AV1";
                case "vp9":
                    return "VP9";
                case "vp8":
                    return "VP8";
                case "mpeg2video":
                    return "MPEG-2";
                case "mpeg4":
                    return "MPEG-4";
                case "vc1":
                    return "VC-1";
                default:
                    return string.IsNullOrWhiteSpace(codec)
                        ? string.Empty
                        : codec.Trim().ToUpperInvariant();
            }
        }

        private static string FormatAudioCodec(string codec, string profile)
        {
            var codecValue = (codec ?? string.Empty).Trim().ToLowerInvariant();
            var profileValue = (profile ?? string.Empty).Trim();

            switch (codecValue)
            {
                case "ac3":
                    return "AC3";
                case "eac3":
                    return "E-AC3";
                case "truehd":
                    return "TrueHD";
                case "dts":
                    if (!string.IsNullOrWhiteSpace(profileValue))
                    {
                        var lowerProfile = profileValue.ToLowerInvariant();
                        if (lowerProfile.Contains("dts-hd ma") ||
                            lowerProfile.Contains("dts hd ma") ||
                            lowerProfile.Contains("master audio"))
                        {
                            return "DTS-HD MA";
                        }

                        if (lowerProfile.Contains("dts-hd hra") ||
                            lowerProfile.Contains("dts hd hra") ||
                            lowerProfile.Contains("high resolution"))
                        {
                            return "DTS-HD HRA";
                        }
                    }
                    return "DTS";
                case "aac":
                    return "AAC";
                case "flac":
                    return "FLAC";
                case "opus":
                    return "Opus";
                case "mp3":
                    return "MP3";
                case "vorbis":
                    return "Vorbis";
                case "pcm_s16le":
                case "pcm_s24le":
                case "pcm_s32le":
                case "pcm_bluray":
                    return "PCM";
                default:
                    return string.IsNullOrWhiteSpace(codec)
                        ? string.Empty
                        : codec.Trim().ToUpperInvariant();
            }
        }

        private static string DetectHdr(JObject video)
        {
            try
            {
                var sideData = video["side_data_list"]?.ToString().ToLowerInvariant() ?? string.Empty;
                var profile = video["profile"]?.ToString().ToLowerInvariant() ?? string.Empty;

                if (sideData.Contains("dovi") ||
                    sideData.Contains("dolby vision") ||
                    profile.Contains("dolby vision"))
                {
                    return "Dolby Vision";
                }

                var transfer = (video["color_transfer"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
                if (transfer == "smpte2084")
                {
                    return "HDR";
                }

                if (transfer == "arib-std-b67")
                {
                    return "HLG";
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string FormatChannels(int channels, string layout)
        {
            var normalized = (layout ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains("7.1"))
            {
                return "7.1";
            }

            if (normalized.Contains("5.1"))
            {
                return "5.1";
            }

            if (normalized.Contains("stereo"))
            {
                return "2.0";
            }

            if (normalized.Contains("mono"))
            {
                return "1.0";
            }

            switch (channels)
            {
                case 8:
                    return "7.1";
                case 6:
                    return "5.1";
                case 2:
                    return "2.0";
                case 1:
                    return "1.0";
                default:
                    return channels > 0
                        ? channels.ToString(CultureInfo.InvariantCulture) + "ch"
                        : string.Empty;
            }
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

        private static string BuildCacheKey(string videoPath)
        {
            try
            {
                var file = new FileInfo(videoPath);
                return string.Concat(
                    file.FullName,
                    "|",
                    file.Length.ToString(CultureInfo.InvariantCulture),
                    "|",
                    file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                return videoPath ?? string.Empty;
            }
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
                process.Exited += (sender, args) =>
                {
                    try { exitTcs.TrySetResult(process.ExitCode); }
                    catch { exitTcs.TrySetResult(-1); }
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
                    var exitCode = await exitTcs.Task.ConfigureAwait(false);
                    var output = await outputTask.ConfigureAwait(false);
                    await errorTask.ConfigureAwait(false);
                    return exitCode == 0 ? (output ?? string.Empty) : string.Empty;
                }
            }
        }
    }
}
