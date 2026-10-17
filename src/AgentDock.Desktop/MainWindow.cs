using System.Globalization;
using AgentDock.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TaskStatus = AgentDock.Core.TaskStatus;

namespace AgentDock.Desktop;

/// <summary>主面板：项目筛选的任务列表、角色/凭据/通知/悬浮窗设置、清理与完全退出。</summary>
public sealed partial class MainWindow : ShadUI.Window
{
    private const string AllProjects = "全部项目";

    private readonly App _app;
    private readonly AppState _state;
    private readonly DispatcherTimer _noticeTimer;
    private readonly Border _notice;
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly Border _warningBorder;
    private readonly TextBlock _warnings = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8A5A00")) };
    private readonly TextBlock _hostText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _startButton;
    private readonly Button _overlayButton;
    private readonly Button _refreshButton;
    private readonly ComboBox _projectFilter = new() { MinWidth = 160, MaxWidth = 230 };
    private readonly TextBlock _countText = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = .75, FontSize = 12.5 };
    private readonly TextBlock _activeMetric = Ui.Text("0", "metric");
    private readonly TextBlock _inputMetric = Ui.Text("待更新", "metric");
    private readonly TextBlock _outputMetric = Ui.Text("待更新", "metric");
    private readonly TextBlock _compactMetrics = Ui.Text("", "muted");
    private Control _metricCard = null!;
    private readonly TextBlock _emptyTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _emptyDescription = new() { Classes = { "muted" }, TextAlignment = TextAlignment.Center, MaxWidth = 420 };
    private readonly StackPanel _emptyTasks = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24), IsHitTestVisible = false };
    private readonly ListBox _taskList = new() { Classes = { "taskTable" } };
    private readonly TabControl _pages = new() { Classes = { "workspace" } };
    private readonly Button _stopButton;
    private readonly Button _viewButton;
    private readonly TextBlock _selectionText = new() { TextWrapping = TextWrapping.Wrap, Opacity = .8, FontSize = 12 };
    private readonly List<string> _projectItems = new() { AllProjects };

    private readonly TextBox _credentialBox = new() { PasswordChar = '●', Watermark = "输入 OpenCode Go API Key", MinWidth = 160, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _credentialState = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 32, Increment = 1, Value = 4, Width = 110 };
    private readonly Dictionary<string, TextBox> _rolePrompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumericUpDown> _roleTimeouts = new(StringComparer.Ordinal);
    private readonly CheckBox _notifyCompletion = new() { Content = "普通任务完成时也通知（默认关闭；失败、中断和凭据问题始终通知）" };
    private readonly CheckBox _startAtLogin = new() { Content = "登录系统时启动 AgentDock（默认关闭）" };
    private readonly CheckBox _overlayTopmost = new() { Content = "悬浮窗置顶" };
    private readonly Slider _overlayOpacity = new() { Minimum = .3, Maximum = 1, Width = 260, Value = .88, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _opacityText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    private readonly ComboBox _cleanupScope = new() { ItemsSource = new[] { "终态任务正文与结果", "全部终态任务历史" }, SelectedIndex = 0, Width = 230 };
    private readonly TextBlock _cleanupPreviewText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private Button _cleanupApply = null!;
    private CleanupPreview? _pendingCleanup;
    private ConfigurationView? _appliedView;
    private ResultWindow? _result;

    public MainWindow(App app)
    {
        // ShadUI's numeric input behavior looks up a parent name scope before
        // TemplateApplied. Code-built views need the scope XAML normally creates.
        NameScope.SetNameScope(this, new NameScope());
        _app = app;
        _state = app.State;
        Title = "AgentDock";
        Icon = Icons.Create();
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 600;
        Background = Ui.Canvas;
        Ui.ConfigureWindow(this);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowPlacement.KeepOnScreen(this);

        _startButton = MakeButton("启动后台", () => _app.StartHostAsync());
        _overlayButton = MakeButton("显示悬浮窗", () => { _app.ToggleOverlay(); RefreshHeader(); return Task.CompletedTask; });
        _refreshButton = MakeButton("刷新", () => _state.PollAsync());
        _stopButton = MakeButton("停止所选", StopSelectedAsync);
        _viewButton = MakeButton("查看结果", ViewSelectedAsync);
        _stopButton.IsEnabled = false;
        _viewButton.IsEnabled = false;
        _startButton.Classes.Clear();
        _startButton.Classes.Add("Primary");
        _stopButton.Classes.Clear();
        _stopButton.Classes.Add("Destructive");
        _viewButton.Classes.Clear();
        _viewButton.Classes.Add("Primary");

        _notice = new Border { IsVisible = false, Padding = new Thickness(14, 10), Margin = new Thickness(24, 0, 24, 12), CornerRadius = new CornerRadius(8), Child = _noticeText };
        _warningBorder = new Border { IsVisible = false, Padding = new Thickness(14, 10), Margin = new Thickness(24, 0, 24, 12), CornerRadius = new CornerRadius(8), Background = Ui.Brush("#FFFBEB"), Child = _warnings };
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice.IsVisible = false; };
        _overlayOpacity.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _opacityText.Text = _overlayOpacity.Value.ToString("0.00", CultureInfo.CurrentCulture);
            _app.UpdateOverlayPrefs(_overlayTopmost.IsChecked == true, _overlayOpacity.Value);
        };
        _overlayTopmost.IsCheckedChanged += (_, _) => _app.UpdateOverlayPrefs(_overlayTopmost.IsChecked == true, _overlayOpacity.Value);

        NameNumericEditor(_concurrency, "全局并发上限");
        Content = BuildLayout();
        // Apply breakpoints after the current arrange pass. Mutating child grid
        // tracks during SizeChanged can leave their parent with a stale height.
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(UpdateDensity, DispatcherPriority.Loaded);
        _state.Changed += Refresh;
        _taskList.SelectionChanged += (_, _) => UpdateSelection();
        Closing += (_, e) =>
        {
            // 关窗只隐藏：后台与任务继续运行。
            if (_app.Exiting) return;
            e.Cancel = true;
            Hide();
        };
        Refresh();
    }

    public void ShowNotice(string message, bool failure)
    {
        _noticeText.Text = message;
        _noticeText.Foreground = new SolidColorBrush(Color.Parse(failure ? "#B3261E" : "#1E6B33"));
        _notice.Background = new SolidColorBrush(failure ? Color.FromArgb(30, 179, 38, 30) : Color.FromArgb(26, 30, 107, 51));
        _notice.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public void RefreshHeader()
    {
        if (_state.HostReachable && _state.Host is not null)
            _hostText.Text = "● 后台运行中";
        else
            _hostText.Text = "○ 后台未连接";
        _overlayButton.Content = _app.IsOverlayVisible ? "隐藏悬浮窗" : "显示悬浮窗";
        _hostText.Foreground = _state.HostReachable ? Ui.Brush("#7FD3B7") : Ui.Brush("#BBCAC5");
        ToolTip.SetTip(_hostText, _state.HostReachable && _state.Host is { } host
            ? $"v{host.Version} · PID {host.ProcessId} · 未完成 {host.UnfinishedTasks}"
            : _state.LastError ?? "点击「启动后台」以连接。");
    }

    private void Refresh()
    {
        RefreshHeader();
        RefreshProjects();
        RefreshTasks();
        if (_state.Configuration is { } view && !ReferenceEquals(view, _appliedView))
        {
            _appliedView = view;
            ApplyConfigurationToUi(view);
        }
    }

    private void RefreshProjects()
    {
        var desired = new List<string> { AllProjects };
        desired.AddRange(_state.Tasks.Select(t => t.Project).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (desired.SequenceEqual(_projectItems, StringComparer.Ordinal)) return;
        var keep = _projectFilter.SelectedItem as string ?? AllProjects;
        _projectItems.Clear();
        _projectItems.AddRange(desired);
        _projectFilter.ItemsSource = _projectItems.ToArray();
        _projectFilter.SelectedItem = _projectItems.Contains(keep) ? keep : AllProjects;
    }

    private void RefreshTasks()
    {
        var selectedId = (_taskList.SelectedItem as TaskSummary)?.Id;
        var filter = _projectFilter.SelectedItem as string;
        var tasks = _state.Tasks
            .Where(t => filter is null || filter == AllProjects || string.Equals(t.Project, filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _taskList.ItemsSource = tasks;
        _taskList.SelectedItem = tasks.FirstOrDefault(t => t.Id == selectedId) ?? tasks.FirstOrDefault();
        _countText.Text = filter is null || filter == AllProjects ? $"共 {tasks.Length} 项" : $"筛选 {tasks.Length} / {_state.Tasks.Length} 项";
        _activeMetric.Text = tasks.Count(t => t.Status.IsUnfinished()).ToString(CultureInfo.CurrentCulture);
        SetUsageMetric(_inputMetric, _inputMetricNote, tasks.Select(t => t.InputTokens));
        SetUsageMetric(_outputMetric, _outputMetricNote, tasks.Select(t => t.OutputTokens));
        _compactMetrics.Text = $"活跃 {_activeMetric.Text} 项   ·   输入 {Fmt.Total(tasks.Select(t => t.InputTokens))}   ·   输出 {Fmt.Total(tasks.Select(t => t.OutputTokens))}";
        _emptyTasks.IsVisible = tasks.Length == 0;
        _emptyTitle.Text = _state.HostReachable ? "准备好接收任务" : "连接后台以查看任务";
        _emptyDescription.Text = _state.HostReachable
            ? "由 Codex 派发的子代理任务会显示在这里。你可以按项目筛选、查看结果或停止任务。"
            : "点击左下方「启动后台」。连接恢复后，已有任务会自动显示。";
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var task = _taskList.SelectedItem as TaskSummary;
        _stopButton.IsEnabled = task is { Status: TaskStatus.Queued or TaskStatus.Running or TaskStatus.Paused };
        _viewButton.IsEnabled = task is not null;
        RefreshInspector(task);
    }

    private void UpdateDensity()
    {
        var compact = Bounds.Height < 660;
        _metricCard.IsVisible = !compact;
        _compactMetrics.IsVisible = compact;
        var narrow = Bounds.Width < 1140;
        _inspector.IsVisible = !narrow;
        _taskWorkspace.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 280);
        _compactActions.IsVisible = narrow;
        _taskHeading.IsVisible = Bounds.Height >= 560;
        _settingsConnections.Columns = narrow ? 1 : 2;
        _settingsConnections.Rows = narrow ? 2 : 1;
        _credentialCard.Margin = narrow ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 8, 0);
        _concurrencyCard.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
    }

    private Control BuildCredentialRow()
    {
        AutomationProperties.SetName(_credentialBox, "OpenCode Go 凭据");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        Put(row, _credentialBox, 0);
        Put(row, MakeButton("保存凭据", SaveCredentialAsync), 0, 1);
        return new StackPanel { Spacing = 10, Children = { _credentialState, row, Hint("凭据在本机加密保存；保存成功后输入框清空。请使用 OpenCode Go 凭据。") } };
    }

    private Control BuildConcurrencyRow() => new StackPanel
    {
        Spacing = 6,
        Children =
        {
            _concurrency,
            Hint("全局并发上限（1–32，默认 4）"),
            Hint("超过上限的任务排队；同一项目内由 AgentDock 管理的写任务串行执行。")
        }
    };

    private Control BuildRolesEditor()
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var role in Roles.All)
        {
            var prompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 88, FontSize = 12.5 };
            ShadUI.ControlAssist.SetHeight(prompt, 88);
            var timeout = new NumericUpDown { Minimum = 1, Maximum = 86400, Increment = 60, Width = 130, Value = 1200, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(prompt, "角色提示词 " + role);
            NameNumericEditor(timeout, "角色超时秒数 " + role);
            _rolePrompts[role] = prompt;
            _roleTimeouts[role] = timeout;
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(86) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Margin = new Thickness(0, 0, 0, 6),
                ColumnSpacing = 12
            };
            Put(row, new TextBlock { Text = role, VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) }, 0, 0);
            Put(row, prompt, 0, 1);
            Put(row, timeout, 0, 2);
            Put(row, new TextBlock { Text = "秒", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.Muted }, 0, 3);
            panel.Children.Add(row);
        }
        return new StackPanel { Spacing = 4, Children = { panel, Hint("提示词描述工作要求，不改变执行权限；超时范围 1–86400 秒。") } };
    }

    private static void NameNumericEditor(NumericUpDown control, string name)
    {
        AutomationProperties.SetName(control, name);
        control.TemplateApplied += (_, args) =>
        {
            // Windows focuses the template's TextBox; its automation name is not inherited.
            if (args.NameScope.Find<TextBox>("PART_TextBox") is { } editor)
                AutomationProperties.SetName(editor, name);
        };
    }

    private Control BuildNotificationSettings()
    {
        AutomationProperties.SetName(_notifyCompletion, "完成通知开关");
        AutomationProperties.SetName(_startAtLogin, "登录自启开关");
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _notifyCompletion,
                _startAtLogin,
                Hint("失败、中断和凭据问题无论界面是否可见都会提醒；登录自启依赖安装版桌面程序，开发运行可能提示找不到配套程序。")
            }
        };
    }

    private Control BuildOverlaySettings()
    {
        AutomationProperties.SetName(_overlayTopmost, "悬浮窗置顶");
        AutomationProperties.SetName(_overlayOpacity, "悬浮窗透明度");
        _opacityText.Text = _overlayOpacity.Value.ToString("0.00", CultureInfo.CurrentCulture);
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _overlayTopmost,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new TextBlock { Text = "透明度", VerticalAlignment = VerticalAlignment.Center }, _overlayOpacity, _opacityText }
                },
                Hint("悬浮窗约 340×190，数值字号 12–14pt，仅展示活跃任务与真实用量。")
            }
        };
    }

    private Control BuildCleanupSettings()
    {
        _cleanupApply = MakeButton("确认清理", ApplyCleanupAsync);
        _cleanupApply.IsEnabled = false;
        _cleanupScope.SelectionChanged += (_, _) =>
        {
            _pendingCleanup = null;
            _cleanupApply.IsEnabled = false;
            _cleanupPreviewText.Text = "";
        };
        AutomationProperties.SetName(_cleanupScope, "清理范围");
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _cleanupScope, MakeButton("预览清理", PreviewCleanupAsync), _cleanupApply } },
                _cleanupPreviewText,
                Hint("先预览实际范围再确认；未完成的任务受保护，不会按终态保留规则清理。")
            }
        };
    }

    private Control BuildExitSection()
    {
        var exit = MakeButton("完全退出 AgentDock…", () => _app.ExitAsync(), "Destructive");
        AutomationProperties.SetName(exit, "完全退出");
        return new StackPanel
        {
            Spacing = 6,
            Children = { exit, Hint("存在未完成任务时会先确认；确认后停止运行中的任务并取消排队/暂停任务。只关闭窗口不会停止任务。") }
        };
    }

    private async Task StopSelectedAsync()
    {
        if (_taskList.SelectedItem is not TaskSummary task) return;
        if (!task.Status.IsUnfinished())
        {
            ShowNotice($"任务已结束（{Fmt.Status(task.Status)}），无需停止。", false);
            return;
        }
        _stopButton.IsEnabled = false;
        try
        {
            var summary = await _state.Api.StopAsync(task.Id, _state.Token);
            ShowNotice($"已请求停止「{summary.Title}」；保留已有文件修改，不自动回滚。", false);
            await _state.PollAsync();
        }
        catch (Exception ex) { ShowNotice("停止失败：" + AppState.Describe(ex), true); }
        finally { UpdateSelection(); }
    }

    private async Task ViewSelectedAsync()
    {
        if (_taskList.SelectedItem is not TaskSummary task) return;
        if (_result is null || _result.TaskId != task.Id || !_result.IsVisible)
        {
            _result?.Close();
            _result = new ResultWindow(task.Id, _state);
            _result.Closed += (_, _) => _result = null;
        }
        if (!_result.IsVisible) _result.Show(this);
        _result.Activate();
        await Task.CompletedTask;
    }

    private async Task SaveConfigurationAsync()
    {
        var current = _state.Configuration?.Configuration;
        if (current is null)
        {
            ShowNotice("尚未连接后台，无法保存配置。", true);
            return;
        }
        DockConfiguration next;
        try { next = BuildConfiguration(current); }
        catch (DockException ex) { ShowNotice(ex.Message, true); return; }
        try
        {
            var view = await _state.Api.SetConfigurationAsync(next, _state.Token);
            _appliedView = view;
            _state.ApplyConfiguration(view);
            ShowNotice("配置已保存。提示词与超时仅影响新接收的任务；排队与运行中的任务保持原配置。", false);
        }
        catch (Exception ex)
        {
            ShowNotice("配置保存失败：" + AppState.Describe(ex), true);
            if (_state.Configuration is { } known) ApplyConfigurationToUi(known);
        }
    }

    private async Task SaveCredentialAsync()
    {
        var key = _credentialBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowNotice("请输入 OpenCode Go 凭据。", true);
            return;
        }
        try
        {
            var view = await _state.Api.SetCredentialAsync(key, _state.Token);
            _credentialBox.Text = "";
            _appliedView = view;
            _state.ApplyConfiguration(view);
            ShowNotice("凭据已保存。界面不回显凭据内容。", false);
        }
        catch (Exception ex) { ShowNotice("凭据保存失败：" + AppState.Describe(ex), true); }
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var notice = await ReleaseCatalog.CheckAsync(client, _state.Host?.Version ?? "0.1.0", _state.Token);
            ShowNotice(notice.Message, false);
        }
        catch (Exception) { ShowNotice("无法检查新版本，请稍后重试或查看发行页面。", true); }
    }

    private async Task PreviewCleanupAsync()
    {
        try
        {
            var preview = await _state.Api.CleanupPreviewAsync(CleanupScope, null, _state.Token);
            _pendingCleanup = preview;
            _cleanupPreviewText.Text = $"预览（{CleanupScope}）：将清理任务摘要 {preview.Summaries} 条、任务正文 {preview.Bodies} 条；未完成任务受保护 {preview.UnfinishedProtected} 条。手动清理正文或全部历史包含未满 7 天的已结束任务，清理后 Codex 无法取回正文。";
            _cleanupApply.IsEnabled = preview.Summaries + preview.Bodies > 0;
        }
        catch (Exception ex) { ShowNotice("清理预览失败：" + AppState.Describe(ex), true); }
    }

    private async Task ApplyCleanupAsync()
    {
        if (_pendingCleanup is not { } preview)
        {
            ShowNotice("请先预览清理范围。", true);
            return;
        }
        var confirmed = await ConfirmWindow.AskAsync(this, "清理历史",
            $"将清理任务摘要 {preview.Summaries} 条、任务正文 {preview.Bodies} 条。\n手动清理正文或全部历史包含未满 7 天的已结束任务，清理后 Codex 无法取回正文。\n未完成任务不会受影响（{preview.UnfinishedProtected} 条受保护）。\n\n是否继续？");
        if (!confirmed) return;
        try
        {
            var applied = await _state.Api.CleanupApplyAsync(CleanupScope, null, preview.Revision!, _state.Token);
            _pendingCleanup = null;
            _cleanupApply.IsEnabled = false;
            _cleanupPreviewText.Text = $"已清理任务摘要 {applied.Summaries} 条、任务正文 {applied.Bodies} 条；受保护未完成任务 {applied.UnfinishedProtected} 条。";
            ShowNotice("清理完成。", false);
            await _state.PollAsync();
        }
        catch (Exception ex) { ShowNotice("清理失败：" + AppState.Describe(ex), true); }
    }

    private DockConfiguration BuildConfiguration(DockConfiguration current)
    {
        var roles = new Dictionary<string, RoleConfiguration>(StringComparer.Ordinal);
        foreach (var role in Roles.All)
        {
            var prompt = _rolePrompts[role].Text ?? "";
            if (string.IsNullOrWhiteSpace(prompt)) throw new DockException("InvalidConfiguration", $"角色 {role} 的提示词不能为空。");
            if (prompt.Length > 64000) throw new DockException("InvalidConfiguration", $"角色 {role} 的提示词超过 64000 字符。");
            roles[role] = new RoleConfiguration(prompt, (int)(_roleTimeouts[role].Value ?? 0m));
        }
        return current with
        {
            MaxConcurrency = (int)(_concurrency.Value ?? 4m),
            Roles = roles,
            NotifyOnCompletion = _notifyCompletion.IsChecked == true,
            StartAtLogin = _startAtLogin.IsChecked == true,
            OverlayTopmost = _overlayTopmost.IsChecked == true,
            OverlayOpacity = Math.Round(_overlayOpacity.Value, 2)
        };
    }

    private void ApplyConfigurationToUi(ConfigurationView view)
    {
        var config = view.Configuration;
        _credentialState.Text = view.CredentialConfigured ? "凭据：已配置" : "凭据：未配置";
        _concurrency.Value = config.MaxConcurrency;
        foreach (var role in Roles.All)
        {
            if (!config.Roles.TryGetValue(role, out var roleConfig)) continue;
            _rolePrompts[role].Text = roleConfig.Prompt;
            _roleTimeouts[role].Value = roleConfig.TimeoutSeconds;
        }
        _notifyCompletion.IsChecked = config.NotifyOnCompletion;
        _startAtLogin.IsChecked = config.StartAtLogin;
        _overlayTopmost.IsChecked = config.OverlayTopmost;
        _overlayOpacity.Value = config.OverlayOpacity;
        _opacityText.Text = config.OverlayOpacity.ToString("0.00", CultureInfo.CurrentCulture);
        _app.UpdateOverlayPrefs(config.OverlayTopmost, config.OverlayOpacity);
        _warnings.Text = string.Join("\n", view.Warnings);
        _warningBorder.IsVisible = view.Warnings.Length > 0;
    }

    private string CleanupScope => _cleanupScope.SelectedIndex == 1 ? "all" : "bodies";

    private Button MakeButton(string text, Func<Task> action, string variant = "Outline")
    {
        var button = Ui.Button(text, variant);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { ShowNotice("操作失败：" + AppState.Describe(ex), true); }
        };
        return button;
    }

    private static IBrush StatusBrush(TaskStatus status) => status switch
    {
        TaskStatus.Running => Ui.Accent,
        TaskStatus.Completed => new SolidColorBrush(Color.Parse("#348568")),
        TaskStatus.Failed or TaskStatus.TimedOut or TaskStatus.Interrupted => new SolidColorBrush(Color.Parse("#C62828")),
        TaskStatus.Paused => new SolidColorBrush(Color.Parse("#B26A00")),
        _ => new SolidColorBrush(Color.Parse("#5A6472"))
    };

    private static void Put(Grid grid, Control child, int row, int column = 0)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static Control Section(string title, Control content) => Ui.Card(new StackPanel
    {
        Spacing = 14,
        Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, Foreground = Ui.Ink }, content }
    });

    private static Control ExpanderSection(string title, Control content)
    {
        var expander = new Expander
        {
            Classes = { "settingsGroup" },
            ClipToBounds = true,
            Header = title,
            Content = new Border { Padding = new Thickness(0, 16, 0, 0), Child = content },
            IsExpanded = false
        };
        AutomationProperties.SetName(expander, title);
        var card = Ui.Card(expander, new Thickness(20, 12));
        if (title.StartsWith("角色", StringComparison.Ordinal)) card.Name = "RoleSettings";
        return card;
    }

    private static TextBlock Hint(string text) => Ui.Text(text, "muted");
}
