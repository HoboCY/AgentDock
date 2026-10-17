using AgentDock.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AgentDock.Desktop;

public sealed class App : Application
{
    private readonly Queue<(string Message, bool Failure)> _toasts = new();
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private TrayIcon? _tray;
    private ToastWindow? _toast;
    private DispatcherTimer? _pollTimer;
    private bool _overlayTopmost = true;
    private double _overlayOpacity = .88;

    public AppState State { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;
    public OverlayWindow Overlay { get; private set; } = null!;
    public bool Exiting { get; private set; }
    public bool IsOverlayVisible => Overlay is { IsVisible: true };
    public bool TrayCreated => _tray is not null;
    public double? MainOpenedMilliseconds { get; private set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new ShadUI.ShadTheme());
        Styles.Add(new StyleInclude(new Uri("avares://AgentDock.Desktop/"))
        {
            Source = new Uri("avares://AgentDock.Desktop/DesktopStyles.axaml")
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        // 窗口生命周期与任务生命周期分离：关窗只隐藏，完全退出是独立操作。
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        State = new AppState(new HostApi(new DockClient(Program.HomeOverride)));
        State.NotifyRequested += Notify;
        State.Changed += OnStateChanged;

        Overlay = new OverlayWindow(this);
        Main = new MainWindow(this);
        Main.Opened += (_, _) => MainOpenedMilliseconds ??=
            (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        desktop.MainWindow = Program.TrayOnly ? null : Main;

        try { BuildTray(); }
        catch (Exception) { desktop.MainWindow = Main; Main.Show(); }

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await State.PollAsync();
        _pollTimer.Start();
        _ = State.PollAsync();

        if (Program.SmokeOutputPath is { } smokePath) SmokeTest.Schedule(this, smokePath);
    }

    public void ShowMain()
    {
        if (!Main.IsVisible) Main.Show();
        Main.WindowState = WindowState.Normal;
        Main.Activate();
    }

    public void ShowOverlay()
    {
        Overlay.Topmost = _overlayTopmost;
        Overlay.Opacity = _overlayOpacity;
        if (!Overlay.IsVisible) Overlay.Show();
    }

    public void ToggleOverlay()
    {
        if (Overlay.IsVisible) Overlay.Hide();
        else ShowOverlay();
        Main.RefreshHeader();
    }

    public void UpdateOverlayPrefs(bool topmost, double opacity)
    {
        _overlayTopmost = topmost;
        _overlayOpacity = Math.Clamp(opacity, .3, 1);
        Overlay.Topmost = _overlayTopmost;
        Overlay.Opacity = _overlayOpacity;
    }

    public async Task StartHostAsync()
    {
        Notify("正在启动后台…", false);
        var status = await State.StartHostAsync();
        if (status is not null)
            Notify($"后台已启动：v{status.Version} · PID {status.ProcessId}", false);
    }

    public async Task ExitAsync()
    {
        if (Exiting) return;
        if (!await ConfirmShutdownAsync()) return;
        Exiting = true;
        _pollTimer?.Stop();
        _desktop.Shutdown();
    }

    public void ShutdownForSmoke(int exitCode)
    {
        Exiting = true;
        _pollTimer?.Stop();
        _desktop.Shutdown(exitCode);
    }

    /// <summary>失败/中断/凭据问题即使窗口隐藏也提醒；普通完成按配置可选。</summary>
    public void Notify(string message, bool failure)
    {
        if (Main.IsVisible)
        {
            Main.ShowNotice(message, failure);
            if (!failure) return;
        }
        _toasts.Enqueue((message, failure));
        if (_toast is null) ShowNextToast();
    }

    private async Task<bool> ConfirmShutdownAsync()
    {
        HostStatus? status = null;
        try { status = await State.Api.StatusAsync(State.Token); }
        catch (Exception) { /* 下面的分支按不可达处理。 */ }
        if (status is { UnfinishedTasks: > 0 })
        {
            var confirmed = await ConfirmWindow.AskAsync(Main, "完全退出",
                $"仍有 {status.UnfinishedTasks} 项未完成任务。\n完全退出将停止运行中的任务，并取消排队或暂停的任务；已产生的文件修改会保留。\n\n是否继续退出？");
            if (!confirmed) return false;
        }
        else if (status is null)
        {
            var confirmed = await ConfirmWindow.AskAsync(Main, "完全退出",
                "无法连接后台，退出界面不会停止后台任务。\n\n是否仅退出界面？");
            return confirmed;
        }
        try
        {
            await State.Api.ShutdownAsync(status.UnfinishedTasks > 0, State.Token);
            return true;
        }
        catch (Exception ex)
        {
            return await ConfirmWindow.AskAsync(Main, "完全退出", "后台未能正常退出：" + AppState.Describe(ex) + "\n\n是否仍退出界面？");
        }
    }

    private void ShowNextToast()
    {
        if (_toasts.Count == 0) return;
        var (message, failure) = _toasts.Dequeue();
        _toast = new ToastWindow(message, failure, ShowMain);
        _toast.Closed += (_, _) =>
        {
            _toast = null;
            ShowNextToast();
        };
        _toast.Show();
    }

    private void OnStateChanged()
    {
        if (_tray is not null) _tray.ToolTipText = "AgentDock · " + State.SummaryLine;
    }

    private void BuildTray()
    {
        var menu = new NativeMenu();
        menu.Items.Add(TrayItem("主面板", ShowMain));
        menu.Items.Add(TrayItem("显示/隐藏悬浮窗", ToggleOverlay));
        menu.Items.Add(TrayItem("启动后台", () => _ = StartHostAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(TrayItem("完全退出…", () => _ = ExitAsync()));
        var tray = new TrayIcon
        {
            Icon = Icons.Create(),
            ToolTipText = "AgentDock",
            Menu = menu
        };
        tray.Clicked += (_, _) => ShowMain();
        _tray = tray;
    }

    private static NativeMenuItem TrayItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
