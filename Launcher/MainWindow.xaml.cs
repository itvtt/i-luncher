using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

public partial class MainWindow : Window
{
    private readonly ILauncherCatalog _catalog;
    private readonly Func<LauncherEntry, string, bool> _confirmUninstall;
    private readonly List<LauncherCard> _allCards = [];
    private readonly List<Button> _categoryButtons = [];
    private string _selectedCategory = string.Empty;
    private bool _installedOnly;
    private int _activeOperations;
    private bool _catalogRefreshPending;
    private Task? _catalogRefreshTask;
    private int _catalogRefreshGeneration;

    public ObservableCollection<LauncherCard> LauncherCards { get; } = [];

    public MainWindow() : this(new LauncherCatalog(new IntegratedAppProvider()))
    {
    }

    public MainWindow(ILauncherCatalog catalog, Func<LauncherEntry, string, bool>? confirmUninstall = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _confirmUninstall = confirmUninstall ?? ConfirmUninstall;
        InitializeComponent();
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
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCatalog();
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

    private async void LauncherCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherCard card })
        {
            return;
        }

        if (!card.CanRun) return;
        card.IsInstalling = false;
        card.IsBusy = true;
        card.StatusMessage = "앱을 준비하고 시작하는 중…";
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

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherCard card } || !card.CanInstall) return;
        if (card.IsInstalled)
        {
            if (!card.Entry.CanUninstall) return;
            string confirmation = BuildUninstallConfirmationMessage(card.Entry);
            if (!_confirmUninstall(card.Entry, confirmation)) return;
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
            return;
        }

        card.IsInstalling = true;
        card.IsBusy = true;
        card.StatusMessage = "설치를 준비하고 있습니다…";
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

    private void BeginOperation()
    {
        _activeOperations++;
        RefreshButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        _activeOperations--;
        RefreshButton.IsEnabled = _activeOperations == 0;
    }

    private static bool ConfirmUninstall(LauncherEntry entry, string message) =>
        MessageBox.Show(message, "\uC571 \uC81C\uAC70", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

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
                foreach (string name in new[] { nameof(IsBusy), nameof(DescriptionText), nameof(CanRun), nameof(CanInstall), nameof(RunLabel), nameof(InstallLabel), nameof(InstallAccessibleName), nameof(InstallToolTip), nameof(IsAvailable) })
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
        public bool CanRun => !IsBusy && Entry.IsAvailable;
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
        public string RunAccessibleName => $"{Entry.Name} \uC2E4\uD589";
        public string RunToolTip => "\uC571 \uC2E4\uD589";
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstall)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallAccessibleName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallToolTip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsManagedInstalled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RegistrationGlyphData)));
            StatusMessage = string.Empty;
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
