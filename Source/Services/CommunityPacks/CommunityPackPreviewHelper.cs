using AnikiHelper.Services.Packs;
using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnikiHelper.Services.CommunityPacks
{
    internal static class CommunityPackPreviewHelper
    {
        private const string FallbackResourceUri = "pack://application:,,,/AnikiHelper;component/Assets/CommunityPackFallback.png";

        public static ImageSource LoadImageOrFallback(string path, int decodePixelWidth = 420)
        {
            var image = LoadImage(path, decodePixelWidth);
            return image ?? LoadFallback(decodePixelWidth);
        }

        public static ImageSource LoadImage(string path, int decodePixelWidth = 420)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodePixelWidth > 0)
                {
                    image.DecodePixelWidth = decodePixelWidth;
                }
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        public static ImageSource LoadFallback(int decodePixelWidth = 420)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodePixelWidth > 0)
                {
                    image.DecodePixelWidth = decodePixelWidth;
                }
                image.UriSource = new Uri(FallbackResourceUri, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        public static string TryGetInheritedPreviewPath(
            string pluginUserDataPath,
            string packType,
            string localPackId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginUserDataPath) ||
                    string.IsNullOrWhiteSpace(packType) ||
                    string.IsNullOrWhiteSpace(localPackId))
                {
                    return null;
                }

                var inheritedRoot = Path.Combine(AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "CommunityPacks"), "InheritedPreviews");
                if (!Directory.Exists(inheritedRoot))
                {
                    return null;
                }

                var prefix = MakeFileNameSafe(packType) + "-" + MakeFileNameSafe(localPackId);
                return FindPreviewByPrefix(inheritedRoot, prefix);
            }
            catch
            {
                return null;
            }
        }

        public static void InheritPreviewFromInstalledCommunityPack(
            string pluginUserDataPath,
            string sourcePackType,
            string sourceLocalPackId,
            string targetPackType,
            string targetLocalPackId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginUserDataPath) ||
                    string.IsNullOrWhiteSpace(sourcePackType) ||
                    string.IsNullOrWhiteSpace(sourceLocalPackId) ||
                    string.IsNullOrWhiteSpace(targetPackType) ||
                    string.IsNullOrWhiteSpace(targetLocalPackId))
                {
                    return;
                }

                // If the child was already installed directly from the Community catalog,
                // it owns a more specific preview and must never be replaced by the Complete Pack.
                var directPreview = TryGetCachedInstalledCommunityPreviewPath(
                    pluginUserDataPath,
                    targetPackType,
                    targetLocalPackId);
                if (!string.IsNullOrWhiteSpace(directPreview) && File.Exists(directPreview))
                {
                    return;
                }

                var sourcePreview = TryGetCachedInstalledCommunityPreviewPath(
                    pluginUserDataPath,
                    sourcePackType,
                    sourceLocalPackId);
                if (string.IsNullOrWhiteSpace(sourcePreview) || !File.Exists(sourcePreview))
                {
                    return;
                }

                var extension = Path.GetExtension(sourcePreview);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".jpg";
                }

                var inheritedRoot = Path.Combine(AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "CommunityPacks"), "InheritedPreviews");
                Directory.CreateDirectory(inheritedRoot);

                var prefix = MakeFileNameSafe(targetPackType) + "-" + MakeFileNameSafe(targetLocalPackId);
                foreach (var oldPath in Directory.GetFiles(inheritedRoot, prefix + "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.Delete(oldPath);
                    }
                    catch
                    {
                    }
                }

                var destination = Path.Combine(inheritedRoot, prefix + extension.ToLowerInvariant());
                File.Copy(sourcePreview, destination, true);
            }
            catch
            {
                // Preview inheritance is cosmetic and must never block pack application.
            }
        }

        public static bool InheritPreviewFromPackArchive(
            string pluginUserDataPath,
            string sourceZipPath,
            string targetPackType,
            string targetLocalPackId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginUserDataPath) ||
                    string.IsNullOrWhiteSpace(sourceZipPath) ||
                    !File.Exists(sourceZipPath) ||
                    string.IsNullOrWhiteSpace(targetPackType) ||
                    string.IsNullOrWhiteSpace(targetLocalPackId))
                {
                    return false;
                }

                // A directly installed Community pack owns its own cached preview and
                // always wins over a preview inherited from a Complete Pack.
                var directPreview = TryGetCachedInstalledCommunityPreviewPath(
                    pluginUserDataPath,
                    targetPackType,
                    targetLocalPackId);
                if (!string.IsNullOrWhiteSpace(directPreview) && File.Exists(directPreview))
                {
                    return true;
                }

                using (var archive = ZipFile.OpenRead(sourceZipPath))
                {
                    var matches = archive.Entries
                        .Where(entry =>
                            string.Equals(entry.FullName, "preview.jpg", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.FullName, "preview.png", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matches.Count != 1)
                    {
                        return false;
                    }

                    var previewEntry = matches[0];
                    const long maximumPreviewBytes = 20L * 1024L * 1024L;
                    if (previewEntry.Length <= 0 || previewEntry.Length > maximumPreviewBytes)
                    {
                        return false;
                    }

                    string extension;
                    using (var input = previewEntry.Open())
                    {
                        var header = new byte[8];
                        var read = input.Read(header, 0, header.Length);
                        var isJpeg = read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
                        var isPng = read >= 8 &&
                                    header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G' &&
                                    header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
                        if (!isJpeg && !isPng)
                        {
                            return false;
                        }
                        extension = isPng ? ".png" : ".jpg";
                    }

                    var inheritedRoot = Path.Combine(
                        AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "CommunityPacks"),
                        "InheritedPreviews");
                    Directory.CreateDirectory(inheritedRoot);

                    var prefix = MakeFileNameSafe(targetPackType) + "-" + MakeFileNameSafe(targetLocalPackId);
                    foreach (var oldPath in Directory.GetFiles(inheritedRoot, prefix + "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.Delete(oldPath);
                        }
                        catch
                        {
                        }
                    }

                    var destination = Path.Combine(inheritedRoot, prefix + extension);
                    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        using (var input = previewEntry.Open())
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                        }
                        File.Copy(temporary, destination, true);
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(temporary))
                            {
                                File.Delete(temporary);
                            }
                        }
                        catch
                        {
                        }
                    }

                    return File.Exists(destination);
                }
            }
            catch
            {
                // Embedded previews are cosmetic and must never block Complete Pack application.
                return false;
            }
        }

        public static string TryGetCachedInstalledCommunityPreviewPath(
            string pluginUserDataPath,
            string packType,
            string localPackId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginUserDataPath) ||
                    string.IsNullOrWhiteSpace(packType) ||
                    string.IsNullOrWhiteSpace(localPackId))
                {
                    return null;
                }

                var communityRoot = AnikiPackStorage.GetAreaRoot(pluginUserDataPath, "CommunityPacks");
                var installationPath = Path.Combine(communityRoot, "Installations", packType + ".json");
                if (!File.Exists(installationPath))
                {
                    return null;
                }

                var index = JsonConvert.DeserializeObject<CommunityPackInstallationIndex>(File.ReadAllText(installationPath));
                var installation = index?.Packs?.FirstOrDefault(x =>
                    x != null && string.Equals(x.LocalPackId, localPackId, StringComparison.OrdinalIgnoreCase));
                if (installation == null || string.IsNullOrWhiteSpace(installation.CommunityId))
                {
                    return null;
                }

                var previewRoot = Path.Combine(communityRoot, "Previews");
                if (!Directory.Exists(previewRoot))
                {
                    return null;
                }

                var typePart = MakeFileNameSafe(packType);
                var idPart = MakeFileNameSafe(installation.CommunityId);
                var versionPart = MakeFileNameSafe(installation.Version);
                var exactPrefix = typePart + "-" + idPart + "-" + versionPart;

                var exact = FindPreviewByPrefix(previewRoot, exactPrefix);
                if (!string.IsNullOrWhiteSpace(exact))
                {
                    return exact;
                }

                // Fallback for older cached versions when the installation index was
                // migrated before the version field was normalized.
                return FindPreviewByPrefix(previewRoot, typePart + "-" + idPart + "-");
            }
            catch
            {
                return null;
            }
        }

        private static string FindPreviewByPrefix(string folder, string prefix)
        {
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            return Directory.GetFiles(folder, prefix + "*", SearchOption.TopDirectoryOnly)
                .Where(path => allowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
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
    }
}
