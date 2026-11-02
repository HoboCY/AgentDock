using AgentDock.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentDock.Desktop;

/// <summary>紧凑半透明悬浮窗（约 340x190）：只读展示活跃任务与用量，可拖动。</summary>
public sealed class OverlayWindow : Window
{
    private readonly App _app;
    private readonly AppState _state;
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = .8, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _tasks = new() { Spacing = 7 };
    private readonly TextBlock _emptyMessage = new() { FontSize = 12.5, Opacity = .7, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    private readonly Dictionary<string, (StackPanel Panel, TextBlock Title, TextBlock Detail)> _rows = new(StringComparer.Ordinal);
    private bool _placed;

    public OverlayWindow(App app)
    {
        _app = app;
        _state = app.State;
        Title = "AgentDock 悬浮窗";
        Width = 340;
        Height = 190;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        Opacity = .88;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        var title = new TextBlock { Text = "AgentDock", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_status, 1);
        header.Children.Add(title);
        header.Children.Add(_status);

        var open = Small("主面板");
        open.Click += (_, _) => _app.ShowMain();
        var hide = Small("收起");
        hide.Click += (_, _) => _app.ToggleOverlay();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { hide, open } };

        var body = new ScrollViewer { Content = _tasks, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var root = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Background = Ui.Brush("#F0182B28"),
            BorderBrush = Ui.Brush("#44665B"),
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = GridLength.Auto }
                },
                Children = { header, body, footer }
            }
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = root;
        void SchedulePlacement(object? sender, EventArgs args) =>
            Dispatcher.UIThread.Post(EnsureOnScreen, DispatcherPriority.Loaded);
        Opened += SchedulePlacement;
        ScalingChanged += SchedulePlacement;
        Screens.Changed += SchedulePlacement;
        Closed += (_, _) => Screens.Changed -= SchedulePlacement;
        _state.Changed += Refresh;
        _tasks.Children.Add(_emptyMessage);
        Refresh();
    }

    public void Refresh()
    {
        var activeCount = _state.Tasks.Count(t => t.Status.IsUnfinished());
        _status.Text = !_state.HostReachable ? "后台未连接" : activeCount == 0 ? "无活跃任务" : $"活跃 {activeCount}";
        var active = _state.Tasks.Where(t => t.Status.IsUnfinished()).ToArray();
        var ids = active.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _tasks.Children.Remove(_rows[id].Panel);
            _rows.Remove(id);
        }
        _emptyMessage.IsVisible = active.Length == 0;
        _emptyMessage.Text = _state.HostReachable ? "暂无活跃任务" : "后台未连接，显示最后一次已知数据。";
        var now = DateTimeOffset.Now;
        for (var i = 0; i < active.Length; i++)
        {
            var task = active[i];
            if (!_rows.TryGetValue(task.Id, out var row))
            {
                var title = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White };
                var detail = new TextBlock { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
                row = (new StackPanel { Spacing = 1, Children = { title, detail } }, title, detail);
                _rows.Add(task.Id, row);
                _tasks.Children.Insert(i + 1, row.Panel);
            }
            else if (!ReferenceEquals(_tasks.Children[i + 1], row.Panel))
                _tasks.Children.Move(_tasks.Children.IndexOf(row.Panel), i + 1);
            row.Title.Text = $"{Fmt.Status(task.Status)} · {task.Title}";
            ToolTip.SetTip(row.Title, task.Project + "\n" + task.Title);
            row.Detail.Text = $"{task.Role} · {Fmt.Elapsed(task, now)} · {Fmt.Speed(task.GenerationTokensPerSecond)}\n输入 {Fmt.Tokens(task.InputTokens)} / 输出 {Fmt.Tokens(task.OutputTokens)}";
        }
    }

    private void EnsureOnScreen()
    {
        if (!IsVisible) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scale = OperatingSystem.IsWindows() ? screen.Scaling : 1;
        var rectangle = new PixelRect(Position, PixelSize.FromSize(ClientSize, scale));
        if (_placed && screen.WorkingArea.Contains(rectangle)) return;
        _placed = true;
        WindowPlacement.BottomRight(this, 28);
    }

    private static Button Small(string text) => new()
    {
        Content = text,
        FontSize = 12,
        Padding = new Thickness(8, 2),
        MinHeight = 24,
        Classes = { "Ghost" },
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255))
    };
}
