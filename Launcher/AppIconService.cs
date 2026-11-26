using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 카드 아이콘. 우선순위:
///   1. 카탈로그에 지정한 아이콘 (icon: 저장소 안 경로 또는 주소)
///   2. 저장소 .csproj 의 ApplicationIcon (실행 파일 아이콘의 원본, 설치 전에도 보임)
///   3. 설치된 실행 파일의 아이콘
///   4. 저장소에서 이름으로 찾은 아이콘 (Assets/…icon.png 등)
///   5. 예전에 본 실행 파일 아이콘 (제거한 뒤에도)
/// 저장소에서 받은 아이콘은 %LOCALAPPDATA%\i-luncher\icons 에 PNG 로 두고 7일마다 (못 찾았으면 하루마다) 다시 확인.
/// 그보다 새 릴리스가 나왔으면 바로 다시 확인 (아이콘 파일 이름은 그대로 두고 그림만 바꾼 릴리스도 반영).
/// </summary>
public sealed class AppIconService(GitHubClient github, string cacheDirectory, Func<string?> defaultHost)
{
    private static readonly TimeSpan RecheckFound = TimeSpan.FromDays(7);
    private static readonly TimeSpan RecheckMissing = TimeSpan.FromDays(1);
    private const long MaxIconBytes = 2_000_000;

    private sealed class Meta
    {
        public string Key { get; set; } = "";
        public string? Path { get; set; }
        public bool Confident { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    private string MetaPath(AppDefinition app) => Path.Combine(cacheDirectory, AppDefinition.SafeFolderName(app.Id) + ".repo.json");
    private string ImagePath(AppDefinition app) => Path.Combine(cacheDirectory, AppDefinition.SafeFolderName(app.Id) + ".repo.png");
    private static string Key(AppDefinition app) => app.Repository.Trim().ToLowerInvariant() + "|" + (app.Icon ?? "").Trim();

    /// <summary>지금 가진 것 중 가장 좋은 아이콘 (네트워크 없음). 백그라운드 스레드에서 불러도 됨</summary>
    public ImageSource? Current(AppDefinition app, InstalledApp? installed)
    {
        // 내 앱에 고른 아이콘 (기본 아이콘 · 이미지 파일)
        if (CustomIcons.IsCustom(app.Icon) && CustomIcons.Load(app.Icon) is { } custom) return custom;
        var meta = ReadMeta(app);
        ImageSource? repository = meta is { Path: not null } && meta.Key == Key(app) ? LoadPng(ImagePath(app)) : null;
        if (repository is not null && meta!.Confident) return repository;
        if (installed?.ExecutablePath is { } executable && AppIcons.Load(app.Id, executable, cacheDirectory) is { } fromExecutable) return fromExecutable;
        return repository ?? AppIcons.Load(app.Id, null, cacheDirectory);
    }

    /// <summary>저장소에서 아이콘을 (필요하면) 다시 찾음. releasedAt = 최신 릴리스 시각 (캐시보다 새로우면 다시 찾음). 바뀌었으면 true</summary>
    public async Task<bool> RefreshAsync(AppDefinition app, DateTimeOffset? releasedAt = null, CancellationToken cancellationToken = default)
    {
        if (CustomIcons.IsCustom(app.Icon)) return false;  // 고른 아이콘은 저장소에서 찾지 않음
        var meta = ReadMeta(app);
        string key = Key(app);
        if (meta is not null && meta.Key == key && DateTimeOffset.Now - meta.CheckedAt < (meta.Path is null ? RecheckMissing : RecheckFound)
            && !(releasedAt is { } released && released > meta.CheckedAt)
            && (meta.Path is null || File.Exists(ImagePath(app))))
            return false;
        if (GitHubRepository.Parse(app.Repository, defaultHost()) is not { } repository) return false;

        var found = await DiscoverAsync(app, repository, cancellationToken).ConfigureAwait(false);
        var next = new Meta { Key = key, CheckedAt = DateTimeOffset.Now };
        if (found is { } icon && Decode(icon.Bytes) is { } image)
        {
            SavePng(image, ImagePath(app));
            next.Path = icon.Path;
            next.Confident = icon.Confident;
        }
        WriteMeta(app, next);
        return next.Path != meta?.Path || next.Key != meta?.Key || next.Path is not null;
    }

    private async Task<(string Path, byte[] Bytes, bool Confident)?> DiscoverAsync(AppDefinition app, GitHubRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            // 1. 카탈로그에 적은 아이콘
            if (!string.IsNullOrWhiteSpace(app.Icon))
            {
                string icon = app.Icon!.Trim();
                byte[] bytes = Uri.TryCreate(icon, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                    ? await github.GetUrlBytesAsync(repository, url, MaxIconBytes, cancellationToken).ConfigureAwait(false)
                    : await github.GetRawFileAsync(repository, icon.Replace('\\', '/').TrimStart('/'), null, MaxIconBytes, cancellationToken).ConfigureAwait(false);
                return (icon, bytes, true);
            }

            // 2·4. 저장소 파일 목록에서 찾기 (아주 큰 저장소는 자른 목록만 받음)
            var info = await github.GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
            var (entries, _) = await github.GetTreeAsync(repository, info.DefaultBranch, recursive: true, cancellationToken).ConfigureAwait(false);
            var files = entries.Where(entry => entry.IsFile).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
            var names = IconDiscovery.Names(app.Name, app.InstallName, repository.Name);
            foreach (string project in IconDiscovery.ProjectCandidates(entries, names))
            {
                byte[] projectBytes = await github.GetRawFileAsync(repository, project, info.DefaultBranch, 512_000, cancellationToken).ConfigureAwait(false);
                if (IconDiscovery.ParseApplicationIcon(System.Text.Encoding.UTF8.GetString(projectBytes), project) is { } iconPath &&
                    files.TryGetValue(iconPath, out var iconEntry))
                    return (iconEntry.Path, await github.GetRawFileAsync(repository, iconEntry.Path, info.DefaultBranch, MaxIconBytes, cancellationToken).ConfigureAwait(false), true);
            }
            if (IconDiscovery.ChooseByName(entries, names) is { } guessed)
                return (guessed, await github.GetRawFileAsync(repository, guessed, info.DefaultBranch, MaxIconBytes, cancellationToken).ConfigureAwait(false), false);
            return null;
        }
        catch (Exception error) when (error is GitHubApiException or InvalidDataException or IOException or OperationCanceledException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine($"Icon discovery for {app.Id} failed: {error.Message}");
            return null;
        }
    }

    /// <summary>.ico(가장 큰 프레임) 또는 .png 를 256px 이하 그림으로. 읽을 수 없으면 null</summary>
    public static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames.OrderByDescending(item => item.PixelWidth * item.PixelHeight).ThenByDescending(item => item.Format.BitsPerPixel).First();
            if (frame.PixelWidth > 256 || frame.PixelHeight > 256)
            {
                double scale = 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            frame.Freeze();
            return frame;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException
                                      or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    private static ImageSource? LoadPng(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException) { return null; }
    }

    private static void SavePng(BitmapSource image, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    private Meta? ReadMeta(AppDefinition app)
    {
        try { return File.Exists(MetaPath(app)) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath(app))) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void WriteMeta(AppDefinition app, Meta meta)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(MetaPath(app), JsonSerializer.Serialize(meta));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
