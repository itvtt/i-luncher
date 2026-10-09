using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

public partial class MainWindow : Window
{
    private readonly ILauncherCatalog _catalog;
    private readonly ILauncherUpdateCatalog? _updateCatalog;
    private readonly Func<LauncherEntry, string, bool> _confirmUninstall;
    private readonly Func<LauncherEntry, string, bool> _confirmInstall;
    private readonly List<LauncherCard> _allCards = [];
    private readonly List<Button> _categoryButtons = [];
    private string _selectedCategory = string.Empty;
    private bool _installedOnly;
    private int _activeOperations;
    private bool _catalogRefreshPending;
    private Task? _catalogRefreshTask;
    private bool _versionCheckRunning;
    private int _catalogRefreshGeneration;

    public ObservableCollection<LauncherCard> LauncherCards { get; } = [];

    public MainWindow() : this(new LauncherCatalog(new IntegratedAppProvider()))
    {
    }

    public MainWindow(ILauncherCatalog catalog, Func<LauncherEntry, string, bool>? confirmUninstall = null,
        Func<LauncherEntry, string, bool>? confirmInstall = null, INoticeService? noticeService = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _updateCatalog = catalog as ILauncherUpdateCatalog;
        _confirmUninstall = confirmUninstall ?? ConfirmUninstall;
        _confirmInstall = confirmInstall ?? ConfirmInstall;
        InitializeComponent();
        InitializeNotices(noticeService);
        Rect workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        DataContext = this;
        ApplySidebarSelection();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshCatalog();
        SearchBox.Focus();
        StartNotices();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCatalog();
        _ = RefreshNoticesAsync();
    }

    private void RefreshCatalog()
    {
        _catalogRefreshGeneration++;
        _catalogRefreshPending = true;
        if (_catalogRefreshTask is null)
        {
            _catalogRefreshTask = LoadCatalogLoopAsync();
        }
    }

    private async Task LoadCatalogLoopAsync()
    {
        try
        {
            while (_catalogRefreshPending)
            {
                _catalogRefreshPending = false;
                int generation = _catalogRefreshGeneration;
                try
                {
                    (CatalogResult result, LauncherCard[] cards) = await Task.Run(() =>
                    {
                        CatalogResult loaded = _catalog.Load();
                        LauncherCard[] loadedCards = loaded.Entries.Select(entry => new LauncherCard(entry)).ToArray();
                        return (loaded, loadedCards);
                    });

                    if (generation != _catalogRefreshGeneration)
                    {
                        continue;
                    }

                    _allCards.Clear();
                    _allCards.AddRange(cards);
                    UpdateCategoryButtons();
                    ShowCatalogErrors(result.Errors);
                    ApplyFilter();
                    ScheduleVersionCheck();
                }
                catch (Exception ex)
                {
                    if (generation == _catalogRefreshGeneration)
                    {
                        _allCards.Clear();
                        LauncherCards.Clear();
                        UpdateCategoryButtons();
                        ShowCatalogErrors([GetFriendlyMessage(ex)]);
                        UpdateEmptyState();
                        UpdateCount();
                    }
                }
            }
        }
        finally
        {
            _catalogRefreshTask = null;
            if (_catalogRefreshPending)
            {
                _catalogRefreshTask = LoadCatalogLoopAsync();
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyFilter();
    }

    private void AllAppsButton_Click(object sender, RoutedEventArgs e)
    {
        _installedOnly = false;
        _selectedCategory = string.Empty;
        ApplySidebarSelection();
        ApplyFilter();
    }

    private void InstalledAppsButton_Click(object sender, RoutedEventArgs e)
    {
        _installedOnly = true;
        _selectedCategory = string.Empty;
        ApplySidebarSelection();
        ApplyFilter();
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        _installedOnly = false;
        _selectedCategory = category;
        ApplySidebarSelection();
        ApplyFilter();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _installedOnly = false;
        _selectedCategory = string.Empty;
        SearchBox.Clear();
        ApplySidebarSelection();
        ApplyFilter();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape &&
                 (!string.IsNullOrWhiteSpace(SearchBox.Text) || _installedOnly || !string.IsNullOrEmpty(_selectedCategory)))
        {
            ClearFiltersButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void UpdateCategoryButtons()
    {
        CategoryButtons.Children.Clear();
        _categoryButtons.Clear();
        string[] categories = _allCards.Select(card => card.Entry.Category?.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
            .ToArray()!;
        if (!categories.Any(category => string.Equals(category, _selectedCategory, StringComparison.CurrentCultureIgnoreCase)))
            _selectedCategory = string.Empty;
        foreach (string category in categories)
        {
            Button button = new()
            {
                Content = category,
                Tag = category,
                ToolTip = category,
                Style = (Style)FindResource("SidebarNavButtonStyle")
            };
            button.Click += CategoryButton_Click;
            CategoryButtons.Children.Add(button);
            _categoryButtons.Add(button);
        }
        ApplySidebarSelection();
    }

    private void ApplySidebarSelection()
    {
        SetSidebarButtonSelected(AllAppsButton, !_installedOnly && string.IsNullOrEmpty(_selectedCategory));
        SetSidebarButtonSelected(InstalledAppsButton, _installedOnly);
        foreach (Button button in _categoryButtons)
            SetSidebarButtonSelected(button, !_installedOnly && string.Equals(button.Tag as string, _selectedCategory, StringComparison.CurrentCultureIgnoreCase));
        SectionTitle.Text = !string.IsNullOrEmpty(_selectedCategory) ? _selectedCategory : _installedOnly ? "설치된 앱" : "전체 앱";
    }

    private static void SetSidebarButtonSelected(Button button, bool selected)
    {
        button.Background = selected ? new SolidColorBrush(Color.FromRgb(38, 51, 73)) : Brushes.Transparent;
        button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(52, 68, 95)) : Brushes.Transparent;
        button.Foreground = selected ? new SolidColorBrush(Color.FromRgb(220, 231, 255)) : new SolidColorBrush(Color.FromRgb(150, 160, 178));
    }

    private void ApplyFilter()
    {
        if (SearchBox is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        IEnumerable<LauncherCard> filtered = _allCards;
        if (_installedOnly) filtered = filtered.Where(card => card.IsInstalled);
        if (!string.IsNullOrEmpty(_selectedCategory))
            filtered = filtered.Where(card => string.Equals(card.Entry.Category?.Trim(), _selectedCategory, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query)) filtered = filtered.Where(card => card.Matches(query));

        LauncherCards.Clear();
        foreach (LauncherCard card in filtered)
        {
            LauncherCards.Add(card);
        }

        UpdateEmptyState();
        UpdateCount();
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = LauncherCards.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        CardScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        bool hasFilter = !string.IsNullOrWhiteSpace(SearchBox?.Text) || _installedOnly || !string.IsNullOrEmpty(_selectedCategory);
        EmptyStateTitle.Text = hasFilter ? "일치하는 앱이 없습니다" : "등록된 앱이 없습니다";
        ClearFiltersButton.Visibility = hasFilter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        CountText.Text = $"{LauncherCards.Count}개 앱";
    }

    private void ShowCatalogErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            return;
        }

        ErrorText.Text = string.Join(Environment.NewLine, errors.Select((error, index) => $"{index + 1}. {error}"));
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void DismissErrorButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
    }

    private void LauncherCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button) SetCardInteractionVisual(button, true, false);
    }

    private void LauncherCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button) SetCardInteractionVisual(button, false, false);
    }

    private void LauncherCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && e.ChangedButton == MouseButton.Left)
            SetCardInteractionVisual(button, true, true);
    }

    private void LauncherCard_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && e.ChangedButton == MouseButton.Left)
            SetCardInteractionVisual(button, button.IsMouseOver, false);
    }

    private void LauncherCard_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is Button button && e.Key is Key.Space or Key.Enter)
            SetCardInteractionVisual(button, button.IsMouseOver, true);
    }

    private void LauncherCard_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is Button button && e.Key is Key.Space or Key.Enter)
            SetCardInteractionVisual(button, button.IsMouseOver, false);
    }

    private static void SetCardInteractionVisual(Button button, bool hovered, bool pressed)
    {
        button.ApplyTemplate();
        ScaleTransform? transform = GetMutableCardTransform(button);
        if (button.Template.FindName("LaunchHoverOverlay", button) is not Border hoverOverlay ||
            button.Template.FindName("LaunchHoverOutline", button) is not Border hoverOutline ||
            transform is null)
            return;

        double overlayOpacity = pressed ? 0.15 : hovered ? 0.075 : 0;
        double outlineOpacity = hovered ? 0.7 : 0;
        double scale = pressed ? 0.992 : hovered ? 1.012 : 1.0;
        AnimateDouble(hoverOverlay, UIElement.OpacityProperty, overlayOpacity, 160);
        AnimateDouble(hoverOutline, UIElement.OpacityProperty, outlineOpacity, 160);
        if (!SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = 1;
            transform.ScaleY = 1;
            return;
        }

        AnimateDouble(transform, ScaleTransform.ScaleXProperty, scale, pressed ? 75 : 160);
        AnimateDouble(transform, ScaleTransform.ScaleYProperty, scale, pressed ? 75 : 160);
    }

    private static void AnimateDouble(Animatable target, DependencyProperty property, double value, int durationMilliseconds)
    {
        double current = (double)target.GetValue(property);
        target.BeginAnimation(property, new DoubleAnimation(current, value,
            new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void AnimateDouble(UIElement target, DependencyProperty property, double value, int durationMilliseconds)
    {
        double current = (double)target.GetValue(property);
        target.BeginAnimation(property, new DoubleAnimation(current, value,
            new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static ScaleTransform? GetMutableCardTransform(Button button)
    {
        if (button.RenderTransform is not ScaleTransform transform) return null;
        if (transform.IsFrozen)
        {
            transform = transform.CloneCurrentValue();
            button.RenderTransform = transform;
        }
        return transform;
    }

    private static void PulseLaunch(Button button)
    {
        if (button.Template.FindName("LaunchHoverOverlay", button) is Border hoverOverlay)
        {
            double currentOpacity = hoverOverlay.Opacity;
            var glow = new DoubleAnimationUsingKeyFrames();
            glow.KeyFrames.Add(new DiscreteDoubleKeyFrame(currentOpacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(Math.Max(currentOpacity, 0.2),
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            glow.KeyFrames.Add(new EasingDoubleKeyFrame(currentOpacity,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            hoverOverlay.BeginAnimation(UIElement.OpacityProperty, glow);
        }

        if (!SystemParameters.ClientAreaAnimation) return;
        ScaleTransform? transform = GetMutableCardTransform(button);
        if (transform is null) return;

        double current = transform.ScaleX;
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(current + 0.026,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(current,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);

        var verticalAnimation = new DoubleAnimationUsingKeyFrames();
        verticalAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame(transform.ScaleY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        verticalAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(transform.ScaleY + 0.026,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        verticalAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(transform.ScaleY,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, verticalAnimation);
    }

    private async void LauncherCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherCard card })
        {
            return;
        }

        card.RefreshInstalledState();
        if (card.IsBusy) return;
        if (!card.IsInstalled)
        {
            if (!card.CanInstall) return;
            string confirmation = BuildInstallConfirmationMessage(card.Entry);
            if (!_confirmInstall(card.Entry, confirmation)) return;

            // Re-read after the modal prompt. A concurrent install must not be turned into removal.
            card.RefreshInstalledState();
            if (card.IsBusy || card.IsInstalled || !card.CanInstall) return;
            await InstallCardAsync(card);
            return;
        }
        if (!card.CanRun) return;
        PulseLaunch((Button)sender);
        card.IsInstalling = false;
        card.IsBusy = true;
        card.StatusMessage = "앱을 시작하는 중…";
        BeginOperation();
        try
        {
            await _catalog.LaunchAsync(card.Entry);
            card.StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            card.StatusMessage = "시작하지 못했습니다.";
            ShowError($"{card.Entry.Name}을(를) 시작하지 못했습니다: {GetFriendlyMessage(ex)}");
        }
        finally
        {
            card.IsBusy = false;
            card.IsInstalling = false;
            EndOperation();
        }
    }

    private void LauncherCard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { Tag: LauncherCard card, ContextMenu: ContextMenu menu }) return;
        card.RefreshInstalledState();
        menu.DataContext = card;
        MenuItem[] actions = menu.Items.OfType<MenuItem>().ToArray();
        if (actions.Length > 0)
        {
            MenuItem updateItem = actions[0];
            updateItem.Tag = card;
            updateItem.Visibility = card.CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            updateItem.IsEnabled = card.CanUpdate;
        }
        if (actions.Length > 1)
        {
            MenuItem removeItem = actions[1];
            removeItem.Tag = card;
            removeItem.Visibility = card.CanRemove ? Visibility.Visible : Visibility.Collapsed;
            removeItem.IsEnabled = card.CanRemove;
        }
        if (!card.CanRemove && !card.CanUpdate && e is not null) e.Handled = true;
    }

    private async void UpdateContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: LauncherCard card }) return;
        card.RefreshInstalledState();
        if (!card.CanUpdate || _updateCatalog is null) return;
        await UpdateCardAsync(card);
    }

    private async void RemoveContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: LauncherCard card }) return;
        card.RefreshInstalledState();
        if (!card.CanRemove) return;
        string confirmation = BuildUninstallConfirmationMessage(card.Entry);
        if (!_confirmUninstall(card.Entry, confirmation)) return;

        // Re-read after confirmation so a changed state can never toggle into another action.
        card.RefreshInstalledState();
        if (!card.CanRemove) return;
        await UninstallCardAsync(card);
    }

    private async Task InstallCardAsync(LauncherCard card)
    {
        card.RefreshInstalledState();
        if (!card.CanInstall || card.IsInstalled) return;

        card.IsInstalling = true;
        card.IsBusy = true;
        card.StatusMessage = "설치 중…";
        BeginOperation();
        try
        {
            string result = await _catalog.InstallAsync(card.Entry);
            card.RefreshInstalledState();
            card.StatusMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            card.StatusMessage = "설치를 취소했습니다.";
        }
        catch (Exception ex)
        {
            card.StatusMessage = "설치하지 못했습니다.";
            ShowError($"{card.Entry.Name} 설치: {GetFriendlyMessage(ex)}");
        }
        finally
        {
            card.IsBusy = false;
            card.IsInstalling = false;
            EndOperation();
            ApplyFilter();
        }
    }

    private async Task UninstallCardAsync(LauncherCard card)
    {
        card.RefreshInstalledState();
        if (!card.CanRemove) return;
        card.IsUninstalling = true;
        card.IsBusy = true;
        card.StatusMessage = "\uC81C\uAC70\uB97C \uC900\uBE44\uD558\uACE0 \uC788\uC2B5\uB2C8\uB2E4\u2026";
        BeginOperation();
        try
        {
            await _catalog.UninstallAsync(card.Entry);
            card.RefreshInstalledState();
            card.StatusMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            card.RefreshInstalledState();
            card.StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            card.RefreshInstalledState();
            card.StatusMessage = "\uC81C\uAC70\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.";
            ShowError($"{card.Entry.Name} \uC81C\uAC70: {GetFriendlyMessage(ex)}");
        }
        finally
        {
            card.IsBusy = false;
            card.IsUninstalling = false;
            EndOperation();
            ApplyFilter();
        }
    }

    private async Task UpdateCardAsync(LauncherCard card)
    {
        card.RefreshInstalledState();
        if (!card.CanUpdate || _updateCatalog is null) return;
        card.IsBusy = true;
        card.StatusMessage = "업데이트 중…";
        BeginOperation();
        try
        {
            await _updateCatalog.UpdateAsync(card.Entry);
            card.RefreshInstalledState();
            card.NotifyVersionState();
            card.StatusMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            card.StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            card.StatusMessage = "\uC5C5\uB370\uC774\uD2B8\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.";
            ShowError($"{card.Entry.Name} \uC5C5\uB370\uC774\uD2B8: {GetFriendlyMessage(ex)}");
        }
        finally
        {
            card.IsBusy = false;
            EndOperation();
            ApplyFilter();
        }
    }

    private void ScheduleVersionCheck()
    {
        if (_updateCatalog is null || _activeOperations != 0 || _versionCheckRunning) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_updateCatalog is not null && _activeOperations == 0 && !_versionCheckRunning)
            {
                _versionCheckRunning = true;
                _ = CheckVersionsAsync();
            }
        }));
    }

    private async Task CheckVersionsAsync()
    {
        try
        {
            await _updateCatalog!.CheckForUpdatesAsync();
            foreach (LauncherCard card in _allCards) card.NotifyVersionState();
        }
        catch
        {
            // Keep the last known version state when the update source is unavailable.
        }
        finally
        {
            _versionCheckRunning = false;
        }
    }

    private void BeginOperation()
    {
        _activeOperations++;
        RefreshButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        _activeOperations--;
        RefreshButton.IsEnabled = _activeOperations == 0;
        if (_activeOperations == 0) ScheduleVersionCheck();
    }

    private static bool ConfirmUninstall(LauncherEntry entry, string message) =>
        MessageBox.Show(message, "\uC571 \uC81C\uAC70", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool ConfirmInstall(LauncherEntry entry, string message)
    {
        var dialog = new InstallConfirmationWindow(entry.Name, new LauncherCard(entry).IconSource)
        {
            Owner = this
        };
        return dialog.ShowDialog() == true;
    }

    private static string BuildInstallConfirmationMessage(LauncherEntry entry) =>
        $"{entry.Name}\uC744(\uB97C) \uC124\uCE58\uD558\uC2DC\uACA0\uC2B5\uB2C8\uAE4C?";

    private static string BuildUninstallConfirmationMessage(LauncherEntry entry) =>
        $"\"{entry.Name}\" \uC571\uC744 \uC81C\uAC70\uD560\uAE4C\uC694?\n\n" +
        "\uC774 \uC571\uC758 \uAC1C\uBCC4 \uC124\uCE58 \uB4F1\uB85D\uACFC \uC2DC\uC791 \uBA54\uB274 \uBC14\uB85C\uAC00\uAE30\uB9CC \uC81C\uAC70\uD569\uB2C8\uB2E4. " +
        "\uB2E4\uC6B4\uB85C\uB4DC\uD55C \uD30C\uC77C, \uACF5\uC6A9 \uC2E4\uD589 \uD658\uACBD, \uC0AC\uC6A9\uC790 \uB370\uC774\uD130\uB294 \uC720\uC9C0\uB429\uB2C8\uB2E4.";

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_catalog.RootDirectory))
            {
                ShowError("앱 폴더를 찾을 수 없습니다.");
                return;
            }

            Process.Start(new ProcessStartInfo(_catalog.RootDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError($"앱 폴더를 열지 못했습니다: {GetFriendlyMessage(ex)}");
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = isMaximized ? Visibility.Visible : Visibility.Collapsed;

        string actionName = isMaximized ? "이전 크기로 복원" : "최대화";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, actionName);
        MaximizeButton.ToolTip = actionName;
    }

    private static string GetFriendlyMessage(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException => "실행 파일을 찾을 수 없습니다.",
            DirectoryNotFoundException => "지정된 폴더를 찾을 수 없습니다.",
            UnauthorizedAccessException => "이 항목에 접근할 권한이 없습니다.",
            _ when string.IsNullOrWhiteSpace(exception.Message) => "알 수 없는 오류가 발생했습니다.",
            _ => exception.Message
        };
    }

    public sealed class LauncherCard : INotifyPropertyChanged
    {
        private static readonly Brush InstalledCardTitleBrush = CreateFrozenBrush(Color.FromRgb(245, 247, 251));
        private static readonly Brush UninstalledCardTitleBrush = CreateFrozenBrush(Color.FromRgb(205, 214, 228));
        private static readonly Brush InstalledCardDescriptionBrush = CreateFrozenBrush(Color.FromRgb(172, 183, 202));
        private static readonly Brush UninstalledCardDescriptionBrush = CreateFrozenBrush(Color.FromRgb(155, 167, 186));
        private static readonly Brush InstalledCardBackgroundBrush = CreateFrozenBrush(Color.FromRgb(25, 39, 42));
        private static readonly Brush UninstalledCardBackgroundBrush = CreateFrozenBrush(Color.FromRgb(35, 45, 59));
        private static readonly Brush InstalledCardBorderBrush = CreateFrozenBrush(Color.FromRgb(89, 191, 174));
        private static readonly Brush UninstalledCardBorderBrush = CreateFrozenBrush(Color.FromRgb(117, 131, 152));
        private static readonly Brush InstalledCardHoverBrush = CreateFrozenBrush(Color.FromRgb(89, 191, 174));
        private static readonly Brush UninstalledCardHoverBrush = CreateFrozenBrush(Color.FromRgb(117, 131, 152));
        private static readonly Brush UpdateVersionBrush = CreateFrozenBrush(Color.FromRgb(168, 218, 181));
        private static readonly Brush MutedVersionBrush = CreateFrozenBrush(Color.FromRgb(133, 146, 166));

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool _isBusy;
        private bool _isInstalling;
        private bool _isUninstalling;
        private bool _isInstalled;
        private string _statusMessage = "";
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                foreach (string name in new[] { nameof(IsBusy), nameof(DescriptionText), nameof(CanRun), nameof(CanActivate), nameof(CanInteract), nameof(CanRemove), nameof(CanUpdate), nameof(CanInstall), nameof(RunLabel), nameof(InstallLabel), nameof(InstallAccessibleName), nameof(InstallToolTip), nameof(RunAccessibleName), nameof(RunToolTip), nameof(IsAvailable) })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
        public bool IsInstalling
        {
            get => _isInstalling;
            set
            {
                _isInstalling = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstalling)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallAccessibleName)));
            }
        }
        public bool IsUninstalling
        {
            get => _isUninstalling;
            set
            {
                _isUninstalling = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUninstalling)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallAccessibleName)));
            }
        }
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DescriptionText)));
            }
        }
        public string DescriptionText => IsBusy
            ? string.IsNullOrWhiteSpace(StatusMessage) ? "작업 중…" : StatusMessage
            : Entry.Description;
        public bool CanRun => IsInstalled && !IsBusy && Entry.IsAvailable;
        public bool CanActivate => !IsBusy && (IsInstalled ? Entry.IsAvailable : Entry.CanInstall);
        public bool CanRemove => IsInstalled && !IsBusy && Entry.CanUninstall;
        public bool CanUpdate => IsInstalled && !IsBusy && Entry.UpdateAvailable;
        public bool CanInteract => CanActivate || CanRemove;
        public double IconOpacity => IsInstalled ? 1.0 : 0.85;
        public Brush CardTitleBrush => IsInstalled ? InstalledCardTitleBrush : UninstalledCardTitleBrush;
        public Brush CardDescriptionBrush => IsInstalled ? InstalledCardDescriptionBrush : UninstalledCardDescriptionBrush;
        public Brush CardBackgroundBrush => IsInstalled ? InstalledCardBackgroundBrush : UninstalledCardBackgroundBrush;
        public Brush CardBorderBrush => IsInstalled ? InstalledCardBorderBrush : UninstalledCardBorderBrush;
        public Brush CardHoverBrush => IsInstalled ? InstalledCardHoverBrush : UninstalledCardHoverBrush;
        public string VersionText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Entry.InstalledVersion))
                    return CompactVersion(Entry.InstalledVersion);
                if (Entry.UpdateAvailable && !string.IsNullOrWhiteSpace(Entry.AvailableVersion))
                    return CompactVersion(Entry.AvailableVersion);
                if (IsInstalled)
                    return CompactVersion(Entry.BundledVersion);
                if (!string.IsNullOrWhiteSpace(Entry.AvailableVersion))
                    return CompactVersion(Entry.AvailableVersion);
                return CompactVersion(Entry.BundledVersion);
            }
        }

        private static string CompactVersion(string? version)
        {
            var match = System.Text.RegularExpressions.Regex.Match(version?.Trim() ?? string.Empty,
                @"^[vV]?(\d+(?:\.\d+)*)(?:[-+][^\s]+)?$");
            return match.Success
                ? string.Join(".", match.Groups[1].Value.Split('.').Take(3))
                : "버전 정보 없음";
        }

        public Brush VersionBrush => Entry.UpdateAvailable ? UpdateVersionBrush : MutedVersionBrush;
        public Visibility VersionVisibility => Visibility.Visible;
        public string VersionToolTip
        {
            get
            {
                var details = new List<string>();
                if (!string.IsNullOrWhiteSpace(Entry.InstalledVersion))
                    details.Add($"설치된 앱: {Entry.InstalledVersion}");
                else if (IsInstalled)
                    details.Add("설치된 앱의 버전은 확인할 수 없습니다.");
                if (!string.IsNullOrWhiteSpace(Entry.BundledVersion))
                    details.Add($"런처의 앱: {Entry.BundledVersion}");
                if (!string.IsNullOrWhiteSpace(Entry.AvailableVersion))
                    details.Add($"{(Entry.UpdateAvailable ? "새 버전" : "온라인 앱")}: {Entry.AvailableVersion}");
                if (Entry.UpdateAvailable)
                    details.Add(string.IsNullOrWhiteSpace(Entry.AvailableVersion)
                        ? "업데이트할 수 있지만 새 버전 정보는 확인할 수 없습니다."
                        : "새 버전으로 업데이트할 수 있습니다.");
                return details.Count == 0 ? "버전 정보를 확인할 수 없습니다." : string.Join("\n", details);
            }
        }
        public Visibility InstallVisibility => Entry.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        public int RunColumn => InstallVisibility == Visibility.Visible ? 1 : 0;
        public int RunColumnSpan => InstallVisibility == Visibility.Visible ? 1 : 2;
        public Thickness RunMargin => InstallVisibility == Visibility.Visible ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        public bool CanInstall => !IsBusy && Entry.CanInstall && (!IsInstalled || Entry.CanUninstall);
        public string RunLabel => "\uC2E4\uD589";
        public string InstallLabel => IsBusy && IsInstalling ? "\uC124\uCE58 \uC911\u2026"
            : IsBusy && IsUninstalling ? "\uC81C\uAC70 \uC911\u2026"
            : IsInstalled ? (Entry.CanUninstall ? "\uC81C\uAC70" : "\uC124\uCE58\uC644\uB8CC") : "\uC124\uCE58";
        public string InstallAutomationId => $"Install_{Entry.Id}";
        public string RunAccessibleName => IsInstalled ? $"\uC2E4\uD589 {Entry.Name}" : $"\uC124\uCE58 \uD655\uC778 {Entry.Name}";
        public string RunToolTip => IsBusy ? $"{Entry.Name} \uC791\uC5C5 \uC911"
            : IsInstalled ? $"\uC2E4\uD589 {Entry.Name}"
            : Entry.CanInstall ? "\uBBF8\uC124\uCE58 \uC571\uC744 \uD074\uB9AD\uD558\uBA74 \uC124\uCE58 \uC5EC\uBD80\uB97C \uD655\uC778\uD569\uB2C8\uB2E4"
            : "\uC124\uCE58\uD560 \uC218 \uC5C6\uB294 \uC571\uC785\uB2C8\uB2E4";
        public string InstallAccessibleName => $"{Entry.Name} {InstallLabel}";
        public string InstallToolTip => IsInstalled && Entry.CanUninstall
            ? "\uC2DC\uC791 \uBA54\uB274 \uB4F1\uB85D \uC81C\uAC70"
            : IsInstalled ? "\uC774\uBBF8 \uC124\uCE58\uB428"
            : Entry.CanInstall ? "\uC2DC\uC791 \uBA54\uB274\uC5D0 \uC124\uCE58"
            : "\uC124\uCE58 \uBD88\uAC00";
        public LauncherEntry Entry { get; }
        public ImageSource? IconSource { get; }
        public string IconToolTip => $"{Entry.Name}\n{Entry.Category}\n{Entry.Description}";
        public Visibility IconVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility InitialsVisibility => IconSource is null ? Visibility.Visible : Visibility.Collapsed;
        public string Initials { get; }
        public bool IsAvailable => Entry.IsAvailable;
        public bool IsInstalled => _isInstalled;
        public bool IsManagedInstalled => IsInstalled && Entry.CanUninstall;
        public string RegistrationGlyphData => IsManagedInstalled
            ? "M 4,7 L 28,7 M 12,7 L 13,4 L 19,4 L 20,7 M 8,9 L 10,27 L 22,27 L 24,9 M 13,12 L 13,23 M 19,12 L 19,23"
            : IsInstalled
                ? "M 5,16 L 12,22 L 27,7"
                : "M 16,3 L 16,19 M 10,13 L 16,19 L 22,13 M 5,21 L 5,26 L 27,26 L 27,21";
        public string AutomationId => $"Launch_{Entry.Id}";
        public string AccessibleName => $"{Entry.Name} \uC2E4\uD589. {Entry.Description}";

        public LauncherCard(LauncherEntry entry)
        {
            Entry = entry;
            _isInstalled = entry.TryGetInstalledExecutablePath() is not null;
            _statusMessage = string.Empty;
            IconSource = LoadIcon(entry.IconPath);
            Initials = GetInitials(entry.Name);
        }

        public void RefreshInstalledState()
        {
            bool isInstalled = Entry.TryGetInstalledExecutablePath() is not null;
            if (_isInstalled == isInstalled) return;
            _isInstalled = isInstalled;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRun)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanActivate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInteract)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRemove)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunAccessibleName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunToolTip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconOpacity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTitleBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardDescriptionBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardHoverBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstall)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallAccessibleName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallToolTip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsManagedInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RegistrationGlyphData)));
            NotifyVersionState();
            StatusMessage = string.Empty;
        }

        public void NotifyVersionState()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUpdate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionToolTip)));
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public bool Matches(string query)
        {
            return Entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Entry.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Entry.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        private static ImageSource? LoadIcon(string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return null;
            }

            try
            {
                if (Uri.TryCreate(iconPath, UriKind.Absolute, out Uri? resourceUri) &&
                    string.Equals(resourceUri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(Path.GetExtension(resourceUri.AbsolutePath), ".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        var iconDecoder = new IconBitmapDecoder(resourceUri,
                            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        return SelectIconFrame(iconDecoder);
                    }

                    var resourceBitmap = new BitmapImage(resourceUri);
                    resourceBitmap.Freeze();
                    return resourceBitmap;
                }

                if (!File.Exists(iconPath))
                    return null;

                using FileStream stream = File.OpenRead(iconPath);
                if (string.Equals(Path.GetExtension(iconPath), ".ico", StringComparison.OrdinalIgnoreCase))
                {
                    var iconDecoder = new IconBitmapDecoder(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return SelectIconFrame(iconDecoder);
                }

                BitmapImage bitmap = new();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 128;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapFrame? SelectIconFrame(IconBitmapDecoder decoder)
        {
            BitmapFrame? frame = decoder.Frames
                .OrderBy(candidate => candidate.PixelWidth >= 64 && candidate.PixelHeight >= 64 ? 0 : 1)
                .ThenBy(candidate => candidate.PixelWidth >= 64 && candidate.PixelHeight >= 64
                    ? Math.Max(candidate.PixelWidth, candidate.PixelHeight)
                    : -Math.Min(candidate.PixelWidth, candidate.PixelHeight))
                .FirstOrDefault();
            frame?.Freeze();
            return frame;
        }

        private static string GetInitials(string name)
        {
            string trimmedName = name.Trim();
            if (trimmedName.Length == 0)
            {
                return "앱";
            }

            string[] words = trimmedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length >= 2
                ? string.Concat(words[0][0], words[1][0]).ToUpperInvariant()
                : trimmedName[..1].ToUpperInvariant();
        }
    }
}
