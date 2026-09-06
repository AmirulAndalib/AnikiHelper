using AnikiHelper.Services.Packs;
using AnikiHelper.Services.VisualPacks;
using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnikiHelper.Services.LoginPacks
{
    public sealed class LoginPackImportResult
    {
        public string LocalId { get; set; }
        public string PackId { get; set; }
        public string PackName { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public bool WasAlreadyInLibrary { get; set; }
        public bool WasUpdated { get; set; }
    }

    public sealed class LoginPackLibrarySnapshot
    {
        public int MaximumPacks { get; set; }
        public string ActivePackId { get; set; }
        public List<LoginPackLibraryPack> Packs { get; set; } = new List<LoginPackLibraryPack>();
    }

    public sealed class LoginPackLibraryPack
    {
        public string LocalId { get; set; }
        public string PackId { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string SourceFileName { get; set; }
        public string ContentHash { get; set; }
        public DateTime ImportedUtc { get; set; }

        [JsonIgnore]
        public bool IsActive { get; set; }

        [JsonIgnore]
        public string FolderPath { get; set; }

        [JsonIgnore]
        public string VideoPath { get; set; }

        [JsonIgnore]
        public long SizeBytes { get; set; }
    }

    internal sealed class LoginPackLibraryIndex
    {
        public int Version { get; set; } = 1;
        public string ActivePackId { get; set; }
        public List<LoginPackLibraryPack> Packs { get; set; } = new List<LoginPackLibraryPack>();
    }

    internal sealed class LoginPackManifest
    {
        [JsonProperty("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("author")]
        public string Author { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("codec")]
        public string Codec { get; set; }

        [JsonProperty("hasAudio")]
        public bool? HasAudio { get; set; }
    }

    internal sealed class LoginPackImportService
    {
        public const int MaximumLibraryPacks = 20;
        public const long MaximumVideoBytes = 50L * 1024L * 1024L;

        private const int SupportedFormatVersion = 1;
        private const int MaximumArchiveEntries = 8;
        private const long MaximumManifestBytes = 64L * 1024L;
        private const string ManifestFileName = "loginpack.json";
        private const string VideoFileName = "Login.mp4";
        private const string DefaultAuthor = "Unknown";

        private readonly ILogger logger;
        private readonly string loginPacksRoot;
        private readonly string libraryRoot;
        private readonly string indexFilePath;

        public LoginPackImportService(IPlayniteAPI api, string pluginUserDataPath, ILogger logger)
        {
            if (api == null)
            {
                throw new ArgumentNullException(nameof(api));
            }

            this.logger = logger;
            loginPacksRoot = AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "LoginPacks");
            libraryRoot = Path.Combine(loginPacksRoot, "Library");
            indexFilePath = Path.Combine(loginPacksRoot, "index.json");
        }

        public LoginPackLibrarySnapshot GetLibrary()
        {
            EnsureLibraryFolders();
            var index = LoadIndex();
            if (RemoveMissingLibraryEntries(index))
            {
                SaveIndex(index);
            }

            PopulateRuntimeProperties(index);
            return new LoginPackLibrarySnapshot
            {
                MaximumPacks = MaximumLibraryPacks,
                ActivePackId = index.ActivePackId ?? string.Empty,
                Packs = index.Packs
                    .OrderByDescending(x => x.IsActive)
                    .ThenByDescending(x => x.ImportedUtc)
                    .ToList()
            };
        }

        public LoginPackImportResult Import(string zipFilePath, bool activateImportedPack = false)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                throw new FileNotFoundException("The selected Login Pack ZIP file could not be found.", zipFilePath);
            }

            EnsureLibraryFolders();
            var stagingFolder = Path.Combine(loginPacksRoot, ".import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingFolder);

            try
            {
                LoginPackManifest manifest;
                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    ValidateArchiveEnvelope(archive);
                    var manifestEntry = GetRequiredRootEntry(archive, ManifestFileName);
                    var videoEntry = GetRequiredRootEntry(archive, VideoFileName);
                    manifest = ReadManifest(manifestEntry);
                    ValidateManifest(manifest);
                    ValidateVideoEntry(videoEntry);

                    CopyEntry(manifestEntry, Path.Combine(stagingFolder, ManifestFileName));
                    CopyEntry(videoEntry, Path.Combine(stagingFolder, VideoFileName));
                }

                var stagedVideoPath = Path.Combine(stagingFolder, VideoFileName);
                ValidateMp4Codec(stagedVideoPath);
                var contentHash = ComputeFileHash(stagedVideoPath);

                var index = LoadIndex();
                if (RemoveMissingLibraryEntries(index))
                {
                    SaveIndex(index);
                }

                var stablePackId = manifest.Id.Trim();
                var version = manifest.Version.Trim();
                var existing = index.Packs.FirstOrDefault(x =>
                    string.Equals(x.PackId, stablePackId, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    if (CommunityVisualPackService.CompareVersions(version, existing.Version) <= 0)
                    {
                        TryDeleteDirectory(stagingFolder);
                        if (activateImportedPack)
                        {
                            SetActivePack(existing.LocalId);
                        }

                        return CreateImportResult(existing, true, false);
                    }

                    var result = UpdateExistingPack(index, existing, stagingFolder, zipFilePath, contentHash, manifest);
                    if (activateImportedPack)
                    {
                        SetActivePack(existing.LocalId);
                    }

                    return result;
                }

                if (index.Packs.Count >= MaximumLibraryPacks)
                {
                    throw new InvalidOperationException(
                        $"The Login Pack library is full ({MaximumLibraryPacks}/{MaximumLibraryPacks}). Delete a pack before importing another one.");
                }

                var localId = CreateLocalId(contentHash, index);
                var destinationFolder = Path.Combine(libraryRoot, localId);
                Directory.Move(stagingFolder, destinationFolder);

                var record = CreateLibraryRecord(localId, zipFilePath, contentHash, manifest);
                index.Packs.Add(record);
                SaveIndex(index);

                if (activateImportedPack)
                {
                    SetActivePack(localId);
                }

                logger?.Info($"[AnikiHelper][LoginPack] Imported '{record.Name}' ({record.Version}).");
                return CreateImportResult(record, false, false);
            }
            catch
            {
                TryDeleteDirectory(stagingFolder);
                throw;
            }
        }

        public void SetActivePack(string localId)
        {
            if (string.IsNullOrWhiteSpace(localId))
            {
                ClearActivePack();
                return;
            }

            EnsureLibraryFolders();
            var index = LoadIndex();
            if (RemoveMissingLibraryEntries(index))
            {
                SaveIndex(index);
            }

            var record = FindPack(index, localId);
            var folder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(folder);

            if (string.Equals(index.ActivePackId, record.LocalId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            index.ActivePackId = record.LocalId;
            SaveIndex(index);
        }

        public void ClearActivePack()
        {
            EnsureLibraryFolders();
            var index = LoadIndex();
            if (string.IsNullOrWhiteSpace(index.ActivePackId))
            {
                return;
            }

            index.ActivePackId = string.Empty;
            SaveIndex(index);
        }

        public string GetVideoPath(string localId)
        {
            if (string.IsNullOrWhiteSpace(localId))
            {
                return string.Empty;
            }

            EnsureLibraryFolders();
            var index = LoadIndex();
            if (RemoveMissingLibraryEntries(index))
            {
                SaveIndex(index);
            }

            var record = FindPack(index, localId);
            var folder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(folder);
            return Path.Combine(folder, VideoFileName);
        }

        public string GetActiveVideoPath()
        {
            EnsureLibraryFolders();
            var index = LoadIndex();
            if (RemoveMissingLibraryEntries(index))
            {
                SaveIndex(index);
            }

            if (string.IsNullOrWhiteSpace(index.ActivePackId))
            {
                return string.Empty;
            }

            try
            {
                var record = FindPack(index, index.ActivePackId);
                var folder = Path.Combine(libraryRoot, record.LocalId);
                ValidateStoredPack(folder);
                return Path.Combine(folder, VideoFileName);
            }
            catch
            {
                index.ActivePackId = string.Empty;
                SaveIndex(index);
                return string.Empty;
            }
        }

        public void Delete(string localId)
        {
            if (string.IsNullOrWhiteSpace(localId))
            {
                throw new ArgumentException("A Login Pack id is required.", nameof(localId));
            }

            EnsureLibraryFolders();
            var index = LoadIndex();
            var record = FindPack(index, localId);
            var folder = Path.Combine(libraryRoot, record.LocalId);
            var trashFolder = Path.Combine(loginPacksRoot, ".delete-" + Guid.NewGuid().ToString("N"));
            var wasActive = string.Equals(index.ActivePackId, record.LocalId, StringComparison.OrdinalIgnoreCase);

            if (Directory.Exists(folder))
            {
                Directory.Move(folder, trashFolder);
            }

            try
            {
                index.Packs.Remove(record);
                if (wasActive)
                {
                    index.ActivePackId = string.Empty;
                }

                SaveIndex(index);
                TryDeleteDirectory(trashFolder);
                logger?.Info($"[AnikiHelper][LoginPack] Deleted '{record.Name}'.");
            }
            catch
            {
                if (Directory.Exists(trashFolder) && !Directory.Exists(folder))
                {
                    Directory.Move(trashFolder, folder);
                }

                throw;
            }
        }

        public void Export(string localId, string destinationZipPath)
        {
            if (string.IsNullOrWhiteSpace(destinationZipPath))
            {
                throw new ArgumentException("An export destination is required.", nameof(destinationZipPath));
            }

            EnsureLibraryFolders();
            var index = LoadIndex();
            var record = FindPack(index, localId);
            var folder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(folder);

            var destinationFullPath = Path.GetFullPath(destinationZipPath);
            var parent = Path.GetDirectoryName(destinationFullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var tempPath = destinationFullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    archive.CreateEntryFromFile(Path.Combine(folder, ManifestFileName), ManifestFileName, CompressionLevel.Optimal);
                    archive.CreateEntryFromFile(Path.Combine(folder, VideoFileName), VideoFileName, CompressionLevel.Optimal);
                }

                if (File.Exists(destinationFullPath))
                {
                    File.Delete(destinationFullPath);
                }

                File.Move(tempPath, destinationFullPath);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private LoginPackImportResult UpdateExistingPack(
            LoginPackLibraryIndex index,
            LoginPackLibraryPack existing,
            string stagingFolder,
            string sourceZipPath,
            string contentHash,
            LoginPackManifest manifest)
        {
            var destinationFolder = Path.Combine(libraryRoot, existing.LocalId);
            var backupFolder = Path.Combine(loginPacksRoot, ".backup-" + Guid.NewGuid().ToString("N"));
            var previousRecord = CloneRecord(existing);

            try
            {
                if (Directory.Exists(destinationFolder))
                {
                    Directory.Move(destinationFolder, backupFolder);
                }

                Directory.Move(stagingFolder, destinationFolder);
                ApplyManifestToRecord(existing, sourceZipPath, contentHash, manifest);
                SaveIndex(index);
                TryDeleteDirectory(backupFolder);
                logger?.Info($"[AnikiHelper][LoginPack] Updated '{existing.Name}' to {existing.Version}.");
                return CreateImportResult(existing, false, true);
            }
            catch
            {
                TryDeleteDirectory(destinationFolder);
                if (Directory.Exists(backupFolder) && !Directory.Exists(destinationFolder))
                {
                    Directory.Move(backupFolder, destinationFolder);
                }

                RestoreRecord(existing, previousRecord);
                try { SaveIndex(index); } catch { }
                throw;
            }
        }

        private static LoginPackLibraryPack CreateLibraryRecord(
            string localId,
            string sourceZipPath,
            string contentHash,
            LoginPackManifest manifest)
        {
            var record = new LoginPackLibraryPack { LocalId = localId };
            ApplyManifestToRecord(record, sourceZipPath, contentHash, manifest);
            return record;
        }

        private static void ApplyManifestToRecord(
            LoginPackLibraryPack record,
            string sourceZipPath,
            string contentHash,
            LoginPackManifest manifest)
        {
            record.PackId = manifest.Id.Trim();
            record.Name = manifest.Name.Trim();
            record.Author = NormalizeAuthor(manifest.Author);
            record.Version = manifest.Version.Trim();
            record.Description = (manifest.Description ?? string.Empty).Trim();
            record.SourceFileName = Path.GetFileName(sourceZipPath) ?? string.Empty;
            record.ContentHash = contentHash ?? string.Empty;
            record.ImportedUtc = DateTime.UtcNow;
        }

        private static LoginPackLibraryPack CloneRecord(LoginPackLibraryPack source)
        {
            return new LoginPackLibraryPack
            {
                LocalId = source.LocalId,
                PackId = source.PackId,
                Name = source.Name,
                Author = source.Author,
                Version = source.Version,
                Description = source.Description,
                SourceFileName = source.SourceFileName,
                ContentHash = source.ContentHash,
                ImportedUtc = source.ImportedUtc
            };
        }

        private static void RestoreRecord(LoginPackLibraryPack target, LoginPackLibraryPack source)
        {
            target.LocalId = source.LocalId;
            target.PackId = source.PackId;
            target.Name = source.Name;
            target.Author = source.Author;
            target.Version = source.Version;
            target.Description = source.Description;
            target.SourceFileName = source.SourceFileName;
            target.ContentHash = source.ContentHash;
            target.ImportedUtc = source.ImportedUtc;
        }

        private static LoginPackImportResult CreateImportResult(LoginPackLibraryPack record, bool alreadyInstalled, bool updated)
        {
            return new LoginPackImportResult
            {
                LocalId = record.LocalId,
                PackId = record.PackId,
                PackName = record.Name,
                Author = record.Author,
                Version = record.Version,
                Description = record.Description,
                WasAlreadyInLibrary = alreadyInstalled,
                WasUpdated = updated
            };
        }

        private static void ValidateArchiveEnvelope(ZipArchive archive)
        {
            if (archive == null)
            {
                throw new InvalidDataException("The Login Pack ZIP could not be opened.");
            }

            var fileEntries = archive.Entries.Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
            if (fileEntries.Count == 0 || fileEntries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException("The Login Pack ZIP contains an invalid number of files.");
            }

            foreach (var entry in fileEntries)
            {
                var normalized = (entry.FullName ?? string.Empty).Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(normalized) ||
                    normalized.StartsWith("/", StringComparison.Ordinal) ||
                    normalized.Contains("../") ||
                    normalized.Contains(":") ||
                    normalized.Contains("/"))
                {
                    throw new InvalidDataException("Login Pack files must be stored at the root of the ZIP.");
                }
            }
        }

        private static ZipArchiveEntry GetRequiredRootEntry(ZipArchive archive, string fileName)
        {
            var entry = archive.Entries.FirstOrDefault(x =>
                !string.IsNullOrEmpty(x.Name) &&
                string.Equals(x.FullName.Replace('\\', '/'), fileName, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                throw new InvalidDataException("The Login Pack is missing required file: " + fileName);
            }

            return entry;
        }

        private static LoginPackManifest ReadManifest(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0 || entry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("loginpack.json has an invalid size.");
            }

            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
            {
                var json = reader.ReadToEnd();
                var manifest = JsonConvert.DeserializeObject<LoginPackManifest>(json);
                if (manifest == null)
                {
                    throw new InvalidDataException("loginpack.json could not be read.");
                }

                return manifest;
            }
        }

        private static void ValidateManifest(LoginPackManifest manifest)
        {
            if (manifest.FormatVersion != SupportedFormatVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported Login Pack formatVersion '{manifest.FormatVersion}'. Expected {SupportedFormatVersion}.");
            }

            if (!string.IsNullOrWhiteSpace(manifest.Type))
            {
                var normalizedType = new string(manifest.Type
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant)
                    .ToArray());

                if (!normalizedType.Contains("login"))
                {
                    throw new InvalidDataException("loginpack.json has an invalid pack type.");
                }
            }

            ValidateManifestText(manifest.Id, "id", 120);
            ValidateManifestText(manifest.Name, "name", 160);
            ValidateOptionalManifestText(manifest.Author, "author", 160);
            ValidateManifestText(manifest.Version, "version", 64);

            if (!string.IsNullOrWhiteSpace(manifest.Description) && manifest.Description.Length > 1000)
            {
                throw new InvalidDataException("loginpack.json description is too long.");
            }
        }

        private static void ValidateManifestText(string value, string fieldName, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("loginpack.json is missing required field: " + fieldName);
            }

            if (value.Trim().Length > maximumLength)
            {
                throw new InvalidDataException("loginpack.json field is too long: " + fieldName);
            }
        }

        private static void ValidateOptionalManifestText(string value, string fieldName, int maximumLength)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > maximumLength)
            {
                throw new InvalidDataException("loginpack.json field is too long: " + fieldName);
            }
        }

        private static string NormalizeAuthor(string author)
        {
            return string.IsNullOrWhiteSpace(author) ? DefaultAuthor : author.Trim();
        }

        private static void ValidateVideoEntry(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0)
            {
                throw new InvalidDataException("Login.mp4 is empty.");
            }

            if (entry.Length >= MaximumVideoBytes)
            {
                throw new InvalidDataException("Login.mp4 must be smaller than 50 MB.");
            }
        }

        private static void ValidateMp4Codec(string videoPath)
        {
            var info = new FileInfo(videoPath);
            if (!info.Exists || info.Length <= 0 || info.Length >= MaximumVideoBytes)
            {
                throw new InvalidDataException("Login.mp4 has an invalid size.");
            }

            var signatures = new[] { "avc1", "avc3", "hvc1", "hev1" };
            var overlap = new byte[3];
            var overlapLength = 0;
            var buffer = new byte[64 * 1024];

            using (var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var bytes = new byte[overlapLength + read];
                    if (overlapLength > 0)
                    {
                        Buffer.BlockCopy(overlap, 0, bytes, 0, overlapLength);
                    }
                    Buffer.BlockCopy(buffer, 0, bytes, overlapLength, read);

                    var text = Encoding.ASCII.GetString(bytes);
                    if (signatures.Any(sig => text.IndexOf(sig, StringComparison.Ordinal) >= 0))
                    {
                        return;
                    }

                    overlapLength = Math.Min(3, bytes.Length);
                    if (overlapLength > 0)
                    {
                        Buffer.BlockCopy(bytes, bytes.Length - overlapLength, overlap, 0, overlapLength);
                    }
                }
            }

            throw new InvalidDataException("Login.mp4 must use H.264/AVC or H.265/HEVC video.");
        }

        private static string ComputeFileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private string CreateLocalId(string contentHash, LoginPackLibraryIndex index)
        {
            var seed = string.IsNullOrWhiteSpace(contentHash)
                ? Guid.NewGuid().ToString("N")
                : contentHash.ToLowerInvariant();
            var length = Math.Min(12, seed.Length);
            var baseId = "login-" + seed.Substring(0, length);
            var candidate = baseId;
            var suffix = 2;

            while (index.Packs.Any(x => string.Equals(x.LocalId, candidate, StringComparison.OrdinalIgnoreCase)) ||
                   Directory.Exists(Path.Combine(libraryRoot, candidate)))
            {
                candidate = baseId + "-" + suffix++;
            }

            return candidate;
        }

        private static LoginPackLibraryPack FindPack(LoginPackLibraryIndex index, string localId)
        {
            var record = index?.Packs?.FirstOrDefault(x =>
                string.Equals(x.LocalId, localId, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                throw new InvalidOperationException("The selected Login Pack is no longer in the library.");
            }

            return record;
        }

        private void ValidateStoredPack(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException("The Login Pack folder could not be found.");
            }

            var manifestPath = Path.Combine(folder, ManifestFileName);
            var videoPath = Path.Combine(folder, VideoFileName);
            if (!File.Exists(manifestPath) || !File.Exists(videoPath))
            {
                throw new InvalidDataException("The Login Pack library entry is incomplete.");
            }

            var info = new FileInfo(videoPath);
            if (info.Length <= 0 || info.Length >= MaximumVideoBytes)
            {
                throw new InvalidDataException("The Login Pack video has an invalid size.");
            }
        }

        private void PopulateRuntimeProperties(LoginPackLibraryIndex index)
        {
            foreach (var pack in index.Packs)
            {
                var folder = Path.Combine(libraryRoot, pack.LocalId ?? string.Empty);
                var videoPath = Path.Combine(folder, VideoFileName);
                pack.IsActive = string.Equals(index.ActivePackId, pack.LocalId, StringComparison.OrdinalIgnoreCase);
                pack.FolderPath = folder;
                pack.VideoPath = File.Exists(videoPath) ? videoPath : string.Empty;
                pack.SizeBytes = File.Exists(videoPath) ? new FileInfo(videoPath).Length : 0L;
            }
        }

        private bool RemoveMissingLibraryEntries(LoginPackLibraryIndex index)
        {
            var changed = false;
            foreach (var pack in index.Packs.ToList())
            {
                var folder = Path.Combine(libraryRoot, pack.LocalId ?? string.Empty);
                var video = Path.Combine(folder, VideoFileName);
                var manifest = Path.Combine(folder, ManifestFileName);
                if (!Directory.Exists(folder) || !File.Exists(video) || !File.Exists(manifest))
                {
                    index.Packs.Remove(pack);
                    changed = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(index.ActivePackId) &&
                !index.Packs.Any(x => string.Equals(x.LocalId, index.ActivePackId, StringComparison.OrdinalIgnoreCase)))
            {
                index.ActivePackId = string.Empty;
                changed = true;
            }

            return changed;
        }

        private LoginPackLibraryIndex LoadIndex()
        {
            EnsureLibraryFolders();
            try
            {
                if (!File.Exists(indexFilePath))
                {
                    return new LoginPackLibraryIndex();
                }

                var index = JsonConvert.DeserializeObject<LoginPackLibraryIndex>(File.ReadAllText(indexFilePath));
                if (index == null)
                {
                    return new LoginPackLibraryIndex();
                }

                NormalizeIndex(index);
                return index;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][LoginPack] Failed to read Login Pack index. A fresh index will be used.");
                return new LoginPackLibraryIndex();
            }
        }

        private void SaveIndex(LoginPackLibraryIndex index)
        {
            NormalizeIndex(index);
            EnsureLibraryFolders();
            var temp = indexFilePath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(index, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(indexFilePath))
            {
                File.Delete(indexFilePath);
            }
            File.Move(temp, indexFilePath);
        }

        private static void NormalizeIndex(LoginPackLibraryIndex index)
        {
            if (index.Packs == null)
            {
                index.Packs = new List<LoginPackLibraryPack>();
            }

            index.Packs = index.Packs
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.LocalId))
                .GroupBy(x => x.LocalId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            index.ActivePackId = index.ActivePackId ?? string.Empty;
            index.Version = 1;
        }

        private void EnsureLibraryFolders()
        {
            Directory.CreateDirectory(loginPacksRoot);
            Directory.CreateDirectory(libraryRoot);
        }

        private static void CopyEntry(ZipArchiveEntry entry, string destination)
        {
            using (var source = entry.Open())
            using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }
    }
}
