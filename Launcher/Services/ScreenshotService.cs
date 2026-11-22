using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ILauncher.Services;

/// <summary>
/// 앱 페이지의 스크린샷. 런처에 든 기본 목록의 그림은 런처 안(저장소 basic-media\, 이름 그대로).
/// GitHub 기본 목록의 그림("basic:경로#sha")은 런처에 같은 그림(같은 sha)이 있으면 그것, 아니면 GitHub 공지 저장소에서 받아 sha 별로 보관.
/// 그 밖은 받아 데이터 폴더(screenshots\)에 보관: API 서버에 올린 이미지는 이름이 내용(SHA-256)이라
/// 한 번 받으면 다시 받지 않음. http(s) 주소는 주소로 만든 이름으로 보관.
/// </summary>
public sealed class ScreenshotService(string cacheDirectory, Func<string?> apiServer,
    Func<string, long, CancellationToken, Task<byte[]>>? basicMedia = null) : IDisposable
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("i-luncher/" + ILuncher.AppKit.AppUpdater.CurrentVersion);
        return http;
    }

    /// <summary>받을 주소: http(s) 그대로, 아니면 API 서버의 /launcher/media/&lt;이름&gt; (서버가 없으면 null)</summary>
    public Uri? Address(string shot)
    {
        if (shot.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || shot.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(shot, UriKind.Absolute, out var url) ? url : null;
        return apiServer() is { Length: > 0 } server ? new Uri(server.TrimEnd('/') + "/launcher/media/" + Uri.EscapeDataString(shot)) : null;
    }

    private string CachePath(string shot)
    {
        string name = shot.Contains('/') || shot.Contains('\\')
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shot)))[..16].ToLowerInvariant() + Path.GetExtension(new Uri(shot).AbsolutePath)
            : shot;
        return Path.Combine(cacheDirectory, string.Concat(name.Split(Path.GetInvalidFileNameChars())));
    }

    /// <summary>스크린샷 한 장 (화면에 맞춰 줄여 읽음). 받지 못하면 null</summary>
    public async Task<BitmapSource?> LoadAsync(string shot, int decodeHeight, CancellationToken cancellationToken)
    {
        try
        {
            if (BasicApps.TryParseMedia(shot, out string mediaPath, out string mediaName, out string mediaSha))
                return await LoadBasicAsync(mediaPath, mediaName, mediaSha, decodeHeight, cancellationToken);
            if (BuiltIn(shot) is { } builtIn) return await Task.Run(() => Decode(builtIn, decodeHeight), cancellationToken);
            string path = CachePath(shot);
            if (!File.Exists(path))
            {
                if (Address(shot) is not { } url) return null;
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
                byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (data.Length == 0 || data.Length > MaxBytes) return null;
                Directory.CreateDirectory(cacheDirectory);
                string temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, data, cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            return await Task.Run(() => Decode(path, decodeHeight), cancellationToken);
        }
        catch (Exception error) when (error is GitHubApiException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
                                          or NotSupportedException or UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>GitHub 기본 목록의 그림: 런처에 든 같은 그림 → 보관한 그림(sha 별) → GitHub 에서 받기</summary>
    private async Task<BitmapSource?> LoadBasicAsync(string path, string name, string sha, int decodeHeight, CancellationToken cancellationToken)
    {
        if (BuiltIn(name) is { } builtIn && (sha.Length == 0 || BasicApps.GitBlobSha(builtIn) == sha))
            return await Task.Run(() => Decode(builtIn, decodeHeight), cancellationToken);
        string cached = Path.Combine(cacheDirectory, "basic", string.Concat(((sha.Length > 0 ? sha[..Math.Min(12, sha.Length)] + "-" : "") + name).Split(Path.GetInvalidFileNameChars())));
        if (!File.Exists(cached))
        {
            if (basicMedia is null) return null;
            byte[] data = await basicMedia(path, MaxBytes, cancellationToken);
            if (data.Length == 0) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            string temporary = cached + ".tmp";
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, cached, overwrite: true);
        }
        return await Task.Run(() => Decode(cached, decodeHeight), cancellationToken);
    }

    /// <summary>런처에 든 기본 목록의 그림 (저장소 basic-media\)</summary>
    public static byte[]? BuiltIn(string shot)
    {
        if (shot.Contains('/') || shot.Contains('\\')) return null;
        using Stream? stream = typeof(ScreenshotService).Assembly.GetManifestResourceStream("basic-media/" + shot);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static BitmapSource? Decode(byte[] data, int decodeHeight)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
        image.StreamSource = new MemoryStream(data);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? Decode(string path, int decodeHeight)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeHeight > 0) image.DecodePixelHeight = decodeHeight;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>앱 페이지의 스크린샷 한 장 (높이에 맞춘 폭)</summary>
public sealed class ScreenshotItem(string shot, ImageSource image, double height)
{
    public string Shot { get; } = shot;
    public ImageSource Image { get; } = image;
    public double Height { get; } = height;
    public double Width => Image.Height > 0 ? Math.Round(Height * Image.Width / Image.Height) : Height * 16 / 10;
}
