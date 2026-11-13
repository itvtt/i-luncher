using System.ComponentModel;
using System.Windows;

namespace ILauncher;

/// <summary>스토어형 화면의 섹션 하나: 제목, 옆의 작은 글자(개수 등), 오른쪽 파란 링크(모두 업데이트 등), 줄 목록(AppCard·FavoriteCard)</summary>
public sealed class StoreSection(string title, IReadOnlyList<object> items, string note = "", string? actionText = null, Action? action = null) : INotifyPropertyChanged
{
    private string? _actionText = actionText;
    private bool _actionEnabled = true;

    public string Title { get; } = title;
    public string Note { get; } = note;
    public IReadOnlyList<object> Items { get; } = items;
    public Action? Action { get; } = action;

    public string? ActionText
    {
        get => _actionText;
        set { _actionText = value; Changed(nameof(ActionText)); Changed(nameof(ActionVisibility)); }
    }

    public bool ActionEnabled
    {
        get => _actionEnabled;
        set { _actionEnabled = value; Changed(nameof(ActionEnabled)); }
    }

    /// <summary>섹션이 하나뿐이고 링크도 없으면 제목 줄을 숨김 (큰 제목과 겹침)</summary>
    public bool ShowHeader { get; set; } = true;
    public Visibility HeaderVisibility => ShowHeader ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActionVisibility => string.IsNullOrEmpty(_actionText) || Action is null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
