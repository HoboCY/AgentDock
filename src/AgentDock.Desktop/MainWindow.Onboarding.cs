using AgentDock.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AgentDock.Desktop;

public sealed partial class MainWindow
{
    private CodexIntegration _codex = null!;
    private GlobalRouting _routing = null!;
    private CodexConnection? _codexStatus;
    private RoutingStatus? _routingStatus;
    private readonly TextBlock _setupError = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("#B3261E"), IsVisible = false };
    private readonly TextBox _setupKey = new() { PasswordChar = '●', Watermark = "粘贴 OpenCode Go API Key", MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _setupBody = new() { Spacing = 10 };
    private readonly TextBlock[] _stepStates = Enumerable.Range(0, 4).Select(_ => Hint("待配置")).ToArray();
    private readonly Button[] _stepButtons = new Button[4];
    private readonly Button _setupNext = Ui.Button("启动后台", "Primary");
    private readonly Button _setupBack = Ui.Button("上一步");
    private readonly Button _setupSkip = Ui.Button("稍后配置", "Ghost");
    private readonly Button _setupRefresh = Ui.Button("重新检查", "Ghost");
    private readonly TextBlock _setupProgress = Hint("第 1 步，共 4 步");
    private bool _setupBusy;
    private int _setupStep;
    private TabItem _setupTab = null!;

    private void InitializeOnboarding()
    {
        _codex = new CodexIntegration(_state.Api.Client.Home);
        _routing = new GlobalRouting(_codex.CodexHome, _state.Api.Client.Home);
        _routingStatus = _routing.UpgradeEnabled();
    }

    private Control BuildOnboarding()
    {
        AutomationProperties.SetName(_setupKey, "引导 OpenCode Go 凭据");
        _setupNext.Click += async (_, _) => await RunSetupActionAsync(AdvanceSetupAsync);
        _setupRefresh.Click += async (_, _) => await RunSetupActionAsync(() => CheckSetupAsync(false));
        _setupBack.Click += (_, _) => { _setupStep = Math.Max(0, _setupStep - 1); RenderSetup(); };
        _setupSkip.Click += (_, _) => { _setupKey.Text = ""; _pages.SelectedIndex = 0; };
        var steps = new UniformGrid { Columns = 4, Rows = 1 };
        var titles = new[] { "启动后台", "保存凭据", "接入 Codex", "启用委派" };
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            var button = Ui.Button($"{i + 1}  {titles[i]}", "Ghost");
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) => { _setupStep = index; RenderSetup(); };
            _stepButtons[i] = button;
            steps.Children.Add(new StackPanel { Spacing = 8, Children = { button, _stepStates[i] } });
        }
        var body = new StackPanel
        {
            Spacing = 12, Margin = new Thickness(14, 12), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { Ui.Text("开始使用", "pageTitle"),
                steps, Divider(), _setupProgress, _setupBody, _setupError }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _setupBack, _setupNext } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        Put(footer, new StackPanel { Orientation = Orientation.Horizontal, Children = { _setupSkip, _setupRefresh } }, 0);
        Put(footer, buttons, 0, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Put(root, new ScrollViewer { Content = body }, 0);
        Put(root, new Border { Background = Brushes.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 8), Child = footer }, 1);
        RenderSetup();
        return root;
    }

    private async Task OpenSetupAsync()
    {
        _pages.SelectedItem = _setupTab;
        await RunSetupActionAsync(() => CheckSetupAsync(true));
    }

    private async Task CheckSetupAsync(bool chooseStep)
    {
        await _state.PollAsync();
        if (_state.HostReachable) ApplyCredentialStatus(await _state.Api.GetConfigurationAsync(_state.Token));
        _routingStatus = _routing.UpgradeEnabled();
        _codexStatus = null;
        try { _codexStatus = await _codex.InspectAsync(_state.Token); }
        finally
        {
            if (chooseStep) _setupStep = !_state.HostReachable ? 0 : _state.Configuration?.CredentialConfigured != true ? 1 : _codexStatus?.Registered != true ? 2 : 3;
            RenderSetup();
        }
    }

    private async Task AdvanceSetupAsync()
    {
        switch (_setupStep)
        {
            case 0:
                if (await _state.StartHostAsync() is null) throw new InvalidOperationException(_state.LastError ?? "后台未能启动，请检查安装后重试。");
                _setupStep = 1;
                break;
            case 1:
                if (!string.IsNullOrWhiteSpace(_setupKey.Text))
                {
                    var view = await _state.Api.SetCredentialAsync(_setupKey.Text, _state.Token);
                    _setupKey.Text = "";
                    ApplyCredentialStatus(view);
                }
                if (_state.Configuration?.CredentialConfigured != true) throw new InvalidOperationException("请输入并保存 OpenCode Go API Key。");
                _setupStep = 2;
                break;
            case 2:
                if (_codexStatus?.Registered != true) _codexStatus = await _codex.RegisterAsync(_state.Token);
                _setupStep = 3;
                break;
            case 3:
                await CheckSetupAsync(false);
                if (!_state.HostReachable || _state.Configuration?.CredentialConfigured != true || _codexStatus?.Registered != true)
                    throw new InvalidOperationException("请先完成后台、凭据和 Codex 接入步骤。");
                _routingStatus = _routing.Enable();
                if (!_routingStatus.Enabled || _routingStatus.Problem is not null)
                    throw new InvalidOperationException(_routingStatus.Problem ?? "全局规则未生效，请刷新后检查。");
                OnboardingProgress.Complete(_state.Api.Client.Home);
                _pages.SelectedIndex = 0;
                ShowNotice("配置完成。请重启 Codex 并新建对话，让插件和全局委派规则生效。凭据有效性以实际任务结果为准。", false);
                break;
        }
    }

    private async Task RunSetupActionAsync(Func<Task> action)
    {
        if (_setupBusy) return;
        _setupBusy = true;
        _setupError.IsVisible = false;
        RenderSetup();
        try { await action(); }
        catch (Exception error)
        {
            _setupError.Text = error is InvalidOperationException ? error.Message : AppState.Describe(error);
            _setupError.IsVisible = true;
        }
        finally { _setupBusy = false; RenderSetup(); }
    }

    private void RenderSetup()
    {
        if (_stepButtons[0] is null) return;
        UpdateSetupStatus();
        _setupBody.Children.Clear();
        RenderSetupBody(SetupReadiness());
    }

    private bool[] SetupReadiness() => [_state.HostReachable, _state.Configuration?.CredentialConfigured == true, _codexStatus?.Registered == true, _routingStatus?.Enabled == true];

    private void UpdateSetupStatus()
    {
        if (_stepButtons[0] is null) return;
        var ready = SetupReadiness();
        for (var i = 0; i < 4; i++)
        {
            _stepStates[i].Text = ready[i] ? i == 1 ? "已保存 · 待实际任务验证" : i == 2 ? "已注册并启用" : "已完成" : "待配置";
            _stepStates[i].Foreground = ready[i] ? Ui.Accent : Ui.Muted;
            _stepButtons[i].IsEnabled = !_setupBusy;
        }
        _setupProgress.Text = $"第 {_setupStep + 1} 步，共 4 步" + (_setupBusy ? " · 正在处理…" : "");
        _setupNext.IsEnabled = !_setupBusy && (_setupStep != 1 || ready[0]) && (_setupStep != 3 || ready[0] && ready[1] && ready[2]);
        _setupKey.IsEnabled = !_setupBusy;
        _setupBack.IsEnabled = !_setupBusy && _setupStep > 0;
        _setupSkip.IsEnabled = _setupRefresh.IsEnabled = !_setupBusy;
        _setupNext.Content = new[] { ready[0] ? "继续" : "启动后台", "保存并继续", ready[2] ? "继续" : "接入 Codex 并继续", ready[3] ? "完成设置" : "启用全局委派并完成" }[_setupStep];
    }

    private void RenderSetupBody(bool[] ready)
    {
        switch (_setupStep)
        {
            case 0:
                _setupBody.Children.Add(Ui.Text("让任务在后台独立运行", "sectionTitle"));
                _setupBody.Children.Add(Hint("关闭主面板后，已接受的任务仍会继续。后台只在你启动或 Codex 调用时运行。"));
                _setupBody.Children.Add(Hint(ready[0] ? "后台已连接，可以继续。" : "点击下方「启动后台」，连接成功后进入下一步。"));
                break;
            case 1:
                _setupBody.Children.Add(Ui.Text("配置 OpenCode Go 凭据", "sectionTitle"));
                _setupBody.Children.Add(Hint("使用你在 OpenCode Go 获取的 API Key。凭据在本机保护存储，保存后输入框会清空。"));
                _setupBody.Children.Add(Hint(ready[1] ? "凭据已保存。可直接继续，也可输入新凭据替换。" : "凭据尚未配置。"));
                _setupBody.Children.Add(_setupKey);
                _setupBody.Children.Add(Hint("保存成功仅确认本机存储；是否有效、是否过期，需要实际任务验证。此步骤不会发送模型请求。"));
                if (!ready[0]) _setupBody.Children.Add(Hint("请先返回第 1 步启动后台。"));
                break;
            case 2:
                _setupBody.Children.Add(Ui.Text("让 Codex 找到 AgentDock", "sectionTitle"));
                _setupBody.Children.Add(Hint("点击下方按钮注册并启用本机插件。完成后，重启 ChatGPT / Codex 桌面 App，再新建对话验证 AgentDock 工具。"));
                _setupBody.Children.Add(Hint("此步骤通过 Codex 命令工具配置插件；日常仍在桌面 App 中使用。"));
                _setupBody.Children.Add(Hint(_codexStatus?.Message ?? (_codex.Executable is null ? "未找到 Codex，可选择已安装的程序文件。" : "已找到 Codex，点击接入或重新检查。")));
                _setupBody.Children.Add(Hint("插件配置工具：" + (_codex.Executable ?? "未找到")));
                var choose = Ui.Button(OperatingSystem.IsMacOS() ? "选择应用或 Codex 命令文件…" : "选择 Codex 程序…");
                choose.IsEnabled = !_setupBusy;
                choose.Click += async (_, _) => await RunSetupActionAsync(async () =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = OperatingSystem.IsMacOS() ? "选择 ChatGPT.app、Codex.app 或 codex 命令文件" : "选择 Codex 程序",
                        AllowMultiple = false,
                        FileTypeFilter = OperatingSystem.IsMacOS()
                            ? [new FilePickerFileType("ChatGPT / Codex 应用或命令文件")
                                { AppleUniformTypeIdentifiers = ["com.apple.application-bundle", "public.unix-executable", "public.script"] }, FilePickerFileTypes.All]
                            : null,
                    });
                    if (files.Count == 0) return;
                    _codex.Executable = files[0].TryGetLocalPath();
                    _codexStatus = await _codex.InspectAsync(_state.Token);
                });
                _setupBody.Children.Add(choose);
                if (OperatingSystem.IsMacOS())
                    _setupBody.Children.Add(Hint("应用包含 Codex 命令工具时可直接选择 ChatGPT.app 或 Codex.app；也可选择已安装的 codex 命令文件。"));
                _setupBody.Children.Add(Hint("Codex 配置目录：" + _codex.CodexHome));
                break;
            case 3:
                _setupBody.Children.Add(Ui.Text("让合适的子任务自动委派", "sectionTitle"));
                _setupBody.Children.Add(Hint("启用后，对所有项目生效：边界清晰、可独立完成的调查、实现、审查和验证任务通过 AgentDock 执行。主代理负责规划、整合和最终验收。"));
                _setupBody.Children.Add(Hint("保留主模型及登录方式，架构判断仍用原生 architect。浏览器、桌面、应用操作和简单任务由主代理处理。遵守各项目限制，失败时不自动切换模型或重复派发。"));
                _setupBody.Children.Add(Hint("规则位置：" + _routingStatus?.Path));
                if (_routingStatus?.Problem is { } problem) _setupBody.Children.Add(Hint(problem));
                _setupBody.Children.Add(new Expander { Header = "查看将写入的规则", Content = new TextBox { Text = GlobalRouting.Instructions, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 160 } });
                _setupBody.Children.Add(Hint("生效方式：重启 Codex 并新建对话。项目规则仍可能限制委派；此处不会修改主模型、认证和其他规则。"));
                if (ready[3])
                {
                    var disable = Ui.Button("关闭全局委派并恢复原规则");
                    disable.IsEnabled = !_setupBusy;
                    disable.Click += async (_, _) => await RunSetupActionAsync(() =>
                    {
                        _routingStatus = _routing.Disable();
                        ShowNotice("全局委派已关闭，原区块已恢复。请重启 Codex 并新建对话。", false);
                        return Task.CompletedTask;
                    });
                    _setupBody.Children.Add(disable);
                }
                break;
        }
    }

    // Refresh credential status without resetting unsaved role/settings edits.
    private void ApplyCredentialStatus(ConfigurationView view)
    {
        if (_appliedView is null) ApplyConfigurationToUi(view);
        _appliedView = view;
        _state.ApplyConfiguration(view);
    }
}
