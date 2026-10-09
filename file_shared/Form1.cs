using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Runtime.InteropServices;
using System.Configuration.Install;
using System.Linq;
using System.Reflection;
using System.Net.NetworkInformation;

namespace file_shared
{
    public partial class Form1 : Form
    {
        // ==============================
        // 서버 주소 (현재 PC의 로컬 IPv4 기반, 자동 구성)
        // ==============================
        private static readonly string LOCAL_IP = GetLocalIPv4();
        private static readonly string SERVER_URL = $"http://{LOCAL_IP}:5000";
        private static readonly string DISPLAY_URL = $"http://{LOCAL_IP}:5000";

        private static string GetLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (desc.Contains("virtual") || desc.Contains("vmware") ||
                        desc.Contains("hyper-v") || desc.Contains("vpn") ||
                        desc.Contains("vethernet") || desc.Contains("tap") ||
                        desc.Contains("loopback"))
                        continue;

                    // 이더넷/무선만 허용
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                        ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                        continue;

                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return addr.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private HttpClient _httpClient;
        private string _lastShortCode = "";

        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();

        private static readonly string[] ImageExts =
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".tiff" };

        public Form1()
        {
            InitializeComponent();
            var applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (applicationIcon != null)
                Icon = applicationIcon;
            InitHttpClient();

            ChangeWindowMessageFilterEx(Handle, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
        }
        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr pChangeFilterStruct);

        private const uint MSGFLT_ALLOW = 1;
        private const uint WM_DROPFILES = 0x0233;
        private const uint WM_COPYDATA = 0x004A;
        private const uint WM_COPYGLOBALDATA = 0x0049;
        // ==============================
        // HttpClient 초기화 (자체서명 인증서 무시)
        // ==============================
        private void InitHttpClient()
        {
            ServicePointManager.ServerCertificateValidationCallback =
                (sender, cert, chain, errors) => true;
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
        }

        // ==============================
        // 파일 선택 버튼
        // ==============================
        private async void btnSelect_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Multiselect = true;
                dlg.Title = "업로드할 파일 선택";
                if (dlg.ShowDialog() == DialogResult.OK)
                    await UploadFilesAsync(dlg.FileNames);
            }
        }

        // ==============================
        // 드래그앤드롭
        // ==============================
        private void pnlDrop_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                pnlDrop.BackColor = Color.FromArgb(44, 40, 78);
            }
        }

        private void pnlDrop_DragLeave(object sender, EventArgs e)
        {
            pnlDrop.BackColor = Color.FromArgb(30, 30, 48);
        }

        private async void pnlDrop_DragDrop(object sender, DragEventArgs e)
        {
            pnlDrop.BackColor = Color.FromArgb(240, 242, 255);
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
                await UploadFilesAsync(files);
        }

        // ==============================
        // 파일 업로드 (다중 지원)
        // ==============================
        private async Task UploadFilesAsync(string[] filePaths)
        {
            btnSelect.Enabled = false;
            txtLink.Text = "";
            lblStatus.Text = "";

            for (int i = 0; i < filePaths.Length; i++)
            {
                var path = filePaths[i];
                lblStatus.Text = $"업로드 중... ({i + 1}/{filePaths.Length}) {Path.GetFileName(path)}";
                progressBar.Value = 0;
                ClearPreview();

                try
                {
                    var result = await UploadSingleFileAsync(path);
                    if (result != null)
                    {
                        _lastShortCode = result["short_code"];
                        var link = $"{DISPLAY_URL}/s/{_lastShortCode}";
                        txtLink.Text = link;
                        lblStatus.Text = $"완료: {result["original_name"]}";
                        progressBar.Value = 100;

                        ApplyButtonMode(IsImageFile(path));

                        if (IsImageFile(path))
                            ShowPreview(path);
                    }
                }
                catch (Exception ex)
                {
                    lblStatus.Text = $"오류: {ex.Message}";
                    MessageBox.Show($"업로드 실패\n{ex.Message}", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            btnSelect.Enabled = true;
        }

        private async Task<Dictionary<string, string>> UploadSingleFileAsync(string filePath)
        {
            using (var multipart = new MultipartFormDataContent())
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                var fileContent = new ProgressableStreamContent(fileStream, (sent, total) =>
                {
                    if (total <= 0) return;
                    int pct = (int)(sent * 100 / total);
                    if (progressBar.InvokeRequired)
                        progressBar.Invoke(new Action(() => progressBar.Value = Math.Min(pct, 99)));
                    else
                        progressBar.Value = Math.Min(pct, 99);
                });

                fileContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                // 한글 파일명 UTF-8 인코딩 (RFC 5987)
                var fileName = Path.GetFileName(filePath);
                fileContent.Headers.ContentDisposition =
                    new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                    {
                        Name = "\"file\"",
                        FileNameStar = fileName  // filename* = UTF-8''한글파일명 형태로 자동 인코딩
                    };
                multipart.Add(fileContent);

                var response = await _httpClient.PostAsync($"{SERVER_URL}/upload", multipart);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string serverMsg = json;
                    try
                    {
                        var errObj = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                        if (errObj != null && errObj.ContainsKey("error"))
                            serverMsg = errObj["error"];
                    }
                    catch { }
                    throw new Exception($"[{(int)response.StatusCode}] {serverMsg}");
                }

                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            }
        }

        // ==============================
        // 링크 복사
        // ==============================
        private void btnCopy_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(txtLink.Text))
            {
                Clipboard.SetText(txtLink.Text);
                lblStatus.Text = "링크가 복사되었습니다!";
            }
        }

        // ==============================
        // 이미지 여부 / 버튼 모드 전환
        // ==============================
        private bool IsImageFile(string nameOrPath)
        {
            var ext = Path.GetExtension(nameOrPath).ToLowerInvariant();
            return Array.IndexOf(ImageExts, ext) >= 0;
        }

        private void ShowPreview(string filePath)
        {
            try
            {
                Image oldImg = picPreview.Image;
                picPreview.Image = null;

                Bitmap newBmp;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var tmp = new Bitmap(fs))
                {
                    newBmp = new Bitmap(tmp); // 스트림과 완전히 분리된 메모리 독립 복사본
                }

                picPreview.Image = newBmp;
                oldImg?.Dispose();
                picPreview.Visible = true;
            }
            catch
            {
                picPreview.Visible = false;
            }
        }

        private void ClearPreview()
        {
            picPreview.Visible = false;
            picPreview.Image?.Dispose();
            picPreview.Image = null;
        }

        private void ApplyButtonMode(bool isImage)
        {
            btnCopy.Visible = !isImage;
            btnImgLink.Visible = isImage;
            btnImgHtml.Visible = isImage;
            btnImgEmbed.Visible = isImage;
        }

        // 버튼1: 링크 복사 → /s 링크를 표시칸에 표시 + 클립보드 복사
        private void btnImgLink_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_lastShortCode)) return;
            var link = $"{DISPLAY_URL}/s/{_lastShortCode}";
            txtLink.Text = link;
            Clipboard.SetText(link);
            lblStatus.Text = "이미지 링크가 복사되었습니다!";
        }

        // 버튼2: HTML 복사 → /p 링크를 표시칸에 표시 + 클립보드 복사
        private void btnImgHtml_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_lastShortCode)) return;
            var link = $"{DISPLAY_URL}/p/{_lastShortCode}";
            txtLink.Text = link;
            Clipboard.SetText(link);
            lblStatus.Text = "미리보기 HTML 링크가 복사되었습니다!";
        }

        // 버튼3: 임베드 복사 (메일/게시판 본문용 img 스니펫)
        private void btnImgEmbed_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_lastShortCode)) return;
            var imgUrl = $"{DISPLAY_URL}/v/{_lastShortCode}";
            Clipboard.SetText($"<img src=\"{imgUrl}\" style=\"max-width:100%;\" alt=\"image\">");
            lblStatus.Text = "임베드 코드가 복사되었습니다!";
        }

        // ==============================
        // Ctrl+V 붙여넣기 업로드
        // ==============================
        private async void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.Handled = true;
                await PasteAndUploadAsync();
            }
        }

        private async Task PasteAndUploadAsync()
        {
            // (a) 탐색기에서 복사한 파일
            if (Clipboard.ContainsFileDropList())
            {
                var coll = Clipboard.GetFileDropList();
                var paths = new string[coll.Count];
                coll.CopyTo(paths, 0);
                if (paths.Length > 0)
                    await UploadFilesAsync(paths);
                return;
            }

            // (b) 클립보드 이미지(스크린샷 등) → 임시 png 저장 후 업로드
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    var tmp = Path.Combine(Path.GetTempPath(),
                        $"paste_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    img.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    try
                    {
                        await UploadFilesAsync(new[] { tmp });
                    }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
                return;
            }

            lblStatus.Text = "클립보드에 붙여넣을 파일/이미지가 없습니다.";
        }

        // ==============================
        // 유틸
        // ==============================
        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ==============================
        // 커스텀 타이틀바 — 드래그 이동
        // ==============================
        private void pnlHeader_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        // ==============================
        // 닫기
        // ==============================
        private void btnClose_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _httpClient?.Dispose();
            base.OnFormClosed(e);
        }

        internal class ProgressableStreamContent : HttpContent
        {
            private const int DefaultBufferSize = 81920;
            private readonly Stream _content;
            private readonly Action<long, long> _onProgress;

            public ProgressableStreamContent(Stream content, Action<long, long> onProgress)
            {
                _content = content;
                _onProgress = onProgress;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                var buffer = new byte[DefaultBufferSize];
                long totalSent = 0;
                long totalLength = _content.Length;
                int bytesRead;

                while ((bytesRead = await _content.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, bytesRead);
                    totalSent += bytesRead;
                    _onProgress?.Invoke(totalSent, totalLength);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _content.Length;
                return true;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _content?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
