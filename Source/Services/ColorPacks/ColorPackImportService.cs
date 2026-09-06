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
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;

namespace AnikiHelper.Services.ColorPacks
{
    public sealed class ColorPackImportResult
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

    public sealed class ColorPackLibrarySnapshot
    {
        public int MaximumPacks { get; set; }
        public string ActivePackId { get; set; }
        public List<ColorPackLibraryPack> Packs { get; set; } = new List<ColorPackLibraryPack>();
    }

    public sealed class ColorPackLibraryPack
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
        public long SizeBytes { get; set; }
    }

    internal sealed class ColorPackLibraryIndex
    {
        public int Version { get; set; } = 1;
        public string ActivePackId { get; set; }
        public List<ColorPackLibraryPack> Packs { get; set; } = new List<ColorPackLibraryPack>();
    }

    internal sealed class ColorPackManifest
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

        [JsonProperty("template")]
        public string Template { get; set; }

        [JsonProperty("resource")]
        public string Resource { get; set; }
    }

    internal sealed class ColorPackImportService
    {
        public const int MaximumLibraryPacks = 20;

        private const int SupportedFormatVersion = 1;
        private const int MaximumArchiveEntries = 8;
        private const long MaximumArchiveUncompressedBytes = 3L * 1024L * 1024L;
        private const long MaximumManifestBytes = 64L * 1024L;
        private const long MaximumColorXamlBytes = 2L * 1024L * 1024L;
        private const int MaximumColorXamlElements = 2000;
        private const string ManifestFileName = "colorpack.json";
        private const string ColorsFileName = "colors.xaml";

        private static readonly XNamespace PresentationNamespace =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace XamlNamespace =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace SystemNamespace =
            "clr-namespace:System;assembly=mscorlib";

        private static readonly HashSet<string> AllowedPresentationElements =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ResourceDictionary",
                "Color",
                "SolidColorBrush",
                "LinearGradientBrush",
                "RadialGradientBrush",
                "GradientStop",
                "RotateTransform",
                "LinearGradientBrush.RelativeTransform",
                "Style",
                "Setter"
            };

        private readonly IPlayniteAPI api;
        private readonly ILogger logger;
        private readonly string colorPacksRoot;
        private readonly string libraryRoot;
        private readonly string indexFilePath;

        public ColorPackImportService(IPlayniteAPI api, string pluginUserDataPath, ILogger logger)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.logger = logger;

            colorPacksRoot = AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "ColorPacks");
            libraryRoot = Path.Combine(colorPacksRoot, "Library");
            indexFilePath = Path.Combine(colorPacksRoot, "index.json");
        }

        public ColorPackLibrarySnapshot GetLibrary()
        {
            EnsureLibraryFolders();
            var index = LoadIndex();
            var changed = RemoveMissingLibraryEntries(index);
            if (changed)
            {
                SaveIndex(index);
            }

            PopulateRuntimeProperties(index);
            return new ColorPackLibrarySnapshot
            {
                MaximumPacks = MaximumLibraryPacks,
                ActivePackId = index.ActivePackId ?? string.Empty,
                Packs = index.Packs
                    .OrderByDescending(x => x.IsActive)
                    .ThenByDescending(x => x.ImportedUtc)
                    .ToList()
            };
        }

        public ColorPackImportResult Import(string zipFilePath, bool activateImportedPack = false)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                throw new FileNotFoundException("The selected Color Pack ZIP file could not be found.", zipFilePath);
            }

            ResolveCompatibleThemePath();
            EnsureLibraryFolders();

            var stagingFolder = Path.Combine(colorPacksRoot, ".import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingFolder);

            try
            {
                ColorPackManifest manifest;
                string normalizedColorXaml;

                using (var archive = ZipFile.OpenRead(zipFilePath))
                {
                    ValidateArchiveEnvelope(archive);
                    var manifestEntry = GetRequiredRootEntry(archive, ManifestFileName);
                    var colorsEntry = GetRequiredRootEntry(archive, ColorsFileName);
                    manifest = ReadManifest(manifestEntry);
                    ValidateManifest(manifest);
                    normalizedColorXaml = ReadAndNormalizeColorXaml(colorsEntry);

                    CopyEntry(manifestEntry, Path.Combine(stagingFolder, ManifestFileName));
                    File.WriteAllText(
                        Path.Combine(stagingFolder, ColorsFileName),
                        normalizedColorXaml,
                        new UTF8Encoding(false));
                }

                var contentHash = ComputeContentHash(normalizedColorXaml);
                var index = LoadIndex();
                var indexChanged = RemoveMissingLibraryEntries(index);
                if (indexChanged)
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

                    var result = UpdateExistingPack(
                        index,
                        existing,
                        stagingFolder,
                        zipFilePath,
                        contentHash,
                        manifest);

                    if (activateImportedPack)
                    {
                        SetActivePack(existing.LocalId);
                    }

                    return result;
                }

                if (index.Packs.Count >= MaximumLibraryPacks)
                {
                    throw new InvalidOperationException(
                        $"The Color Pack library is full ({MaximumLibraryPacks}/{MaximumLibraryPacks}). Delete a pack before importing another one.");
                }

                var localId = CreateLocalId(contentHash, index);
                var destinationFolder = Path.Combine(libraryRoot, localId);
                Directory.Move(stagingFolder, destinationFolder);

                var record = CreateLibraryRecord(localId, zipFilePath, contentHash, manifest);
                index.Packs.Add(record);
                SaveIndex(index);

                if (activateImportedPack)
                {
                    try
                    {
                        SetActivePack(localId);
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

                logger?.Info($"[AnikiHelper][ColorPack] Imported '{record.Name}' ({record.LocalId}, PackId: {record.PackId}, version: {record.Version}).");
                return CreateImportResult(record, false, false);
            }
            finally
            {
                TryDeleteDirectory(stagingFolder);
            }
        }

        public void SetActivePack(string localId)
        {
            if (string.IsNullOrWhiteSpace(localId))
            {
                throw new ArgumentException("A Color Pack local id is required.", nameof(localId));
            }

            var index = LoadIndex();
            var record = FindPack(index, localId);
            ValidateStoredPack(Path.Combine(libraryRoot, record.LocalId));

            if (string.Equals(index.ActivePackId, record.LocalId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            index.ActivePackId = record.LocalId;
            SaveIndex(index);
            logger?.Info($"[AnikiHelper][ColorPack] Active library pack set to '{record.Name}' ({record.LocalId}).");
        }

        public ResourceDictionary LoadResourceDictionary(string localId)
        {
            var index = LoadIndex();
            var record = FindPack(index, localId);
            var folder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(folder);

            var colorsPath = Path.Combine(folder, ColorsFileName);
            var normalizedXaml = ValidateAndNormalizeColorXaml(File.ReadAllText(colorsPath));
            var bytes = new UTF8Encoding(false).GetBytes(normalizedXaml);

            using (var stream = new MemoryStream(bytes, false))
            {
                var parserContext = new ParserContext
                {
                    BaseUri = new Uri(colorsPath, UriKind.Absolute)
                };

                var dictionary = XamlReader.Load(stream, parserContext) as ResourceDictionary;
                if (dictionary == null)
                {
                    throw new InvalidDataException("colors.xaml does not contain a ResourceDictionary.");
                }

                dictionary.Remove("BackgroundImageIndex");
                return dictionary;
            }
        }

        public void Delete(string localId)
        {
            var index = LoadIndex();
            var record = FindPack(index, localId);
            var deletingActivePack = string.Equals(index.ActivePackId, record.LocalId, StringComparison.OrdinalIgnoreCase);
            var packFolder = Path.Combine(libraryRoot, record.LocalId);
            var deletedFolder = Path.Combine(colorPacksRoot, ".deleted-" + Guid.NewGuid().ToString("N"));
            var moved = false;
            var committed = false;

            try
            {
                if (Directory.Exists(packFolder))
                {
                    Directory.Move(packFolder, deletedFolder);
                    moved = true;
                }

                index.Packs.RemoveAll(x => string.Equals(x.LocalId, record.LocalId, StringComparison.OrdinalIgnoreCase));
                if (deletingActivePack)
                {
                    index.ActivePackId = string.Empty;
                }

                SaveIndex(index);
                committed = true;
                TryDeleteDirectory(deletedFolder);
                logger?.Info($"[AnikiHelper][ColorPack] Deleted '{record.Name}' ({record.LocalId}).");
            }
            catch
            {
                if (!committed && moved && Directory.Exists(deletedFolder) && !Directory.Exists(packFolder))
                {
                    Directory.Move(deletedFolder, packFolder);
                }

                throw;
            }
            finally
            {
                if (committed)
                {
                    TryDeleteDirectory(deletedFolder);
                }
            }
        }

        public void Export(string localId, string destinationZipPath)
        {
            if (string.IsNullOrWhiteSpace(destinationZipPath))
            {
                throw new ArgumentException("An export path is required.", nameof(destinationZipPath));
            }

            var index = LoadIndex();
            var record = FindPack(index, localId);
            var folder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(folder);

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
                    archive.CreateEntryFromFile(
                        Path.Combine(folder, ManifestFileName),
                        ManifestFileName,
                        CompressionLevel.Optimal);
                    archive.CreateEntryFromFile(
                        Path.Combine(folder, ColorsFileName),
                        ColorsFileName,
                        CompressionLevel.Optimal);
                }

                File.Copy(temporaryZip, destinationZipPath, true);
            }
            finally
            {
                TryDeleteFile(temporaryZip);
            }
        }

        private ColorPackImportResult UpdateExistingPack(
            ColorPackLibraryIndex index,
            ColorPackLibraryPack record,
            string stagingFolder,
            string sourceZipPath,
            string contentHash,
            ColorPackManifest manifest)
        {
            var destinationFolder = Path.Combine(libraryRoot, record.LocalId);
            ValidateStoredPack(destinationFolder);

            var backupFolder = Path.Combine(colorPacksRoot, ".update-backup-" + Guid.NewGuid().ToString("N"));
            var failedFolder = Path.Combine(colorPacksRoot, ".update-failed-" + Guid.NewGuid().ToString("N"));
            var previous = CloneRecord(record);
            var destinationMoved = false;
            var updateInstalled = false;

            try
            {
                Directory.Move(destinationFolder, backupFolder);
                destinationMoved = true;
                Directory.Move(stagingFolder, destinationFolder);
                updateInstalled = true;

                ApplyManifestToRecord(record, sourceZipPath, contentHash, manifest);
                SaveIndex(index);
                TryDeleteDirectory(backupFolder);

                logger?.Info($"[AnikiHelper][ColorPack] Updated '{record.Name}' ({record.LocalId}) from {previous.Version} to {record.Version}.");
                return CreateImportResult(record, false, true);
            }
            catch
            {
                if (updateInstalled && Directory.Exists(destinationFolder))
                {
                    Directory.Move(destinationFolder, failedFolder);
                }

                if (destinationMoved && Directory.Exists(backupFolder) && !Directory.Exists(destinationFolder))
                {
                    Directory.Move(backupFolder, destinationFolder);
                }

                RestoreRecord(record, previous);
                try
                {
                    SaveIndex(index);
                }
                catch (Exception rollbackEx)
                {
                    logger?.Warn(rollbackEx, "[AnikiHelper][ColorPack] Failed to restore the library index after an update failure.");
                }

                throw;
            }
            finally
            {
                TryDeleteDirectory(backupFolder);
                TryDeleteDirectory(failedFolder);
            }
        }

        private static ColorPackLibraryPack CreateLibraryRecord(
            string localId,
            string sourceZipPath,
            string contentHash,
            ColorPackManifest manifest)
        {
            var record = new ColorPackLibraryPack { LocalId = localId };
            ApplyManifestToRecord(record, sourceZipPath, contentHash, manifest);
            return record;
        }

        private static void ApplyManifestToRecord(
            ColorPackLibraryPack record,
            string sourceZipPath,
            string contentHash,
            ColorPackManifest manifest)
        {
            record.PackId = manifest.Id.Trim();
            record.Name = string.IsNullOrWhiteSpace(manifest.Name)
                ? Path.GetFileNameWithoutExtension(sourceZipPath)
                : manifest.Name.Trim();
            record.Author = manifest.Author?.Trim() ?? string.Empty;
            record.Version = manifest.Version.Trim();
            record.Description = manifest.Description?.Trim() ?? string.Empty;
            record.SourceFileName = Path.GetFileName(sourceZipPath);
            record.ContentHash = contentHash;
            record.ImportedUtc = DateTime.UtcNow;
        }

        private static ColorPackLibraryPack CloneRecord(ColorPackLibraryPack source)
        {
            return new ColorPackLibraryPack
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

        private static void RestoreRecord(ColorPackLibraryPack target, ColorPackLibraryPack source)
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

        private static ColorPackImportResult CreateImportResult(
            ColorPackLibraryPack record,
            bool alreadyInstalled,
            bool updated)
        {
            return new ColorPackImportResult
            {
                LocalId = record?.LocalId ?? string.Empty,
                PackId = record?.PackId ?? string.Empty,
                PackName = record?.Name ?? string.Empty,
                Author = record?.Author ?? string.Empty,
                Version = record?.Version ?? string.Empty,
                Description = record?.Description ?? string.Empty,
                WasAlreadyInLibrary = alreadyInstalled,
                WasUpdated = updated
            };
        }

        private static void ValidateArchiveEnvelope(ZipArchive archive)
        {
            if (archive.Entries.Count == 0)
            {
                throw new InvalidDataException("The selected ZIP file is empty.");
            }

            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException($"The Color Pack contains too many files ({archive.Entries.Count}/{MaximumArchiveEntries}).");
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
                    throw new InvalidDataException("The uncompressed Color Pack is larger than 3 MB.");
                }
            }
        }

        private static ZipArchiveEntry GetRequiredRootEntry(ZipArchive archive, string fileName)
        {
            var matches = archive.Entries
                .Where(x => string.Equals((x.FullName ?? string.Empty).Replace('\\', '/'), fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                throw new InvalidDataException("Missing required Color Pack file: " + fileName);
            }

            if (matches.Count > 1)
            {
                throw new InvalidDataException("The ZIP contains more than one root file named " + fileName + ".");
            }

            return matches[0];
        }

        private static ColorPackManifest ReadManifest(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0 || entry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("colorpack.json has an invalid file size.");
            }

            try
            {
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream))
                {
                    return JsonConvert.DeserializeObject<ColorPackManifest>(reader.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("colorpack.json could not be read: " + ex.Message, ex);
            }
        }

        private static void ValidateManifest(ColorPackManifest manifest)
        {
            if (manifest == null)
            {
                throw new InvalidDataException("colorpack.json is empty or invalid.");
            }

            if (manifest.FormatVersion != SupportedFormatVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported Color Pack format version: {manifest.FormatVersion}. Expected version {SupportedFormatVersion}.");
            }

            if (!string.Equals(manifest.Type, "colorPack", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("colorpack.json does not describe a Color Pack.");
            }

            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                throw new InvalidDataException("colorpack.json does not contain a permanent pack id.");
            }

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                throw new InvalidDataException("colorpack.json does not contain a version.");
            }

            if (!string.IsNullOrWhiteSpace(manifest.Resource) &&
                !string.Equals(manifest.Resource.Trim(), ColorsFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The Color Pack resource must be colors.xaml.");
            }

            CommunityVisualPackService.CompareVersions(manifest.Version.Trim(), manifest.Version.Trim());
        }

        private static string ReadAndNormalizeColorXaml(ZipArchiveEntry entry)
        {
            if (entry.Length <= 0 || entry.Length > MaximumColorXamlBytes)
            {
                throw new InvalidDataException("colors.xaml has an invalid file size. Maximum allowed size is 2 MB.");
            }

            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                return ValidateAndNormalizeColorXaml(reader.ReadToEnd());
            }
        }

        private static string ValidateAndNormalizeColorXaml(string xaml)
        {
            if (string.IsNullOrWhiteSpace(xaml))
            {
                throw new InvalidDataException("colors.xaml is empty.");
            }

            XDocument document;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumColorXamlBytes
                };

                using (var stringReader = new StringReader(xaml))
                using (var xmlReader = XmlReader.Create(stringReader, settings))
                {
                    document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("colors.xaml is not valid XML: " + ex.Message, ex);
            }

            if (document.Root == null ||
                document.Root.Name.Namespace != PresentationNamespace ||
                document.Root.Name.LocalName != "ResourceDictionary")
            {
                throw new InvalidDataException("colors.xaml must contain a WPF ResourceDictionary.");
            }

            var elements = document.Root.DescendantsAndSelf().ToList();
            if (elements.Count > MaximumColorXamlElements)
            {
                throw new InvalidDataException("colors.xaml contains too many XAML elements.");
            }

            foreach (var element in elements.ToList())
            {
                if (element.Name.Namespace == SystemNamespace && element.Name.LocalName == "Int32")
                {
                    var systemKey = (string)element.Attribute(XamlNamespace + "Key");
                    if (string.Equals(systemKey, "BackgroundImageIndex", StringComparison.OrdinalIgnoreCase))
                    {
                        element.Remove();
                        continue;
                    }
                }

                if (element.Name.Namespace != PresentationNamespace ||
                    !AllowedPresentationElements.Contains(element.Name.LocalName))
                {
                    throw new InvalidDataException("colors.xaml contains a forbidden XAML element: " + element.Name);
                }

                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    if (attribute.Name.Namespace != XNamespace.None)
                    {
                        if (attribute.Name.Namespace != XamlNamespace || attribute.Name.LocalName != "Key")
                        {
                            throw new InvalidDataException("colors.xaml contains a forbidden XAML attribute: " + attribute.Name);
                        }
                    }
                    else if (string.Equals(attribute.Name.LocalName, "Source", StringComparison.OrdinalIgnoreCase))
                    {
                        // Imported dictionaries must be completely self-contained. In particular,
                        // ResourceDictionary.Source must never resolve another local or remote file.
                        throw new InvalidDataException("colors.xaml cannot reference an external ResourceDictionary.");
                    }

                    var value = attribute.Value?.Trim() ?? string.Empty;
                    if (value.IndexOf('{') >= 0)
                    {
                        var isAllowedResourceReference =
                            (value.StartsWith("{DynamicResource ", StringComparison.Ordinal) ||
                             value.StartsWith("{StaticResource ", StringComparison.Ordinal)) &&
                            value.EndsWith("}", StringComparison.Ordinal);
                        if (!isAllowedResourceReference)
                        {
                            throw new InvalidDataException("colors.xaml contains a forbidden markup extension.");
                        }
                    }
                }
            }

            var keyedResources = document.Root.Elements()
                .Select(x => (string)x.Attribute(XamlNamespace + "Key"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            if (keyedResources.Count != keyedResources.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                throw new InvalidDataException("colors.xaml contains duplicate resource keys.");
            }

            if (!document.Descendants(PresentationNamespace + "Color").Any())
            {
                throw new InvalidDataException("colors.xaml does not contain any color resources.");
            }

            return document.ToString(SaveOptions.DisableFormatting);
        }

        private static string ComputeContentHash(string normalizedColorXaml)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = new UTF8Encoding(false).GetBytes(normalizedColorXaml ?? string.Empty);
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
            }
        }

        private static string CreateLocalId(string contentHash, ColorPackLibraryIndex index)
        {
            var baseId = "color-" + contentHash.Substring(0, 16).ToLowerInvariant();
            var candidate = baseId;
            var suffix = 2;
            while (index.Packs.Any(x => string.Equals(x.LocalId, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = baseId + "-" + suffix;
                suffix++;
            }

            return candidate;
        }

        private static ColorPackLibraryPack FindPack(ColorPackLibraryIndex index, string localId)
        {
            var record = index.Packs.FirstOrDefault(x =>
                string.Equals(x.LocalId, localId, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                throw new InvalidOperationException("The selected Color Pack no longer exists in the library.");
            }

            return record;
        }

        private void ValidateStoredPack(string folder)
        {
            var manifestPath = Path.Combine(folder, ManifestFileName);
            var colorsPath = Path.Combine(folder, ColorsFileName);
            if (!File.Exists(manifestPath) || !File.Exists(colorsPath))
            {
                throw new InvalidDataException("The stored Color Pack is incomplete.");
            }

            var manifestInfo = new FileInfo(manifestPath);
            var colorsInfo = new FileInfo(colorsPath);
            if (manifestInfo.Length <= 0 || manifestInfo.Length > MaximumManifestBytes ||
                colorsInfo.Length <= 0 || colorsInfo.Length > MaximumColorXamlBytes)
            {
                throw new InvalidDataException("The stored Color Pack contains an invalid file size.");
            }

            ColorPackManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<ColorPackManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The stored colorpack.json is invalid.", ex);
            }

            ValidateManifest(manifest);
            ValidateAndNormalizeColorXaml(File.ReadAllText(colorsPath));
        }

        private void PopulateRuntimeProperties(ColorPackLibraryIndex index)
        {
            foreach (var pack in index.Packs)
            {
                pack.FolderPath = Path.Combine(libraryRoot, pack.LocalId);
                pack.SizeBytes = GetDirectorySize(pack.FolderPath);
                pack.IsActive = string.Equals(index.ActivePackId, pack.LocalId, StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool RemoveMissingLibraryEntries(ColorPackLibraryIndex index)
        {
            var before = index.Packs.Count;
            var previousActive = index.ActivePackId ?? string.Empty;
            index.Packs.RemoveAll(x =>
                x == null ||
                string.IsNullOrWhiteSpace(x.LocalId) ||
                !File.Exists(Path.Combine(libraryRoot, x.LocalId, ManifestFileName)) ||
                !File.Exists(Path.Combine(libraryRoot, x.LocalId, ColorsFileName)));

            if (!index.Packs.Any(x => string.Equals(x.LocalId, index.ActivePackId, StringComparison.OrdinalIgnoreCase)))
            {
                index.ActivePackId = string.Empty;
            }

            return before != index.Packs.Count ||
                   !string.Equals(previousActive, index.ActivePackId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private ColorPackLibraryIndex LoadIndex()
        {
            EnsureLibraryFolders();
            if (!File.Exists(indexFilePath))
            {
                return new ColorPackLibraryIndex();
            }

            try
            {
                var index = JsonConvert.DeserializeObject<ColorPackLibraryIndex>(File.ReadAllText(indexFilePath))
                            ?? new ColorPackLibraryIndex();
                NormalizeIndex(index);
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

                logger?.Warn(ex, "[AnikiHelper][ColorPack] Library index was invalid and has been reset.");
                return new ColorPackLibraryIndex();
            }
        }

        private void SaveIndex(ColorPackLibraryIndex index)
        {
            EnsureLibraryFolders();
            index.Version = 1;
            NormalizeIndex(index);

            var temporaryPath = indexFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(index, Newtonsoft.Json.Formatting.Indented),
                    new UTF8Encoding(false));

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

        private static void NormalizeIndex(ColorPackLibraryIndex index)
        {
            index.ActivePackId = index.ActivePackId ?? string.Empty;
            index.Packs = index.Packs ?? new List<ColorPackLibraryPack>();
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

        private string ResolveCompatibleThemePath()
        {
            var themeId = api.ApplicationSettings?.FullscreenTheme;
            if (!string.IsNullOrWhiteSpace(themeId))
            {
                var roots = new[] { api.Paths?.ConfigurationPath, api.Paths?.ApplicationPath };
                foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(root, "Themes", "Fullscreen", themeId);
                    if (File.Exists(Path.Combine(candidate, "AnikiThemeSettings.yaml")) &&
                        Directory.Exists(Path.Combine(candidate, "Themes Option", "2.Interface", "ThemeColors")))
                    {
                        return candidate;
                    }
                }
            }

            throw new InvalidOperationException(
                "The selected Fullscreen theme does not support Color Packs. Install the matching Aniki ReMake update first.");
        }

        private void EnsureLibraryFolders()
        {
            Directory.CreateDirectory(colorPacksRoot);
            Directory.CreateDirectory(libraryRoot);
        }

        private static void CopyEntry(ZipArchiveEntry entry, string destination)
        {
            using (var source = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
            }
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

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
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
