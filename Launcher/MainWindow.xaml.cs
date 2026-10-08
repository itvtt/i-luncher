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
                    ShowCatalogErrors(result.Errors);
                    ApplyFilter();
                }
                catch (Exception ex)
                {
                    if (generation == _catalogRefreshGeneration)
                    {
                        _allCards.Clear();
                        LauncherCards.Clear();
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

    private void ApplyFilter()
    {
        if (SearchBox is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        IEnumerable<LauncherCard> filtered = string.IsNullOrWhiteSpace(query)
            ? _allCards
            : _allCards.Where(card => card.Matches(query));

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
        LauncherItems.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        bool hasSearch = !string.IsNullOrWhiteSpace(SearchBox?.Text);
        EmptyStateTitle.Text = hasSearch ? "검색 결과가 없습니다" : "등록된 앱이 없습니다";
        EmptyStateDescription.Text = hasSearch
            ? "다른 이름, 설명 또는 분류로 검색해 보세요."
            : "새로고침하여 앱을 다시 찾아보세요.";
    }

    private void UpdateCount()
    {
        if (string.IsNullOrWhiteSpace(SearchBox?.Text))
        {
            CountText.Text = $"앱 {LauncherCards.Count}개";
            return;
        }

        CountText.Text = $"{LauncherCards.Count} / {_allCards.Count}개";
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
            card.StatusMessage = card.IsInstalled ? "\uC124\uCE58\uC644\uB8CC" : string.Empty;
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
                card.StatusMessage = card.IsInstalled ? "\uC124\uCE58\uC644\uB8CC" : "\uC81C\uAC70 완료";
            }
            catch (OperationCanceledException)
            {
                card.RefreshInstalledState();
                card.StatusMessage = card.IsInstalled ? "\uC124\uCE58\uC644\uB8CC" : "\uC81C\uAC70\uB97C \uCDE8\uC18C\uD588\uC2B5\uB2C8\uB2E4.";
            }
            catch (Exception ex)
            {
                card.RefreshInstalledState();
                card.StatusMessage = card.IsInstalled ? "\uC124\uCE58\uC644\uB8CC" : "\uC81C\uAC70\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.";
                ShowError($"{card.Entry.Name} \uC81C\uAC70: {GetFriendlyMessage(ex)}");
            }
            finally
            {
                card.IsBusy = false;
                card.IsUninstalling = false;
                EndOperation();
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
            card.StatusMessage = card.IsInstalled ? "\uC124\uCE58\uC644\uB8CC" : result;
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
                foreach (string name in new[] { nameof(IsBusy), nameof(CanRun), nameof(CanInstall), nameof(RunLabel), nameof(InstallLabel), nameof(InstallAccessibleName), nameof(InstallToolTip), nameof(IsAvailable) })
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
            set { _statusMessage = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage))); }
        }
        public bool CanRun => !IsBusy && Entry.IsAvailable;
        public Visibility InstallVisibility => Entry.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        public int RunColumn => InstallVisibility == Visibility.Visible ? 1 : 0;
        public int RunColumnSpan => InstallVisibility == Visibility.Visible ? 1 : 2;
        public Thickness RunMargin => InstallVisibility == Visibility.Visible ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        public bool CanInstall => !IsBusy && Entry.CanInstall && (!IsInstalled || Entry.CanUninstall);
        public string RunLabel => IsBusy && !IsInstalling && !IsUninstalling
            ? "\uD3EC\uD130\uBE14 \uC2E4\uD589 \uC911\u2026" : "\uD3EC\uD130\uBE14 \uC2E4\uD589";
        public string InstallLabel => IsBusy && IsInstalling ? "\uC124\uCE58 \uC911\u2026"
            : IsBusy && IsUninstalling ? "\uC81C\uAC70 \uC911\u2026"
            : IsInstalled ? (Entry.CanUninstall ? "\uC81C\uAC70" : "\uC124\uCE58\uC644\uB8CC") : "\uC124\uCE58";
        public string InstallAutomationId => $"Install_{Entry.Id}";
        public string RunAccessibleName => $"{Entry.Name} \uD3EC\uD130\uBE14 \uC2E4\uD589";
        public string RunToolTip => "\uC124\uCE58 \uB4F1\uB85D \uC5C6\uC774 \uBC14\uB85C \uC2E4\uD589\uD569\uB2C8\uB2E4.";
        public string InstallAccessibleName => $"{Entry.Name} {InstallLabel}";
        public string InstallToolTip => IsInstalled && Entry.CanUninstall
            ? "\uAC1C\uBCC4 \uC124\uCE58 \uB4F1\uB85D\uACFC \uC2DC\uC791 \uBA54\uB274 \uBC14\uB85C\uAC00\uAE30\uB9CC \uC81C\uAC70\uD558\uACE0, \uB2E4\uC6B4\uB85C\uB4DC\uD55C \uD30C\uC77C\uACFC \uACF5\uC6A9 \uC2E4\uD589 \uD658\uACBD, \uC0AC\uC6A9\uC790 \uB370\uC774\uD130\uB294 \uC720\uC9C0\uD569\uB2C8\uB2E4."
            : IsInstalled ? "\uC774 \uC571\uC740 \uC124\uCE58\uB418\uC5B4 \uC788\uC9C0\uB9CC, \uC774 \uC2E4\uD589 \uC720\uD615\uC5D0\uC11C\uB294 \uC81C\uAC70\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4."
            : Entry.CanInstall
                ? "\uC124\uCE58\uD558\uBA74 \uC2DC\uC791 \uBA54\uB274\uC5D0 \uB4F1\uB85D\uD558\uACE0 \uBC14\uB85C \uAC00\uAE30\uB97C \uB9CC\uB4ED\uB2C8\uB2E4."
                : "\uC124\uCE58 \uAE30\uB2A5\uC744 \uC9C0\uC6D0\uD558\uC9C0 \uC54A\uB294 \uC571\uC785\uB2C8\uB2E4.";
        public LauncherEntry Entry { get; }
        public ImageSource? IconSource { get; }
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
        public string AccessibleName => $"{Entry.Name} \uD3EC\uD130\uBE14 \uC2E4\uD589. {Entry.Description}";

        public LauncherCard(LauncherEntry entry)
        {
            Entry = entry;
            _isInstalled = entry.TryGetInstalledExecutablePath() is not null;
            _statusMessage = _isInstalled ? "\uC124\uCE58\uC644\uB8CC" : string.Empty;
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
            StatusMessage = isInstalled ? "\uC124\uCE58\uC644\uB8CC" : string.Empty;
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
                    var resourceBitmap = new BitmapImage(resourceUri);
                    resourceBitmap.Freeze();
                    return resourceBitmap;
                }

                if (!File.Exists(iconPath))
                    return null;

                using FileStream stream = File.OpenRead(iconPath);
                BitmapImage bitmap = new();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
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
