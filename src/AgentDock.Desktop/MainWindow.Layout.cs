using AgentDock.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentDock.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBlock _detailTitle = new() { FontSize = 21, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Ink };
    private readonly TextBlock _detailStatus = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _detailProject = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Ink };
    private readonly TextBlock _detailRole = new() { FontSize = 13, Foreground = Ui.Ink };
    private readonly TextBlock _detailInput = new() { FontSize = 16, Foreground = Ui.Ink };
    private readonly TextBlock _detailOutput = new() { FontSize = 16, Foreground = Ui.Ink };
    private readonly TextBlock _detailSpeed = new() { FontSize = 14, Foreground = Ui.Ink };
    private readonly TextBlock _detailElapsed = new() { FontSize = 14, Foreground = Ui.Ink };
    private readonly TextBlock _detailId = new() { FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailError = new() { FontSize = 12, Foreground = Ui.Brush("#B44336"), TextWrapping = TextWrapping.Wrap };
    private Grid _taskWorkspace = null!;
    private Border _inspector = null!;
    private Control _compactActions = null!;
    private Button _compactView = null!;
    private Button _compactStop = null!;
    private UniformGrid _settingsConnections = null!;
    private Control _credentialCard = null!;
    private Control _concurrencyCard = null!;

    private Control BuildLayout()
    {
        var tasks = new TabItem { Header = "任务", Content = BuildTasksTab() };
        var settings = new TabItem { Header = "设置", Content = BuildSettingsTab() };
        _setupTab = new TabItem { Header = "开始使用", Content = BuildOnboarding() };
        foreach (var tab in new[] { tasks, settings, _setupTab })
        {
            AutomationProperties.SetName(tab, (string)tab.Header!);
            tab.HeaderTemplate = new FuncDataTemplate<string>((label, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { Ui.Symbol(label == "任务" ? "tasks" : "settings", Ui.Accent, 15), new TextBlock { Text = label, FontSize = 13 } }
            });
            _pages.Items.Add(tab);
        }

        foreach (var button in new[] { _startButton, _overlayButton })
        {
            button.Classes.Clear();
            button.Classes.Add("Ghost");
            button.Classes.Add("utilityAction");
            // ShadUI variants also color their template presenters from resources.
            button.Resources["ForegroundColor"] = Color.Parse("#D7E7E1");
            button.Resources["GhostColor"] = Color.Parse("#2B4840");
            ShadUI.ButtonAssist.SetIcon(button, Ui.Symbol(button == _startButton ? "play" : "window", Ui.Brush("#BED4CB")));
        }
        var utilities = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        Put(utilities, _hostText, 0);
        Put(utilities, _overlayButton, 0, 1);
        Put(utilities, _startButton, 0, 2);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var banners = new StackPanel { Children = { _notice, _warningBorder } };
        _notice.Margin = _warningBorder.Margin = new Thickness(14, 6, 14, 0);
        Put(root, banners, 0);
        Put(root, _pages, 1);
        Put(root, new Border { Background = Ui.Sidebar, Padding = new Thickness(14, 4), Child = utilities }, 2);
        return root;
    }

    private Control BuildTasksTab()
    {
        _projectFilter.ItemsSource = _projectItems.ToArray();
        _projectFilter.SelectedIndex = 0;
        _projectFilter.SelectionChanged += (_, _) => RefreshTasks();
        AutomationProperties.SetName(_projectFilter, "项目筛选");
        _projectFilter.ItemTemplate = new FuncDataTemplate<string>((project, _) => new TextBlock
        {
            Text = project == AllProjects ? AllProjects : ProjectName(project ?? ""), TextTrimming = TextTrimming.CharacterEllipsis
        });
        _projectFilter.SelectionChanged += (_, _) => ToolTip.SetTip(_projectFilter, _projectFilter.SelectedItem);

        ToolTip.SetTip(_compactMetrics, "统计当前项目范围；未报告的用量保持待更新，不使用估算值。");
        var listHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        Put(listHeading, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "任务", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Ink }, _countText
        } }, 0);
        Put(listHeading, _projectFilter, 0, 1);
        Put(listHeading, _refreshButton, 0, 2);
        _taskList.ItemTemplate = new FuncDataTemplate<TaskSummary>((task, _) => BuildTaskRow(task));
        AutomationProperties.SetName(_taskList, "任务列表");
        _taskList.DoubleTapped += async (_, _) => await ViewSelectedAsync();
        _emptyTasks.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Ui.Brush("#E7EFEB"),
            HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.Symbol("tasks", Ui.Accent, 24)
        });
        _emptyTasks.Children.Add(_emptyTitle);
        _emptyTasks.Children.Add(_emptyDescription);
        var list = new Panel { Children = { _taskList, _emptyTasks } };

        _compactView = MakeButton("查看结果", ViewSelectedAsync, "Primary");
        _compactStop = MakeButton("停止所选", StopSelectedAsync);
        _compactStop.Classes.Add("dangerOutline");
        _compactStop.Resources["ForegroundColor"] = Color.Parse("#AB4338");
        _selectionText.TextTrimming = TextTrimming.CharacterEllipsis;
        _selectionText.MaxLines = 2;
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        Put(actions, _selectionText, 0);
        Put(actions, _compactView, 0, 1);
        Put(actions, _compactStop, 0, 2);
        _compactActions = actions;
        _compactActions.IsVisible = false;
        var center = new Grid { Margin = new Thickness(14, 10), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 10 };
        Put(center, listHeading, 0);
        Put(center, _compactMetrics, 1);
        Put(center, list, 2);
        Put(center, actions, 3);
        _taskWorkspace = new Grid { ColumnDefinitions = new ColumnDefinitions("*,280") };
        Put(_taskWorkspace, center, 0);
        _inspector = BuildInspector();
        Put(_taskWorkspace, _inspector, 0, 1);
        return _taskWorkspace;
    }

    private Border BuildInspector()
    {
        AutomationProperties.SetName(_detailTitle, "所选任务标题");
        _detailTitle.Name = "SelectedTaskTitle";
        _detailProject.Name = "SelectedTaskProject";
        _detailInput.Name = "SelectedTaskInput";
        _detailOutput.Name = "SelectedTaskOutput";
        var usage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 16, RowSpacing = 22 };
        var labels = new[] { "输入 Token", "输出 Token", "生成速度", "总耗时" };
        var values = new[] { _detailInput, _detailOutput, _detailSpeed, _detailElapsed };
        for (var i = 0; i < labels.Length; i++)
            Put(usage, new StackPanel { Spacing = 8, Children = { Hint(labels[i]), values[i] } }, i / 2, i % 2);
        var info = new StackPanel { Spacing = 22, Children =
        {
            Hint("任务详情"), _detailTitle,
            new Border { Background = Ui.Brush("#E8F1EC"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), HorizontalAlignment = HorizontalAlignment.Left, Child = _detailStatus },
            new StackPanel { Spacing = 7, Children = { Hint("项目"), _detailProject } },
            new StackPanel { Spacing = 7, Children = { Hint("执行角色"), _detailRole } },
            Divider(), usage, Divider(),
            new StackPanel { Spacing = 7, Children = { Hint("任务 ID"), _detailId } }, _detailError
        } };
        _viewButton.HorizontalAlignment = _stopButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _stopButton.Classes.Clear();
        _stopButton.Classes.Add("Outline");
        _stopButton.Classes.Add("dangerOutline");
        _stopButton.Resources["ForegroundColor"] = Color.Parse("#AB4338");
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 24 };
        Put(content, new ScrollViewer { Content = info, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0);
        Put(content, new StackPanel { Spacing = 10, Children = { _viewButton, _stopButton } }, 1);
        return new Border
        {
            Name = "TaskInspector", BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0),
            Background = Ui.Brush("#FFFFFF"), Padding = new Thickness(24, 28, 24, 24), Child = content
        };
    }

    private void RefreshInspector(TaskSummary? task)
    {
        _compactView.IsEnabled = _viewButton.IsEnabled;
        _compactStop.IsEnabled = _stopButton.IsEnabled;
        _detailTitle.Text = task?.Title ?? "等待下一次委派";
        _detailStatus.Text = task is null ? "暂无任务" : "● " + Fmt.Status(task.Status);
        _detailStatus.Foreground = task is null ? Ui.Muted : StatusBrush(task.Status);
        _detailProject.Text = task?.Project ?? "选择任务后查看项目与用量。";
        _detailRole.Text = task?.Role ?? "—";
        _detailInput.Text = task is null ? "—" : Fmt.Tokens(task.InputTokens);
        _detailOutput.Text = task is null ? "—" : Fmt.Tokens(task.OutputTokens);
        _detailSpeed.Text = task is null ? "—" : Fmt.Speed(task.GenerationTokensPerSecond);
        _detailElapsed.Text = task is null ? "—" : Fmt.Elapsed(task, DateTimeOffset.Now);
        _detailId.Text = task?.Id ?? "—";
        _detailError.Text = task?.Error;
        _detailError.IsVisible = !string.IsNullOrWhiteSpace(task?.Error);
        _selectionText.Text = task is null ? "选择任务以查看结果" : $"{Fmt.Status(task.Status)} · {task.Role}\n{task.Title}";
        ToolTip.SetTip(_selectionText, task is null ? null : $"{task.Project}\n{task.Id}\n{task.Error}");
    }

    private static Control? BuildTaskRow(TaskSummary? task)
    {
        // Recycled Avalonia containers clear the data template with null.
        if (task is null) return null;
        var state = StatusBrush(task.Status);
        var grid = new Grid { Name = "TaskDataRow", ColumnDefinitions = new ColumnDefinitions("60,*,90"), ColumnSpacing = 10, MinHeight = 48 };
        var status = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new Border { Width = 8, Height = 8, Background = state, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left },
            new TextBlock { Text = Fmt.Status(task.Status), FontSize = 11, Foreground = state, FontWeight = FontWeight.Medium }
        } };
        Put(grid, status, 0);
        var title = new TextBlock { Text = task.Title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Ui.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = new TextBlock { Text = $"{ProjectName(task.Project)}  ·  {task.Role}", FontSize = 11, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
        Put(grid, new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Children = { title, meta } }, 0, 1);
        Put(grid, new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Children =
        {
            new TextBlock { Text = Fmt.Elapsed(task, DateTimeOffset.Now), FontSize = 12, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Right },
            new TextBlock { Text = "输出 " + Fmt.Tokens(task.OutputTokens), FontSize = 11, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis }
        } }, 0, 2);
        ToolTip.SetTip(grid, $"{task.Title}\n{task.Project}\n输入 {Fmt.Tokens(task.InputTokens)} · 输出 {Fmt.Tokens(task.OutputTokens)}\n生成速度 {Fmt.Speed(task.GenerationTokensPerSecond)}" + (task.Error is null ? "" : "\n错误：" + task.Error));
        AutomationProperties.SetName(grid, $"{task.Title}，{Fmt.Status(task.Status)}，{task.Role}，{task.Project}，输入 {Fmt.Tokens(task.InputTokens)}，输出 {Fmt.Tokens(task.OutputTokens)}，生成速度 {Fmt.Speed(task.GenerationTokensPerSecond)}，总耗时 {Fmt.Elapsed(task, DateTimeOffset.Now)}");
        return grid;
    }

    private Control BuildSettingsTab()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(14, 10), MaxWidth = 1060, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(MakeButton("打开配置引导", OpenSetupAsync));
        _settingsConnections = new UniformGrid { Name = "ConnectionSettings", Columns = 2, Rows = 1 };
        _credentialCard = Section("OpenCode Go", BuildCredentialRow());
        _credentialCard.Name = "CredentialSettings";
        _credentialCard.Margin = new Thickness(0, 0, 8, 0);
        _settingsConnections.Children.Add(_credentialCard);
        _concurrencyCard = Section("并发与调度", BuildConcurrencyRow());
        _concurrencyCard.Name = "ConcurrencySettings";
        _concurrencyCard.Margin = new Thickness(8, 0, 0, 0);
        _settingsConnections.Children.Add(_concurrencyCard);
        panel.Children.Add(_settingsConnections);
        panel.Children.Add(ExpanderSection("角色提示词与超时（五个固定角色）", BuildRolesEditor()));
        panel.Children.Add(ExpanderSection("通知与启动", BuildNotificationSettings()));
        panel.Children.Add(ExpanderSection("悬浮窗", BuildOverlaySettings()));
        panel.Children.Add(ExpanderSection("历史清理", BuildCleanupSettings()));
        panel.Children.Add(Section("版本更新", new StackPanel { Spacing = 8, Children =
        {
            new WrapPanel { Orientation = Orientation.Horizontal, Children =
            {
                MakeButton("检查更新", CheckUpdatesAsync),
                MakeButton("查看发行页面", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleaseCatalog.ReleasesPage) { UseShellExecute = true }); return Task.CompletedTask; })
            } }, Hint("仅在你点击时检查新版本；未完成任务结束前不会应用更新。")
        } }));
        panel.Children.Add(Section("后台与退出", BuildExitSection()));
        var save = MakeButton("保存配置", SaveConfigurationAsync, "Primary");
        var saveBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 20 };
        Put(saveBar, Hint("提示词与超时仅影响新接收的任务。\n排队与运行中的任务保持原配置。"), 0);
        Put(saveBar, save, 0, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Put(root, new ScrollViewer { Content = panel }, 0);
        Put(root, new Border { Background = Brushes.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = saveBar }, 1);
        return root;
    }

    private static Border Divider() => new() { Height = 1, Background = Ui.Line };
    private static string ProjectName(string project) => project.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? project;
}
