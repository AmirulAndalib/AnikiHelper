using AnikiHelper.Services.Packs;
using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AnikiHelper.Services.VisualPacks
{
    public sealed class CommunityVisualPackCatalog
    {
        [JsonProperty("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonProperty("packs")]
        public List<CommunityVisualPackCatalogItem> Packs { get; set; } = new List<CommunityVisualPackCatalogItem>();
    }

    public sealed class CommunityVisualPackCatalogItem
    {
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

        [JsonProperty("previewUrl")]
        public string PreviewUrl { get; set; }

        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; }

        [JsonProperty("publishedAt")]
        public string PublishedAt { get; set; }

        [JsonProperty("updatedAt")]
        public string UpdatedAt { get; set; }

        [JsonProperty("featured")]
        public bool Featured { get; set; }
    }

    public sealed class CommunityVisualPackInstallation
    {
        public string CommunityId { get; set; }
        public string LocalPackId { get; set; }
        public string Version { get; set; }
        public DateTime InstalledUtc { get; set; }
    }

    internal sealed class CommunityVisualPackInstallationIndex
    {
        public int Version { get; set; } = 1;
        public List<CommunityVisualPackInstallation> Packs { get; set; } = new List<CommunityVisualPackInstallation>();
    }

    internal sealed class CommunityVisualPackManifest
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
    }

    public sealed class CommunityVisualPackCatalogResult
    {
        public List<CommunityVisualPackCatalogItem> Packs { get; set; } = new List<CommunityVisualPackCatalogItem>();
        public bool UsedCachedCatalog { get; set; }
    }

    public sealed class CommunityVisualPackInstallResult
    {
        public string PackName { get; set; }
        public string Version { get; set; }
        public bool WasUpdate { get; set; }
        public string LocalPackId { get; set; }
    }

    public sealed class CommunityVisualPackService : IDisposable
    {
        public const string CatalogUrl = "https://raw.githubusercontent.com/Mike-Aniki/AnikiCommunityPacks/main/catalog.json";
        private const int SupportedCatalogFormatVersion = 1;
        private const int SupportedPackFormatVersion = 1;
        private const string ManifestFileName = "visualpack.json";

        private static readonly Regex SafeIdRegex = new Regex("^[A-Za-z0-9._-]{3,120}$", RegexOptions.Compiled);
        private static readonly Regex SemVerRegex = new Regex(
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$",
            RegexOptions.Compiled);

        private readonly global::AnikiHelper.AnikiHelper plugin;
        private readonly IPlayniteAPI api;
        private readonly ILogger logger;
        private readonly HttpClient http;
        private readonly string cacheRoot;
        private readonly string previewCacheRoot;
        private readonly string catalogCachePath;
        private readonly string installationsPath;
        private bool disposed;

        public CommunityVisualPackService(global::AnikiHelper.AnikiHelper plugin, IPlayniteAPI api, string pluginUserDataPath, ILogger logger)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.logger = logger;

            cacheRoot = Path.Combine(AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "VisualPacks"), "Community");
            previewCacheRoot = Path.Combine(cacheRoot, "Previews");
            catalogCachePath = Path.Combine(cacheRoot, "catalog.json");
            installationsPath = Path.Combine(cacheRoot, "installations.json");

            Directory.CreateDirectory(cacheRoot);
            Directory.CreateDirectory(previewCacheRoot);

            http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AnikiHelper-CommunityPacks/1.0");
        }

        public async Task<CommunityVisualPackCatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            string json = null;
            var usedCache = false;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl))
                {
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true
                    };

                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }

                ValidateCatalog(json);
                WriteTextAtomic(catalogCachePath, json);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks] Online catalog load failed.");

                if (!File.Exists(catalogCachePath))
                {
                    throw new InvalidOperationException("The Community Packs catalog could not be downloaded and no cached catalog is available.", ex);
                }

                json = File.ReadAllText(catalogCachePath);
                ValidateCatalog(json);
                usedCache = true;
            }

            var catalog = JsonConvert.DeserializeObject<CommunityVisualPackCatalog>(json) ?? new CommunityVisualPackCatalog();
            return new CommunityVisualPackCatalogResult
            {
                UsedCachedCatalog = usedCache,
                Packs = (catalog.Packs ?? new List<CommunityVisualPackCatalogItem>())
                    .OrderByDescending(x => x.Featured)
                    .ThenByDescending(x => ParseDateSafe(x.UpdatedAt))
                    .ThenBy(x => x.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }

        public async Task<string> GetPreviewPathAsync(CommunityVisualPackCatalogItem item, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateCatalogItem(item);

            if (!TryGetHttpsUri(item.PreviewUrl, out var previewUri))
            {
                return null;
            }

            var safeVersion = MakeFileNameSafe(item.Version);
            var extension = GetSafeImageExtension(previewUri.AbsolutePath);
            var path = Path.Combine(previewCacheRoot, MakeFileNameSafe(item.Id) + "-" + safeVersion + extension);

            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            try
            {
                using (var response = await http.GetAsync(previewUri, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes == null || bytes.Length == 0)
                    {
                        return null;
                    }

                    Directory.CreateDirectory(previewCacheRoot);
                    var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllBytes(temporary, bytes);
                        File.Copy(temporary, path, true);
                    }
                    finally
                    {
                        TryDeleteFile(temporary);
                    }
                }

                CleanupOldPreviewVersions(item.Id, path);
                return path;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks] Preview download failed for " + item.Id + ".");
                return null;
            }
        }

        public Dictionary<string, CommunityVisualPackInstallation> GetInstalledPacks()
        {
            ThrowIfDisposed();
            var index = LoadInstallationIndex();
            var local = plugin.GetCustomVisualPackLibrary();
            var localIds = new HashSet<string>(
                (local?.Packs ?? new List<VisualPackLibraryPack>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id),
                StringComparer.OrdinalIgnoreCase);

            var changed = index.Packs.RemoveAll(x =>
                x == null ||
                string.IsNullOrWhiteSpace(x.CommunityId) ||
                string.IsNullOrWhiteSpace(x.LocalPackId) ||
                !localIds.Contains(x.LocalPackId)) > 0;

            if (changed)
            {
                SaveInstallationIndex(index);
            }

            return index.Packs
                .GroupBy(x => x.CommunityId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.InstalledUtc).First(), StringComparer.OrdinalIgnoreCase);
        }

        public async Task<CommunityVisualPackInstallResult> InstallOrUpdateAsync(
            CommunityVisualPackCatalogItem item,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateCatalogItem(item);

            if (!TryGetHttpsUri(item.DownloadUrl, out var downloadUri))
            {
                throw new InvalidDataException("The Visual Pack download URL is invalid or is not HTTPS.");
            }

            var installationIndex = LoadInstallationIndex();
            var existing = installationIndex.Packs.FirstOrDefault(x =>
                string.Equals(x.CommunityId, item.Id, StringComparison.OrdinalIgnoreCase));
            var localSnapshot = plugin.GetCustomVisualPackLibrary();
            var localIds = new HashSet<string>(
                (localSnapshot?.Packs ?? new List<VisualPackLibraryPack>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id),
                StringComparer.OrdinalIgnoreCase);

            if (existing != null && !localIds.Contains(existing.LocalPackId))
            {
                installationIndex.Packs.Remove(existing);
                existing = null;
                SaveInstallationIndex(installationIndex);
            }

            var wasUpdate = existing != null;
            var wasActive = existing != null && string.Equals(
                localSnapshot?.ActivePackId ?? string.Empty,
                existing.LocalPackId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            if (wasUpdate && CompareVersions(item.Version, existing.Version) <= 0)
            {
                return new CommunityVisualPackInstallResult
                {
                    PackName = item.Name,
                    Version = existing.Version,
                    WasUpdate = false,
                    LocalPackId = existing.LocalPackId
                };
            }

            var tempFolder = Path.Combine(cacheRoot, "Temp");
            Directory.CreateDirectory(tempFolder);
            var tempZip = Path.Combine(tempFolder, MakeFileNameSafe(item.Id) + "-" + MakeFileNameSafe(item.Version) + "-" + Guid.NewGuid().ToString("N") + ".zip");

            try
            {
                using (var response = await http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                    }
                }

                ValidateDownloadedManifest(tempZip, item);

                // Community install/update only adds the pack to the Helper library. Activation is controlled by Fullscreen > Selected Visual Pack.
                VisualPackImportResult importResult = null;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(() => importResult = plugin.ImportCustomVisualPack(tempZip, wasUpdate));
                }
                else
                {
                    importResult = plugin.ImportCustomVisualPack(tempZip, wasUpdate);
                }

                if (importResult == null || string.IsNullOrWhiteSpace(importResult.PackId))
                {
                    throw new InvalidOperationException("Aniki Helper did not return a valid local Visual Pack after import.");
                }

                if (existing != null &&
                    !string.Equals(existing.LocalPackId, importResult.PackId, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (dispatcher != null && !dispatcher.CheckAccess())
                        {
                            dispatcher.Invoke(() => plugin.DeleteCustomVisualPack(existing.LocalPackId));
                        }
                        else
                        {
                            plugin.DeleteCustomVisualPack(existing.LocalPackId);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn(ex, "[AnikiHelper][CommunityPacks] Old pack version could not be removed after update.");

                        // If this update created a new local library entry, roll it back so a
                        // failed replacement can never leave the 20-pack library over capacity.
                        if (importResult.WasAlreadyInLibrary == false)
                        {
                            try
                            {
                                if (dispatcher != null && !dispatcher.CheckAccess())
                                {
                                    dispatcher.Invoke(() => plugin.DeleteCustomVisualPack(importResult.PackId));
                                }
                                else
                                {
                                    plugin.DeleteCustomVisualPack(importResult.PackId);
                                }
                            }
                            catch (Exception cleanupEx)
                            {
                                logger?.Warn(cleanupEx, "[AnikiHelper][CommunityPacks] Failed to roll back the newly imported update after replacement failure.");
                            }
                        }

                        throw;
                    }
                }

                // Updating the currently selected pack must keep it selected. A new install or
                // an update of a non-active pack must never steal activation from the user.
                if (wasActive && !string.IsNullOrWhiteSpace(importResult.PackId))
                {
                    if (dispatcher != null && !dispatcher.CheckAccess())
                    {
                        dispatcher.Invoke(() => plugin.ApplyCustomVisualPack(importResult.PackId));
                    }
                    else
                    {
                        plugin.ApplyCustomVisualPack(importResult.PackId);
                    }
                }

                installationIndex = LoadInstallationIndex();
                installationIndex.Packs.RemoveAll(x => string.Equals(x.CommunityId, item.Id, StringComparison.OrdinalIgnoreCase));
                installationIndex.Packs.Add(new CommunityVisualPackInstallation
                {
                    CommunityId = item.Id,
                    LocalPackId = importResult.PackId,
                    Version = item.Version,
                    InstalledUtc = DateTime.UtcNow
                });
                SaveInstallationIndex(installationIndex);

                return new CommunityVisualPackInstallResult
                {
                    PackName = item.Name,
                    Version = item.Version,
                    WasUpdate = wasUpdate,
                    LocalPackId = importResult.PackId
                };
            }
            finally
            {
                TryDeleteFile(tempZip);
            }
        }

        public bool Uninstall(string communityId)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(communityId))
            {
                return false;
            }

            var installationIndex = LoadInstallationIndex();
            var existing = installationIndex.Packs.FirstOrDefault(x =>
                x != null && string.Equals(x.CommunityId, communityId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                return false;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (!string.IsNullOrWhiteSpace(existing.LocalPackId))
            {
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(() => plugin.DeleteCustomVisualPack(existing.LocalPackId));
                }
                else
                {
                    plugin.DeleteCustomVisualPack(existing.LocalPackId);
                }
            }

            installationIndex = LoadInstallationIndex();
            installationIndex.Packs.RemoveAll(x =>
                x != null && string.Equals(x.CommunityId, communityId, StringComparison.OrdinalIgnoreCase));
            SaveInstallationIndex(installationIndex);

            return true;
        }

        public static int CompareVersions(string left, string right)
        {
            var a = ParseSemVersion(left);
            var b = ParseSemVersion(right);

            var result = a.Major.CompareTo(b.Major);
            if (result != 0) return result;
            result = a.Minor.CompareTo(b.Minor);
            if (result != 0) return result;
            result = a.Patch.CompareTo(b.Patch);
            if (result != 0) return result;

            if (string.IsNullOrEmpty(a.PreRelease) && string.IsNullOrEmpty(b.PreRelease)) return 0;
            if (string.IsNullOrEmpty(a.PreRelease)) return 1;
            if (string.IsNullOrEmpty(b.PreRelease)) return -1;

            var aa = a.PreRelease.Split('.');
            var bb = b.PreRelease.Split('.');
            var length = Math.Max(aa.Length, bb.Length);
            for (var i = 0; i < length; i++)
            {
                if (i >= aa.Length) return -1;
                if (i >= bb.Length) return 1;

                var aNum = int.TryParse(aa[i], out var ai);
                var bNum = int.TryParse(bb[i], out var bi);
                if (aNum && bNum)
                {
                    result = ai.CompareTo(bi);
                }
                else if (aNum != bNum)
                {
                    result = aNum ? -1 : 1;
                }
                else
                {
                    result = string.CompareOrdinal(aa[i], bb[i]);
                }

                if (result != 0) return result;
            }

            return 0;
        }

        private void ValidateCatalog(string json)
        {
            CommunityVisualPackCatalog catalog;
            try
            {
                catalog = JsonConvert.DeserializeObject<CommunityVisualPackCatalog>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The Community Packs catalog is not valid JSON.", ex);
            }

            if (catalog == null || catalog.FormatVersion != SupportedCatalogFormatVersion)
            {
                throw new InvalidDataException("Unsupported Community Packs catalog format.");
            }

            catalog.Packs = catalog.Packs ?? new List<CommunityVisualPackCatalogItem>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in catalog.Packs)
            {
                ValidateCatalogItem(item);
                if (!ids.Add(item.Id))
                {
                    throw new InvalidDataException("The Community catalog contains a duplicate pack id: " + item.Id);
                }
            }
        }

        private static void ValidateCatalogItem(CommunityVisualPackCatalogItem item)
        {
            if (item == null)
            {
                throw new InvalidDataException("The Community catalog contains an empty Visual Pack entry.");
            }

            if (string.IsNullOrWhiteSpace(item.Id) || !SafeIdRegex.IsMatch(item.Id))
            {
                throw new InvalidDataException("Invalid Community Pack id: " + (item.Id ?? "<empty>"));
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                throw new InvalidDataException("Community Pack '" + item.Id + "' has no name.");
            }

            ParseSemVersion(item.Version);

            Uri previewUri;
            if (!TryGetHttpsUri(item.PreviewUrl, out previewUri))
            {
                throw new InvalidDataException("Community Pack '" + item.Id + "' has an invalid preview URL.");
            }

            Uri downloadUri;
            if (!TryGetHttpsUri(item.DownloadUrl, out downloadUri))
            {
                throw new InvalidDataException("Community Pack '" + item.Id + "' has an invalid download URL.");
            }
        }

        private static void ValidateDownloadedManifest(string zipPath, CommunityVisualPackCatalogItem item)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var entries = archive.Entries
                        .Where(x => !string.IsNullOrEmpty(x.Name) &&
                                    string.Equals(x.Name, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (entries.Count != 1)
                    {
                        throw new InvalidDataException("The downloaded Visual Pack must contain exactly one visualpack.json file.");
                    }

                    CommunityVisualPackManifest manifest;
                    using (var stream = entries[0].Open())
                    using (var reader = new StreamReader(stream))
                    {
                        manifest = JsonConvert.DeserializeObject<CommunityVisualPackManifest>(reader.ReadToEnd());
                    }

                    if (manifest == null || manifest.FormatVersion != SupportedPackFormatVersion)
                    {
                        throw new InvalidDataException("The downloaded Visual Pack manifest has an unsupported format version.");
                    }

                    if (!string.Equals(manifest.Id ?? string.Empty, item.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The downloaded Visual Pack id does not match the Community catalog.");
                    }

                    if (!string.Equals(manifest.Version ?? string.Empty, item.Version ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The downloaded Visual Pack version does not match the Community catalog.");
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The downloaded Community Visual Pack is not a valid ZIP package: " + ex.Message, ex);
            }
        }

        private CommunityVisualPackInstallationIndex LoadInstallationIndex()
        {
            if (!File.Exists(installationsPath))
            {
                return new CommunityVisualPackInstallationIndex();
            }

            try
            {
                var index = JsonConvert.DeserializeObject<CommunityVisualPackInstallationIndex>(File.ReadAllText(installationsPath))
                            ?? new CommunityVisualPackInstallationIndex();
                index.Packs = index.Packs ?? new List<CommunityVisualPackInstallation>();
                return index;
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][CommunityPacks] Installation index was invalid and has been reset.");
                return new CommunityVisualPackInstallationIndex();
            }
        }

        private void SaveInstallationIndex(CommunityVisualPackInstallationIndex index)
        {
            index = index ?? new CommunityVisualPackInstallationIndex();
            index.Version = 1;
            index.Packs = index.Packs ?? new List<CommunityVisualPackInstallation>();
            WriteTextAtomic(installationsPath, JsonConvert.SerializeObject(index, Formatting.Indented));
        }

        private static void WriteTextAtomic(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, text ?? string.Empty, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Copy(temporary, path, true);
                    File.Delete(temporary);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                TryDeleteFile(temporary);
            }
        }

        private static DateTime ParseDateSafe(string value)
        {
            return DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;
        }

        private static bool TryGetHttpsUri(string value, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
                !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uri = candidate;
            return true;
        }

        private static string GetSafeImageExtension(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            return ".jpg";
        }

        private static string MakeFileNameSafe(string value)
        {
            var result = string.IsNullOrWhiteSpace(value) ? "pack" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(c, '_');
            }
            return result;
        }

        private void CleanupOldPreviewVersions(string id, string keepPath)
        {
            try
            {
                var prefix = MakeFileNameSafe(id) + "-";
                foreach (var file in Directory.GetFiles(previewCacheRoot, prefix + "*"))
                {
                    if (!string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteFile(file);
                    }
                }
            }
            catch
            {
            }
        }

        private static SemVersion ParseSemVersion(string value)
        {
            var match = SemVerRegex.Match(value ?? string.Empty);
            if (!match.Success)
            {
                throw new InvalidDataException("Invalid Visual Pack version: " + (value ?? "<empty>"));
            }

            return new SemVersion
            {
                Major = long.Parse(match.Groups[1].Value),
                Minor = long.Parse(match.Groups[2].Value),
                Patch = long.Parse(match.Groups[3].Value),
                PreRelease = match.Groups[4].Success ? match.Groups[4].Value : string.Empty
            };
        }

        private sealed class SemVersion
        {
            public long Major { get; set; }
            public long Minor { get; set; }
            public long Patch { get; set; }
            public string PreRelease { get; set; }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(CommunityVisualPackService));
            }
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
            catch
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            http.Dispose();
        }
    }
}
