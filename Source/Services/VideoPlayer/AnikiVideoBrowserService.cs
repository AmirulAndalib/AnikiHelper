using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.VideoPlayer
{
    internal sealed class AnikiVideoBrowserResult
    {
        public string DirectoryPath { get; set; } = string.Empty;
        public string LocationTitle { get; set; } = string.Empty;
        public IReadOnlyList<AnikiVideoBrowserItem> Items { get; set; } = Array.Empty<AnikiVideoBrowserItem>();
        public IReadOnlyList<string> VideoSequence { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// File-system work for Aniki Video Player. All potentially slow directory enumeration runs
    /// away from the WPF thread, then the completed list is handed to the UI in one operation.
    /// </summary>
    internal sealed class AnikiVideoBrowserService
    {
        private static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv",
            ".mpg", ".mpeg", ".m2v", ".ts", ".mts", ".m2ts", ".vob",
            ".flv", ".f4v", ".3gp", ".3g2", ".ogv", ".asf", ".divx"
        };

        // Folders created by Windows/NAS operating systems that are not useful in a TV media browser.
        private static readonly HashSet<string> IgnoredDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information", "@eaDir", ".Trash", ".Trashes",
            ".recycle", ".Recycle", "#recycle", "@Recycle", "lost+found", ".AppleDouble",
            ".TemporaryItems", "Temporary Items", "Network Trash Folder"
        };

        private readonly ILogger logger;

        public AnikiVideoBrowserService(ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
        }

        public Task<IReadOnlyList<AnikiVideoBrowserItem>> BuildHomeAsync(
            Func<string, string, string> localize,
            CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<AnikiVideoBrowserItem>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var items = new List<AnikiVideoBrowserItem>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var folderTypeLabel = localize("VideoPlayer_Folder", "FOLDER");

                AddHomeFolder(items, seenPaths,
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    localize("VideoPlayer_Videos", "Videos"),
                    folderTypeLabel);

                cancellationToken.ThrowIfCancellationRequested();

                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    AddHomeFolder(items, seenPaths,
                        Path.Combine(userProfile, "Downloads"),
                        localize("VideoPlayer_Downloads", "Downloads"),
                        folderTypeLabel);
                }

                cancellationToken.ThrowIfCancellationRequested();

                AddHomeFolder(items, seenPaths,
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    localize("VideoPlayer_Desktop", "Desktop"),
                    folderTypeLabel);

                foreach (var drive in DriveInfo.GetDrives())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (!drive.IsReady || string.IsNullOrWhiteSpace(drive.RootDirectory?.FullName))
                        {
                            continue;
                        }

                        var root = drive.RootDirectory.FullName;
                        if (!seenPaths.Add(root))
                        {
                            continue;
                        }

                        string label = string.Empty;
                        try { label = drive.VolumeLabel; } catch { }

                        items.Add(new AnikiVideoBrowserItem
                        {
                            Name = string.IsNullOrWhiteSpace(label) ? root : label + " (" + root.TrimEnd('\\') + ")",
                            FullPath = root,
                            SecondaryText = FormatDriveSpace(drive),
                            TypeLabel = localize("VideoPlayer_Drive", "DRIVE"),
                            IsDirectory = true,
                            IsDrive = true
                        });
                    }
                    catch
                    {
                    }
                }

                return items;
            }, cancellationToken);
        }

        public async Task<IReadOnlyList<AnikiVideoBrowserItem>> BuildNetworkLocationsAsync(
            IEnumerable<KeyValuePair<string, string>> configuredLocations,
            Func<string, string, string> localize,
            CancellationToken cancellationToken)
        {
            var snapshot = (configuredLocations ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Take(4)
                .ToList();

            var tasks = snapshot
                .Select(location => BuildNetworkLocationItemAsync(location, localize, cancellationToken))
                .ToArray();

            if (tasks.Length == 0)
            {
                return Array.Empty<AnikiVideoBrowserItem>();
            }

            var result = await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result.Where(item => item != null).ToList();
        }

        private async Task<AnikiVideoBrowserItem> BuildNetworkLocationItemAsync(
            KeyValuePair<string, string> location,
            Func<string, string, string> localize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = NormalizeNetworkPath(location.Value);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            // UNC checks can block for a surprisingly long time when a NAS is sleeping/offline.
            // Run the Windows check away from WPF and stop waiting after a short timeout.
            var available = await DirectoryExistsWithTimeoutAsync(
                path,
                TimeSpan.FromSeconds(3.5),
                cancellationToken).ConfigureAwait(false);

            var name = string.IsNullOrWhiteSpace(location.Key)
                ? GetDirectoryDisplayName(path)
                : location.Key.Trim();

            var unavailable = localize("VideoPlayer_NetworkUnavailable", "Unavailable");
            return new AnikiVideoBrowserItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? GetDirectoryDisplayName(path) : name,
                FullPath = path,
                SecondaryText = available
                    ? GetDirectoryDisplayName(path)
                    : unavailable + "  •  " + GetDirectoryDisplayName(path),
                TypeLabel = localize("VideoPlayer_Network", "NETWORK"),
                IsNetworkLocation = true,
                IsAvailable = available
            };
        }

        private static async Task<bool> DirectoryExistsWithTimeoutAsync(
            string path,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var checkTask = Task.Run(() =>
            {
                try
                {
                    return Directory.Exists(path);
                }
                catch
                {
                    return false;
                }
            });

            var delayTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(checkTask, delayTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (ReferenceEquals(completed, checkTask))
            {
                try
                {
                    return await checkTask.ConfigureAwait(false);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public Task<AnikiVideoBrowserResult> ScanDirectoryAsync(
            string path,
            Func<string, string, string> localize,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => ScanDirectory(path, localize, cancellationToken), cancellationToken);
        }

        public Task<IReadOnlyList<string>> BuildVideoSequenceAsync(string directoryPath, CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<string>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                {
                    return Array.Empty<string>();
                }

                var videos = new List<string>();
                foreach (var filePath in Directory.EnumerateFiles(directoryPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var extension = Path.GetExtension(filePath);
                        if (!SupportedExtensions.Contains(extension ?? string.Empty))
                        {
                            continue;
                        }

                        var info = new FileInfo(filePath);
                        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        {
                            continue;
                        }
                        videos.Add(info.FullName);
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }

                videos.Sort((left, right) => CompareNatural(
                    Path.GetFileName(left), Path.GetFileName(right)));
                return videos;
            }, cancellationToken);
        }

        private AnikiVideoBrowserResult ScanDirectory(
            string path,
            Func<string, string, string> localize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(path ?? string.Empty);
            }

            var normalized = Path.GetFullPath(path);
            var directories = new List<AnikiVideoBrowserItem>();
            var videos = new List<AnikiVideoBrowserItem>();

            foreach (var directoryPath in Directory.EnumerateDirectories(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new DirectoryInfo(directoryPath);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                        ShouldIgnoreDirectory(info.Name))
                    {
                        continue;
                    }

                    directories.Add(new AnikiVideoBrowserItem
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        SecondaryText = localize("VideoPlayer_Folder", "Folder"),
                        TypeLabel = localize("VideoPlayer_Folder", "FOLDER"),
                        IsDirectory = true
                    });
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            foreach (var filePath in Directory.EnumerateFiles(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var extension = Path.GetExtension(filePath);
                    if (!SupportedExtensions.Contains(extension ?? string.Empty))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    videos.Add(new AnikiVideoBrowserItem
                    {
                        Name = Path.GetFileNameWithoutExtension(info.Name),
                        FullPath = info.FullName,
                        SecondaryText = FormatFileSize(info.Length),
                        TypeLabel = string.IsNullOrWhiteSpace(extension)
                            ? localize("VideoPlayer_Video", "VIDEO")
                            : extension.TrimStart('.').ToUpperInvariant(),
                        IsVideo = true
                    });
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            directories.Sort((left, right) => CompareNatural(left?.Name, right?.Name));
            videos.Sort((left, right) => CompareNatural(left?.Name, right?.Name));

            var combined = new List<AnikiVideoBrowserItem>(directories.Count + videos.Count);
            combined.AddRange(directories);
            combined.AddRange(videos);

            return new AnikiVideoBrowserResult
            {
                DirectoryPath = normalized,
                LocationTitle = GetDirectoryDisplayName(normalized),
                Items = combined,
                VideoSequence = videos.Select(item => item.FullPath).ToList()
            };
        }

        private static bool ShouldIgnoreDirectory(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return IgnoredDirectoryNames.Contains(name) ||
                   name.StartsWith(".Trash-", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddHomeFolder(
            IList<AnikiVideoBrowserItem> items,
            ISet<string> seenPaths,
            string path,
            string displayName,
            string typeLabel)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    return;
                }

                var normalized = Path.GetFullPath(path);
                if (!seenPaths.Add(normalized))
                {
                    return;
                }

                items.Add(new AnikiVideoBrowserItem
                {
                    Name = displayName,
                    FullPath = normalized,
                    SecondaryText = string.Empty,
                    TypeLabel = typeLabel,
                    IsDirectory = true,
                    IsHomeShortcut = true
                });
            }
            catch
            {
            }
        }

        private static string NormalizeNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var value = path.Trim().Replace('/', '\\');
            if (value.Length > 3)
            {
                value = value.TrimEnd('\\');
            }

            return value;
        }

        private static string GetDirectoryDisplayName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                var normalized = Path.GetFullPath(path.Trim().Replace('/', '\\'));
                if (normalized.Length > 3)
                {
                    normalized = normalized.TrimEnd('\\');
                }
                var info = new DirectoryInfo(normalized);
                if (!string.IsNullOrWhiteSpace(info.Name))
                {
                    return info.Name;
                }

                var root = Path.GetPathRoot(path) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(root))
                {
                    try
                    {
                        var drive = new DriveInfo(root);
                        if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                        {
                            return drive.VolumeLabel;
                        }
                    }
                    catch
                    {
                    }

                    return root.TrimEnd('\\');
                }
            }
            catch
            {
                try
                {
                    var value = path.Trim().TrimEnd('\\', '/');
                    var index = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
                    if (index >= 0 && index + 1 < value.Length)
                    {
                        return value.Substring(index + 1);
                    }
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Human/natural sort used by the TV browser and playback sequence:
        /// Episode 2 comes before Episode 10 and S01E2 before S01E10.
        /// </summary>
        private static int CompareNatural(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;

            var i = 0;
            var j = 0;
            while (i < left.Length && j < right.Length)
            {
                var leftDigit = char.IsDigit(left[i]);
                var rightDigit = char.IsDigit(right[j]);

                if (leftDigit && rightDigit)
                {
                    var leftStart = i;
                    var rightStart = j;

                    while (i < left.Length && left[i] == '0') i++;
                    while (j < right.Length && right[j] == '0') j++;

                    var leftSignificant = i;
                    var rightSignificant = j;

                    while (i < left.Length && char.IsDigit(left[i])) i++;
                    while (j < right.Length && char.IsDigit(right[j])) j++;

                    var leftLength = i - leftSignificant;
                    var rightLength = j - rightSignificant;
                    if (leftLength != rightLength)
                    {
                        return leftLength.CompareTo(rightLength);
                    }

                    for (var k = 0; k < leftLength; k++)
                    {
                        var cmpDigit = left[leftSignificant + k].CompareTo(right[rightSignificant + k]);
                        if (cmpDigit != 0)
                        {
                            return cmpDigit;
                        }
                    }

                    // Equal numeric value: prefer the shorter zero-padded representation.
                    var leftTotal = i - leftStart;
                    var rightTotal = j - rightStart;
                    if (leftTotal != rightTotal)
                    {
                        return leftTotal.CompareTo(rightTotal);
                    }

                    continue;
                }

                if (leftDigit != rightDigit)
                {
                    return leftDigit ? -1 : 1;
                }

                var leftChar = char.ToUpperInvariant(left[i]);
                var rightChar = char.ToUpperInvariant(right[j]);
                if (leftChar != rightChar)
                {
                    return leftChar.CompareTo(rightChar);
                }

                i++;
                j++;
            }

            if (i < left.Length) return 1;
            if (j < right.Length) return -1;
            return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        }

        private static string FormatFileSize(long bytes)
        {
            var value = (double)Math.Max(0, bytes);
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var index = 0;
            while (value >= 1024.0 && index < units.Length - 1)
            {
                value /= 1024.0;
                index++;
            }

            return index == 0
                ? value.ToString("0") + " " + units[index]
                : value.ToString("0.0") + " " + units[index];
        }

        private static string FormatDriveSpace(DriveInfo drive)
        {
            try
            {
                return FormatFileSize(drive.AvailableFreeSpace) + " free";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
