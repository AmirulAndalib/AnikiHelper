using AnikiHelper.Services.Packs;
using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AnikiHelper.Services.VisualPacks
{
    public sealed class VisualPackImportResult
    {
        public string LocalId { get; set; }

        // Compatibility alias: CommunityVisualPackService historically called the
        // Helper-generated local identity PackId.
        public string PackId
        {
            get => LocalId;
            set => LocalId = value;
        }

        public string StablePackId { get; set; }
        public string PackName { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public bool WasAlreadyInLibrary { get; set; }
        public bool WasUpdated { get; set; }
    }

    public sealed class VisualPackLibrarySnapshot
    {
        public int MaximumPacks { get; set; }
        public string ActivePackId { get; set; }
        public List<VisualPackLibraryPack> Packs { get; set; } = new List<VisualPackLibraryPack>();
    }

    public sealed class VisualPackLibraryPack
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

        // Runtime compatibility for the existing UI and theme settings code. LocalId is
        // the persisted library identity; Id can be removed once every caller has moved
        // to the common pack model introduced after the Visual Pack migration.
        [JsonIgnore]
        public string Id
        {
            get => LocalId;
            set => LocalId = value;
        }

        // Version 1 of index.json persisted the local library identity as "Id".
        // Keep this write-only migration property so existing libraries deserialize
        // without losing their folders, selections or Community installation links.
        [JsonProperty("Id", NullValueHandling = NullValueHandling.Ignore)]
        private string LegacyId
        {
            get => null;
            set
            {
                if (string.IsNullOrWhiteSpace(LocalId))
                {
                    LocalId = value;
                }
            }
        }

        [JsonIgnore]
        public bool IsActive { get; set; }

        [JsonIgnore]
        public string FolderPath { get; set; }

        [JsonIgnore]
        public string PreviewPath { get; set; }

        [JsonIgnore]
        public long SizeBytes { get; set; }
    }

    internal sealed class VisualPackLibraryIndex
    {
        public int Version { get; set; } = 1;
        public string ActivePackId { get; set; }

        // Compatibility with the short-lived restart-based implementation. If an older
        // build left a queued pack here, GetLibrary promotes it to ActivePackId once and
        // clears it. Runtime loading is now the only activation mechanism.
        public string PendingPackId { get; set; }
        public List<VisualPackLibraryPack> Packs { get; set; } = new List<VisualPackLibraryPack>();
    }

    internal sealed class VisualPackManifest
    {
        [JsonProperty("formatVersion")]
        public int FormatVersion { get; set; }

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

        [JsonProperty("builtInSeed")]
        public bool BuiltInSeed { get; set; }
    }

    internal sealed class VisualPackAssetSpec
    {
        public VisualPackAssetSpec(string fileName, int width, int height)
        {
            FileName = fileName;
            Width = width;
            Height = height;
        }

        public string FileName { get; }
        public int Width { get; }
        public int Height { get; }
    }

    internal sealed class VisualPackImportService
    {
        public const int MaximumLibraryPacks = 20;

        private const int CurrentLibraryIndexVersion = 2;
        private const int SupportedFormatVersion = 1;
        private const int MaximumArchiveEntries = 64;
        private const long MaximumImageBytes = 25L * 1024L * 1024L;
        private const long MaximumArchiveUncompressedBytes = 200L * 1024L * 1024L;
        private const long MaximumManifestBytes = 64L * 1024L;
        private const string CustomPackFolderName = "153.Custom";
        private const string ManifestFileName = "visualpack.json";
        private const string BuiltInCustomSeedHash = "CFF54D2C6ABF31E2D25FDBDB345A2A7FC06FF895F5794B38A10EE79D50C3D4F6";

        private static readonly VisualPackAssetSpec[] RequiredAssets =
        {
            new VisualPackAssetSpec("MainBackground.jpg", 1920, 1080),
            new VisualPackAssetSpec("Welcome.jpg", 1920, 1080),
            new VisualPackAssetSpec("StatView.jpg", 1920, 1080),
            new VisualPackAssetSpec("FriendsView.jpg", 1920, 1080),
            new VisualPackAssetSpec("AchievementsView.jpg", 1920, 1080),
            new VisualPackAssetSpec("MediaView.jpg", 1920, 1080),
            new VisualPackAssetSpec("StoreView.jpg", 1920, 1080),
            new VisualPackAssetSpec("MainMenu.jpg", 531, 986),
            new VisualPackAssetSpec("SettingsBackground.jpg", 487, 1080),
            new VisualPackAssetSpec("FrameSettingsBackground.jpg", 1247, 900),
            new VisualPackAssetSpec("MessageBox.jpg", 830, 429),
            new VisualPackAssetSpec("GameMenu.jpg", 470, 655),
            new VisualPackAssetSpec("ItemMenu.jpg", 503, 818),
            new VisualPackAssetSpec("Login.jpg", 857, 238)
        };

        private readonly IPlayniteAPI api;
        private readonly string visualPacksRoot;
        private readonly string libraryRoot;
        private readonly string indexFilePath;
        private readonly ILogger logger;

        public VisualPackImportService(IPlayniteAPI api, string pluginUserDataPath, ILogger logger)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.logger = logger;

            visualPacksRoot = AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "VisualPacks");
            libraryRoot = Path.Combine(visualPacksRoot, "Library");
            indexFilePath = Path.Combine(visualPacksRoot, "index.json");
        }

        public VisualPackLibrarySnapshot GetLibrary()
        {
            EnsureLibraryFolders();
            var index = LoadIndex();
            var changed = UpgradeIndexMetadata(index);
            changed = PromoteLegacyPendingSelection(index) || changed;
            changed = RemoveMissingLibraryEntries(index) || changed;

            if (index.Packs.Count == 0)
            {
                if (TryMigrateCurrentCustomPack(index))
                {
                    changed = true;
                }

                if (TryMigrateLegacyBackup(index))
                {
                    changed = true;
                }
            }

            // 153.Custom is now only the theme-owned neutral fallback.
            // Never infer the active library pack from the physical theme folder.

            if (changed)
            {
                SaveIndex(index);
            }

            PopulateRuntimeProperties(index);

            return new VisualPackLibrarySnapshot
            {
                MaximumPacks = MaximumLibraryPacks,
                ActivePackId = index.ActivePackId ?? string.Empty,
                Packs = index.Packs
                    .OrderByDescending(x => x.IsActive)
                    .ThenByDescending(x => x.ImportedUtc)
                    .ToList()
            };
        }

        public void SetActivePack(string packId)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("A Visual Pack id is required.", nameof(packId));
            }

            var index = LoadIndex();
            var record = FindPack(index, packId);
            var sourceFolder = Path.Combine(libraryRoot, record.Id);
            ValidateStoredPack(sourceFolder);

            if (string.Equals(index.ActivePackId ?? string.Empty, record.Id, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(index.PendingPackId))
            {
                return;
            }

            index.ActivePackId = record.Id;
            index.PendingPackId = string.Empty;
            SaveIndex(index);
            logger?.Info($"[AnikiHelper][VisualPack] Active library pack set to '{record.Name}' ({record.Id}) without touching live theme files.");
        }

        public string GetPackFolder(string packId)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                return string.Empty;
            }

            var index = LoadIndex();
            var record = FindPack(index, packId);
            var sourceFolder = Path.Combine(libraryRoot, record.Id);
            ValidateStoredPack(sourceFolder);
            return sourceFolder;
        }

        public VisualPackImportResult Import(string zipFilePath, bool allowOneTemporaryOverflow = false, bool activateImportedPack = true)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                throw new FileNotFoundException("The selected Visual Pack ZIP file could not be found.", zipFilePath);
            }

            ResolveCompatibleThemePath();
            EnsureLibraryFolders();

            var stagingFolder = Path.Combine(visualPacksRoot, ".import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingFolder);

            try
            {
                VisualPackManifest sourceManifest;

                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    ValidateArchiveEnvelope(archive);
                    var entries = ResolveRequiredEntries(archive);
                    sourceManifest = ReadOptionalManifest(archive);

                    foreach (var spec in RequiredAssets)
                    {
                        var entry = entries[spec.FileName];
                        ValidateImage(entry, spec);
                        CopyEntry(entry, Path.Combine(stagingFolder, spec.FileName));
                    }
                }

                var contentHash = ComputeContentHash(stagingFolder);
                var index = LoadIndex();
                var indexChanged = UpgradeIndexMetadata(index);
                indexChanged = RemoveMissingLibraryEntries(index) || indexChanged;

                if (index.Packs.Count == 0)
                {
                    if (TryMigrateCurrentCustomPack(index))
                    {
                        indexChanged = true;
                    }

                    if (TryMigrateLegacyBackup(index))
                    {
                        indexChanged = true;
                    }
                }

                if (indexChanged)
                {
                    SaveIndex(index);
                }

                var packName = string.IsNullOrWhiteSpace(sourceManifest?.Name)
                    ? Path.GetFileNameWithoutExtension(zipFilePath)
                    : sourceManifest.Name.Trim();
                var author = sourceManifest?.Author?.Trim() ?? string.Empty;
                var description = sourceManifest?.Description?.Trim() ?? string.Empty;
                var hasStableIdentity = HasStableIdentity(sourceManifest);
                var sourcePackId = hasStableIdentity ? sourceManifest.Id.Trim() : string.Empty;
                var sourceVersion = hasStableIdentity ? sourceManifest.Version.Trim() : string.Empty;

                var existingIdentity = hasStableIdentity
                    ? index.Packs.FirstOrDefault(x =>
                        string.Equals(x.PackId, sourcePackId, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (existingIdentity != null)
                {
                    if (ComparePackVersions(sourceVersion, existingIdentity.Version) <= 0)
                    {
                        TryDeleteDirectory(stagingFolder);
                        if (activateImportedPack)
                        {
                            Apply(existingIdentity.LocalId);
                        }

                        return CreateImportResult(existingIdentity, true, false);
                    }

                    var updateResult = UpdateExistingPack(
                        index,
                        existingIdentity,
                        stagingFolder,
                        zipFilePath,
                        contentHash,
                        sourcePackId,
                        packName,
                        author,
                        sourceVersion,
                        description);

                    if (activateImportedPack)
                    {
                        Apply(existingIdentity.LocalId);
                    }

                    return updateResult;
                }

                var duplicate = index.Packs.FirstOrDefault(x =>
                    string.Equals(x.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase));

                // A content duplicate only wins over a new permanent identity when it is
                // a legacy record. This upgrades packs imported before PackId was stored,
                // while still honoring the rule that a genuinely new PackId is a new pack.
                if (duplicate != null && (!hasStableIdentity || string.IsNullOrWhiteSpace(duplicate.PackId)))
                {
                    if (hasStableIdentity && string.IsNullOrWhiteSpace(duplicate.PackId))
                    {
                        duplicate.PackId = sourcePackId;
                        duplicate.Name = packName;
                        duplicate.Author = author;
                        duplicate.Version = sourceVersion;
                        duplicate.Description = description;
                        duplicate.SourceFileName = Path.GetFileName(zipFilePath);
                        WriteNormalizedManifest(
                            Path.Combine(libraryRoot, duplicate.LocalId),
                            duplicate.PackId,
                            duplicate.Name,
                            duplicate.Author,
                            duplicate.Version,
                            duplicate.Description,
                            false);
                        SaveIndex(index);
                    }

                    TryDeleteDirectory(stagingFolder);
                    if (activateImportedPack)
                    {
                        Apply(duplicate.LocalId);
                    }

                    return CreateImportResult(duplicate, true, false);
                }

                var maximumAllowedForThisImport = MaximumLibraryPacks + (allowOneTemporaryOverflow ? 1 : 0);
                if (index.Packs.Count >= maximumAllowedForThisImport)
                {
                    throw new InvalidOperationException(
                        $"The Visual Pack library is full ({MaximumLibraryPacks}/{MaximumLibraryPacks}). Delete a pack before importing another one.");
                }

                var localId = CreateLocalId(contentHash, index);
                var destinationFolder = Path.Combine(libraryRoot, localId);

                WriteNormalizedManifest(
                    stagingFolder,
                    hasStableIdentity ? sourcePackId : localId,
                    packName,
                    author,
                    sourceVersion,
                    description,
                    false);
                Directory.Move(stagingFolder, destinationFolder);

                var record = new VisualPackLibraryPack
                {
                    LocalId = localId,
                    PackId = sourcePackId,
                    Name = packName,
                    Author = author,
                    Version = sourceVersion,
                    Description = description,
                    SourceFileName = Path.GetFileName(zipFilePath),
                    ContentHash = contentHash,
                    ImportedUtc = DateTime.UtcNow
                };

                index.Packs.Add(record);
                SaveIndex(index);

                if (activateImportedPack)
                {
                    try
                    {
                        Apply(localId);
                    }
                    catch
                    {
                        index = LoadIndex();
                        index.Packs.RemoveAll(x => string.Equals(x.LocalId, localId, StringComparison.OrdinalIgnoreCase));
                        if (string.Equals(index.ActivePackId, localId, StringComparison.OrdinalIgnoreCase))
                        {
                            index.ActivePackId = string.Empty;
                        }

                        SaveIndex(index);
                        TryDeleteDirectory(destinationFolder);
                        throw;
                    }
                }

                logger?.Info(activateImportedPack
                    ? $"[AnikiHelper][VisualPack] Imported and activated '{packName}' ({localId}, PackId: {FormatPackIdForLog(sourcePackId)}) without modifying theme files."
                    : $"[AnikiHelper][VisualPack] Imported '{packName}' ({localId}, PackId: {FormatPackIdForLog(sourcePackId)}) into the library without changing the active pack.");

                return CreateImportResult(record, false, false);
            }
            finally
            {
                TryDeleteDirectory(stagingFolder);
            }
        }

        public void Apply(string packId)
        {
            // Activation no longer copies anything into Themes Option\2.Interface\Images\153.Custom.
            // That directory is the theme-owned neutral fallback. The selected library pack is
            // loaded into WPF resources by AnikiThemeSettingsService whenever Custom is active.
            SetActivePack(packId);
        }

        public void Delete(string packId)
        {
            var index = LoadIndex();
            var record = FindPack(index, packId);

            var deletingActivePack = string.Equals(index.ActivePackId, record.Id, StringComparison.OrdinalIgnoreCase);

            var packFolder = Path.Combine(libraryRoot, record.Id);
            var deletedFolder = Path.Combine(visualPacksRoot, ".deleted-" + Guid.NewGuid().ToString("N"));
            var packMoved = false;
            var indexCommitted = false;

            try
            {
                if (Directory.Exists(packFolder))
                {
                    Directory.Move(packFolder, deletedFolder);
                    packMoved = true;
                }

                index.Packs.RemoveAll(x => string.Equals(x.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                if (deletingActivePack)
                {
                    index.ActivePackId = string.Empty;
                    index.PendingPackId = string.Empty;
                }

                SaveIndex(index);
                indexCommitted = true;
                TryDeleteDirectory(deletedFolder);
                logger?.Info($"[AnikiHelper][VisualPack] Deleted library pack '{record.Name}' ({record.Id}).");
            }
            catch
            {
                if (!indexCommitted && packMoved && !Directory.Exists(packFolder) && Directory.Exists(deletedFolder))
                {
                    Directory.Move(deletedFolder, packFolder);
                }

                throw;
            }
            finally
            {
                if (indexCommitted)
                {
                    TryDeleteDirectory(deletedFolder);
                }
            }
        }

        public void Export(string packId, string destinationZipPath)
        {
            if (string.IsNullOrWhiteSpace(destinationZipPath))
            {
                throw new ArgumentException("An export path is required.", nameof(destinationZipPath));
            }

            var index = LoadIndex();
            var record = FindPack(index, packId);
            var sourceFolder = Path.Combine(libraryRoot, record.Id);
            ValidateStoredPack(sourceFolder);

            var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var temporaryZip = destinationZipPath + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                using (var file = new FileStream(temporaryZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    foreach (var spec in RequiredAssets)
                    {
                        archive.CreateEntryFromFile(
                            Path.Combine(sourceFolder, spec.FileName),
                            spec.FileName,
                            CompressionLevel.Optimal);
                    }

                    var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
                    using (var stream = manifestEntry.Open())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(JsonConvert.SerializeObject(new VisualPackManifest
                        {
                            FormatVersion = SupportedFormatVersion,
                            Id = string.IsNullOrWhiteSpace(record.PackId) ? record.LocalId : record.PackId,
                            Name = record.Name,
                            Author = record.Author,
                            Version = record.Version,
                            Description = record.Description,
                            BuiltInSeed = false
                        }, Formatting.Indented));
                    }
                }

                File.Copy(temporaryZip, destinationZipPath, true);
                logger?.Info($"[AnikiHelper][VisualPack] Exported '{record.Name}' to '{destinationZipPath}'.");
            }
            finally
            {
                TryDeleteFile(temporaryZip);
            }
        }

        private VisualPackImportResult UpdateExistingPack(
            VisualPackLibraryIndex index,
            VisualPackLibraryPack record,
            string stagingFolder,
            string sourceZipPath,
            string contentHash,
            string stablePackId,
            string name,
            string author,
            string version,
            string description)
        {
            var destinationFolder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(destinationFolder);

            var backupFolder = Path.Combine(visualPacksRoot, ".update-backup-" + Guid.NewGuid().ToString("N"));
            var failedUpdateFolder = Path.Combine(visualPacksRoot, ".update-failed-" + Guid.NewGuid().ToString("N"));
            var previousPackId = record.PackId;
            var previousName = record.Name;
            var previousAuthor = record.Author;
            var previousVersion = record.Version;
            var previousDescription = record.Description;
            var previousSourceFileName = record.SourceFileName;
            var previousContentHash = record.ContentHash;
            var previousImportedUtc = record.ImportedUtc;
            var destinationMoved = false;
            var updateInstalled = false;

            WriteNormalizedManifest(
                stagingFolder,
                stablePackId,
                name,
                author,
                version,
                description,
                false);

            try
            {
                Directory.Move(destinationFolder, backupFolder);
                destinationMoved = true;
                Directory.Move(stagingFolder, destinationFolder);
                updateInstalled = true;

                record.PackId = stablePackId;
                record.Name = name;
                record.Author = author;
                record.Version = version;
                record.Description = description;
                record.SourceFileName = Path.GetFileName(sourceZipPath);
                record.ContentHash = contentHash;
                record.ImportedUtc = DateTime.UtcNow;
                SaveIndex(index);

                TryDeleteDirectory(backupFolder);
                logger?.Info(
                    $"[AnikiHelper][VisualPack] Updated '{record.Name}' ({record.LocalId}, PackId: {record.PackId}) from {previousVersion} to {record.Version}.");
                return CreateImportResult(record, false, true);
            }
            catch
            {
                if (updateInstalled)
                {
                    try
                    {
                        if (Directory.Exists(destinationFolder))
                        {
                            Directory.Move(destinationFolder, failedUpdateFolder);
                        }
                    }
                    catch (Exception rollbackFolderEx)
                    {
                        logger?.Warn(rollbackFolderEx, "[AnikiHelper][VisualPack] Failed to move the rejected update out of the library folder.");
                    }
                }

                if (destinationMoved && Directory.Exists(backupFolder) && !Directory.Exists(destinationFolder))
                {
                    Directory.Move(backupFolder, destinationFolder);
                }

                record.PackId = previousPackId;
                record.Name = previousName;
                record.Author = previousAuthor;
                record.Version = previousVersion;
                record.Description = previousDescription;
                record.SourceFileName = previousSourceFileName;
                record.ContentHash = previousContentHash;
                record.ImportedUtc = previousImportedUtc;

                try
                {
                    SaveIndex(index);
                }
                catch (Exception rollbackEx)
                {
                    logger?.Warn(rollbackEx, "[AnikiHelper][VisualPack] Failed to restore the library index after an update failure.");
                }

                throw;
            }
            finally
            {
                if (updateInstalled)
                {
                    TryDeleteDirectory(backupFolder);
                    TryDeleteDirectory(failedUpdateFolder);
                }
            }
        }

        private bool UpgradeIndexMetadata(VisualPackLibraryIndex index)
        {
            if (index == null)
            {
                return false;
            }

            var changed = index.Version < CurrentLibraryIndexVersion;
            NormalizeIndexStrings(index);

            foreach (var pack in index.Packs.Where(x => x != null && !string.IsNullOrWhiteSpace(x.LocalId)))
            {
                if (!string.IsNullOrWhiteSpace(pack.PackId))
                {
                    continue;
                }

                var manifest = ReadManifestFromFolder(Path.Combine(libraryRoot, pack.LocalId));
                try
                {
                    if (!HasStableIdentity(manifest))
                    {
                        continue;
                    }

                    pack.PackId = manifest.Id.Trim();
                    pack.Version = manifest.Version.Trim();
                    pack.Description = manifest.Description?.Trim() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(pack.Name) && !string.IsNullOrWhiteSpace(manifest.Name))
                    {
                        pack.Name = manifest.Name.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(pack.Author) && !string.IsNullOrWhiteSpace(manifest.Author))
                    {
                        pack.Author = manifest.Author.Trim();
                    }

                    changed = true;
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"[AnikiHelper][VisualPack] Stored metadata for '{pack.LocalId}' could not be upgraded.");
                }
            }

            return changed;
        }

        private static void NormalizeIndexStrings(VisualPackLibraryIndex index)
        {
            if (index == null)
            {
                return;
            }

            index.Packs = index.Packs ?? new List<VisualPackLibraryPack>();
            index.ActivePackId = index.ActivePackId ?? string.Empty;
            index.PendingPackId = index.PendingPackId ?? string.Empty;

            foreach (var pack in index.Packs.Where(x => x != null))
            {
                pack.LocalId = pack.LocalId ?? string.Empty;
                pack.PackId = pack.PackId ?? string.Empty;
                pack.Name = pack.Name ?? string.Empty;
                pack.Author = pack.Author ?? string.Empty;
                pack.Version = pack.Version ?? string.Empty;
                pack.Description = pack.Description ?? string.Empty;
                pack.SourceFileName = pack.SourceFileName ?? string.Empty;
                pack.ContentHash = pack.ContentHash ?? string.Empty;
            }
        }

        private static bool HasStableIdentity(VisualPackManifest manifest)
        {
            if (manifest == null)
            {
                return false;
            }

            var hasId = !string.IsNullOrWhiteSpace(manifest.Id);
            var hasVersion = !string.IsNullOrWhiteSpace(manifest.Version);

            if (!hasId && hasVersion)
            {
                throw new InvalidDataException("visualpack.json contains a version but no permanent id.");
            }

            if (!hasVersion)
            {
                // Manifests produced by older Helper builds contained a local id but no
                // version. They remain valid legacy packs, not permanent identities.
                return false;
            }

            CommunityVisualPackService.CompareVersions(manifest.Version.Trim(), manifest.Version.Trim());
            return true;
        }

        private static int ComparePackVersions(string incomingVersion, string installedVersion)
        {
            if (string.IsNullOrWhiteSpace(installedVersion))
            {
                return 1;
            }

            return CommunityVisualPackService.CompareVersions(incomingVersion, installedVersion);
        }

        private static VisualPackImportResult CreateImportResult(
            VisualPackLibraryPack record,
            bool wasAlreadyInLibrary,
            bool wasUpdated)
        {
            return new VisualPackImportResult
            {
                LocalId = record?.LocalId ?? string.Empty,
                StablePackId = record?.PackId ?? string.Empty,
                PackName = record?.Name ?? string.Empty,
                Author = record?.Author ?? string.Empty,
                Version = record?.Version ?? string.Empty,
                Description = record?.Description ?? string.Empty,
                WasAlreadyInLibrary = wasAlreadyInLibrary,
                WasUpdated = wasUpdated
            };
        }

        private static string FormatPackIdForLog(string packId)
        {
            return string.IsNullOrWhiteSpace(packId) ? "legacy" : packId;
        }

        private bool TryMigrateCurrentCustomPack(VisualPackLibraryIndex index)
        {
            try
            {
                var themePath = TryResolveCompatibleThemePath();
                if (string.IsNullOrWhiteSpace(themePath))
                {
                    return false;
                }

                var sourceFolder = GetCustomPackFolder(themePath);
                if (!HasAllRequiredFiles(sourceFolder))
                {
                    return false;
                }

                var manifest = ReadManifestFromFolder(sourceFolder);
                if (manifest?.BuiltInSeed == true)
                {
                    return false;
                }

                ValidateStoredPack(sourceFolder);
                var hash = ComputeContentHash(sourceFolder);
                if (string.Equals(hash, BuiltInCustomSeedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var existing = index.Packs.FirstOrDefault(x =>
                    string.Equals(x.ContentHash, hash, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    index.ActivePackId = existing.Id;
                    return true;
                }

                if (index.Packs.Count >= MaximumLibraryPacks)
                {
                    return false;
                }

                var localId = CreateLocalId(hash, index);
                var destinationFolder = Path.Combine(libraryRoot, localId);
                CopyPackFiles(sourceFolder, destinationFolder);

                var name = string.IsNullOrWhiteSpace(manifest?.Name)
                    ? "Current Custom Pack"
                    : manifest.Name.Trim();
                var author = manifest?.Author?.Trim() ?? string.Empty;

                var hasStableIdentity = HasStableIdentity(manifest);
                var stablePackId = hasStableIdentity ? manifest.Id.Trim() : string.Empty;
                var version = hasStableIdentity ? manifest.Version.Trim() : string.Empty;
                var description = manifest?.Description?.Trim() ?? string.Empty;

                WriteNormalizedManifest(
                    destinationFolder,
                    hasStableIdentity ? stablePackId : localId,
                    name,
                    author,
                    version,
                    description,
                    false);

                index.Packs.Add(new VisualPackLibraryPack
                {
                    LocalId = localId,
                    PackId = stablePackId,
                    Name = name,
                    Author = author,
                    Version = version,
                    Description = description,
                    SourceFileName = string.Empty,
                    ContentHash = hash,
                    ImportedUtc = DateTime.UtcNow
                });

                index.ActivePackId = localId;
                logger?.Info($"[AnikiHelper][VisualPack] Migrated the existing Custom pack into the library ({localId}).");
                return true;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][VisualPack] Existing Custom pack migration failed.");
                return false;
            }
        }

        private bool TryMigrateLegacyBackup(VisualPackLibraryIndex index)
        {
            try
            {
                var sourceFolder = Path.Combine(visualPacksRoot, "CustomBackup");
                if (!HasAllRequiredFiles(sourceFolder) || index.Packs.Count >= MaximumLibraryPacks)
                {
                    return false;
                }

                var manifest = ReadManifestFromFolder(sourceFolder);
                if (manifest?.BuiltInSeed == true)
                {
                    return false;
                }

                ValidateStoredPack(sourceFolder);
                var hash = ComputeContentHash(sourceFolder);
                if (string.Equals(hash, BuiltInCustomSeedHash, StringComparison.OrdinalIgnoreCase) ||
                    index.Packs.Any(x => string.Equals(x.ContentHash, hash, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                var localId = CreateLocalId(hash, index);
                var destinationFolder = Path.Combine(libraryRoot, localId);
                CopyPackFiles(sourceFolder, destinationFolder);

                var name = string.IsNullOrWhiteSpace(manifest?.Name)
                    ? "Previous Custom Pack"
                    : manifest.Name.Trim();
                var author = manifest?.Author?.Trim() ?? string.Empty;

                var hasStableIdentity = HasStableIdentity(manifest);
                var stablePackId = hasStableIdentity ? manifest.Id.Trim() : string.Empty;
                var version = hasStableIdentity ? manifest.Version.Trim() : string.Empty;
                var description = manifest?.Description?.Trim() ?? string.Empty;

                WriteNormalizedManifest(
                    destinationFolder,
                    hasStableIdentity ? stablePackId : localId,
                    name,
                    author,
                    version,
                    description,
                    false);

                index.Packs.Add(new VisualPackLibraryPack
                {
                    LocalId = localId,
                    PackId = stablePackId,
                    Name = name,
                    Author = author,
                    Version = version,
                    Description = description,
                    SourceFileName = string.Empty,
                    ContentHash = hash,
                    ImportedUtc = DateTime.UtcNow.AddTicks(-1)
                });

                logger?.Info($"[AnikiHelper][VisualPack] Migrated the previous Custom backup into the library ({localId}).");
                return true;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][VisualPack] Previous Custom backup migration failed.");
                return false;
            }
        }

        private bool RefreshActivePackFromTheme(VisualPackLibraryIndex index)
        {
            try
            {
                // Once Helper has an explicit active library selection, it is the source of truth.
                // The live Fullscreen theme can keep 153.Custom image files locked, so the
                // selected pack may intentionally be rendered directly from the Helper library
                // instead of being copied over those files. Only auto-detect from the theme
                // when no valid active library selection exists yet (migration/legacy case).
                if (!string.IsNullOrWhiteSpace(index?.ActivePackId) &&
                    index.Packs != null &&
                    index.Packs.Any(x => x != null &&
                        string.Equals(x.Id, index.ActivePackId, StringComparison.OrdinalIgnoreCase) &&
                        HasAllRequiredFiles(Path.Combine(libraryRoot, x.Id))))
                {
                    return false;
                }

                var themePath = TryResolveCompatibleThemePath();
                if (string.IsNullOrWhiteSpace(themePath))
                {
                    return false;
                }

                var targetFolder = GetCustomPackFolder(themePath);
                if (!HasAllRequiredFiles(targetFolder))
                {
                    if (!string.IsNullOrEmpty(index.ActivePackId))
                    {
                        index.ActivePackId = string.Empty;
                        return true;
                    }

                    return false;
                }

                var manifest = ReadManifestFromFolder(targetFolder);
                if (manifest?.BuiltInSeed == true)
                {
                    if (!string.IsNullOrEmpty(index.ActivePackId))
                    {
                        index.ActivePackId = string.Empty;
                        return true;
                    }

                    return false;
                }

                var hash = ComputeContentHash(targetFolder);
                if (string.Equals(hash, BuiltInCustomSeedHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(index.ActivePackId))
                    {
                        index.ActivePackId = string.Empty;
                        return true;
                    }

                    return false;
                }

                var matchingPack = index.Packs.FirstOrDefault(x =>
                    string.Equals(x.ContentHash, hash, StringComparison.OrdinalIgnoreCase));
                var detectedId = matchingPack?.Id ?? string.Empty;

                if (!string.Equals(index.ActivePackId ?? string.Empty, detectedId, StringComparison.OrdinalIgnoreCase))
                {
                    index.ActivePackId = detectedId;
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][VisualPack] Active pack detection failed.");
            }

            return false;
        }

        private bool PromoteLegacyPendingSelection(VisualPackLibraryIndex index)
        {
            if (index == null || string.IsNullOrWhiteSpace(index.PendingPackId))
            {
                return false;
            }

            var pending = index.Packs?.FirstOrDefault(x => x != null &&
                string.Equals(x.Id, index.PendingPackId, StringComparison.OrdinalIgnoreCase));

            if (pending != null && HasAllRequiredFiles(Path.Combine(libraryRoot, pending.Id)))
            {
                index.ActivePackId = pending.Id;
                logger?.Info($"[AnikiHelper][VisualPack] Migrated queued pack '{pending.Name}' ({pending.Id}) to runtime activation.");
            }

            index.PendingPackId = string.Empty;
            return true;
        }

        private void PopulateRuntimeProperties(VisualPackLibraryIndex index)
        {
            foreach (var pack in index.Packs)
            {
                pack.FolderPath = Path.Combine(libraryRoot, pack.Id);
                pack.PreviewPath = Path.Combine(pack.FolderPath, "MainBackground.jpg");
                pack.SizeBytes = GetDirectorySize(pack.FolderPath);
                pack.IsActive = string.Equals(index.ActivePackId, pack.Id, StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool RemoveMissingLibraryEntries(VisualPackLibraryIndex index)
        {
            var before = index.Packs.Count;
            var previousActivePackId = index.ActivePackId ?? string.Empty;
            index.Packs.RemoveAll(x =>
                string.IsNullOrWhiteSpace(x?.Id) ||
                !HasAllRequiredFiles(Path.Combine(libraryRoot, x.Id)));

            if (!index.Packs.Any(x => string.Equals(x.Id, index.ActivePackId, StringComparison.OrdinalIgnoreCase)))
            {
                index.ActivePackId = string.Empty;
            }

            return before != index.Packs.Count ||
                   !string.Equals(previousActivePackId, index.ActivePackId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private VisualPackLibraryIndex LoadIndex()
        {
            EnsureLibraryFolders();

            if (!File.Exists(indexFilePath))
            {
                return new VisualPackLibraryIndex();
            }

            try
            {
                var json = File.ReadAllText(indexFilePath);
                var index = JsonConvert.DeserializeObject<VisualPackLibraryIndex>(json) ?? new VisualPackLibraryIndex();
                index.Packs = index.Packs ?? new List<VisualPackLibraryPack>();
                index.ActivePackId = index.ActivePackId ?? string.Empty;
                index.PendingPackId = index.PendingPackId ?? string.Empty;
                NormalizeIndexStrings(index);
                return index;
            }
            catch (Exception ex)
            {
                var backupPath = indexFilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try
                {
                    File.Copy(indexFilePath, backupPath, true);
                }
                catch
                {
                }

                logger?.Warn(ex, "[AnikiHelper][VisualPack] Library index was invalid and has been reset.");
                return new VisualPackLibraryIndex();
            }
        }

        private void SaveIndex(VisualPackLibraryIndex index)
        {
            EnsureLibraryFolders();
            index.Version = CurrentLibraryIndexVersion;
            index.Packs = index.Packs ?? new List<VisualPackLibraryPack>();
            NormalizeIndexStrings(index);

            var temporaryPath = indexFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            var json = JsonConvert.SerializeObject(index, Formatting.Indented);

            try
            {
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));

                if (File.Exists(indexFilePath))
                {
                    File.Replace(temporaryPath, indexFilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, indexFilePath);
                }
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private void EnsureLibraryFolders()
        {
            Directory.CreateDirectory(visualPacksRoot);
            Directory.CreateDirectory(libraryRoot);
        }

        private static VisualPackLibraryPack FindPack(VisualPackLibraryIndex index, string packId)
        {
            var record = index.Packs.FirstOrDefault(x =>
                string.Equals(x.Id, packId, StringComparison.OrdinalIgnoreCase));

            if (record == null)
            {
                throw new InvalidOperationException("The selected Visual Pack no longer exists in the library.");
            }

            return record;
        }

        private static string CreateLocalId(string contentHash, VisualPackLibraryIndex index)
        {
            var baseId = "pack-" + contentHash.Substring(0, 16).ToLowerInvariant();
            var candidate = baseId;
            var suffix = 2;

            while (index.Packs.Any(x => string.Equals(x.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = baseId + "-" + suffix;
                suffix++;
            }

            return candidate;
        }

        private static string ComputeContentHash(string folder)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var spec in RequiredAssets.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    var nameBytes = Encoding.UTF8.GetBytes(spec.FileName.ToLowerInvariant());
                    sha.TransformBlock(nameBytes, 0, nameBytes.Length, nameBytes, 0);

                    using (var stream = File.OpenRead(Path.Combine(folder, spec.FileName)))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sha.TransformBlock(buffer, 0, read, buffer, 0);
                        }
                    }
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }

        private static void WriteNormalizedManifest(
            string folder,
            string packId,
            string packName,
            string author,
            string version,
            string description,
            bool builtInSeed)
        {
            var manifest = new VisualPackManifest
            {
                FormatVersion = SupportedFormatVersion,
                Id = packId ?? string.Empty,
                Name = packName ?? string.Empty,
                Author = author ?? string.Empty,
                Version = version ?? string.Empty,
                Description = description ?? string.Empty,
                BuiltInSeed = builtInSeed
            };

            var manifestPath = Path.Combine(folder, ManifestFileName);
            var temporaryPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(manifest, Formatting.Indented),
                    new UTF8Encoding(false));

                if (File.Exists(manifestPath))
                {
                    File.Replace(temporaryPath, manifestPath, null);
                }
                else
                {
                    File.Move(temporaryPath, manifestPath);
                }
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static VisualPackManifest ReadManifestFromFolder(string folder)
        {
            try
            {
                var path = Path.Combine(folder, ManifestFileName);
                return File.Exists(path)
                    ? JsonConvert.DeserializeObject<VisualPackManifest>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private string ResolveCompatibleThemePath()
        {
            var result = TryResolveCompatibleThemePath();
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }

            throw new InvalidOperationException(
                "The selected Fullscreen theme does not support Custom Visual Packs. Install the matching Aniki ReMake update first.");
        }

        private string TryResolveCompatibleThemePath()
        {
            var themeId = api.ApplicationSettings?.FullscreenTheme;
            if (string.IsNullOrWhiteSpace(themeId))
            {
                return null;
            }

            var roots = new[]
            {
                api.Paths?.ConfigurationPath,
                api.Paths?.ApplicationPath
            };

            foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(root, "Themes", "Fullscreen", themeId);
                if (File.Exists(Path.Combine(candidate, "AnikiThemeSettings.yaml")) &&
                    File.Exists(Path.Combine(candidate, "Themes Option", "2.Interface", "VisualPack", "153.Custom.xaml")))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string GetCustomPackFolder(string themePath)
        {
            return Path.Combine(themePath, "Themes Option", "2.Interface", "Images", CustomPackFolderName);
        }

        private static void ValidateArchiveEnvelope(ZipArchive archive)
        {
            if (archive.Entries.Count == 0)
            {
                throw new InvalidDataException("The selected ZIP file is empty.");
            }

            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException($"The Visual Pack contains too many files ({archive.Entries.Count}/{MaximumArchiveEntries}).");
            }

            long totalLength = 0;
            foreach (var entry in archive.Entries)
            {
                var normalized = (entry.FullName ?? string.Empty).Replace('\\', '/');
                var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                if (Path.IsPathRooted(normalized) || normalized.IndexOf(':') >= 0 || segments.Any(x => x == ".."))
                {
                    throw new InvalidDataException("The ZIP contains an unsafe file path: " + entry.FullName);
                }

                totalLength += entry.Length;
                if (totalLength > MaximumArchiveUncompressedBytes)
                {
                    throw new InvalidDataException("The uncompressed Visual Pack is larger than 200 MB.");
                }
            }
        }

        private static Dictionary<string, ZipArchiveEntry> ResolveRequiredEntries(ZipArchive archive)
        {
            var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var spec in RequiredAssets)
            {
                var matches = archive.Entries
                    .Where(x => !string.IsNullOrEmpty(x.Name) &&
                                string.Equals(x.Name, spec.FileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    throw new InvalidDataException("Missing required image: " + spec.FileName);
                }

                if (matches.Count > 1)
                {
                    throw new InvalidDataException("The ZIP contains more than one file named " + spec.FileName + ".");
                }

                result[spec.FileName] = matches[0];
            }

            return result;
        }

        private static VisualPackManifest ReadOptionalManifest(ZipArchive archive)
        {
            var entries = archive.Entries
                .Where(x => !string.IsNullOrEmpty(x.Name) &&
                            string.Equals(x.Name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count == 0)
            {
                return null;
            }

            if (entries.Count > 1)
            {
                throw new InvalidDataException("The ZIP contains more than one visualpack.json manifest.");
            }

            var entry = entries[0];
            if (entry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("visualpack.json is larger than 64 KB.");
            }

            try
            {
                string json;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                var manifest = JsonConvert.DeserializeObject<VisualPackManifest>(json);
                if (manifest == null)
                {
                    throw new InvalidDataException("visualpack.json is empty or invalid.");
                }

                if (manifest.FormatVersion != SupportedFormatVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported Visual Pack format version: {manifest.FormatVersion}. Expected version {SupportedFormatVersion}.");
                }

                return manifest;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("visualpack.json could not be read: " + ex.Message, ex);
            }
        }

        private static void ValidateStoredPack(string folder)
        {
            foreach (var spec in RequiredAssets)
            {
                var path = Path.Combine(folder, spec.FileName);
                if (!File.Exists(path))
                {
                    throw new InvalidDataException("Missing required image: " + spec.FileName);
                }

                ValidateImageFile(path, spec);
            }
        }

        private static void ValidateImage(ZipArchiveEntry entry, VisualPackAssetSpec spec)
        {
            if (entry.Length <= 0 || entry.Length > MaximumImageBytes)
            {
                throw new InvalidDataException(
                    $"{spec.FileName} has an invalid file size. Maximum allowed size is 25 MB.");
            }

            try
            {
                using (var stream = entry.Open())
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    ValidateImageStream(buffer, spec);
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(spec.FileName + " is not a valid JPEG image: " + ex.Message, ex);
            }
        }

        private static void ValidateImageFile(string path, VisualPackAssetSpec spec)
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumImageBytes)
            {
                throw new InvalidDataException(
                    $"{spec.FileName} has an invalid file size. Maximum allowed size is 25 MB.");
            }

            using (var stream = File.OpenRead(path))
            {
                ValidateImageStream(stream, spec);
            }
        }

        private static void ValidateImageStream(Stream stream, VisualPackAssetSpec spec)
        {
            using (var image = Image.FromStream(stream, false, true))
            {
                if (image.RawFormat.Guid != ImageFormat.Jpeg.Guid)
                {
                    throw new InvalidDataException(spec.FileName + " must be a real JPEG image.");
                }

                if (image.Width != spec.Width || image.Height != spec.Height)
                {
                    throw new InvalidDataException(
                        $"{spec.FileName} has the wrong dimensions: {image.Width}x{image.Height}. Expected {spec.Width}x{spec.Height}.");
                }
            }
        }

        private static void CopyEntry(ZipArchiveEntry entry, string destination)
        {
            using (var source = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
            }
        }

        private static void CopyPackFiles(string sourceFolder, string destinationFolder)
        {
            Directory.CreateDirectory(destinationFolder);

            foreach (var spec in RequiredAssets)
            {
                File.Copy(
                    Path.Combine(sourceFolder, spec.FileName),
                    Path.Combine(destinationFolder, spec.FileName),
                    true);
            }

            var manifestPath = Path.Combine(sourceFolder, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                File.Copy(manifestPath, Path.Combine(destinationFolder, ManifestFileName), true);
            }
        }

        private static void CopyExistingPackFiles(string sourceFolder, string destinationFolder)
        {
            Directory.CreateDirectory(destinationFolder);

            foreach (var spec in RequiredAssets)
            {
                var source = Path.Combine(sourceFolder, spec.FileName);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(destinationFolder, spec.FileName), true);
                }
            }

            var manifest = Path.Combine(sourceFolder, ManifestFileName);
            if (File.Exists(manifest))
            {
                File.Copy(manifest, Path.Combine(destinationFolder, ManifestFileName), true);
            }
        }

        private static void RestorePackFiles(string backupFolder, string targetFolder, bool hadCompleteTarget, bool hadManifest)
        {
            if (hadCompleteTarget)
            {
                foreach (var spec in RequiredAssets)
                {
                    var backup = Path.Combine(backupFolder, spec.FileName);
                    if (File.Exists(backup))
                    {
                        File.Copy(backup, Path.Combine(targetFolder, spec.FileName), true);
                    }
                }
            }

            var targetManifest = Path.Combine(targetFolder, ManifestFileName);
            var backupManifest = Path.Combine(backupFolder, ManifestFileName);
            if (hadManifest && File.Exists(backupManifest))
            {
                File.Copy(backupManifest, targetManifest, true);
            }
            else if (!hadManifest)
            {
                TryDeleteFile(targetManifest);
            }
        }

        private static bool HasAllRequiredFiles(string folder)
        {
            return Directory.Exists(folder) && RequiredAssets.All(x => File.Exists(Path.Combine(folder, x.FileName)));
        }

        private static long GetDirectorySize(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                        .Sum(x => new FileInfo(x).Length)
                    : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static void TryMoveDirectory(string sourceFolder, string destinationFolder)
        {
            try
            {
                Directory.Move(sourceFolder, destinationFolder);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
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
    }
}
