using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnikiHelper.Services.VideoPlayer
{
    internal static class AnikiVideoStoragePaths
    {
        private static readonly object migrationSync = new object();
        private static readonly HashSet<string> migratedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string GetPlayerStateRoot(string pluginUserDataPath, ILogger logger)
        {
            var baseRoot = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? Path.Combine(Path.GetTempPath(), "AnikiHelper")
                : pluginUserDataPath;

            var videoCenterRoot = Path.Combine(baseRoot, "VideoCenter");
            var targetRoot = Path.Combine(videoCenterRoot, "VideoPlayer");
            var legacyRoot = Path.Combine(baseRoot, "VideoPlayer");

            TryMigrateLegacyPlayerFolder(legacyRoot, targetRoot, logger);
            return targetRoot;
        }

        private static void TryMigrateLegacyPlayerFolder(string legacyRoot, string targetRoot, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(legacyRoot) || string.IsNullOrWhiteSpace(targetRoot))
            {
                return;
            }

            var migrationKey = legacyRoot + "|" + targetRoot;
            lock (migrationSync)
            {
                if (migratedRoots.Contains(migrationKey))
                {
                    return;
                }

                migratedRoots.Add(migrationKey);

                try
                {
                    if (!Directory.Exists(legacyRoot))
                    {
                        return;
                    }

                    Directory.CreateDirectory(targetRoot);
                    var migratedAny = false;

                    foreach (var sourcePath in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.TopDirectoryOnly))
                    {
                        var fileName = Path.GetFileName(sourcePath);
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            continue;
                        }

                        var targetPath = Path.Combine(targetRoot, fileName);
                        if (File.Exists(targetPath))
                        {
                            continue;
                        }

                        try
                        {
                            File.Move(sourcePath, targetPath);
                            migratedAny = true;
                        }
                        catch
                        {
                            try
                            {
                                File.Copy(sourcePath, targetPath, false);
                                File.Delete(sourcePath);
                                migratedAny = true;
                            }
                            catch
                            {
                                // Keep the legacy file in place if it cannot be migrated safely.
                            }
                        }
                    }

                    try
                    {
                        if (Directory.Exists(legacyRoot) &&
                            Directory.GetFiles(legacyRoot).Length == 0 &&
                            Directory.GetDirectories(legacyRoot).Length == 0)
                        {
                            Directory.Delete(legacyRoot, false);
                        }
                    }
                    catch
                    {
                    }

                    if (migratedAny)
                    {
                        logger?.Info("[AnikiHelper][VideoCenter] Migrated legacy VideoPlayer state into VideoCenter\\VideoPlayer.");
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoCenter] Legacy VideoPlayer storage migration failed. Existing data will remain untouched.");
                }
            }
        }
    }
}
