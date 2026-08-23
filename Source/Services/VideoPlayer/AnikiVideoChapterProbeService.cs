using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoEndingChapter
    {
        public long StartMs { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    internal sealed class AnikiVideoSkipChapter
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty; // intro / recap
    }

    internal sealed class AnikiVideoChapterAnalysis
    {
        public AnikiVideoEndingChapter EndingChapter { get; set; }
        public IReadOnlyList<AnikiVideoSkipChapter> SkipChapters { get; set; } =
            Array.Empty<AnikiVideoSkipChapter>();
    }

    /// <summary>ffprobe chapter analyzer for intro, recap and ending markers.</summary>
    internal sealed class AnikiVideoChapterProbeService
    {
        private sealed class ParsedChapter
        {
            public double Start { get; set; }
            public double End { get; set; }
            public string Title { get; set; } = string.Empty;
        }

        private readonly global::AnikiHelper.AnikiHelperSettings settings;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<string, AnikiVideoChapterAnalysis> cache =
            new ConcurrentDictionary<string, AnikiVideoChapterAnalysis>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> noChapterCache =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public AnikiVideoChapterProbeService(global::AnikiHelper.AnikiHelperSettings settings, ILogger logger)
        {
            this.settings = settings;
            this.logger = logger ?? LogManager.GetLogger();
        }

        public string ResolveFfprobePath()
        {
            var configured = CleanPath(settings?.VideoFfprobePath);
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            // Most FFmpeg packages ship ffmpeg.exe and ffprobe.exe side by side.
            var ffmpeg = CleanPath(settings?.VideoThumbnailFfmpegPath);
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                try
                {
                    var folder = Path.GetDirectoryName(ffmpeg);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        var sibling = Path.Combine(folder, "ffprobe.exe");
                        if (File.Exists(sibling))
                        {
                            return sibling;
                        }
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        public bool IsAvailable => File.Exists(ResolveFfprobePath());

        public async Task<AnikiVideoEndingChapter> TryGetEndingChapterAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            var analysis = await TryAnalyzeAsync(videoPath, cancellationToken).ConfigureAwait(false);
            return analysis?.EndingChapter;
        }

        public async Task<AnikiVideoChapterAnalysis> TryAnalyzeAsync(
            string videoPath,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                {
                    return null;
                }

                var ffprobe = ResolveFfprobePath();
                if (!File.Exists(ffprobe))
                {
                    return null;
                }

                var cacheKey = BuildCacheKey(videoPath);
                if (cache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                if (noChapterCache.ContainsKey(cacheKey))
                {
                    return null;
                }

                var args = "-v error -print_format json -show_chapters \"" + videoPath + "\"";
                var json = await RunProcessCaptureAsync(ffprobe, args, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    noChapterCache.TryAdd(cacheKey, 0);
                    return null;
                }

                var root = JObject.Parse(json);
                var chapters = root["chapters"] as JArray;
                if (chapters == null || chapters.Count == 0)
                {
                    noChapterCache.TryAdd(cacheKey, 0);
                    return null;
                }

                var parsed = chapters
                    .Select(chapter => new ParsedChapter
                    {
                        Start = ParseSeconds(chapter?["start_time"]?.ToString()),
                        End = ParseSeconds(chapter?["end_time"]?.ToString()),
                        Title = chapter?["tags"]?["title"]?.ToString() ?? string.Empty
                    })
                    .Where(x => x.Start >= 0.0)
                    .OrderBy(x => x.Start)
                    .ToList();

                if (parsed.Count == 0)
                {
                    noChapterCache.TryAdd(cacheKey, 0);
                    return null;
                }

                // A few containers omit/duplicate chapter end times. The next chapter is a safe
                // boundary for Skip Intro/Recap in that case.
                for (var i = 0; i < parsed.Count; i++)
                {
                    if (parsed[i].End > parsed[i].Start)
                    {
                        continue;
                    }

                    if (i + 1 < parsed.Count && parsed[i + 1].Start > parsed[i].Start)
                    {
                        parsed[i].End = parsed[i + 1].Start;
                    }
                }

                var timelineEnd = parsed.Max(x => Math.Max(x.Start, x.End));

                var endingMatch = parsed
                    .Where(x => IsEndingChapterTitle(x.Title))
                    .Where(x => timelineEnd <= 0.0 || x.Start >= timelineEnd * 0.50)
                    .OrderByDescending(x => x.Start)
                    .FirstOrDefault();

                var ending = endingMatch == null
                    ? null
                    : new AnikiVideoEndingChapter
                    {
                        StartMs = Math.Max(0L, (long)Math.Round(endingMatch.Start * 1000.0)),
                        Title = endingMatch.Title ?? string.Empty
                    };

                var skipChapters = parsed
                    .Where(x => x.End > x.Start + 2.0)
                    .Where(x => timelineEnd <= 0.0 || x.Start <= timelineEnd * 0.60)
                    .Select(x =>
                    {
                        var kind = GetSkipKind(x.Title);
                        return string.IsNullOrWhiteSpace(kind)
                            ? null
                            : new AnikiVideoSkipChapter
                            {
                                StartMs = Math.Max(0L, (long)Math.Round(x.Start * 1000.0)),
                                EndMs = Math.Max(0L, (long)Math.Round(x.End * 1000.0)),
                                Title = x.Title ?? string.Empty,
                                Kind = kind
                            };
                    })
                    .Where(x => x != null && x.EndMs > x.StartMs + 2000L)
                    .OrderBy(x => x.StartMs)
                    .ToList();

                var analysis = new AnikiVideoChapterAnalysis
                {
                    EndingChapter = ending,
                    SkipChapters = skipChapters
                };

                cache[cacheKey] = analysis;

                if (ending != null)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter] Ending chapter detected: '{ending.Title}' at {ending.StartMs} ms for '{videoPath}'.");
                }

                foreach (var skip in skipChapters)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, $"[AnikiHelper][VideoCenter] Skippable {skip.Kind} chapter detected: '{skip.Title}' ({skip.StartMs}-{skip.EndMs} ms) for '{videoPath}'.");
                }

                return analysis;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] ffprobe chapter analysis failed.");
                return null;
            }
        }

        private static string GetSkipKind(string title)
        {
            var value = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (IsRecapChapterTitle(value))
            {
                return "recap";
            }

            if (IsIntroChapterTitle(value))
            {
                return "intro";
            }

            return string.Empty;
        }

        private static bool IsIntroChapterTitle(string normalizedTitle)
        {
            var value = normalizedTitle ?? string.Empty;
            if (value == "op" ||
                value == "opening" ||
                value == "intro" ||
                value == "introduction" ||
                value == "title sequence" ||
                value == "opening sequence" ||
                value == "opening credits" ||
                value == "opening theme" ||
                value == "opening song" ||
                value == "vorspann")
            {
                return true;
            }

            return value.StartsWith("opening ", StringComparison.Ordinal) ||
                   value.StartsWith("intro ", StringComparison.Ordinal) ||
                   value.StartsWith("op ", StringComparison.Ordinal) ||
                   (value.Length > 2 &&
                    value.StartsWith("op", StringComparison.Ordinal) &&
                    value.Substring(2).All(char.IsDigit)) ||
                   value.Contains("generique debut") ||
                   value.Contains("generique d ouverture") ||
                   value.Contains("generique ouverture");
        }

        private static bool IsRecapChapterTitle(string normalizedTitle)
        {
            var value = normalizedTitle ?? string.Empty;
            return value == "recap" ||
                   value.StartsWith("recap ", StringComparison.Ordinal) ||
                   value == "previously" ||
                   value.StartsWith("previously ", StringComparison.Ordinal) ||
                   value.Contains("previously on") ||
                   value.Contains("last time") ||
                   value.Contains("previous episode") ||
                   value.Contains("precedemment") ||
                   value == "rappel" ||
                   value.StartsWith("rappel ", StringComparison.Ordinal) ||
                   value == "resume" ||
                   value.StartsWith("resume ", StringComparison.Ordinal) ||
                   value.Contains("zusammenfassung") ||
                   value.Contains("riassunto") ||
                   value == "resumen" ||
                   value.StartsWith("resumen ", StringComparison.Ordinal) ||
                   value == "resumo" ||
                   value.StartsWith("resumo ", StringComparison.Ordinal);
        }

        private static bool IsEndingChapterTitle(string title)
        {
            var value = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.Contains("credit") ||
                value.Contains("generique") ||
                value.Contains("abspann") ||
                value.Contains("endroll") ||
                value.Contains("end roll") ||
                value.Contains("staff roll") ||
                value.Contains("staffroll"))
            {
                return true;
            }

            return value == "ending" ||
                   value.StartsWith("ending ", StringComparison.Ordinal) ||
                   value == "end" ||
                   value == "the end" ||
                   value == "outro" ||
                   value.StartsWith("outro ", StringComparison.Ordinal);
        }

        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            var previousWasSpace = false;
            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    previousWasSpace = false;
                }
                else if (!previousWasSpace)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
            }

            return sb.ToString().Trim();
        }

        private static double ParseSeconds(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : -1.0;
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

        private static string CleanPath(string value)
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
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
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
    }
}
