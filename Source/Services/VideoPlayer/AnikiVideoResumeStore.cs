using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoResumeStore
    {
        private sealed class ResumeEntry
        {
            public string Path { get; set; }
            public long PositionMs { get; set; }
            public long DurationMs { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private readonly object sync = new object();
        private readonly ILogger logger;
        private readonly string filePath;
        private Dictionary<string, ResumeEntry> entries = new Dictionary<string, ResumeEntry>(StringComparer.OrdinalIgnoreCase);
        private Task lastSaveTask = Task.CompletedTask;

        public AnikiVideoResumeStore(string pluginUserDataPath, ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
            var root = AnikiVideoStoragePaths.GetPlayerStateRoot(pluginUserDataPath, this.logger);
            filePath = Path.Combine(root, "ResumePositions.json");
            Load();
        }

        public bool TryGet(string path, out long positionMs)
        {
            positionMs = 0;
            var key = Normalize(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (sync)
            {
                if (entries.TryGetValue(key, out var entry) && entry != null && entry.PositionMs > 0)
                {
                    positionMs = entry.PositionMs;
                    return true;
                }
            }

            return false;
        }

        public bool TryGet(string path, out long positionMs, out long durationMs)
        {
            positionMs = 0;
            durationMs = 0;
            var key = Normalize(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (sync)
            {
                if (entries.TryGetValue(key, out var entry) && entry != null && entry.PositionMs > 0)
                {
                    positionMs = entry.PositionMs;
                    durationMs = Math.Max(0, entry.DurationMs);
                    return true;
                }
            }

            return false;
        }

        public IReadOnlyList<string> GetRecentPaths(int limit = 12)
        {
            lock (sync)
            {
                return entries.Values
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path) && x.PositionMs > 0)
                    .OrderByDescending(x => x.UpdatedUtc)
                    .Select(x => x.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(0, limit))
                    .ToList();
            }
        }

        public void Record(string path, long positionMs, long durationMs)
        {
            var key = Normalize(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (sync)
            {
                if (ShouldForget(positionMs, durationMs))
                {
                    entries.Remove(key);
                    return;
                }

                entries[key] = new ResumeEntry
                {
                    Path = path,
                    PositionMs = positionMs,
                    DurationMs = durationMs,
                    UpdatedUtc = DateTime.UtcNow
                };

                if (entries.Count > 250)
                {
                    foreach (var oldKey in entries
                        .OrderByDescending(x => x.Value?.UpdatedUtc ?? DateTime.MinValue)
                        .Skip(220)
                        .Select(x => x.Key)
                        .ToList())
                    {
                        entries.Remove(oldKey);
                    }
                }
            }
        }

        public void Remove(string path)
        {
            var key = Normalize(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (sync)
            {
                entries.Remove(key);
            }
        }

        public Task SaveAsync()
        {
            Dictionary<string, ResumeEntry> snapshot;
            lock (sync)
            {
                snapshot = entries.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
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

                    var json = JsonConvert.SerializeObject(snapshot.Values.OrderByDescending(x => x.UpdatedUtc), Formatting.Indented);
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
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to save resume positions.");
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

                var json = File.ReadAllText(filePath);
                var list = JsonConvert.DeserializeObject<List<ResumeEntry>>(json) ?? new List<ResumeEntry>();
                entries = list
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path) && x.PositionMs > 0)
                    .GroupBy(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                    .ToDictionary(
                        x => x.Key,
                        x => x.OrderByDescending(y => y.UpdatedUtc).First(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to load resume positions.");
                entries = new Dictionary<string, ResumeEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool ShouldForget(long positionMs, long durationMs)
        {
            if (positionMs < 30000)
            {
                return true;
            }

            if (durationMs > 0)
            {
                var remaining = durationMs - positionMs;
                if (remaining <= 120000 || positionMs >= durationMs * 0.95)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return string.Empty;
                }
                return Path.GetFullPath(path).Trim().ToUpperInvariant();
            }
            catch
            {
                return (path ?? string.Empty).Trim().ToUpperInvariant();
            }
        }
    }
}
