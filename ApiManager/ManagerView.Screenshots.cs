using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApiManager;

/// <summary>
/// 앱 목록의 스크린샷 (i-luncher 앱 페이지): + 로 PNG·JPG 를 고르거나 이미지를 복사해 Ctrl+V. 이미지는 바로 서버에 올리고(이름 = 내용의 해시),
/// 앱의 screenshots 목록은 [저장]할 때 바뀜. 어느 앱도 쓰지 않는 이미지는 서버가 하루 뒤 지움.
/// </summary>
public partial class ManagerView
{
    private const int MaxScreenshots = 12;
    private readonly Dictionary<string, ImageSource?> _thumbnails = [];
    private readonly ObservableCollection<ScreenshotRow> _screenshotRows = [];

    private static List<string> Screenshots(AppRow app) =>
        (app.Data["screenshots"] as JsonArray)?.Select(node => Str(node)).Where(name => name.Length > 0).ToList() ?? [];

    private static void SetScreenshots(AppRow app, IReadOnlyList<string> names)
    {
        if (names.Count == 0) app.Data.Remove("screenshots");
        else app.Data["screenshots"] = new JsonArray(names.Select(name => (JsonNode?)name).ToArray());
    }

    private void ShowScreenshots(AppRow? app)
    {
        ScreenshotStrip.ItemsSource = _screenshotRows;
        _screenshotRows.Clear();
        if (app is null) return;
        foreach (string name in Screenshots(app))
        {
            var row = new ScreenshotRow(name, _thumbnails.GetValueOrDefault(name));
            _screenshotRows.Add(row);
            if (!_thumbnails.ContainsKey(name)) _ = LoadThumbnailAsync(name);
        }
        AddScreenshotButton.IsEnabled = _screenshotRows.Count < MaxScreenshots;
    }

    /// <summary>서버에 올린 이미지(이름)만 미리 보기를 받음. http 주소는 이름만 보임</summary>
    private async Task LoadThumbnailAsync(string name)
    {
        _thumbnails[name] = null;
        if (name.Contains('/') || _admin is null) return;
        try
        {
            byte[] data = await Admin.GetBytesAsync("launcher/media/" + Uri.EscapeDataString(name));
            var image = await Task.Run(() => Decode(data, 160));
            _thumbnails[name] = image;
            for (int index = 0; index < _screenshotRows.Count; index++)
                if (_screenshotRows[index].Name == name) _screenshotRows[index] = _screenshotRows[index] with { Image = image };
        }
        catch (Exception error) when (error is AdminException or System.Net.Http.HttpRequestException or TaskCanceledException or NotSupportedException
                                          or IOException or System.Runtime.InteropServices.COMException) { }
    }

    private static BitmapSource Decode(byte[] data, int height)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelHeight = height;
        image.StreamSource = new MemoryStream(data);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async void AddScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppRow app) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "이미지 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Multiselect = true, Title = "스크린샷" };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        foreach (string file in dialog.FileNames)
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetMessage(error.Message, warning: true); return; }
            if (!await UploadScreenshotAsync(app, data, file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg")) return;
        }
    }

    /// <summary>앱 칸에서 Ctrl+V: 클립보드의 이미지를 PNG 로 올림 (글자 칸에서는 보통 붙여넣기)</summary>
    private async void AppForm_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || AppsList.SelectedItem is not AppRow app) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox || !Clipboard.ContainsImage()) return;
        e.Handled = true;
        if (Clipboard.GetImage() is not { } image) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        await UploadScreenshotAsync(app, stream.ToArray(), "image/png");
    }

    private async Task<bool> UploadScreenshotAsync(AppRow app, byte[] data, string contentType)
    {
        var names = Screenshots(app);
        if (names.Count >= MaxScreenshots)
        {
            SetMessage($"스크린샷은 {MaxScreenshots}장까지입니다.", warning: true);
            return false;
        }
        return await Run(async () =>
        {
            string name = Str((await Admin.PostBytesAsync("launcher/media", data, contentType))["name"]);
            if (name.Length == 0 || names.Contains(name)) return;
            _thumbnails[name] = await Task.Run(() => Decode(data, 160));
            names.Add(name);
            SetScreenshots(app, names);
            app.Changed();
            SetAppsDirty(true);
            if (ReferenceEquals(AppsList.SelectedItem, app)) ShowScreenshots(app);
        }, done: "스크린샷을 올렸습니다. 저장하면 앱 페이지에 보입니다.");
    }

    private void RemoveScreenshotButton_Click(object sender, RoutedEventArgs e) => ChangeScreenshots(sender, (names, index) => names.RemoveAt(index));

    private void MoveScreenshotButton_Click(object sender, RoutedEventArgs e) => ChangeScreenshots(sender, (names, index) =>
    {
        if (index == 0) return;
        (names[index - 1], names[index]) = (names[index], names[index - 1]);
    });

    private void ChangeScreenshots(object sender, Action<List<string>, int> change)
    {
        if (AppsList.SelectedItem is not AppRow app || (sender as FrameworkElement)?.Tag is not string name) return;
        var names = Screenshots(app);
        int index = names.IndexOf(name);
        if (index < 0) return;
        change(names, index);
        SetScreenshots(app, names);
        app.Changed();
        SetAppsDirty(true);
        ShowScreenshots(app);
    }
}

/// <summary>앱 목록의 스크린샷 한 장 (서버에 올린 이미지 이름 또는 주소, 미리 보기)</summary>
internal sealed record ScreenshotRow(string Name, ImageSource? Image);
