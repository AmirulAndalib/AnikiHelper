using LibVLCSharp.Shared;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnikiHelper.Services.VideoPlayer
{
    /// <summary>
    /// Owns all LibVLC native/runtime objects for Aniki Video Player.
    /// The engine is initialized lazily and can be released when the feature window closes.
    /// </summary>
    internal sealed class LibVlcPlaybackEngine : IDisposable
    {
        private const string VideoLanPackageFolder = "VideoLAN.LibVLC.Windows.3.0.23.1";
        private static readonly object CoreInitializationLock = new object();
        private static bool coreInitialized;

        private readonly ILogger logger;
        private readonly object runtimeSync = new object();
        private LibVLC libVlc;
        private MediaPlayer mediaPlayer;
        private Media currentMedia;
        private bool initializationAttempted;
        private string initializationError = string.Empty;

        public LibVlcPlaybackEngine(ILogger logger)
        {
            this.logger = logger ?? LogManager.GetLogger();
        }

        public event EventHandler MediaPlayerChanged;
        public event EventHandler Playing;
        public event EventHandler Paused;
        public event EventHandler EndReached;
        public event EventHandler EncounteredError;

        public MediaPlayer MediaPlayer => mediaPlayer;
        public string InitializationError => initializationError ?? string.Empty;
        public bool IsInitialized => mediaPlayer != null && libVlc != null;

        public bool EnsureInitialized(string pluginDirectory, double volume)
        {
            lock (runtimeSync)
            {
                if (IsInitialized)
                {
                    return true;
                }

                if (initializationAttempted)
                {
                    return false;
                }

                initializationAttempted = true;

                try
                {
                    EnsureCoreInitialized(pluginDirectory);

                    libVlc = new LibVLC("--no-video-title-show");
                    mediaPlayer = new MediaPlayer(libVlc);
                    HookEvents();
                    mediaPlayer.Volume = ToVlcVolume(volume);
                    initializationError = string.Empty;
                    MediaPlayerChanged?.Invoke(this, EventArgs.Empty);

                    logger?.Info("[AnikiHelper][VideoPlayer] LibVLC playback engine initialized.");
                    return true;
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, "[AnikiHelper][VideoPlayer] LibVLC initialization failed.");
                    initializationError = "The VLC playback engine could not be initialized. Restore the LibVLC NuGet files and rebuild Aniki Helper.";
                    ReleaseRuntimeCore(resetInitializationAttempt: false);
                    return false;
                }
            }
        }

        public bool Play(string path, double volume)
        {
            lock (runtimeSync)
            {
                if (!IsInitialized || string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                StopMediaCore();

                var media = new Media(libVlc, new Uri(path, UriKind.Absolute));
                currentMedia = media;
                mediaPlayer.Media = media;
                mediaPlayer.Volume = ToVlcVolume(volume);

                return mediaPlayer.Play();
            }
        }

        public void StopMedia()
        {
            lock (runtimeSync)
            {
                StopMediaCore();
            }
        }

        private void StopMediaCore()
        {
            try
            {
                if (mediaPlayer != null)
                {
                    try { mediaPlayer.Stop(); } catch { }
                    try { mediaPlayer.Media = null; } catch { }
                }
            }
            finally
            {
                var oldMedia = currentMedia;
                currentMedia = null;
                try { oldMedia?.Dispose(); } catch { }
            }
        }

        public void SetVolume(double volume)
        {
            lock (runtimeSync)
            {
                if (mediaPlayer != null)
                {
                    mediaPlayer.Volume = ToVlcVolume(volume);
                }
            }
        }

        public IReadOnlyList<VlcTrackOption> GetAudioTracks()
        {
            lock (runtimeSync)
            {
                try
                {
                    if (mediaPlayer == null)
                    {
                        return Array.Empty<VlcTrackOption>();
                    }

                    var descriptions = mediaPlayer.AudioTrackDescription;
                    if (descriptions == null)
                    {
                        return Array.Empty<VlcTrackOption>();
                    }

                    var selected = mediaPlayer.AudioTrack;
                    return descriptions
                        .Select(x => new VlcTrackOption(x.Id, x.Name, x.Id == selected))
                        .ToArray();
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to enumerate audio tracks.");
                    return Array.Empty<VlcTrackOption>();
                }
            }
        }

        public bool SetAudioTrack(int id)
        {
            lock (runtimeSync)
            {
                try
                {
                    return mediaPlayer != null && mediaPlayer.SetAudioTrack(id);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to select audio track.");
                    return false;
                }
            }
        }

        public IReadOnlyList<VlcTrackOption> GetSubtitleTracks()
        {
            lock (runtimeSync)
            {
                try
                {
                    if (mediaPlayer == null)
                    {
                        return Array.Empty<VlcTrackOption>();
                    }

                    var descriptions = mediaPlayer.SpuDescription;
                    if (descriptions == null)
                    {
                        return Array.Empty<VlcTrackOption>();
                    }

                    var selected = mediaPlayer.Spu;
                    return descriptions
                        .Select(x => new VlcTrackOption(x.Id, x.Name, x.Id == selected))
                        .ToArray();
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to enumerate subtitle tracks.");
                    return Array.Empty<VlcTrackOption>();
                }
            }
        }

        public bool SetSubtitleTrack(int id)
        {
            lock (runtimeSync)
            {
                try
                {
                    return mediaPlayer != null && mediaPlayer.SetSpu(id);
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to select subtitle track.");
                    return false;
                }
            }
        }

        public IReadOnlyList<VlcChapterOption> GetChapters()
        {
            lock (runtimeSync)
            {
                try
                {
                    if (mediaPlayer == null)
                    {
                        return Array.Empty<VlcChapterOption>();
                    }

                    var descriptions = mediaPlayer.FullChapterDescriptions(-1);
                    if (descriptions == null || descriptions.Length == 0)
                    {
                        return Array.Empty<VlcChapterOption>();
                    }

                    var selected = mediaPlayer.Chapter;
                    var result = new List<VlcChapterOption>(descriptions.Length);
                    for (var i = 0; i < descriptions.Length; i++)
                    {
                        var chapter = descriptions[i];

                        result.Add(new VlcChapterOption(
                            i,
                            chapter.Name,
                            chapter.TimeOffset,
                            chapter.Duration,
                            i == selected));
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to enumerate chapters.");
                    return Array.Empty<VlcChapterOption>();
                }
            }
        }

        public bool SetChapter(int chapterIndex)
        {
            lock (runtimeSync)
            {
                try
                {
                    if (mediaPlayer == null || chapterIndex < 0 || chapterIndex >= mediaPlayer.ChapterCount)
                    {
                        return false;
                    }

                    mediaPlayer.Chapter = chapterIndex;
                    return true;
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to select chapter.");
                    return false;
                }
            }
        }

        public bool SetPlaybackRate(float rate)
        {
            lock (runtimeSync)
            {
                try
                {
                    return mediaPlayer != null && mediaPlayer.SetRate(rate) == 0;
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to change playback speed.");
                    return false;
                }
            }
        }

        public float GetPlaybackRate()
        {
            lock (runtimeSync)
            {
                try
                {
                    return mediaPlayer?.Rate ?? 1.0f;
                }
                catch
                {
                    return 1.0f;
                }
            }
        }

        public bool ApplyAspectMode(string mode)
        {
            lock (runtimeSync)
            {
                try
                {
                    if (mediaPlayer == null)
                    {
                        return false;
                    }

                    mediaPlayer.Scale = 0.0f;
                    mediaPlayer.AspectRatio = null;
                    mediaPlayer.CropGeometry = string.Empty;

                    switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "fill":
                            mediaPlayer.CropGeometry = "16:9";
                            break;
                        case "16:9":
                            mediaPlayer.AspectRatio = "16:9";
                            break;
                        case "4:3":
                            mediaPlayer.AspectRatio = "4:3";
                            break;
                        case "21:9":
                            mediaPlayer.AspectRatio = "21:9";
                            break;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to change aspect ratio.");
                    return false;
                }
            }
        }

        public bool SetTime(long timeMs)
        {
            lock (runtimeSync)
            {
                try
                {
                    if (mediaPlayer == null)
                    {
                        return false;
                    }

                    var length = Math.Max(0L, mediaPlayer.Length);
                    var target = Math.Max(0L, timeMs);
                    if (length > 0)
                    {
                        target = Math.Min(length, target);
                    }

                    mediaPlayer.Time = target;
                    return true;
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to set playback time.");
                    return false;
                }
            }
        }

        public VlcPlaybackInfo GetPlaybackInfo()
        {
            lock (runtimeSync)
            {
                var result = new VlcPlaybackInfo();
                try
                {
                    if (mediaPlayer != null)
                    {
                        result.DurationMs = Math.Max(0L, mediaPlayer.Length);
                        result.Fps = mediaPlayer.Fps;
                        result.AudioTrackId = mediaPlayer.AudioTrack;
                        result.SubtitleTrackId = mediaPlayer.Spu;
                        result.Chapter = mediaPlayer.Chapter;
                        result.ChapterCount = Math.Max(0, mediaPlayer.ChapterCount);
                        result.Rate = mediaPlayer.Rate;
                    }

                    var tracks = currentMedia?.Tracks;
                    if (tracks == null)
                    {
                        return result;
                    }

                    foreach (var track in tracks)
                    {
                        if (track.TrackType == TrackType.Video && string.IsNullOrWhiteSpace(result.VideoCodec))
                        {
                            result.VideoCodec = FourCcToString(track.Codec);
                            result.VideoBitrate = track.Bitrate;
                            var video = track.Data.Video;
                            result.Width = video.Width;
                            result.Height = video.Height;
                            if (video.FrameRateDen != 0)
                            {
                                result.Fps = video.FrameRateNum / (float)video.FrameRateDen;
                            }
                        }
                        else if (track.TrackType == TrackType.Audio && string.IsNullOrWhiteSpace(result.AudioCodec))
                        {
                            result.AudioCodec = FourCcToString(track.Codec);
                            result.AudioLanguage = track.Language ?? string.Empty;
                            result.AudioDescription = track.Description ?? string.Empty;
                            result.AudioBitrate = track.Bitrate;
                            var audio = track.Data.Audio;
                            result.AudioChannels = audio.Channels;
                            result.AudioRate = audio.Rate;
                        }
                    }
                }
                catch (Exception ex)
                {
                    global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to read media information.");
                }

                return result;
            }
        }

        private static string FourCcToString(uint value)
        {
            try
            {
                var bytes = BitConverter.GetBytes(value);
                var text = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
                return string.IsNullOrWhiteSpace(text) ? value.ToString("X8") : text.ToUpperInvariant();
            }
            catch
            {
                return value.ToString("X8");
            }
        }


        /// <summary>
        /// Releases the Media, MediaPlayer and LibVLC instance while keeping LibVLCSharp's
        /// process-level native loader intact. A later EnsureInitialized creates a fresh engine.
        /// </summary>
        public void Release()
        {
            ReleaseRuntime(resetInitializationAttempt: true);
        }

        private void ReleaseRuntime(bool resetInitializationAttempt)
        {
            lock (runtimeSync)
            {
                ReleaseRuntimeCore(resetInitializationAttempt);
            }
        }

        private void ReleaseRuntimeCore(bool resetInitializationAttempt)
        {
            try
            {
                StopMediaCore();
            }
            catch
            {
            }

            UnhookEvents();

            var oldPlayer = mediaPlayer;
            var oldLibVlc = libVlc;
            mediaPlayer = null;
            libVlc = null;

            try { oldPlayer?.Dispose(); } catch { }
            try { oldLibVlc?.Dispose(); } catch { }

            if (resetInitializationAttempt)
            {
                initializationAttempted = false;
                initializationError = string.Empty;
            }

            MediaPlayerChanged?.Invoke(this, EventArgs.Empty);
        }

        private void EnsureCoreInitialized(string pluginDirectory)
        {
            lock (CoreInitializationLock)
            {
                if (coreInitialized)
                {
                    return;
                }

                var nativeDirectory = ResolveLibVlcDirectory(pluginDirectory);
                if (!string.IsNullOrWhiteSpace(nativeDirectory))
                {
                    logger?.Info($"[AnikiHelper][VideoPlayer] Loading native LibVLC from: {nativeDirectory}");
                    Core.Initialize(nativeDirectory);
                }
                else
                {
                    logger?.Warn($"[AnikiHelper][VideoPlayer] Native LibVLC folder was not found. Falling back to LibVLCSharp discovery. PluginDir={pluginDirectory ?? "<null>"}");
                    Core.Initialize();
                }

                coreInitialized = true;
            }
        }

        private string ResolveLibVlcDirectory(string pluginDirectory)
        {
            try
            {
                var packagedArchitecture = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                var nugetArchitecture = Environment.Is64BitProcess ? "x64" : "x86";
                var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new List<string>();

                Action<string> addCandidate = path =>
                {
                    if (!string.IsNullOrWhiteSpace(path) &&
                        !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(path);
                    }
                };

                if (!string.IsNullOrWhiteSpace(pluginDirectory))
                {
                    addCandidate(Path.Combine(pluginDirectory, "libvlc", packagedArchitecture));
                    addCandidate(Path.Combine(pluginDirectory, "libvlc"));
                    addCandidate(Path.Combine(pluginDirectory, "runtimes", packagedArchitecture, "native"));
                    addCandidate(pluginDirectory);

                    // Development fallback: bin\Debug -> project -> packages\VideoLAN...\build\x86|x64.
                    var cursor = new DirectoryInfo(pluginDirectory);
                    for (var depth = 0; depth < 5 && cursor != null; depth++, cursor = cursor.Parent)
                    {
                        addCandidate(Path.Combine(cursor.FullName, "packages", VideoLanPackageFolder, "build", nugetArchitecture));
                    }
                }

                if (!string.IsNullOrWhiteSpace(appDirectory))
                {
                    addCandidate(Path.Combine(appDirectory, "libvlc", packagedArchitecture));
                    addCandidate(Path.Combine(appDirectory, "libvlc"));
                    addCandidate(Path.Combine(appDirectory, "runtimes", packagedArchitecture, "native"));
                }

                foreach (var candidate in candidates)
                {
                    if (ContainsLibVlc(candidate))
                    {
                        return candidate;
                    }
                }

                // Last-resort lookup is deliberately restricted to the plugin output tree and
                // runs only when Video Player is first used.
                if (!string.IsNullOrWhiteSpace(pluginDirectory) && Directory.Exists(pluginDirectory))
                {
                    try
                    {
                        foreach (var libPath in Directory.EnumerateFiles(pluginDirectory, "libvlc.dll", SearchOption.AllDirectories))
                        {
                            var directory = Path.GetDirectoryName(libPath);
                            if (ContainsLibVlc(directory))
                            {
                                return directory;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Recursive LibVLC lookup failed.");
                    }
                }
            }
            catch (Exception ex)
            {
                global::AnikiHelper.AnikiLog.Debug(logger, ex, "[AnikiHelper][VideoPlayer] Failed to resolve native LibVLC directory.");
            }

            return null;
        }

        private static bool ContainsLibVlc(string directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory) &&
                       File.Exists(Path.Combine(directory, "libvlc.dll")) &&
                       File.Exists(Path.Combine(directory, "libvlccore.dll"));
            }
            catch
            {
                return false;
            }
        }

        private void HookEvents()
        {
            if (mediaPlayer == null)
            {
                return;
            }

            mediaPlayer.Playing += MediaPlayer_Playing;
            mediaPlayer.Paused += MediaPlayer_Paused;
            mediaPlayer.EndReached += MediaPlayer_EndReached;
            mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        }

        private void UnhookEvents()
        {
            if (mediaPlayer == null)
            {
                return;
            }

            try { mediaPlayer.Playing -= MediaPlayer_Playing; } catch { }
            try { mediaPlayer.Paused -= MediaPlayer_Paused; } catch { }
            try { mediaPlayer.EndReached -= MediaPlayer_EndReached; } catch { }
            try { mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError; } catch { }
        }

        private void MediaPlayer_Playing(object sender, EventArgs e) => Playing?.Invoke(this, e);
        private void MediaPlayer_Paused(object sender, EventArgs e) => Paused?.Invoke(this, e);
        private void MediaPlayer_EndReached(object sender, EventArgs e) => EndReached?.Invoke(this, e);
        private void MediaPlayer_EncounteredError(object sender, EventArgs e) => EncounteredError?.Invoke(this, e);

        private static int ToVlcVolume(double volume)
        {
            return (int)Math.Round(Math.Max(0.0, Math.Min(1.0, volume)) * 100.0);
        }

        public void Dispose()
        {
            ReleaseRuntime(resetInitializationAttempt: true);
        }
    }

    internal sealed class VlcTrackOption
    {
        public VlcTrackOption(int id, string name, bool isSelected)
        {
            Id = id;
            Name = name ?? string.Empty;
            IsSelected = isSelected;
        }

        public int Id { get; }
        public string Name { get; }
        public bool IsSelected { get; }
    }

    internal sealed class VlcChapterOption
    {
        public VlcChapterOption(int index, string name, long timeOffsetMs, long durationMs, bool isSelected)
        {
            Index = index;
            Name = name ?? string.Empty;
            TimeOffsetMs = timeOffsetMs;
            DurationMs = durationMs;
            IsSelected = isSelected;
        }

        public int Index { get; }
        public string Name { get; }
        public long TimeOffsetMs { get; }
        public long DurationMs { get; }
        public bool IsSelected { get; }
    }

    internal sealed class VlcPlaybackInfo
    {
        public long DurationMs { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public float Fps { get; set; }
        public string VideoCodec { get; set; } = string.Empty;
        public uint VideoBitrate { get; set; }
        public string AudioCodec { get; set; } = string.Empty;
        public string AudioLanguage { get; set; } = string.Empty;
        public string AudioDescription { get; set; } = string.Empty;
        public uint AudioBitrate { get; set; }
        public uint AudioChannels { get; set; }
        public uint AudioRate { get; set; }
        public int AudioTrackId { get; set; }
        public int SubtitleTrackId { get; set; }
        public int Chapter { get; set; }
        public int ChapterCount { get; set; }
        public float Rate { get; set; } = 1.0f;
    }
}
