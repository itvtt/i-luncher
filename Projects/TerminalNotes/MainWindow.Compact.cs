using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private CompactDashboardWindow? _compactDashboard;
    private CompactTerminalWindow? _compactTerminal;
    private bool _closingCompactWindows;

    internal void ShowCompactDashboard()
    {
        if (_closing || _closePending) return;
        if (_compactDashboard is not null)
        {
            if (_compactDashboard.WindowState == WindowState.Minimized)
                _compactDashboard.WindowState = WindowState.Normal;
            _compactDashboard.Activate();
            return;
        }

        var dashboard = new CompactDashboardWindow(OpenCompactTerminal, RestoreFullDashboard);
        dashboard.Resources.MergedDictionaries.Add(Resources);
        _compactDashboard = dashboard;
        dashboard.Closed += (_, _) =>
        {
            if (ReferenceEquals(_compactDashboard, dashboard)) _compactDashboard = null;
            if (!_closing && !_closingCompactWindows) RestoreFromTray();
        };
        UpdateCompactDashboard();
        dashboard.Show();
        Hide();
        dashboard.Activate();
    }

    internal void UpdateCompactDashboard()
    {
        if (TerminalTabs is null) return;
        var tabs = TerminalTabs.Items.Cast<TabItem>().ToArray();
        if (_compactDashboard is null) return;
        _compactDashboard.UpdateSessions(tabs.Select(tab =>
        {
            var state = (TerminalTabState)tab.Tag;
            var visual = CompactSessionVisual(state.Pane);
            return new CompactSessionInfo(
                tab,
                state.Sequence,
                state.Title,
                visual.Status,
                state.Pane.TokenUsageLabel,
                state.Pane.WorkingDirectory,
                visual.Accent,
                visual.Fill,
                visual.Completed,
                tab.IsSelected);
        }).ToArray(), _splitCapacity);
        _compactTerminal?.UpdateStatus();
    }

    private (string Status, Brush Accent, Brush Fill, bool Completed) CompactSessionVisual(TerminalPane pane)
    {
        bool busy = IsAutomationTerminalBusy(pane);
        bool completed = pane.HasCompletedWork && !busy && pane.Activity is not
            (TerminalActivity.Error or TerminalActivity.RateLimited or TerminalActivity.NeedsInput);
        // A scheduler claim also covers startup and disconnected/idle states.
        // Only native activity may paint an OpenCode pane as progressing.
        var activity = pane.Activity;
        string status = activity switch
        {
            TerminalActivity.Error or TerminalActivity.RateLimited or TerminalActivity.Exited => pane.ActivityLabel,
            TerminalActivity.NeedsInput => "입력 필요",
            _ when IsStartingNativePane(pane) && activity is not TerminalActivity.Active => "OpenCode 준비 중",
            _ when completed => "응답 완료",
            _ => pane.ActivityLabel
        };
        return (status,
            StateColorBrush(TerminalActivityPalette.Foreground(activity, completed)),
            StateColorBrush(TerminalActivityPalette.Background(activity, completed)), completed);
    }

    internal void CloseCompactWindows()
    {
        _closingCompactWindows = true;
        try
        {
            _compactTerminal?.Close();
            _compactTerminal = null;
            _compactDashboard?.Close();
            _compactDashboard = null;
        }
        finally { _closingCompactWindows = false; }
    }

    private void RestoreFullDashboard()
    {
        _compactDashboard?.Close();
    }

    private void OpenCompactTerminal(TabItem tab)
    {
        if (_closing || _closePending || _compactTerminal is not null || !TerminalTabs.Items.Contains(tab)) return;
        SelectTerminal(tab, false);
        var state = (TerminalTabState)tab.Tag;
        state.InputMode = TerminalInputMode.Plan;
        var pane = state.Pane;
        var restoreParent = DetachPane(pane);
        if (restoreParent is null)
        {
            SetStatus("터미널 화면을 작은 창으로 옮길 수 없습니다.");
            return;
        }

        CompactTerminalWindow terminalWindow;
        try
        {
            terminalWindow = new CompactTerminalWindow(
                pane,
                state.Title,
                state.Sequence,
                () => pane.Interrupt(),
                () => state.InputMode,
                mode => { state.InputMode = mode; UpdateWorkbenchTarget(); });
        }
        catch (Exception ex)
        {
            restoreParent();
            SetStatus("작은 터미널 창을 열 수 없습니다 · " + ex.Message);
            return;
        }
        if (_compactDashboard is { IsVisible: true } dashboard) terminalWindow.Owner = dashboard;
        terminalWindow.Resources.MergedDictionaries.Add(Resources);
        _compactTerminal = terminalWindow;
        _arrangingTerminals = true;
        try
        {
            terminalWindow.ShowDialog();
        }
        finally
        {
            terminalWindow.DetachPane();
            _compactTerminal = null;
            _arrangingTerminals = false;
            if (!_closing && TerminalTabs.Items.Contains(tab)) restoreParent();
            ApplyTerminalLayoutAfterArrangement();
            if (_compactDashboard is { IsVisible: true } compactWindow)
                _ = Dispatcher.BeginInvoke(() => compactWindow.Activate());
            else if (IsVisible)
                _ = Dispatcher.BeginInvoke(() => pane.FocusTerminal());
        }
    }

    private static Action? DetachPane(TerminalPane pane)
    {
        if (pane.Parent is ContentControl content && ReferenceEquals(content.Content, pane))
        {
            content.Content = null;
            return () =>
            {
                if (content.Content is null && pane.Parent is null) content.Content = pane;
            };
        }
        if (pane.Parent is Panel panel && panel.Children.Contains(pane))
        {
            int index = panel.Children.IndexOf(pane);
            panel.Children.Remove(pane);
            return () =>
            {
                if (pane.Parent is null) panel.Children.Insert(Math.Min(index, panel.Children.Count), pane);
            };
        }
        return null;
    }

    private sealed record CompactSessionInfo(
        TabItem Tab,
        int Sequence,
        string Title,
        string Status,
        string Usage,
        string WorkingDirectory,
        Brush Accent,
        Brush Fill,
        bool Completed,
        bool IsSelected);

    private sealed class CompactDashboardWindow : Window
    {
        private readonly Action<TabItem> _openTerminal;
        private readonly Grid _tilesGrid = new();
        private readonly TextBlock _summary = new();
        private readonly Button _previous = new() { Content = "‹", Width = 26, Height = 26, Padding = new Thickness(0), FontSize = 16, ToolTip = "이전 터미널 묶음" };
        private readonly Button _next = new() { Content = "›", Width = 26, Height = 26, Padding = new Thickness(0), FontSize = 16, ToolTip = "다음 터미널 묶음" };
        private readonly Dictionary<TabItem, CompactSessionTile> _tiles = [];
        private IReadOnlyList<CompactSessionInfo> _sessions = [];
        private TabItem[] _renderedTabs = [];
        private int _capacity = 1;
        private int _page;
        private bool _initialized;

        public CompactDashboardWindow(Action<TabItem> openTerminal, Action restoreFullDashboard)
        {
            _openTerminal = openTerminal;
            Title = "Terminal Notes · 상태 모니터";
            Width = 344;
            Height = 200;
            MinWidth = 240;
            MinHeight = 120;
            Topmost = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
            FontSize = 14;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = StateColorBrush("#121C2D");
            Foreground = StateColorBrush("#F3F5F8");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(4),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                UseAeroCaptionButtons = false
            });

            var full = new Button
            {
                Content = "↗",
                Width = 26,
                Height = 26,
                FontSize = 13,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "전체 화면으로 돌아가기"
            };
            full.Click += (_, _) => restoreFullDashboard();
            var close = new Button { Content = "×", Width = 26, Height = 26, FontSize = 15, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), ToolTip = "상태 모니터 닫기" };
            close.Click += (_, _) => Close();
            _previous.Click += (_, _) => { if (_page > 0) { _page--; RenderPage(); } };
            _next.Click += (_, _) => { if (_page + 1 < PageCount) { _page++; RenderPage(); } };
            _summary.FontSize = 13;
            _summary.FontWeight = FontWeights.SemiBold;
            _summary.VerticalAlignment = VerticalAlignment.Center;
            var header = new Grid { Background = Brushes.Transparent, Height = 34, Margin = new Thickness(4, 0, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            caption.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = StateColorBrush("#303640"),
                Child = new TextBlock { Text = ">_", FontFamily = new FontFamily("Consolas"), FontSize = 14, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            caption.Children.Add(new TextBlock { Text = "터미널", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            _summary.Margin = new Thickness(8, 0, 0, 0);
            _summary.Foreground = StateColorBrush("#A5ADBA");
            caption.Children.Add(_summary);
            header.Children.Add(caption);
            var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            headerActions.Children.Add(_previous);
            headerActions.Children.Add(_next);
            headerActions.Children.Add(full);
            headerActions.Children.Add(close);
            foreach (var action in headerActions.Children.OfType<Button>())
                action.SetResourceReference(StyleProperty, "QuietIconButton");
            Grid.SetColumn(headerActions, 1); header.Children.Add(headerActions);
            header.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
                    try { DragMove(); } catch (InvalidOperationException) { }
            };

            System.Windows.Automation.AutomationProperties.SetName(_tilesGrid, "미니 터미널 그리드");
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(header);
            Grid.SetRow(_tilesGrid, 1); root.Children.Add(_tilesGrid);
            Content = new Border { Background = Background, BorderBrush = StateColorBrush("#363C47"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(5), Child = root };
        }

        private int PageCount => Math.Max(1, (int)Math.Ceiling(_sessions.Count / (double)_capacity));

        public void UpdateSessions(IReadOnlyList<CompactSessionInfo> sessions, int capacity)
        {
            capacity = capacity is 1 or 2 or 3 or 4 or 6 or 8 ? capacity : 1;
            var oldOrder = _sessions.Select(session => session.Tab).ToArray();
            bool layoutChanged = _capacity != capacity;
            _capacity = capacity;
            _sessions = sessions;
            foreach (var removed in _tiles.Keys.Except(sessions.Select(session => session.Tab)).ToArray())
                _tiles.Remove(removed);
            foreach (var session in sessions)
            {
                if (!_tiles.TryGetValue(session.Tab, out var tile))
                {
                    tile = new CompactSessionTile(session.Tab, _openTerminal);
                    _tiles.Add(session.Tab, tile);
                }
                tile.Update(session);
            }
            if (!_initialized)
            {
                int selected = sessions.ToList().FindIndex(session => session.IsSelected);
                _page = Math.Max(0, selected) / _capacity;
                _initialized = true;
            }
            _page = Math.Clamp(_page, 0, PageCount - 1);
            bool orderChanged = !oldOrder.SequenceEqual(sessions.Select(session => session.Tab));
            if (layoutChanged || orderChanged || !_renderedTabs.SequenceEqual(CurrentPageTabs())) RenderPage();
            else UpdateNavigation();
        }

        private TabItem[] CurrentPageTabs() => _sessions.Skip(_page * _capacity).Take(_capacity).Select(session => session.Tab).ToArray();

        private void RenderPage()
        {
            (int columns, int rows) = _capacity switch
            {
                2 => (2, 1), 3 => (3, 1), 4 => (2, 2), 6 => (3, 2), 8 => (4, 2), _ => (1, 1)
            };
            _tilesGrid.Children.Clear();
            _tilesGrid.ColumnDefinitions.Clear();
            _tilesGrid.RowDefinitions.Clear();
            for (int column = 0; column < columns; column++) _tilesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int row = 0; row < rows; row++) _tilesGrid.RowDefinitions.Add(new RowDefinition());
            var pageSessions = _sessions.Skip(_page * _capacity).Take(_capacity).ToArray();
            for (int slot = 0; slot < _capacity; slot++)
            {
                FrameworkElement element = slot < pageSessions.Length
                    ? _tiles[pageSessions[slot].Tab].Button
                    : new Border { Margin = new Thickness(3), Background = StateColorBrush("#181B21"), BorderBrush = StateColorBrush("#282D36"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
                Grid.SetColumn(element, slot % columns);
                Grid.SetRow(element, slot / columns);
                _tilesGrid.Children.Add(element);
            }
            _renderedTabs = pageSessions.Select(session => session.Tab).ToArray();
            if (_initialized)
            {
                Width = columns switch { 1 => 240, 2 => 280, 3 => 344, _ => 440 };
                Height = rows == 1 ? 128 : 200;
            }
            UpdateNavigation();
        }

        private void UpdateNavigation()
        {
            _summary.Text = PageCount > 1 ? $"{_page + 1}/{PageCount}" : $"{_sessions.Count}개";
            _summary.ToolTip = $"터미널 {_sessions.Count}개 · {_page + 1}/{PageCount} 페이지";
            _previous.IsEnabled = _page > 0;
            _next.IsEnabled = _page + 1 < PageCount;
            _previous.Visibility = _next.Visibility = PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private sealed class CompactSessionTile
        {
            public Button Button { get; }
            private readonly TextBlock _sequence = new() { FontSize = 13, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center };
            private readonly System.Windows.Shapes.Path _openIcon = new()
            {
                Data = Geometry.Parse("M 0 7 L 7 0 M 1 0 L 7 0 L 7 6"), Width = 8, Height = 8,
                StrokeThickness = 1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65
            };
            private readonly TextBlock _title = new()
            {
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                LineHeight = 18,
                MaxHeight = 36
            };

            public CompactSessionTile(TabItem tab, Action<TabItem> openTerminal)
            {
                var heading = new Grid();
                heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var meta = new Grid { Height = 14, Margin = new Thickness(0, 0, 0, 4) };
                meta.Children.Add(_sequence);
                meta.Children.Add(_openIcon);
                heading.Children.Add(meta);
                Grid.SetRow(_title, 1); heading.Children.Add(_title);
                Button = new Button
                {
                    Content = heading,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(7, 5, 7, 5),
                    Margin = new Thickness(3)
                };
                Button.SetResourceReference(FrameworkElement.StyleProperty, "StateTileButton");
                Button.Click += (_, _) => openTerminal(tab);
            }

            public void Update(CompactSessionInfo session)
            {
                _sequence.Text = session.Sequence.ToString("00");
                _title.Text = session.Title;
                System.Windows.Automation.AutomationProperties.SetName(Button, $"{session.Sequence:00} {session.Title}");
                _sequence.Foreground = _title.Foreground = session.Accent;
                _sequence.Opacity = 0.8;
                _openIcon.Stroke = session.Accent;
                Button.Foreground = session.Accent;
                Button.Background = session.Fill;
                Button.BorderBrush = session.IsSelected ? session.Accent : session.Fill;
                Button.BorderThickness = new Thickness(session.IsSelected ? 2 : 1);
                Button.ToolTip = session.Title + "\n" + session.Status + " · " + session.Usage + "\n" + session.WorkingDirectory +
                    (session.Completed ? "\n응답 완료 감지는 작업 성공을 보증하지 않습니다." : "") + "\n클릭하여 대화형 터미널 열기";
            }

        }
    }

    private sealed class CompactTerminalWindow : Window
    {
        private readonly TerminalPane _pane;
        public TerminalPane Pane => _pane;
        private readonly int _sequence;
        private readonly TextBlock _status = new();
        private readonly TextBlock _prominentTitle;
        private readonly Border _headingSurface = new();
        private readonly WrapPanel _modes;
        private readonly ContentControl _paneHost = new();

        public CompactTerminalWindow(
            TerminalPane pane,
            string title,
            int sequence,
            Action interrupt,
            Func<TerminalInputMode> inputMode,
            Action<TerminalInputMode> setInputMode)
        {
            _pane = pane;
            _sequence = sequence;
            Title = title + " · 터미널";
            Width = 900;
            Height = 620;
            MinWidth = 480;
            MinHeight = 320;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
            FontSize = 13;
            UseLayoutRounding = true;
            Background = StateColorBrush("#101216");
            SourceInitialized += (_, _) => ApplyDarkTitleBar(this);
            _prominentTitle = new TextBlock
            {
                Text = title,
                FontSize = 24,
                LineHeight = 32,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 245, 252)),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 72
            };
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.FontSize = 13;

            var stop = new Button
            {
                Content = "■ 중단",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "현재 작업에 Ctrl+C를 보냅니다"
            };
            stop.Click += (_, _) => interrupt();
            var statusRow = new Grid();
            statusRow.ColumnDefinitions.Add(new ColumnDefinition());
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.Children.Add(_status);
            Grid.SetColumn(stop, 1);
            statusRow.Children.Add(stop);
            _modes = new WrapPanel { Margin = new Thickness(12, 8, 12, 8) };
            foreach (var mode in new[] { TerminalInputMode.Plan })
            {
                var option = new RadioButton
                {
                    Content = InputModeLabel(mode),
                    GroupName = "CompactInputMode",
                    IsChecked = inputMode() == mode,
                    MinWidth = 56,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = "메인 플랜 패널에서 편집할 MD"
                };
                option.SetResourceReference(StyleProperty, "InputModeButton");
                option.Checked += (_, _) => setInputMode(mode);
                _modes.Children.Add(option);
            }
            var header = new StackPanel { Margin = new Thickness(16, 10, 16, 10) };
            header.Children.Add(_prominentTitle);
            header.Children.Add(statusRow);
            _headingSurface.Child = header;

            _paneHost.Content = pane;
            _paneHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _paneHost.VerticalContentAlignment = VerticalAlignment.Stretch;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(_headingSurface);
            var modeBar = new Border { Background = StateColorBrush("#181B21"), BorderBrush = StateColorBrush("#303640"),
                BorderThickness = new Thickness(0, 0, 0, 1), Child = _modes };
            Grid.SetRow(modeBar, 1);
            root.Children.Add(modeBar);
            Grid.SetRow(_paneHost, 2);
            root.Children.Add(_paneHost);
            Content = root;
            Loaded += (_, _) => pane.FocusTerminal();
            pane.ActivityChanged += UpdateStatus;
            pane.TokenUsageChanged += UpdateStatus;
            Closing += (_, _) => DetachPane();
            Closed += (_, _) =>
            {
                pane.ActivityChanged -= UpdateStatus;
                pane.TokenUsageChanged -= UpdateStatus;
            };
            UpdateStatus();
        }

        public void UpdateStatus()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(UpdateStatus));
                return;
            }
            _status.Text = $"{_sequence:00} · {_pane.ActivityLabel}{TokenUsageSuffix(_pane)}";
            var foreground = TerminalForeground(_pane);
            _headingSurface.Background = TerminalBackground(_pane);
            _prominentTitle.Foreground = _status.Foreground = foreground;
        }

        public void DetachPane()
        {
            if (ReferenceEquals(_paneHost.Content, _pane)) _paneHost.Content = null;
        }
    }
}
