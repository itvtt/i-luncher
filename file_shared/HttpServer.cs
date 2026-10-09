using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization; // 참조: System.Web.Extensions 추가 필요
using System.Configuration.Install;

using System.Reflection;

namespace file_shared
{
    public class HttpServer
    {
        // ===== 설정 (app.py 값 그대로) =====
        private const string UPLOAD_FOLDER = @"c:\tem";
        private static readonly string IMG_FOLDER = Path.Combine(UPLOAD_FOLDER, "img");
        private static readonly string FILE_FOLDER = Path.Combine(UPLOAD_FOLDER, "file");
        private static readonly string SHORT_MAP_FILE = Path.Combine(UPLOAD_FOLDER, "short_map.json");

        private static readonly HashSet<string> IMAGE_EXTS = new HashSet<string>
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".tiff" };

        private static readonly HashSet<string> DANGEROUS_EXTS = new HashSet<string>
        { ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".com", ".scr" };

        private const long SIZE_THRESHOLD = 50L * 1024 * 1024;   // 50MB
        private const double TTL_LARGE_DAYS = 7;
        private const double TTL_SMALL_DAYS = 30;
        private const int CLEANUP_INTERVAL_HOURS = 6;
        private const long MAX_CONTENT_LENGTH = 2L * 1024 * 1024 * 1024; // 2GB

        private HttpListener _listener;
        private Thread _listenThread;
        private Timer _cleanupTimer;
        private volatile bool _running;

        private readonly object _mapLock = new object();
        private Dictionary<string, string> _shortMap = new Dictionary<string, string>();

        // ===== 시작/중지 =====
        public void Start()
        {
            Directory.CreateDirectory(UPLOAD_FOLDER);
            Directory.CreateDirectory(IMG_FOLDER);
            Directory.CreateDirectory(FILE_FOLDER);

            LoadShortMap();

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://+:5000/");
            _listener.Start();
            _running = true;

            _listenThread = new Thread(ListenLoop) { IsBackground = true };
            _listenThread.Start();

            // 시작 시 1회 정리 + 주기 정리
            _cleanupTimer = new Timer(_ => CleanupShortMap(), null,
                TimeSpan.Zero, TimeSpan.FromHours(CLEANUP_INTERVAL_HOURS));
        }

        public void Stop()
        {
            _running = false;
            _cleanupTimer?.Dispose();
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; } // Stop() 호출 시 예외 발생, 정상 종료

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { HandleRequest(ctx); }
                    catch (Exception ex)
                    {
                        try
                        {
                            ctx.Response.StatusCode = 500;
                            WriteJson(ctx.Response, new Dictionary<string, object> { { "error", ex.Message } });
                        }
                        catch { }
                    }
                });
            }
        }

        // ===== 라우팅 =====
        private void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var path = req.Url.AbsolutePath;

            if (path == "/" && req.HttpMethod == "GET")
            {
                WriteText(ctx.Response, "File Share Server running.", 200);
            }
            else if (path == "/upload" && req.HttpMethod == "POST")
            {
                HandleUpload(ctx);
            }
            else if (path.StartsWith("/download/") && req.HttpMethod == "GET")
            {
                HandleDownload(ctx, Uri.UnescapeDataString(path.Substring("/download/".Length)));
            }
            else if (path.StartsWith("/p/") && req.HttpMethod == "GET")
            {
                HandlePreviewPage(ctx, path.Substring("/p/".Length));
            }
            else if (path.StartsWith("/v/") && req.HttpMethod == "GET")
            {
                HandleView(ctx, path.Substring("/v/".Length));
            }
            else if (path.StartsWith("/s/") && req.HttpMethod == "GET")
            {
                HandleShortPage(ctx, path.Substring("/s/".Length));
            }
            else
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "Not Found" } }, 404);
            }
        }

        // ===== 업로드 =====
        private void HandleUpload(HttpListenerContext ctx)
        {
            var req = ctx.Request;

            if (req.ContentLength64 > MAX_CONTENT_LENGTH)
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "파일이 너무 큽니다." } }, 413);
                return;
            }

            string boundary = GetBoundary(req.ContentType);
            if (boundary == null)
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "잘못된 요청입니다." } }, 400);
                return;
            }

            string originalFileName;
            string savedPath;
            long fileSize;

            if (!MultipartParser.ParseSingleFile(req.InputStream, boundary, out originalFileName, out savedPath, out fileSize, () => GetTempSavePath()))
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "파일이 없습니다." } }, 400);
                return;
            }

            string uniqueFileName = GetUniqueFileName(originalFileName);
            string targetFolder = GetTargetFolder(uniqueFileName);
            string finalPath = Path.Combine(targetFolder, uniqueFileName);
            File.Move(savedPath, finalPath);

            string shortCode = GetShortCode();
            lock (_mapLock)
            {
                _shortMap[shortCode] = uniqueFileName;
            }
            SaveShortMap();

            WriteJson(ctx.Response, new Dictionary<string, object>
            {
                { "success", true },
                { "filename", uniqueFileName },
                { "original_name", originalFileName },
                { "size", fileSize },
                { "download_path", "/download/" + uniqueFileName },
                { "short_code", shortCode },
                { "short_url", "/s/" + shortCode }
            });
        }

        private string GetTempSavePath()
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
        }

        // ===== 다운로드 =====
        private void HandleDownload(HttpListenerContext ctx, string filename)
        {
            string folder = GetTargetFolder(filename);
            string filePath = Path.Combine(folder, filename);

            if (!File.Exists(filePath))
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "파일을 찾을 수 없습니다." } }, 404);
                return;
            }

            string ext = Path.GetExtension(filename).ToLowerInvariant();
            string originalName = StripUuidPrefix(filename);

            if (DANGEROUS_EXTS.Contains(ext))
            {
                string zipName = Path.GetFileNameWithoutExtension(originalName) + ".zip";
                string encodedZipName = Uri.EscapeDataString(zipName);

                ctx.Response.ContentType = "application/zip";
                ctx.Response.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{encodedZipName}");

                using (var zip = new ZipArchive(ctx.Response.OutputStream, ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry(originalName, CompressionLevel.Optimal);
                    using (var entryStream = entry.Open())
                    using (var fs = File.OpenRead(filePath))
                    {
                        fs.CopyTo(entryStream);
                    }
                }
                ctx.Response.OutputStream.Close();
                return;
            }

            string encodedName = Uri.EscapeDataString(originalName);
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{encodedName}");

            using (var fs = File.OpenRead(filePath))
            {
                ctx.Response.ContentLength64 = fs.Length;
                fs.CopyTo(ctx.Response.OutputStream);
            }
            ctx.Response.OutputStream.Close();
        }

        // ===== 미리보기 페이지 (/p) =====
        private void HandlePreviewPage(HttpListenerContext ctx, string code)
        {
            string filename;
            lock (_mapLock) { _shortMap.TryGetValue(code, out filename); }

            if (filename == null)
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "유효하지 않은 링크입니다." } }, 404);
                return;
            }

            string originalName = StripUuidPrefix(filename);
            string ogImgUrl = $"{ctx.Request.Url.Scheme}://{ctx.Request.Url.Authority}/v/{code}";
            string imgUrl = $"/v/{code}";

            string html = $@"<!DOCTYPE html>
<html lang=""ko""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<meta property=""og:image"" content=""{ogImgUrl}"">
<meta name=""twitter:card"" content=""summary_large_image"">
<meta name=""twitter:image"" content=""{ogImgUrl}"">
<link rel=""image_src"" href=""{ogImgUrl}"">
<title>{originalName}</title></head>
<body style=""margin:0;text-align:center;background:#1e1e1e;"">
<img src=""{imgUrl}"" alt=""{originalName}"" style=""max-width:100%;"">
</body></html>";

            WriteHtml(ctx.Response, html);
        }

        // ===== 인라인 서빙 (/v) =====
        private void HandleView(HttpListenerContext ctx, string code)
        {
            string filename;
            lock (_mapLock) { _shortMap.TryGetValue(code, out filename); }

            if (filename == null)
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "유효하지 않은 링크입니다." } }, 404);
                return;
            }

            string folder = GetTargetFolder(filename);
            string filePath = Path.Combine(folder, filename);
            if (!File.Exists(filePath))
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "파일을 찾을 수 없습니다." } }, 404);
                return;
            }

            using (var fs = File.OpenRead(filePath))
            {
                ctx.Response.ContentLength64 = fs.Length;
                fs.CopyTo(ctx.Response.OutputStream);
            }
            ctx.Response.OutputStream.Close();
        }

        // ===== 단축 URL 페이지 (/s) =====
        private void HandleShortPage(HttpListenerContext ctx, string code)
        {
            string filename;
            lock (_mapLock) { _shortMap.TryGetValue(code, out filename); }

            if (filename == null)
            {
                WriteJson(ctx.Response, new Dictionary<string, object> { { "error", "유효하지 않은 링크입니다." } }, 404);
                return;
            }

            string originalName = StripUuidPrefix(filename);
            string encodedFilename = Uri.EscapeDataString(filename);
            string html;

            if (IsImage(filename))
            {
                html = $@"<!DOCTYPE html>
<html lang=""ko""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{originalName}</title></head>
<body style=""margin:0;background:#1e1e1e;display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;font-family:sans-serif;"">
<img src=""/v/{code}"" alt=""{originalName}"" style=""max-width:100%;max-height:90vh;object-fit:contain;"">
<a href=""/download/{encodedFilename}"" style=""margin-top:14px;color:#9cf;"">다운로드</a>
</body></html>";
            }
            else
            {
                html = $@"<!DOCTYPE html>
<html lang=""ko""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{originalName}</title></head>
<body style=""margin:0;background:#1e1e1e;display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;font-family:sans-serif;color:#eee;"">
<div style=""margin-bottom:16px;"">{originalName}</div>
<a href=""/download/{encodedFilename}"" style=""padding:12px 28px;background:#4a7bd6;color:#fff;text-decoration:none;border-radius:8px;"">다운로드</a>
</body></html>";
            }

            WriteHtml(ctx.Response, html);
        }

        // ===== short_map 로드/저장 =====
        private void LoadShortMap()
        {
            if (File.Exists(SHORT_MAP_FILE))
            {
                string json = File.ReadAllText(SHORT_MAP_FILE, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                lock (_mapLock)
                {
                    _shortMap = serializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                }
            }
        }

        private void SaveShortMap()
        {
            var serializer = new JavaScriptSerializer();
            string json;
            lock (_mapLock)
            {
                json = serializer.Serialize(_shortMap);
            }
            File.WriteAllText(SHORT_MAP_FILE, json, Encoding.UTF8);
        }

        // ===== 정리 로직 (cleanup_short_map 포팅) =====
        private void CleanupShortMap()
        {
            try
            {
                int removed = 0;
                var now = DateTime.UtcNow;

                lock (_mapLock)
                {
                    foreach (var code in _shortMap.Keys.ToList())
                    {
                        string filename = _shortMap[code];
                        string folder = GetTargetFolder(filename);
                        string filePath = Path.Combine(folder, filename);

                        if (!File.Exists(filePath))
                        {
                            _shortMap.Remove(code);
                            removed++;
                            continue;
                        }

                        var info = new FileInfo(filePath);
                        long size = info.Length;
                        double ageDays = (now - info.LastWriteTimeUtc).TotalDays;
                        double ttl = size >= SIZE_THRESHOLD ? TTL_LARGE_DAYS : TTL_SMALL_DAYS;

                        if (ageDays > ttl)
                        {
                            try { File.Delete(filePath); } catch { }
                            _shortMap.Remove(code);
                            removed++;
                        }
                    }
                }

                if (removed > 0) SaveShortMap();
            }
            catch { }
        }

        // ===== 유틸 (app.py 함수 포팅) =====
        private string GetShortCode()
        {
            lock (_mapLock)
            {
                string code;
                do { code = Guid.NewGuid().ToString("N").Substring(0, 8); }
                while (_shortMap.ContainsKey(code));
                return code;
            }
        }

        private static string GetTargetFolder(string filename)
        {
            string ext = Path.GetExtension(filename).ToLowerInvariant();
            return IMAGE_EXTS.Contains(ext) ? IMG_FOLDER : FILE_FOLDER;
        }

        private static bool IsImage(string filename)
        {
            return IMAGE_EXTS.Contains(Path.GetExtension(filename).ToLowerInvariant());
        }

        private static string SanitizeFilename(string filename)
        {
            filename = Path.GetFileName(filename);
            foreach (var c in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
                filename = filename.Replace(c, '_');
            filename = filename.Trim('.', ' ');
            return string.IsNullOrEmpty(filename) ? "file" : filename;
        }

        private static string GetUniqueFileName(string originalFilename)
        {
            string safeName = SanitizeFilename(originalFilename);
            string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 4);
            return $"{uniqueId}_{safeName}";
        }

        private static string StripUuidPrefix(string filename)
        {
            int idx = filename.IndexOf('_');
            return idx >= 0 && idx + 1 < filename.Length ? filename.Substring(idx + 1) : filename;
        }

        private static string GetBoundary(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            const string key = "boundary=";
            int idx = contentType.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string b = contentType.Substring(idx + key.Length);
            int semi = b.IndexOf(';');
            if (semi >= 0) b = b.Substring(0, semi);
            return b.Trim().Trim('"');
        }

        // ===== 응답 헬퍼 =====
        private void WriteJson(HttpListenerResponse res, Dictionary<string, object> obj, int status = 200)
        {
            var serializer = new JavaScriptSerializer();
            byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(obj));
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        private void WriteText(HttpListenerResponse res, string text, int status = 200)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        private void WriteHtml(HttpListenerResponse res, string html, int status = 200)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            res.StatusCode = status;
            res.ContentType = "text/html; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }
    }

    // ===== multipart/form-data 스트리밍 파서 (단일 file 필드 전용) =====
    internal static class MultipartParser
    {
        public static bool ParseSingleFile(Stream input, string boundary, out string fileName,
            out string savedPath, out long fileSize, Func<string> tempPathFactory)
        {
            fileName = null;
            savedPath = null;
            fileSize = 0;

            byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
            byte[] delimiterBytes = Encoding.UTF8.GetBytes("\r\n--" + boundary);

            using (var reader = new BufferedStreamReader(input))
            {
                // 첫 boundary 라인 스킵
                string line = reader.ReadLine();
                if (line == null || !line.StartsWith("--" + boundary)) return false;

                // 헤더 파싱 (Content-Disposition에서 filename 추출)
                string dispositionLine = null;
                while (true)
                {
                    line = reader.ReadLine();
                    if (line == null) return false;
                    if (line.Length == 0) break; // 헤더 끝
                    if (line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                        dispositionLine = line;
                }

                if (dispositionLine == null || !dispositionLine.Contains("filename")) return false;

                fileName = ExtractFileName(dispositionLine);
                if (string.IsNullOrEmpty(fileName)) return false;

                savedPath = tempPathFactory();

                using (var outStream = new FileStream(savedPath, FileMode.Create, FileAccess.Write))
                {
                    fileSize = reader.CopyUntilDelimiter(outStream, delimiterBytes);
                }
            }

            return true;
        }

        private static string ExtractFileName(string dispositionLine)
        {
            // filename*=UTF-8''인코딩된이름 (RFC 5987) 우선 처리
            const string starKey = "filename*=";
            int starIdx = dispositionLine.IndexOf(starKey, StringComparison.OrdinalIgnoreCase);
            if (starIdx >= 0)
            {
                string rest = dispositionLine.Substring(starIdx + starKey.Length).Trim();
                int semi = rest.IndexOf(';');
                if (semi >= 0) rest = rest.Substring(0, semi).Trim();

                // "UTF-8''encodedName" 형식에서 encodedName만 추출
                int quotePos = rest.IndexOf("''");
                if (quotePos >= 0)
                {
                    string encoded = rest.Substring(quotePos + 2).Trim('"');
                    try { return Uri.UnescapeDataString(encoded); }
                    catch { return encoded; }
                }
            }

            // 일반 filename="..." 형식
            const string key = "filename=";
            int idx = dispositionLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            string plain = dispositionLine.Substring(idx + key.Length).Trim();
            if (plain.StartsWith("\""))
            {
                int end = plain.IndexOf('"', 1);
                return end > 0 ? plain.Substring(1, end - 1) : null;
            }
            return plain;
        }
    }

    // ===== 바이트 스트림에서 라인 읽기 + 딜리미터까지 스트리밍 복사 =====
    internal class BufferedStreamReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[65536];
        private int _bufLen = 0;
        private int _bufPos = 0;

        public BufferedStreamReader(Stream stream) { _stream = stream; }

        private int ReadByte()
        {
            if (_bufPos >= _bufLen)
            {
                _bufLen = _stream.Read(_buffer, 0, _buffer.Length);
                _bufPos = 0;
                if (_bufLen <= 0) return -1;
            }
            return _buffer[_bufPos++];
        }

        public string ReadLine()
        {
            var ms = new MemoryStream();
            int b;
            bool any = false;
            while ((b = ReadByte()) != -1)
            {
                any = true;
                if (b == '\n')
                {
                    var bytes = ms.ToArray();
                    int len = bytes.Length;
                    if (len > 0 && bytes[len - 1] == '\r') len--;
                    return Encoding.UTF8.GetString(bytes, 0, len);
                }
                ms.WriteByte((byte)b);
            }
            return any ? Encoding.UTF8.GetString(ms.ToArray()) : null;
        }

        // 남은 버퍼 + 스트림에서 delimiterBytes(예: \r\n--boundary)를 만날 때까지 outStream에 기록
        public long CopyUntilDelimiter(Stream outStream, byte[] delimiterBytes)
        {
            long total = 0;
            var carry = new List<byte>();
            byte[] chunk = new byte[65536];

            while (true)
            {
                int n;
                if (_bufPos < _bufLen)
                {
                    n = _bufLen - _bufPos;
                    Array.Copy(_buffer, _bufPos, chunk, 0, n);
                    _bufPos = _bufLen;
                }
                else
                {
                    n = _stream.Read(chunk, 0, chunk.Length);
                }
                if (n <= 0) break;

                carry.AddRange(chunk.Take(n));
                byte[] data = carry.ToArray();

                int idx = IndexOf(data, delimiterBytes);
                if (idx >= 0)
                {
                    outStream.Write(data, 0, idx);
                    total += idx;
                    // 나머지는 다음 파트 헤더이므로 버림 (단일 파일 업로드 전용)
                    break;
                }
                else
                {
                    int safeLen = data.Length - delimiterBytes.Length;
                    if (safeLen > 0)
                    {
                        outStream.Write(data, 0, safeLen);
                        total += safeLen;
                        carry = data.Skip(safeLen).ToList();
                    }
                }
            }

            return total;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        public void Dispose() { }
    }
}