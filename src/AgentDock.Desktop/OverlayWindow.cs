using AgentDock.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentDock.Desktop;

/// <summary>紧凑半透明悬浮窗（约 340x190）：只读展示活跃任务与用量，可拖动。</summary>
public sealed class OverlayWindow : Window
{
    private readonly App _app;
    private readonly AppState _state;
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = .8, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _tasks = new() { Spacing = 7 };
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
        Opened += (_, _) =>
        {
            if (_placed) return;
            _placed = true;
            PlaceBottomRight();
        };
        _state.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var activeCount = _state.Tasks.Count(t => t.Status.IsUnfinished());
        _status.Text = !_state.HostReachable ? "后台未连接" : activeCount == 0 ? "无活跃任务" : $"活跃 {activeCount}";
        _tasks.Children.Clear();
        var active = _state.Tasks.Where(t => t.Status.IsUnfinished()).ToArray();
        if (active.Length == 0)
        {
            _tasks.Children.Add(new TextBlock
            {
                Text = _state.HostReachable ? "暂无活跃任务" : "后台未连接，显示最后一次已知数据。",
                FontSize = 12.5,
                Opacity = .7,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White
            });
            return;
        }
        var now = DateTimeOffset.Now;
        foreach (var task in active)
        {
            var itemTitle = new TextBlock { Text = $"{Fmt.Status(task.Status)} · {task.Title}", FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White };
            ToolTip.SetTip(itemTitle, task.Project + "\n" + task.Title);
            var detail = new TextBlock
            {
                Text = $"{task.Role} · {Fmt.Elapsed(task, now)} · {Fmt.Speed(task.GenerationTokensPerSecond)}\n输入 {Fmt.Tokens(task.InputTokens)} / 输出 {Fmt.Tokens(task.OutputTokens)}",
                FontSize = 12,
                Opacity = .75,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White
            };
            _tasks.Children.Add(new StackPanel { Spacing = 1, Children = { itemTitle, detail } });
        }
    }

    private void PlaceBottomRight()
    {
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
