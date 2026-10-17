using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentDock.Desktop;

/// <summary>确认对话框：完全退出等需要明确确认的操作。</summary>
public sealed class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var confirm = new Button { Content = "确认", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88 };
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm }
                }
            }
        };
    }

    public static Task<bool> AskAsync(Window owner, string title, string message) =>
        new ConfirmWindow(title, message).ShowDialog<bool>(owner);
}

/// <summary>轻量桌面提醒：置顶、不抢焦点，失败/中断/凭据问题在窗口隐藏时也可见。</summary>
public sealed class ToastWindow : Window
{
    public ToastWindow(string message, bool failure, Action onClick)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 330;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12),
            Background = new SolidColorBrush(failure ? Color.FromArgb(242, 96, 28, 28) : Color.FromArgb(242, 28, 32, 40)),
            Child = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13
            }
        };
        border.PointerPressed += (_, _) => { onClick(); Close(); };
        Content = border;
        Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows())
                Dispatcher.UIThread.Post(PositionBottomRight, DispatcherPriority.Loaded);
            else
                PositionBottomRight();
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    private void PositionBottomRight()
    {
        WindowPlacement.BottomRight(this, 24);
    }
}
