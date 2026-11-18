using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ILauncher.Services;

/// <summary>
/// 앱 페이지의 스크린샷을 받아 데이터 폴더(screenshots\)에 보관. API 서버에 올린 이미지는 이름이 내용(SHA-256)이라
/// 한 번 받으면 다시 받지 않음. http(s) 주소는 주소로 만든 이름으로 보관.
/// </summary>
public sealed class ScreenshotService(string cacheDirectory, Func<string?> apiServer) : IDisposable
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
            // 내 앱: 이 PC 에 복사해 둔 그림
            if (Path.IsPathFullyQualified(shot))
                return File.Exists(shot) ? await Task.Run(() => Decode(shot, decodeHeight), cancellationToken) : null;
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
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
                                          or NotSupportedException or UriFormatException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
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
