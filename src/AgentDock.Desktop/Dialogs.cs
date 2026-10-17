using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentDock.Desktop;

/// <summary>确认对话框：完全退出等需要明确确认的操作。</summary>
public sealed class ConfirmWindow : ShadUI.Window
{
    private ConfirmWindow(string title, string message)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMaximize = false;
        CanMinimize = false;
        Ui.ConfigureWindow(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var confirm = Ui.Button("确认", "Destructive");
        var cancel = Ui.Button("取消");
        confirm.MinWidth = cancel.MinWidth = 88;
        cancel.IsCancel = true;
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 20 },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, confirm }
                }
            }
        };
        Opened += (_, _) => cancel.Focus();
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
        Width = 350;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = Ui.Brush(failure ? "#FECACA" : "#3F3F46"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Background = Ui.Brush("#F518181B"),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = failure ? "AgentDock · 需要处理" : "AgentDock", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush(failure ? "#FCA5A5" : "#D4D4D8") },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 13 }
                }
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
