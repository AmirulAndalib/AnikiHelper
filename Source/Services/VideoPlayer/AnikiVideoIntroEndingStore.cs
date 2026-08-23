using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoIntroEndingRecord
    {
        public const int CurrentAnalysisVersion = 11;

        public string Path { get; set; } = string.Empty;
        public long FileLength { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public int AnalysisVersion { get; set; } = CurrentAnalysisVersion;
        public DateTime AnalyzedUtc { get; set; }
        public long IntroStartMs { get; set; } = -1L;
        public long IntroEndMs { get; set; } = -1L;
        public double IntroConfidence { get; set; }
        public long EndingStartMs { get; set; } = -1L;
        public long EndingEndMs { get; set; } = -1L;
        public double EndingConfidence { get; set; }
        public string Note { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string SourceReference { get; set; } = string.Empty;
        public string LookupStatus { get; set; } = string.Empty;
        public DateTime RetryAfterUtc { get; set; }

        [JsonIgnore] public bool HasIntro => IntroStartMs >= 0L && IntroEndMs > IntroStartMs + 2000L;
        [JsonIgnore] public bool HasEnding => EndingStartMs >= 0L;
    }

    internal sealed class AnikiVideoIntroEndingStore
    {
        private sealed class StoreState
        {
            public int Version { get; set; } = 1;
            public List<AnikiVideoIntroEndingRecord> Records { get; set; } = new List<AnikiVideoIntroEndingRecord>();
        }

        private readonly object sync = new object();
        private readonly ILogger logger;
        private readonly string filePath;
        private readonly Dictionary<string, AnikiVideoIntroEndingRecord> records =
            new Dictionary<string, AnikiVideoIntroEndingRecord>(StringComparer.OrdinalIgnoreCase);

        public AnikiVideoIntroEndingStore(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            var baseRoot = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? Path.Combine(Path.GetTempPath(), "AnikiHelper")
                : pluginUserDataPath;
            filePath = Path.Combine(baseRoot, "VideoCenter", "IntroEnding", "analysis.json");
            Load();
        }

        public AnikiVideoIntroEndingRecord GetValid(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            lock (sync)
            {
                if (!records.TryGetValue(Normalize(path), out var record) || record == null)
                {
                    return null;
                }

                return IsCurrentFile(record) ? Clone(record) : null;
            }
        }

        public bool HasStoredRecord(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (sync)
            {
                return records.ContainsKey(Normalize(path));
            }
        }

        public void Upsert(AnikiVideoIntroEndingRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Path)) return;
            lock (sync)
            {
                records[Normalize(record.Path)] = Clone(record);
                SaveUnsafe();
            }
        }

        public void UpsertRange(IEnumerable<AnikiVideoIntroEndingRecord> source)
        {
            var list = (source ?? Enumerable.Empty<AnikiVideoIntroEndingRecord>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                .ToList();
            if (list.Count == 0) return;

            lock (sync)
            {
                foreach (var record in list)
                {
                    records[Normalize(record.Path)] = Clone(record);
                }
                SaveUnsafe();
            }
        }

        public static AnikiVideoIntroEndingRecord CreateEmptyForFile(string path, string note = "")
        {
            var record = new AnikiVideoIntroEndingRecord
            {
                Path = path ?? string.Empty,
                AnalysisVersion = AnikiVideoIntroEndingRecord.CurrentAnalysisVersion,
                AnalyzedUtc = DateTime.UtcNow,
                Note = note ?? string.Empty
            };

            try
            {
                var file = new FileInfo(path);
                record.FileLength = file.Exists ? file.Length : 0L;
                record.LastWriteUtcTicks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0L;
            }
            catch
            {
            }

            return record;
        }

        private bool IsCurrentFile(AnikiVideoIntroEndingRecord record)
        {
            if (record == null)
            {
                return false;
            }

            if (record.AnalysisVersion != AnikiVideoIntroEndingRecord.CurrentAnalysisVersion)
            {
                // Retry only records that previously failed because provider identity was missing.
                var isMissingIdentity = string.Equals(record.LookupStatus, "missing_id", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(record.SourceReference);
                var canReuseOlderRemoteRecord = record.AnalysisVersion >= 4 && record.AnalysisVersion <= 10 && !isMissingIdentity;
                if (!canReuseOlderRemoteRecord) return false;
            }

            try
            {
                var file = new FileInfo(record.Path);
                return file.Exists && file.Length == record.FileLength && file.LastWriteTimeUtc.Ticks == record.LastWriteUtcTicks;
            }
            catch
            {
                return false;
            }
        }

        private void Load()
        {
            lock (sync)
            {
                records.Clear();
                try
                {
                    if (!File.Exists(filePath)) return;
                    var state = JsonConvert.DeserializeObject<StoreState>(File.ReadAllText(filePath));
                    foreach (var record in state?.Records ?? new List<AnikiVideoIntroEndingRecord>())
                    {
                        if (record == null || string.IsNullOrWhiteSpace(record.Path)) continue;
                        records[Normalize(record.Path)] = record;
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] Failed to load detection cache.");
                }
            }
        }

        private void SaveUnsafe()
        {
            try
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
                var state = new StoreState { Records = records.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList() };
                var temp = filePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(temp, filePath);
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter][IntroEnding] Failed to save detection cache.");
            }
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return (path ?? string.Empty).Trim(); }
        }

        private static AnikiVideoIntroEndingRecord Clone(AnikiVideoIntroEndingRecord source)
        {
            if (source == null) return null;
            return new AnikiVideoIntroEndingRecord
            {
                Path = source.Path,
                FileLength = source.FileLength,
                LastWriteUtcTicks = source.LastWriteUtcTicks,
                AnalysisVersion = source.AnalysisVersion,
                AnalyzedUtc = source.AnalyzedUtc,
                IntroStartMs = source.IntroStartMs,
                IntroEndMs = source.IntroEndMs,
                IntroConfidence = source.IntroConfidence,
                EndingStartMs = source.EndingStartMs,
                EndingEndMs = source.EndingEndMs,
                EndingConfidence = source.EndingConfidence,
                Note = source.Note,
                Source = source.Source,
                SourceReference = source.SourceReference,
                LookupStatus = source.LookupStatus,
                RetryAfterUtc = source.RetryAfterUtc
            };
        }
    }
}
