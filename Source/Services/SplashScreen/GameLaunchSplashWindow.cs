using Playnite.SDK.Models;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnikiHelper.Services.SplashScreen
{
    public class GameLaunchSplashWindow : Window
    {
        public Guid GameId { get; }
        private readonly Grid root;
        private readonly Grid visualContent;
        private readonly ScaleTransform contentScale;
        private readonly TranslateTransform contentTranslate;
        private readonly Border transitionVeil;

        private readonly Image backgroundImage;
        private readonly ScaleTransform backgroundScale;
        private readonly MediaElement backgroundVideo;
        private readonly bool isVideoSplash;
        private readonly SplashScreenVideoEndBehavior videoEndBehavior;
        private readonly double backgroundDimming;

        private Image gameLogo;
        private TranslateTransform gameLogoTranslate;
        private bool isClosingAnimated;

        public GameLaunchSplashWindow(
            Game game,
            string backgroundPath,
            string fallbackBackgroundPath,
            bool showLogo,
            SplashScreenLogoPosition logoPosition,
            bool videoSoundEnabled,
            SplashScreenVideoEndBehavior videoEndBehavior,
            double videoVolume,
            double backgroundDimming)
        {
            GameId = game?.Id ?? Guid.Empty;
            this.videoEndBehavior = videoEndBehavior;
            this.backgroundDimming = Math.Max(0, Math.Min(0.8, backgroundDimming));

            isVideoSplash = !string.IsNullOrWhiteSpace(backgroundPath)
                && File.Exists(backgroundPath)
                && SplashScreenMediaScanner.IsVideoFile(backgroundPath);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowState = WindowState.Maximized;
            Background = Brushes.Black;
            Focusable = false;
            ShowActivated = true;
            AllowsTransparency = false;

            // Keep the window itself fully opaque from the first frame. The visual content
            // animates over an immediate black frame, so Playnite is never briefly exposed.
            Opacity = 1;

            root = new Grid
            {
                Background = Brushes.Black,
                ClipToBounds = true
            };

            contentScale = new ScaleTransform(1.025, 1.025);
            contentTranslate = new TranslateTransform(0, 6);

            var contentTransformGroup = new TransformGroup();
            contentTransformGroup.Children.Add(contentScale);
            contentTransformGroup.Children.Add(contentTranslate);

            visualContent = new Grid
            {
                Background = Brushes.Black,
                ClipToBounds = true,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = contentTransformGroup
            };

            root.Children.Add(visualContent);

            backgroundScale = new ScaleTransform(1.0, 1.0);

            backgroundImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = backgroundScale
            };

            backgroundVideo = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.UniformToFill,
                Volume = Math.Max(0, Math.Min(1, videoVolume)),
                IsMuted = !videoSoundEnabled,
                Opacity = 0
            };

            backgroundVideo.MediaOpened += (_, __) =>
            {
                if (isClosingAnimated)
                {
                    return;
                }

                var fadeVideo = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(320),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                backgroundVideo.BeginAnimation(UIElement.OpacityProperty, fadeVideo);
            };

            backgroundVideo.MediaEnded += (_, __) =>
            {
                if (this.videoEndBehavior == SplashScreenVideoEndBehavior.ShowGameBackground)
                {
                    FadeOutEndedVideo();
                }
            };

            if (isVideoSplash)
            {
                if (videoEndBehavior == SplashScreenVideoEndBehavior.ShowGameBackground)
                {
                    AddStaticBackgroundLayer(fallbackBackgroundPath);

                    if (showLogo)
                    {
                        AddGameLogo(game, logoPosition);
                    }
                }

                try
                {
                    backgroundVideo.Source = new Uri(backgroundPath, UriKind.Absolute);
                    visualContent.Children.Add(backgroundVideo);
                }
                catch
                {
                    // Fallback noir
                }
            }
            else
            {
                AddStaticBackgroundLayer(backgroundPath);

                if (showLogo)
                {
                    AddGameLogo(game, logoPosition);
                }
            }

            // Short-lived cinematic veil shared by image and video splashes.
            transitionVeil = new Border
            {
                Background = Brushes.Black,
                Opacity = 0.38,
                IsHitTestVisible = false
            };
            root.Children.Add(transitionVeil);


            Content = root;

            Loaded += GameLaunchSplashWindow_Loaded;

            Closed += (_, __) =>
            {
                StopSlowZoomAnimation(false);
                StopAndCloseVideo();
            };
        }

        private void AddStaticBackgroundLayer(string imagePath)
        {
            if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    backgroundImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
                }
                catch
                {
                    // Fallback noir
                }
            }

            visualContent.Children.Add(backgroundImage);

            var darkOverlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(255 * backgroundDimming),
                    0, 0, 0))
            };
            visualContent.Children.Add(darkOverlay);

            var topGradient = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                Height = 260,
                Background = new LinearGradientBrush(
                    Color.FromArgb(170, 0, 0, 0),
                    Color.FromArgb(0, 0, 0, 0),
                    new Point(0.5, 0),
                    new Point(0.5, 1))
            };
            visualContent.Children.Add(topGradient);

            var bottomGradient = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 340,
                Background = new LinearGradientBrush(
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(200, 0, 0, 0),
                    new Point(0.5, 0),
                    new Point(0.5, 1))
            };
            visualContent.Children.Add(bottomGradient);

            var sideOverlay = new Grid
            {
                IsHitTestVisible = false
            };

            sideOverlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            sideOverlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sideOverlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            var leftShade = new Border
            {
                Background = new LinearGradientBrush(
                    Color.FromArgb(150, 0, 0, 0),
                    Color.FromArgb(0, 0, 0, 0),
                    new Point(0, 0.5),
                    new Point(1, 0.5))
            };
            Grid.SetColumn(leftShade, 0);
            sideOverlay.Children.Add(leftShade);

            var rightShade = new Border
            {
                Background = new LinearGradientBrush(
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(150, 0, 0, 0),
                    new Point(0, 0.5),
                    new Point(1, 0.5))
            };
            Grid.SetColumn(rightShade, 2);
            sideOverlay.Children.Add(rightShade);

            visualContent.Children.Add(sideOverlay);
        }

        private void AddGameLogo(Game game, SplashScreenLogoPosition logoPosition)
        {
            try
            {
                if (game == null)
                {
                    return;
                }

                var extraMetadataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Playnite",
                    "ExtraMetadata",
                    "games",
                    game.Id.ToString(),
                    "Logo.png");

                if (!File.Exists(extraMetadataPath))
                {
                    return;
                }

                gameLogoTranslate = new TranslateTransform(0, 20);

                gameLogo = new Image
                {
                    Source = new BitmapImage(new Uri(extraMetadataPath, UriKind.Absolute)),
                    Stretch = Stretch.Uniform,
                    MaxWidth = 560,
                    MaxHeight = 220,
                    HorizontalAlignment = GetLogoHorizontalAlignment(logoPosition),
                    VerticalAlignment = GetLogoVerticalAlignment(logoPosition),
                    Margin = GetLogoMargin(logoPosition),
                    Opacity = 0,
                    RenderTransform = gameLogoTranslate,
                    IsHitTestVisible = false,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 32,
                        ShadowDepth = 0,
                        Opacity = 0.95
                    }
                };

                RenderOptions.SetBitmapScalingMode(gameLogo, BitmapScalingMode.HighQuality);
                visualContent.Children.Add(gameLogo);
            }
            catch
            {
            }
        }

        private HorizontalAlignment GetLogoHorizontalAlignment(SplashScreenLogoPosition position)
        {
            switch (position)
            {
                case SplashScreenLogoPosition.LeftTop:
                case SplashScreenLogoPosition.LeftCenter:
                case SplashScreenLogoPosition.LeftBottom:
                    return HorizontalAlignment.Left;

                case SplashScreenLogoPosition.RightTop:
                case SplashScreenLogoPosition.RightCenter:
                case SplashScreenLogoPosition.RightBottom:
                    return HorizontalAlignment.Right;

                default:
                    return HorizontalAlignment.Center;
            }
        }

        private VerticalAlignment GetLogoVerticalAlignment(SplashScreenLogoPosition position)
        {
            switch (position)
            {
                case SplashScreenLogoPosition.LeftTop:
                case SplashScreenLogoPosition.CenterTop:
                case SplashScreenLogoPosition.RightTop:
                    return VerticalAlignment.Top;

                case SplashScreenLogoPosition.LeftBottom:
                case SplashScreenLogoPosition.CenterBottom:
                case SplashScreenLogoPosition.RightBottom:
                    return VerticalAlignment.Bottom;

                default:
                    return VerticalAlignment.Center;
            }
        }

        private Thickness GetLogoMargin(SplashScreenLogoPosition position)
        {
            switch (position)
            {
                case SplashScreenLogoPosition.LeftTop:
                    return new Thickness(120, 120, 0, 0);

                case SplashScreenLogoPosition.LeftCenter:
                    return new Thickness(120, 0, 0, 0);

                case SplashScreenLogoPosition.LeftBottom:
                    return new Thickness(120, 0, 0, 120);

                case SplashScreenLogoPosition.CenterTop:
                    return new Thickness(0, 120, 0, 0);

                case SplashScreenLogoPosition.CenterBottom:
                    return new Thickness(0, 0, 0, 120);

                case SplashScreenLogoPosition.RightTop:
                    return new Thickness(0, 120, 120, 0);

                case SplashScreenLogoPosition.RightCenter:
                    return new Thickness(0, 0, 120, 0);

                case SplashScreenLogoPosition.RightBottom:
                    return new Thickness(0, 0, 120, 120);

                default:
                    return new Thickness(0);
            }
        }

        private void GameLaunchSplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartIntroAnimation();

            if (backgroundVideo?.Source != null)
            {
                try
                {
                    backgroundVideo.Position = TimeSpan.Zero;
                    backgroundVideo.Play();
                }
                catch
                {
                }
            }
        }

        private void StartIntroAnimation()
        {
            try
            {
                visualContent.Opacity = 0;
                contentScale.ScaleX = 1.025;
                contentScale.ScaleY = 1.025;
                contentTranslate.Y = 6;
                transitionVeil.Opacity = 0.38;

                if (gameLogo != null)
                {
                    gameLogo.Opacity = 0;
                }

                if (gameLogoTranslate != null)
                {
                    gameLogoTranslate.Y = 20;
                }

                var fadeIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    BeginTime = TimeSpan.FromMilliseconds(20),
                    Duration = TimeSpan.FromMilliseconds(520),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                var scaleX = new DoubleAnimation
                {
                    From = 1.025,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(620),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                var scaleY = new DoubleAnimation
                {
                    From = 1.025,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(620),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                var translateY = new DoubleAnimation
                {
                    From = 6,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(620),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                var veilFade = new DoubleAnimation
                {
                    From = 0.38,
                    To = 0,
                    BeginTime = TimeSpan.FromMilliseconds(50),
                    Duration = TimeSpan.FromMilliseconds(560),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                scaleX.Completed += (_, __) =>
                {
                    if (!isClosingAnimated && backgroundVideo?.Source == null && backgroundImage?.Source != null)
                    {
                        StartSlowZoomAnimation();
                    }
                };

                visualContent.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
                contentTranslate.BeginAnimation(TranslateTransform.YProperty, translateY);
                transitionVeil.BeginAnimation(UIElement.OpacityProperty, veilFade);

                if (gameLogo != null)
                {
                    var logoFade = new DoubleAnimation
                    {
                        From = 0,
                        To = 0.98,
                        BeginTime = TimeSpan.FromMilliseconds(140),
                        Duration = TimeSpan.FromMilliseconds(460),
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                    };

                    gameLogo.BeginAnimation(UIElement.OpacityProperty, logoFade);
                }

                if (gameLogoTranslate != null)
                {
                    var logoSlide = new DoubleAnimation
                    {
                        From = 20,
                        To = 0,
                        BeginTime = TimeSpan.FromMilliseconds(140),
                        Duration = TimeSpan.FromMilliseconds(560),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    gameLogoTranslate.BeginAnimation(TranslateTransform.YProperty, logoSlide);
                }
            }
            catch
            {
                visualContent.Opacity = 1;
                contentScale.ScaleX = 1;
                contentScale.ScaleY = 1;
                contentTranslate.Y = 0;
                transitionVeil.Opacity = 0;

                if (gameLogo != null)
                {
                    gameLogo.Opacity = 0.98;
                }
            }
        }

        private void StartSlowZoomAnimation()
        {
            if (isClosingAnimated)
            {
                return;
            }

            var zoom = new DoubleAnimation
            {
                From = 1.0,
                To = 1.05,
                Duration = TimeSpan.FromMilliseconds(6000),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            backgroundScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            backgroundScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        }

        private void StopSlowZoomAnimation(bool preserveCurrentScale)
        {
            try
            {
                var currentScaleX = backgroundScale.ScaleX;
                var currentScaleY = backgroundScale.ScaleY;

                backgroundScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                backgroundScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                if (preserveCurrentScale)
                {
                    // Keep the current zoom level during the splash fade-out to avoid a visible snap.
                    backgroundScale.ScaleX = currentScaleX;
                    backgroundScale.ScaleY = currentScaleY;
                }
                else
                {
                    backgroundScale.ScaleX = 1.0;
                    backgroundScale.ScaleY = 1.0;
                }
            }
            catch
            {
            }
        }

        private void FadeOutEndedVideo()
        {
            if (isClosingAnimated)
            {
                return;
            }

            try
            {
                var fadeOut = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(450),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };

                fadeOut.Completed += (_, __) =>
                {
                    try
                    {
                        backgroundVideo.Stop();

                        if (!isClosingAnimated && backgroundImage?.Source != null)
                        {
                            StartSlowZoomAnimation();
                        }
                    }
                    catch
                    {
                    }
                };

                backgroundVideo.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
            catch
            {
            }
        }

        private void StopAndCloseVideo()
        {
            try
            {
                backgroundVideo?.Stop();
                backgroundVideo?.Close();
            }
            catch
            {
            }
        }

        public Task BeginCloseAsync()
        {
            if (isClosingAnimated)
            {
                return Task.CompletedTask;
            }

            isClosingAnimated = true;
            var tcs = new TaskCompletionSource<bool>();

            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Preserve the current slow zoom before starting the short cinematic exit.
                    StopSlowZoomAnimation(true);

                    var currentWindowOpacity = Opacity;
                    var currentScaleX = contentScale.ScaleX;
                    var currentScaleY = contentScale.ScaleY;
                    var currentTranslateY = contentTranslate.Y;
                    var currentVeilOpacity = transitionVeil.Opacity;

                    var contentScaleXOut = new DoubleAnimation
                    {
                        From = currentScaleX,
                        To = currentScaleX + 0.015,
                        Duration = TimeSpan.FromMilliseconds(420),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    var contentScaleYOut = new DoubleAnimation
                    {
                        From = currentScaleY,
                        To = currentScaleY + 0.015,
                        Duration = TimeSpan.FromMilliseconds(420),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    var contentTranslateOut = new DoubleAnimation
                    {
                        From = currentTranslateY,
                        To = currentTranslateY - 4,
                        Duration = TimeSpan.FromMilliseconds(420),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    var veilIn = new DoubleAnimation
                    {
                        From = currentVeilOpacity,
                        To = 0.16,
                        Duration = TimeSpan.FromMilliseconds(420),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };

                    var fadeOut = new DoubleAnimation
                    {
                        From = currentWindowOpacity,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(420),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };

                    if (gameLogo != null)
                    {
                        var logoFadeOut = new DoubleAnimation
                        {
                            From = gameLogo.Opacity,
                            To = 0,
                            Duration = TimeSpan.FromMilliseconds(180),
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                        };

                        gameLogo.BeginAnimation(UIElement.OpacityProperty, logoFadeOut);
                    }

                    if (gameLogoTranslate != null)
                    {
                        var logoSlideOut = new DoubleAnimation
                        {
                            From = gameLogoTranslate.Y,
                            To = -8,
                            Duration = TimeSpan.FromMilliseconds(220),
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                        };

                        gameLogoTranslate.BeginAnimation(TranslateTransform.YProperty, logoSlideOut);
                    }

                    contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, contentScaleXOut);
                    contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, contentScaleYOut);
                    contentTranslate.BeginAnimation(TranslateTransform.YProperty, contentTranslateOut);
                    transitionVeil.BeginAnimation(UIElement.OpacityProperty, veilIn);

                    fadeOut.Completed += (s, e) =>
                    {
                        StopAndCloseVideo();
                        tcs.TrySetResult(true);
                    };

                    BeginAnimation(Window.OpacityProperty, fadeOut);
                }
                catch
                {
                    StopAndCloseVideo();
                    tcs.TrySetResult(true);
                }
            });

            return tcs.Task;
        }
    }
}
