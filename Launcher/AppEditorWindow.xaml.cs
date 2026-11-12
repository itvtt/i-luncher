using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>GitHub 저장소 주소로 앱을 추가하거나 고치는 창. 불러오기는 저장소 정보와 최신 릴리스로 빈 칸을 채움</summary>
public partial class AppEditorWindow : Window
{
    private readonly AppDefinition? _existing;
    private readonly AppManager _manager;
    private readonly string? _defaultHost;
    private GitHubRelease? _release;
    private string? _autoName, _autoInstallName;
    private bool _fetching;

    public AppEditorWindow(AppDefinition? existing, AppManager manager, string? defaultHost, IReadOnlyList<string> categories)
    {
        _existing = existing;
        _manager = manager;
        _defaultHost = defaultHost;
        InitializeComponent();
        Title = HeaderText.Text = existing is null ? "앱 추가" : "앱 편집";
        foreach (string category in categories)
        {
            var chip = new Button
            {
                Content = category, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(9, 3, 9, 3), FontSize = 11,
                Style = (Style)FindResource("ToolbarButtonStyle")
            };
            chip.Click += (_, _) => CategoryBox.Text = category;
            CategorySuggestions.Children.Add(chip);
        }
        if (existing is not null)
        {
            RepositoryBox.Text = existing.Repository;
            NameBox.Text = existing.Name;
            CategoryBox.Text = existing.Category;
            DescriptionBox.Text = existing.Description;
            AssetBox.Text = existing.Asset ?? "";
            SetupAssetBox.Text = existing.SetupAsset ?? "";
            SetupArgumentsBox.Text = existing.SetupArguments ?? "";
            InstallNameBox.Text = existing.InstallName ?? "";
            ExecutableBox.Text = existing.Executable ?? "";
            ArgumentsBox.Text = existing.Arguments ?? "";
            IconBox.Text = existing.Icon ?? "";
            OrderBox.Text = existing.Order.ToString();
            IdBox.Text = existing.Id;
            IdBox.IsReadOnly = true;
            PrereleaseBox.IsChecked = existing.IncludePrerelease;
            MasterOnlyBox.IsChecked = existing.MasterOnly;
            KindZip.IsChecked = string.Equals(existing.Kind, InstallKinds.Zip, StringComparison.OrdinalIgnoreCase);
            KindSetup.IsChecked = string.Equals(existing.Kind, InstallKinds.Setup, StringComparison.OrdinalIgnoreCase);
            KindAuto.IsChecked = string.IsNullOrWhiteSpace(existing.Kind);
        }
        Loaded += async (_, _) =>
        {
            RepositoryBox.Focus();
            if (existing is not null) await FetchAsync(fillEmptyOnly: true);
        };
    }

    public AppDefinition? Result { get; private set; }

    private async void FetchButton_Click(object sender, RoutedEventArgs e) => await FetchAsync(fillEmptyOnly: _existing is not null);

    private async void RepositoryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FetchAsync(fillEmptyOnly: _existing is not null);
    }

    private async Task FetchAsync(bool fillEmptyOnly)
    {
        if (_fetching) return;
        ErrorText.Text = "";
        if (!GitHubRepository.TryParse(RepositoryBox.Text, out var repository, _defaultHost))
        {
            if (RepositoryBox.Text.Trim().Length > 0) ErrorText.Text = "GitHub 저장소 주소를 확인하세요. 예: https://github.example.com/team/App";
            return;
        }
        _fetching = true;
        FetchButton.IsEnabled = false;
        ReleaseInfo.Visibility = Visibility.Visible;
        ReleaseInfoText.Text = $"{repository.FullName} 읽는 중…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var info = await _manager.GitHub.GetRepositoryAsync(repository, timeout.Token);
            RepositoryBox.Text = repository.WebUrl;
            bool replaceName = NameBox.Text.Trim().Length == 0 || (!fillEmptyOnly && NameBox.Text == _autoName);
            if (replaceName) NameBox.Text = _autoName = info.Name;
            if (DescriptionBox.Text.Trim().Length == 0) DescriptionBox.Text = info.Description;

            _release = await _manager.GitHub.GetLatestReleaseAsync(repository, PrereleaseBox.IsChecked == true, timeout.Token);
            ShowRelease(repository, info);
        }
        catch (Exception error) when (error is GitHubApiException or OperationCanceledException)
        {
            ReleaseInfoText.Text = error is OperationCanceledException ? "응답이 없습니다." : error.Message;
        }
        finally
        {
            _fetching = false;
            FetchButton.IsEnabled = true;
        }
    }

    private void ShowRelease(GitHubRepository repository, GitHubRepositoryInfo info)
    {
        string visibility = info.Private ? "비공개 저장소" : "저장소";
        if (_release is not { } release)
        {
            AssetList.Visibility = Visibility.Collapsed;
            ReleaseInfoText.Text = $"{visibility} {info.FullName} · 아직 릴리스가 없습니다.\n저장해 두면 Actions 러너가 릴리스를 만든 뒤 카드에 버전이 표시됩니다.";
            return;
        }
        var draft = BuildDefinition(validate: false);
        string kind = AppManager.ResolveKind(draft, release);
        GitHubAsset? zip = AppManager.SelectZipAsset(draft, release);
        GitHubAsset? setup = AppManager.SelectSetupAsset(draft, release);
        string how = kind == InstallKinds.Zip
            ? zip is null ? "설치할 zip을 찾지 못했습니다. 아래 목록에서 고르세요." : $"zip으로 설치: {zip.Name}" + (setup is null ? "" : $" (.NET이 없으면 {setup.Name})")
            : setup is null ? "설치 프로그램을 찾지 못했습니다." : $"설치 프로그램으로 설치: {setup.Name} {AppManager.DefaultSetupArguments(draft, release)}" +
              (AppManager.IsVelopackRelease(release) ? " · Velopack 앱 (앱이 스스로 업데이트)" : "");
        ReleaseInfoText.Text = $"{visibility} {info.FullName} · 최신 릴리스 {release.Tag}" +
                               (release.Version is null ? " (버전 번호를 읽을 수 없는 태그)" : $" → 버전 {release.Version}") +
                               (release.PublishedAt is { } date ? $" · {date.ToLocalTime():yyyy-MM-dd}" : "") + "\n" + how;

        var files = release.Assets.Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                                  || asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                  || asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .Select(asset => asset.Name).ToList();
        AssetList.ItemsSource = files;
        AssetList.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 설치 폴더 이름: 앱 자체 설치 프로그램과 같은 이름이어야 함. zip 이름 앞부분(DeskCal-win-x64.zip → DeskCal)이 그 이름
        string? guess = (zip ?? setup) is { } asset ? AppManager.GuessInstallName(asset.Name) : null;
        guess ??= repository.Name;
        if (InstallNameBox.Text.Trim().Length == 0 || InstallNameBox.Text == _autoInstallName)
            InstallNameBox.Text = _autoInstallName = guess;
    }

    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is not string name) return;
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            AssetBox.Text = name;
            if (KindSetup.IsChecked == true) KindAuto.IsChecked = true;
        }
        else
        {
            SetupAssetBox.Text = name;
        }
    }

    private AppDefinition BuildDefinition(bool validate)
    {
        string? Clean(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
        string repositoryText = RepositoryBox.Text.Trim();
        var repository = GitHubRepository.Parse(repositoryText, _defaultHost);
        string name = NameBox.Text.Trim();
        string id = _existing?.Id ?? (Clean(IdBox) is { } typed ? AppDefinition.CreateId(typed)
            : AppDefinition.CreateId(name.Length > 0 ? name : repository?.Name ?? "app"));
        return new AppDefinition
        {
            Id = id,
            Name = name,
            Repository = repository?.WebUrl ?? repositoryText,
            Description = DescriptionBox.Text.Trim(),
            Category = CategoryBox.Text.Trim(),
            Kind = KindZip.IsChecked == true ? InstallKinds.Zip : KindSetup.IsChecked == true ? InstallKinds.Setup : null,
            Asset = Clean(AssetBox),
            SetupAsset = Clean(SetupAssetBox),
            SetupArguments = Clean(SetupArgumentsBox),
            InstallName = Clean(InstallNameBox),
            Executable = Clean(ExecutableBox),
            Arguments = Clean(ArgumentsBox),
            Icon = Clean(IconBox),
            IncludePrerelease = PrereleaseBox.IsChecked == true,
            MasterOnly = MasterOnlyBox.IsChecked == true,
            // 베타·NEW 는 API 서버 관리 프로그램에서 정함: 여기서 고쳐도 그대로 둠
            Beta = _existing?.Beta ?? false,
            New = _existing?.New ?? false,
            Order = int.TryParse(OrderBox.Text.Trim(), out int order) ? order : _existing?.Order ?? 0
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var app = BuildDefinition(validate: true);
        if (app.Name.Length == 0 && GitHubRepository.Parse(app.Repository, _defaultHost) is { } repository) app.Name = repository.Name;
        if (app.Validate(_defaultHost) is { } problem)
        {
            ErrorText.Text = problem;
            return;
        }
        Result = app;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}
