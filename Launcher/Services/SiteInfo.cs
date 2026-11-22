using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ILauncher.Services;

/// <summary>
/// 웹사이트 즐겨찾기: 그 사이트에서만 이름(og:site_name 또는 title)과 아이콘을 받음 (다른 아이콘 서비스는 쓰지 않음).
/// 아이콘 순서: 페이지의 link rel=apple-touch-icon · icon (큰 것부터, SVG 는 건너뜀) → /favicon.ico.
/// 작은 아이콘(48px 미만)은 밝은 둥근 판 가운데에 놓아 카드에서도 흐리지 않게.
/// 받은 아이콘은 %LOCALAPPDATA%\i-luncher\site-icons\&lt;주소 해시&gt;.png
/// </summary>
public static partial class SiteInfo
{
    public sealed record Result(string? Title, byte[]? IconPng, Uri Address);

    private const int MaxPageBytes = 1_000_000;
    private const int MaxIconBytes = 2_000_000;
    private static readonly HttpClient Http = CreateClient();

    /// <summary>사이트 아이콘을 둘 폴더 (런처가 시작할 때 정함)</summary>
    public static string CacheFolder { get; set; } = Path.Combine(DataFolder.Cache, "site-icons");

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 6, AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        // 일부 사이트는 브라우저가 아니면 다른 페이지를 줌
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en;q=0.6");
        return http;
    }

    /// <summary>"naver.com", "http://intra/wiki" 등을 http(s) 주소로. 알아볼 수 없으면 null</summary>
    public static Uri? Normalize(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0 || text.Contains(' ')) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length > 0
            ? url : null;
    }

    public static bool IsWebAddress(string? path) => path is not null &&
        (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static string CachePath(string address)
    {
        string key = Normalize(address)?.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant() ?? address.ToLowerInvariant();
        return Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant() + ".png");
    }

    /// <summary>받아 둔 사이트 아이콘 (없으면 null). 네트워크 없음</summary>
    public static ImageSource? Cached(string address)
    {
        try
        {
            string path = CachePath(address);
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
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or FileFormatException
                                      or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static void SaveCache(string address, byte[] png)
    {
        Directory.CreateDirectory(CacheFolder);
        string path = CachePath(address);
        File.WriteAllBytes(path + ".tmp", png);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>사이트의 이름과 아이콘 (못 받은 것은 null). 연결이 안 되면 HttpRequestException · TaskCanceledException</summary>
    public static async Task<Result> FetchAsync(Uri address, CancellationToken cancellationToken)
    {
        string? title = null;
        Uri page = address;
        var candidates = new List<Uri>();
        try
        {
            using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            page = response.RequestMessage?.RequestUri ?? address;
            if (response.Content.Headers.ContentType?.MediaType is { } type && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                string html = await ReadTextAsync(response, cancellationToken);
                title = ParseTitle(html);
                candidates.AddRange(ParseIconLinks(html, page));
            }
        }
        catch (HttpRequestException) when (candidates.Count == 0)
        {
            // 페이지는 못 받아도 /favicon.ico 는 될 수 있음 (아래). 둘 다 안 되면 그때 알림
            if (!await ReachableAsync(new Uri(address, "/favicon.ico"), cancellationToken)) throw;
        }
        candidates.Add(new Uri(page, "/favicon.ico"));

        foreach (var candidate in candidates.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryIconAsync(candidate, cancellationToken) is { } png) return new Result(title, png, page);
        }
        return new Result(title, null, page);
    }

    private static async Task<bool> ReachableAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxPageBytes];
        int total = 0, read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0) total += read;
        Encoding encoding = Encoding.UTF8;
        try
        {
            if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
        }
        catch (ArgumentException) { }
        return encoding.GetString(buffer, 0, total);
    }

    private static async Task<byte[]?> TryIconAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            if (url.Scheme == "data") return null;
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxIconBytes) return null;
                if (response.Content.Headers.ContentType?.MediaType is { } type && (type.Contains("svg") || type.Contains("html"))) return null;
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            if (bytes.Length == 0 || bytes.Length > MaxIconBytes) return null;
            if (AppIconService.Decode(bytes) is not { PixelWidth: >= 16 } image) return null;
            // 작은 아이콘은 판에 얹음 (화면이 잠겨 판을 못 그렸으면 아이콘 그대로)
            return ToPng(image.PixelWidth < 48 && OnPlate(image) is var plate && !CustomIcons.IsBlank(plate) ? plate : image);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }  // 이 아이콘만 느림
    }

    /// <summary>작은 아이콘: 128px 밝은 둥근 판 가운데에 64px 로</summary>
    private static BitmapSource OnPlate(BitmapSource icon)
    {
        const int size = 128;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var plate = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE9, 0xEA, 0xEE), 90);
            context.DrawRoundedRectangle(plate, null, new Rect(0, 0, size, size), 29, 29);
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            context.DrawImage(icon, new Rect(32, 32, 64, 64));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ───────── HTML 읽기 (확인 프로그램이 직접 부름) ─────────

    /// <summary>사이트 이름: og:site_name, 없으면 title (앞뒤 공백 · 줄바꿈 정리). 없으면 null</summary>
    public static string? ParseTitle(string html)
    {
        foreach (Match meta in MetaTag().Matches(html))
        {
            string tag = meta.Value;
            if (Attribute(tag, "property") is { } property && property.Equals("og:site_name", StringComparison.OrdinalIgnoreCase) &&
                Attribute(tag, "content") is { Length: > 0 } siteName)
                return Clean(siteName);
        }
        return TitleTag().Match(html) is { Success: true } title && Clean(title.Groups[1].Value) is { Length: > 0 } text ? text : null;
    }

    /// <summary>아이콘 후보 주소 (좋은 것부터): apple-touch-icon · icon 의 sizes 가 큰 것 먼저, SVG · mask-icon 제외</summary>
    public static IReadOnlyList<Uri> ParseIconLinks(string html, Uri page)
    {
        Uri root = page;
        if (BaseTag().Match(html) is { Success: true } baseTag && Attribute(baseTag.Value, "href") is { } baseHref && Uri.TryCreate(page, baseHref, out var based)) root = based;
        var found = new List<(Uri Url, int Score)>();
        foreach (Match link in LinkTag().Matches(html))
        {
            string tag = link.Value;
            string rel = (Attribute(tag, "rel") ?? "").ToLowerInvariant();
            if (!rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => word is "icon" or "apple-touch-icon" or "apple-touch-icon-precomposed")) continue;
            if (rel.Contains("mask-icon")) continue;
            if (Attribute(tag, "href") is not { Length: > 0 } href || !Uri.TryCreate(root, WebUtility.HtmlDecode(href), out var url)) continue;
            if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) continue;
            string type = Attribute(tag, "type") ?? "";
            if (type.Contains("svg", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            int size = 0;
            if (Attribute(tag, "sizes") is { } sizes)
                foreach (Match number in SizeValue().Matches(sizes)) size = Math.Max(size, int.Parse(number.Groups[1].Value));
            if (size == 0) size = rel.Contains("apple-touch-icon") ? 180 : 32;
            // 너무 큰 그림(512 넘음)보다 180~256 이 받기 가볍고 카드에 충분함
            int score = size > 512 ? 400 : size;
            found.Add((url, score));
        }
        return found.OrderByDescending(item => item.Score).Select(item => item.Url).Distinct().ToList();
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, @"\b" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value) : null;
    }

    private static string Clean(string text) => Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();
    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();
    [GeneratedRegex(@"<base\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseTag();
    [GeneratedRegex(@"<title[^>]*>([\s\S]{0,400}?)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();
    [GeneratedRegex(@"(\d{2,4})\s*[xX]\s*\d{2,4}")]
    private static partial Regex SizeValue();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
