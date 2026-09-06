using System;
using System.IO;

namespace AnikiHelper.Services.Packs
{
    internal static class AnikiPackStorage
    {
        public const string RootFolderName = "AnikiPacks";

        private static readonly object MigrationLock = new object();

        public static string GetRoot(string pluginUserDataPath)
        {
            var dataRoot = pluginUserDataPath ?? string.Empty;
            var root = Path.Combine(dataRoot, RootFolderName);
            Directory.CreateDirectory(root);
            return root;
        }

        public static string GetAreaRoot(string pluginUserDataPath, string areaFolderName)
        {
            if (string.IsNullOrWhiteSpace(areaFolderName))
            {
                throw new ArgumentException("A pack storage area name is required.", nameof(areaFolderName));
            }

            var dataRoot = pluginUserDataPath ?? string.Empty;
            var destination = Path.Combine(GetRoot(dataRoot), areaFolderName);
            var legacy = Path.Combine(dataRoot, areaFolderName);

            lock (MigrationLock)
            {
                MigrateLegacyDirectory(legacy, destination);
                Directory.CreateDirectory(destination);
            }

            return destination;
        }

        private static void MigrateLegacyDirectory(string source, string destination)
        {
            if (string.IsNullOrWhiteSpace(source) ||
                string.IsNullOrWhiteSpace(destination) ||
                !Directory.Exists(source) ||
                PathsEqual(source, destination))
            {
                return;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (!Directory.Exists(destination))
            {
                Directory.Move(source, destination);
                return;
            }

            MergeDirectory(source, destination);

            try
            {
                if (Directory.Exists(source) &&
                    Directory.GetFileSystemEntries(source).Length == 0)
                {
                    Directory.Delete(source, false);
                }
            }
            catch
            {
                // Migration is best effort. Existing destination data always wins.
            }
        }

        private static void MergeDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target))
                {
                    File.Move(file, target);
                }
            }

            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(destination, Path.GetFileName(directory));
                if (!Directory.Exists(target))
                {
                    Directory.Move(directory, target);
                }
                else
                {
                    MergeDirectory(directory, target);
                    try
                    {
                        if (Directory.Exists(directory) &&
                            Directory.GetFileSystemEntries(directory).Length == 0)
                        {
                            Directory.Delete(directory, false);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
