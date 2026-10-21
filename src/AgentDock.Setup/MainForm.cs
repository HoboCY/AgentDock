namespace AgentDock.Setup;

/// <summary>简单安装向导：状态、安装/更新、启动、卸载与日志。</summary>
internal sealed class MainForm : Form
{
    private readonly InstallLayout _layout;
    private readonly Label _state = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 4), AutoSize = false };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
        Font = new Font("Consolas", 9F)
    };
    private readonly Button _install = new() { Text = "安装 / 应用更新...", AutoSize = true };
    private readonly Button _launch = new() { Text = "启动 AgentDock", AutoSize = true };
    private readonly Button _uninstall = new() { Text = "卸载（保留用户数据）", AutoSize = true };
    private readonly Button _close = new() { Text = "关闭", AutoSize = true };

    public MainForm(InstallLayout layout)
    {
        _layout = layout;
        Text = "AgentDock 安装程序";
        Width = 760;
        Height = 500;
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterScreen;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        buttons.Controls.AddRange(new Control[] { _install, _launch, _uninstall, _close });

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        table.Controls.Add(_state, 0, 0);
        table.Controls.Add(buttons, 0, 1);
        table.Controls.Add(_log, 0, 2);
        Controls.Add(table);

        _install.Click += (_, _) => BeginInstall();
        _launch.Click += (_, _) => Launch();
        _uninstall.Click += (_, _) => BeginUninstall();
        _close.Click += (_, _) => Close();

        Log($"安装根目录：{_layout.Root}");
        var payload = Installer.FindPayload();
        Log(payload == Payload.BundledSource ? "完整程序包已就绪，点击“安装 / 应用更新”即可。"
            : payload is null ? "请使用下载的完整安装程序安装或更新 AgentDock。" : $"找到安装包：{payload}");
        RefreshState();
    }

    private void BeginInstall()
    {
        var payload = Payload.HasBundledPayload ? null : Installer.FindPayload();
        if (payload is null && !Payload.HasBundledPayload)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "选择 AgentDock 程序包（维护用途）",
                Filter = "AgentDock 安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                Log("已取消：未选择安装包。");
                return;
            }
            payload = dialog.FileName;
        }

        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                var outcome = Installer.Install(_layout, payload, noShellIntegration: false, Log);
                Log(outcome.AlreadyInstalled
                    ? $"修复完成：版本 {outcome.Version}"
                    : $"安装完成：版本 {outcome.Version} -> {outcome.VersionDir}");
                foreach (var warning in outcome.Warnings) Log("警告：" + warning);
            }
            catch (Exception ex)
            {
                _layout.Log("安装失败：" + ex.Message);
                Log("安装失败：" + ex.Message);
            }
            finally
            {
                SafeInvoke(() =>
                {
                    SetBusy(false);
                    RefreshState();
                });
            }
        });
    }

    private void Launch()
    {
        try
        {
            Installer.Launch(_layout);
            Log("已请求启动 AgentDock 桌面程序。");
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex.Message);
        }
    }

    private void BeginUninstall()
    {
        var confirm = MessageBox.Show(
            $"确定卸载 AgentDock 吗？\n\n程序文件将被删除；用户数据保留在：\n{_layout.DataDir}",
            "AgentDock 卸载",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                Uninstaller.Uninstall(_layout, noShellIntegration: false, Log);
                Log("卸载完成。用户数据未删除。");
                SafeInvoke(Close);
            }
            catch (Exception ex)
            {
                Log("卸载失败：" + ex.Message);
                SafeInvoke(() => SetBusy(false));
            }
        });
    }

    private void RefreshState()
    {
        try
        {
            var current = _layout.ReadCurrent();
            _state.Text = current is null
                ? "状态：未安装"
                : $"状态：已安装版本 {current.Version}{Environment.NewLine}位置：{current.Root}{Environment.NewLine}数据：{_layout.DataDir}";
        }
        catch (Exception ex)
        {
            _state.Text = "状态：安装指针损坏——" + ex.Message;
        }
    }

    private void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _launch.Enabled = !busy;
        _uninstall.Enabled = !busy;
    }

    private void Log(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => Log(line));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在关闭。
            }
            return;
        }
        _log.AppendText(line + Environment.NewLine);
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException)
        {
            // 窗口正在关闭。
        }
    }
}
