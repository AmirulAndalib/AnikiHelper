using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AnikiHelper
{
    internal static class SettingsWindowStyler
    {
        private static DispatcherTimer timer;
        private static bool patchedThisOpen;

        public static void Start()
        {
            if (timer != null) return;

            timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            timer.Tick += Tick;
            timer.Start();

            Application.Current.Exit += (_, __) => { Stop(); };
        }

        public static void Stop()
        {
            try { timer?.Stop(); } catch { }

            timer = null;
            patchedThisOpen = false;
        }

        private static void Tick(object sender, EventArgs e)
        {
            var app = Application.Current;
            if (app == null) return;

            // Fullscreen only
            var win = app.Windows.Cast<Window>().FirstOrDefault(w =>
            {
                var t = w.GetType().FullName ?? "";
                return t.IndexOf("Playnite.FullscreenApp.Windows.SettingsWindow", StringComparison.Ordinal) >= 0;
            });

            if (win == null)
            {
                patchedThisOpen = false; // reset for next opening
                return;
            }

            if (!patchedThisOpen)
            {
                patchedThisOpen = true;

                win.Dispatcher.InvokeAsync(() =>
                {
                    try { ApplyFix(win); } catch { /* best-effort */ }
                }, DispatcherPriority.Loaded);
            }

            // SelectedSectionView is swapped by Playnite when the left Settings category changes.
            // Keep this lightweight check outside ApplyFix so UniPlaySong can be injected when the
            // user opens Audio later, even if Settings initially opened on General.
            try { EnsureUniPlaySongAudioSettings(win); } catch { /* best-effort */ }
        }

        private static void ApplyFix(Window settingsWindow)
        {
            // Hide all TextBlocks whose Text is bound to OptionDescription
            var blocks = VisualTreeHelpers.FindVisualChildren<TextBlock>(settingsWindow)
                .Where(tb =>
                {
                    var be = BindingOperations.GetBindingExpression(tb, TextBlock.TextProperty);
                    return be?.ParentBinding?.Path?.Path == "OptionDescription";
                })
                .ToList();

            foreach (var tb in blocks)
            {
                tb.Visibility = Visibility.Collapsed;
                tb.Focusable = false;
                tb.IsHitTestVisible = false;
                KeyboardNavigation.SetIsTabStop(tb, false);
            }

            // Prevent the fullscreen settings ScrollViewer from behaving like the focused element.
            foreach (var sv in VisualTreeHelpers.FindVisualChildren<ScrollViewer>(settingsWindow))
            {
                sv.Focusable = false;
                sv.IsTabStop = false;
                KeyboardNavigation.SetTabNavigation(sv, KeyboardNavigationMode.Continue);
                KeyboardNavigation.SetDirectionalNavigation(sv, KeyboardNavigationMode.Continue);
                KeyboardNavigation.SetControlTabNavigation(sv, KeyboardNavigationMode.Continue);
            }

            // Make normal panels continue navigation instead of cycling inside empty zones.
            foreach (var panel in VisualTreeHelpers.FindVisualChildren<Panel>(settingsWindow))
            {
                KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Continue);
                KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Continue);
                KeyboardNavigation.SetControlTabNavigation(panel, KeyboardNavigationMode.Continue);
            }

            // Protect ComboBox navigation from the global Settings Up/Down handler.
            foreach (var combo in VisualTreeHelpers.FindVisualChildren<ComboBox>(settingsWindow))
            {
                combo.PreviewKeyDown -= SettingsComboBox_PreviewKeyDown;
                combo.PreviewKeyDown += SettingsComboBox_PreviewKeyDown;
            }

            // Force Up/Down to jump to the next real focusable setting instead of scrolling line by line.
            settingsWindow.PreviewKeyDown -= SettingsWindow_PreviewKeyDown;
            settingsWindow.PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        }

        private const string UniPlaySongSectionName = "AnikiUniPlaySongAudioSettings";

        private static void EnsureUniPlaySongAudioSettings(Window settingsWindow)
        {
            if (settingsWindow == null)
            {
                return;
            }

            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            // UniPlaySong exposes itself here on application startup.
            object uniPlaySongPlugin = null;
            try
            {
                uniPlaySongPlugin = app.Properties["UniPlaySongPlugin"];
            }
            catch
            {
                return;
            }

            if (uniPlaySongPlugin == null)
            {
                return;
            }

            var settingsProperty = uniPlaySongPlugin.GetType().GetProperty(
                "Settings",
                BindingFlags.Instance | BindingFlags.Public);

            var uniPlaySongSettings = settingsProperty?.GetValue(uniPlaySongPlugin, null);
            if (uniPlaySongSettings == null)
            {
                return;
            }

            var contentSettings = VisualTreeHelpers.FindVisualChildren<ContentControl>(settingsWindow)
                .FirstOrDefault(c => string.Equals(c.Name, "ContentSettings", StringComparison.Ordinal));

            var audioRoot = contentSettings?.Content as FrameworkElement;
            if (audioRoot == null)
            {
                return;
            }

            var audioTypeName = audioRoot.GetType().FullName ?? string.Empty;
            if (audioTypeName.IndexOf(
                    "Playnite.FullscreenApp.Controls.SettingsSections.Audio",
                    StringComparison.Ordinal) < 0)
            {
                return;
            }

            if (HasNamedDescendant(audioRoot, UniPlaySongSectionName))
            {
                return;
            }

            var host = FindAudioSettingsHost(audioRoot);
            if (host == null)
            {
                return;
            }

            var section = BuildUniPlaySongAudioSection(settingsWindow, uniPlaySongSettings);
            if (section == null)
            {
                return;
            }

            if (!AddSectionToHost(host, section))
            {
                return;
            }

            // Apply the same navigation safety used by the native Settings controls.
            KeyboardNavigation.SetTabNavigation(section, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(section, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetControlTabNavigation(section, KeyboardNavigationMode.Continue);
        }

        private static FrameworkElement BuildUniPlaySongAudioSection(
            Window settingsWindow,
            object uniPlaySongSettings)
        {
            var section = new StackPanel
            {
                Name = UniPlaySongSectionName,
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 18, 0, 0)
            };

            var separator = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 16),
                Opacity = 0.65
            };
            SetDynamicResource(separator, Border.BackgroundProperty, settingsWindow, "SeparatorBrush");
            section.Children.Add(separator);

            var header = new TextBlock
            {
                Text = "UniPlaySong",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 0, 10, 8)
            };
            ApplyStyleIfCompatible(header, settingsWindow, "TextBlockBaseStyle");
            section.Children.Add(header);

            AddBooleanSetting(
                section,
                settingsWindow,
                uniPlaySongSettings,
                "EnableMusic",
                "LOCEnableGameMusic",
                "Enable game music");

            AddBooleanSetting(
                section,
                settingsWindow,
                uniPlaySongSettings,
                "EnableDefaultMusic",
                "LOCEnableDefaultMusic",
                "Enable default music");

            AddBooleanSetting(
                section,
                settingsWindow,
                uniPlaySongSettings,
                "RadioModeEnabled",
                "LOCRadioMode",
                "Radio mode");

            AddRadioSourceSetting(
                section,
                settingsWindow,
                uniPlaySongSettings);

            AddBooleanSetting(
                section,
                settingsWindow,
                uniPlaySongSettings,
                "PlayOnlyOnGameSelect",
                "LOCPlayOnlyOnGameSelect",
                "Play only when selecting a game");

            AddBooleanSetting(
                section,
                settingsWindow,
                uniPlaySongSettings,
                "CalmDownModeEnabled",
                "LOCNightMusicMode",
                "Night music mode");

            return section.Children.Count > 2 ? section : null;
        }

        private static void AddBooleanSetting(
            Panel host,
            Window settingsWindow,
            object source,
            string propertyName,
            string localizationKey,
            string fallbackText)
        {
            if (!HasPublicProperty(source, propertyName))
            {
                return;
            }

            var style = settingsWindow.TryFindResource("SettingsSectionCheckbox") as Style;
            var checkBox = CreateControlForStyle<CheckBox>(style) ?? new CheckBox();

            checkBox.IsThreeState = false;
            checkBox.IsTabStop = true;
            checkBox.Focusable = true;

            if (style != null && style.TargetType.IsAssignableFrom(checkBox.GetType()))
            {
                checkBox.Style = style;
            }

            SetLocalizedContent(checkBox, settingsWindow, localizationKey, fallbackText);

            BindingOperations.SetBinding(
                checkBox,
                ToggleButton.IsCheckedProperty,
                new Binding(propertyName)
                {
                    Source = source,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });

            host.Children.Add(checkBox);
        }

        private static void AddRadioSourceSetting(
            Panel host,
            Window settingsWindow,
            object source)
        {
            const string propertyName = "SwitchRadioMode";
            if (!HasPublicProperty(source, propertyName))
            {
                return;
            }

            var row = new Grid
            {
                Height = 70,
                Margin = new Thickness(-5, 5, 0, 5)
            };

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 22,
                Margin = new Thickness(10, 0, 20, 0)
            };
            ApplyStyleIfCompatible(label, settingsWindow, "SettingsSectionText");
            SetLocalizedText(label, settingsWindow, "LOCRadioSource", "Radio source");
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var comboStyle = settingsWindow.TryFindResource("SettingsSectionCombobox") as Style;
            var combo = CreateControlForStyle<ComboBox>(comboStyle) ?? new ComboBox();

            combo.Items.Add("UPS");
            combo.Items.Add("Spotify");
            combo.MinWidth = 240;
            combo.VerticalAlignment = VerticalAlignment.Center;
            combo.Focusable = true;
            combo.IsTabStop = true;

            if (comboStyle != null && comboStyle.TargetType.IsAssignableFrom(combo.GetType()))
            {
                combo.Style = comboStyle;
            }

            BindingOperations.SetBinding(
                combo,
                Selector.SelectedIndexProperty,
                new Binding(propertyName)
                {
                    Source = source,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    Converter = RadioSourceBooleanToIndexConverter.Instance
                });

            combo.PreviewKeyDown -= SettingsComboBox_PreviewKeyDown;
            combo.PreviewKeyDown += SettingsComboBox_PreviewKeyDown;

            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            KeyboardNavigation.SetDirectionalNavigation(row, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetTabNavigation(row, KeyboardNavigationMode.Continue);

            host.Children.Add(row);
        }

        private static T CreateControlForStyle<T>(Style style) where T : Control
        {
            if (style?.TargetType == null ||
                !typeof(T).IsAssignableFrom(style.TargetType))
            {
                return null;
            }

            try
            {
                return Activator.CreateInstance(style.TargetType) as T;
            }
            catch
            {
                return null;
            }
        }

        private static bool HasPublicProperty(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            return source.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public) != null;
        }

        private static void SetLocalizedContent(
            ContentControl control,
            FrameworkElement resourceOwner,
            string resourceKey,
            string fallbackText)
        {
            if (control == null)
            {
                return;
            }

            if (resourceOwner?.TryFindResource(resourceKey) != null)
            {
                control.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            }
            else
            {
                control.Content = fallbackText;
            }
        }

        private static void SetLocalizedText(
            TextBlock textBlock,
            FrameworkElement resourceOwner,
            string resourceKey,
            string fallbackText)
        {
            if (textBlock == null)
            {
                return;
            }

            if (resourceOwner?.TryFindResource(resourceKey) != null)
            {
                textBlock.SetResourceReference(TextBlock.TextProperty, resourceKey);
            }
            else
            {
                textBlock.Text = fallbackText;
            }
        }

        private static void ApplyStyleIfCompatible(
            FrameworkElement element,
            FrameworkElement resourceOwner,
            string resourceKey)
        {
            if (element == null || resourceOwner == null)
            {
                return;
            }

            var style = resourceOwner.TryFindResource(resourceKey) as Style;
            if (style?.TargetType != null &&
                style.TargetType.IsAssignableFrom(element.GetType()))
            {
                element.Style = style;
            }
        }

        private static void SetDynamicResource(
            FrameworkElement element,
            DependencyProperty property,
            FrameworkElement resourceOwner,
            string resourceKey)
        {
            if (element == null || property == null)
            {
                return;
            }

            if (resourceOwner?.TryFindResource(resourceKey) != null)
            {
                element.SetResourceReference(property, resourceKey);
            }
        }

        private static bool HasNamedDescendant(DependencyObject root, string name)
        {
            if (root == null)
            {
                return false;
            }

            if (root is FrameworkElement rootElement &&
                string.Equals(rootElement.Name, name, StringComparison.Ordinal))
            {
                return true;
            }

            return VisualTreeHelpers.FindVisualChildren<FrameworkElement>(root)
                .Any(fe => string.Equals(fe.Name, name, StringComparison.Ordinal));
        }

        private static Panel FindAudioSettingsHost(FrameworkElement audioRoot)
        {
            var candidates = new List<Panel>();
            CollectLogicalPanels(audioRoot, candidates);

            return candidates
                .Select(panel => new
                {
                    Panel = panel,
                    Score = ScoreAudioSettingsHost(panel)
                })
                .Where(item => item.Score >= 0)
                .OrderByDescending(item => item.Score)
                .Select(item => item.Panel)
                .FirstOrDefault();
        }

        private static void CollectLogicalPanels(DependencyObject root, List<Panel> result)
        {
            if (root == null || result == null)
            {
                return;
            }

            if (root is Panel panel)
            {
                result.Add(panel);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject dependencyChild)
                {
                    CollectLogicalPanels(dependencyChild, result);
                }
            }
        }

        private static int ScoreAudioSettingsHost(Panel panel)
        {
            if (panel == null)
            {
                return -1;
            }

            if (panel is Grid grid && grid.RowDefinitions.Count == 0)
            {
                return -1;
            }

            if (panel is StackPanel stackPanel &&
                stackPanel.Orientation != Orientation.Vertical)
            {
                return -1;
            }

            var directSettingRows = 0;

            foreach (UIElement child in panel.Children)
            {
                if (ContainsNativeSettingControl(child))
                {
                    directSettingRows++;
                }
            }

            // Row-level Grids normally contain just one setting. We want the parent
            // that owns the Audio page's three native rows.
            if (directSettingRows < 2)
            {
                return -1;
            }

            var score = directSettingRows * 100;

            if (panel is StackPanel)
            {
                score += 40;
            }
            else if (panel is Grid)
            {
                score += 25;
            }

            // Avoid selecting a broad page shell if a tighter settings stack exists.
            score -= Math.Min(panel.Children.Count, 30);

            return score;
        }

        private static bool ContainsNativeSettingControl(DependencyObject root)
        {
            if (root == null)
            {
                return false;
            }

            if (root is Slider ||
                root is CheckBox ||
                root is ComboBox ||
                root is ToggleButton)
            {
                return true;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject dependencyChild &&
                    ContainsNativeSettingControl(dependencyChild))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AddSectionToHost(Panel host, FrameworkElement section)
        {
            if (host == null || section == null)
            {
                return false;
            }

            if (host is StackPanel)
            {
                host.Children.Add(section);
                return true;
            }

            if (host is Grid grid)
            {
                if (grid.RowDefinitions.Count == 0)
                {
                    return false;
                }

                var rowIndex = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid.SetRow(section, rowIndex);

                if (grid.ColumnDefinitions.Count > 1)
                {
                    Grid.SetColumnSpan(section, grid.ColumnDefinitions.Count);
                }

                grid.Children.Add(section);
                return true;
            }

            return false;
        }

        private sealed class RadioSourceBooleanToIndexConverter : IValueConverter
        {
            public static readonly RadioSourceBooleanToIndexConverter Instance =
                new RadioSourceBooleanToIndexConverter();

            public object Convert(
                object value,
                Type targetType,
                object parameter,
                CultureInfo culture)
            {
                return value is bool enabled && enabled ? 1 : 0;
            }

            public object ConvertBack(
                object value,
                Type targetType,
                object parameter,
                CultureInfo culture)
            {
                if (value is int index)
                {
                    return index == 1;
                }

                return false;
            }
        }

        private static void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Down && e.Key != Key.Up)
            {
                return;
            }

            var window = sender as Window;
            if (window == null)
            {
                return;
            }

            // Do not hijack text editing.
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            // Do not hijack ComboBox navigation.
            // When a ComboBox dropdown is open, the focused element is often a ComboBoxItem,
            // not the ComboBox itself.
            if (IsComboBoxInteractionActive(window))
            {
                return;
            }

            var direction = e.Key == Key.Down ? 1 : -1;

            if (MoveFocusToNextSettingControl(window, direction))
            {
                e.Handled = true;
            }
        }

        private static void SettingsComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null)
            {
                return;
            }

            if (!combo.IsDropDownOpen)
            {
                return;
            }

            if (e.Key != Key.Up && e.Key != Key.Down)
            {
                return;
            }

            if (combo.Items == null || combo.Items.Count == 0)
            {
                return;
            }

            int index = combo.SelectedIndex;

            if (index < 0)
            {
                index = 0;
            }

            if (e.Key == Key.Up)
            {
                index = Math.Max(0, index - 1);
            }
            else if (e.Key == Key.Down)
            {
                index = Math.Min(combo.Items.Count - 1, index + 1);
            }

            combo.SelectedIndex = index;
            combo.Dispatcher.BeginInvoke(new Action(() =>
            {
                var item = combo.ItemContainerGenerator.ContainerFromItem(combo.SelectedItem) as ComboBoxItem;
                item?.BringIntoView();
            }), DispatcherPriority.Background);
            // Prevent Playnite fullscreen settings from treating Up/Down as menu navigation.
            e.Handled = true;
        }

        private static bool IsComboBoxInteractionActive(Window window)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;

            if (focused != null)
            {
                if (focused is ComboBox || focused is ComboBoxItem)
                {
                    return true;
                }

                if (FindParent<ComboBox>(focused) != null)
                {
                    return true;
                }

                if (FindParent<ComboBoxItem>(focused) != null)
                {
                    return true;
                }
            }

            // Extra safety:
            // if any ComboBox dropdown is open in the Settings window,
            // do not let the global Up/Down handler take over.
            foreach (var combo in VisualTreeHelpers.FindVisualChildren<ComboBox>(window))
            {
                if (combo.IsDropDownOpen)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MoveFocusToNextSettingControl(Window window, int direction)
        {
            try
            {
                var controls = VisualTreeHelpers.FindVisualChildren<Control>(window)
                    .Where(c =>
                        c != null &&
                        c.IsVisible &&
                        c.IsEnabled &&
                        c.Focusable &&
                        c.IsTabStop &&
                        c.ActualWidth > 0 &&
                        c.ActualHeight > 0 &&
                        !(c is ScrollViewer) &&
                        !(c is ComboBoxItem))
                    .OrderBy(c => GetElementTop(c, window))
                    .ThenBy(c => GetElementLeft(c, window))
                    .ToList();

                if (controls.Count == 0)
                {
                    return false;
                }

                var focused = Keyboard.FocusedElement as DependencyObject;
                var focusedControl = focused as Control ?? FindParent<Control>(focused);

                int currentIndex = focusedControl != null
                    ? controls.IndexOf(focusedControl)
                    : -1;

                if (currentIndex < 0)
                {
                    currentIndex = FindNearestIndexFromCurrentPosition(controls, focused, window, direction);
                }

                int nextIndex = currentIndex + direction;

                if (nextIndex < 0)
                {
                    nextIndex = 0;
                }

                if (nextIndex >= controls.Count)
                {
                    nextIndex = controls.Count - 1;
                }

                if (nextIndex == currentIndex)
                {
                    return false;
                }

                var target = controls[nextIndex];
                target.Focus();
                Keyboard.Focus(target);
                target.BringIntoView();

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int FindNearestIndexFromCurrentPosition(
            List<Control> controls,
            DependencyObject focused,
            Window window,
            int direction)
        {
            if (focused is FrameworkElement fe)
            {
                double currentTop = GetElementTop(fe, window);

                if (direction > 0)
                {
                    for (int i = 0; i < controls.Count; i++)
                    {
                        if (GetElementTop(controls[i], window) > currentTop)
                        {
                            return Math.Max(0, i - 1);
                        }
                    }

                    return controls.Count - 1;
                }
                else
                {
                    for (int i = controls.Count - 1; i >= 0; i--)
                    {
                        if (GetElementTop(controls[i], window) < currentTop)
                        {
                            return Math.Min(controls.Count - 1, i + 1);
                        }
                    }

                    return 0;
                }
            }

            return direction > 0 ? 0 : controls.Count - 1;
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                var parent = LogicalTreeHelper.GetParent(child) ?? VisualTreeHelper.GetParent(child);

                if (parent is T typedParent)
                {
                    return typedParent;
                }

                child = parent;
            }

            return null;
        }

        private static double GetElementTop(FrameworkElement element, Window window)
        {
            try
            {
                return element.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static double GetElementLeft(FrameworkElement element, Window window)
        {
            try
            {
                return element.TransformToAncestor(window).Transform(new Point(0, 0)).X;
            }
            catch
            {
                return double.MaxValue;
            }
        }
    }
}