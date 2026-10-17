using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Automation;

namespace AgentDock.Desktop;

/// <summary>有界 UI 集成冒烟：真实显示/隐藏主面板与悬浮窗并记录资源指标；不提交任务、不依赖后台。</summary>
public static class SmokeTest
{
    private static readonly object Gate = new();
    private static bool _finished;

    public static void Schedule(App app, string outputPath) =>
        Dispatcher.UIThread.Post(() => _ = RunAsync(app, outputPath));

    private static async Task RunAsync(App app, string outputPath)
    {
        var sampleSeconds = int.TryParse(Environment.GetEnvironmentVariable("AGENTDOCK_SMOKE_SAMPLE_SECONDS"), out var requested)
            ? Math.Clamp(requested, 3, 120) : 3;
        _ = Task.Run(async () =>
        {
            await Task.Delay((sampleSeconds + 30) * 1000).ConfigureAwait(false);
            lock (Gate)
            {
                if (_finished) return;
                _finished = true;
            }
            Write(outputPath, new { mode = "smoke", ok = false, error = "watchdog-timeout", at = DateTimeOffset.Now });
            Environment.Exit(3);
        });

        var stages = new List<object>();
        var ok = true;
        string? error = null;
        var mainShown = false;
        var mainHidden = false;
        var overlayShown = false;
        var overlayHidden = false;
        var mainReopened = false;
        double? idleCpuPercent = null;
        var pageChecks = new List<string>();
        try
        {
            if (!app.Main.IsVisible) app.Main.Show();
            await Task.Delay(900);
            mainShown = app.Main.IsVisible;
            Capture(app.Main, outputPath + ".main.png");
            stages.Add(Sample("main-shown"));

            await CheckPagesAsync(app, outputPath, pageChecks);

            app.Main.Hide();
            await Task.Delay(300);
            mainHidden = !app.Main.IsVisible;
            stages.Add(Sample("main-hidden"));

            app.ShowOverlay();
            await Task.Delay(500);
            overlayShown = app.Overlay.IsVisible;
            Capture(app.Overlay, outputPath + ".overlay.png");
            stages.Add(Sample("overlay-shown"));

            app.Overlay.Hide();
            await Task.Delay(300);
            overlayHidden = !app.Overlay.IsVisible;
            stages.Add(Sample("overlay-hidden"));
            app.ShowMain();
            await Task.Delay(300);
            mainReopened = app.Main.IsVisible;
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            await Task.Delay(sampleSeconds * 1000);
            process.Refresh();
            idleCpuPercent = (process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
            stages.Add(Sample("main-idle"));
            ok = mainShown && mainHidden && mainReopened && overlayShown && overlayHidden;
            if (!ok) error = "窗口可见性断言未通过。";
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.GetType().Name + ": " + ex.Message;
        }

        var payload = new
        {
            mode = "smoke",
            ok,
            error,
            at = DateTimeOffset.Now,
            hostRequired = false,
            hostReachable = app.State.HostReachable,
            taskCount = app.State.Tasks.Length,
            trayCreated = app.TrayCreated,
            mainOpenedMilliseconds = app.MainOpenedMilliseconds,
            idleCpuPercent,
            sampleSeconds,
            pageChecks,
            process = ProcessInfo(),
            stages,
            windows = new
            {
                mainShown,
                mainHidden,
                mainReopened,
                mainWidth = app.Main.Bounds.Width,
                mainHeight = app.Main.Bounds.Height,
                overlayShown,
                overlayHidden,
                overlayRequestedWidth = app.Overlay.Width,
                overlayRequestedHeight = app.Overlay.Height,
                overlayWidth = app.Overlay.Bounds.Width,
                overlayHeight = app.Overlay.Bounds.Height,
                overlayTopmost = app.Overlay.Topmost,
                overlayOpacity = app.Overlay.Opacity
            }
        };

        lock (Gate)
        {
            if (_finished) return;
            _finished = true;
        }
        Write(outputPath, payload);
        app.ShutdownForSmoke(ok ? 0 : 2);
    }

    private static void Capture(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(path);
    }

    private static async Task CheckPagesAsync(App app, string outputPath, List<string> checks)
    {
        var main = app.Main;
        var width = main.Width;
        var height = main.Height;
        var minimumHeight = main.MinHeight;
        var tabs = main.GetVisualDescendants().OfType<TabControl>().Single();
        foreach (var tab in tabs.Items.Cast<TabItem>())
            if (Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(tab)?.GetName() != tab.Header as string)
                throw new InvalidOperationException("Navigation tabs must expose their visible labels to accessibility tools.");
        checks.Add("navigation-tabs-named");
        CheckTaskRows(main);
        checks.Add("task-cards-fit-workspace");
        var tasks = main.GetVisualDescendants().OfType<ListBox>().Single();
        if (tasks.ItemCount > 0)
        {
            tasks.SelectedIndex = 0;
            await Task.Delay(150);
            CheckTaskRows(main);
            CheckInspector(main, (AgentDock.Core.TaskSummary)tasks.SelectedItem!);
            if (tasks.ItemCount > 1)
            {
                tasks.SelectedIndex = 1;
                await Task.Delay(150);
                CheckInspector(main, (AgentDock.Core.TaskSummary)tasks.SelectedItem!);
                tasks.SelectedIndex = 0;
                await Task.Delay(150);
            }
            Capture(main, outputPath + ".selected.png");
            checks.Add("selection-updates-inspector");
            var filter = main.GetVisualDescendants().OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "项目筛选");
            if (filter.ItemCount > 2)
            {
                filter.SelectedIndex = filter.ItemCount - 1;
                await Task.Delay(150);
                var project = (string)filter.SelectedItem!;
                var filtered = tasks.Items.Cast<AgentDock.Core.TaskSummary>().ToArray();
                if (filtered.Length == 0 || filtered.Any(task => task.Project != project))
                    throw new InvalidOperationException("Project filter must restrict the list to the selected project.");
                CheckInspector(main, (AgentDock.Core.TaskSummary)tasks.SelectedItem!);
                filter.SelectedIndex = 0;
                await Task.Delay(150);
                tasks.SelectedIndex = 0;
                tasks.ScrollIntoView(tasks.SelectedItem!);
                await Task.Delay(150);
                checks.Add("project-filter-updates-list-and-selection");
            }
        }

        main.Width = Math.Min(width, 900);
        main.Height = Math.Min(height, 600);
        await Task.Delay(350);
        Capture(main, outputPath + ".compact.png");
        CheckTaskRows(main);
        var inspector = main.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "TaskInspector");
        if (inspector.IsVisible) throw new InvalidOperationException("Compact workspace must give the task list room by collapsing the inspector.");
        foreach (var label in new[] { "查看结果", "停止所选" })
        {
            var button = main.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Content, label) && control.IsEffectivelyVisible);
            var point = button.TranslatePoint(default, main);
            if (point is not { } p || p.X < 200 || p.X + button.Bounds.Width > main.Bounds.Width || p.Y + button.Bounds.Height > main.Bounds.Height)
                throw new InvalidOperationException("Compact task actions must remain inside the workspace.");
        }
        checks.Add("compact-inspector-collapsed-actions-visible");

        // Model the reduced logical workspace available at high DPI without
        // changing the user's monitor scaling setting.
        main.MinHeight = Math.Min(minimumHeight, 480);
        main.Height = Math.Min(height, 480);
        await Task.Delay(250);
        if (tasks.Bounds.Height < 64)
            throw new InvalidOperationException("Task list must remain usable in a short logical workspace.");
        Capture(main, outputPath + ".short.png");
        checks.Add("short-workspace-task-list-visible");
        main.MinHeight = minimumHeight;

        main.Width = width;
        main.Height = height;
        tabs.SelectedIndex = 1;
        await Task.Delay(350);
        Capture(main, outputPath + ".settings.png");
        var expanders = main.GetVisualDescendants().OfType<Expander>().ToArray();
        foreach (var expander in expanders) expander.IsExpanded = true;
        await Task.Delay(350);
        var numeric = main.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        if (numeric.Length != 6 || numeric.Any(control => !control.GetVisualDescendants().OfType<TextBox>()
                .Any(editor => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(editor)))))
            throw new InvalidOperationException("Six numeric editors must retain their accessible names.");
        checks.Add("six-numeric-editors-named");
        expanders.First().BringIntoView();
        await Task.Delay(200);
        Capture(main, outputPath + ".roles.png");
        foreach (var expander in expanders) expander.IsExpanded = false;
        await Task.Delay(650);
        if (expanders.Any(expander => expander.Bounds.Height > 80))
            throw new InvalidOperationException("Collapsed settings groups must release their editor height.");
        checks.Add("collapsed-settings-release-space");

        main.Width = Math.Min(width, 900);
        main.Height = Math.Min(height, 600);
        var settingsScroll = main.GetVisualDescendants().OfType<ScrollViewer>().First(viewer => viewer.Content is StackPanel);
        settingsScroll.Offset = default;
        await Task.Delay(500);
        var save = main.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "保存配置"));
        var savePosition = save.TranslatePoint(default, main);
        if (savePosition is not { } position || position.Y + save.Bounds.Height > main.Bounds.Height || position.X < 0)
            throw new InvalidOperationException("Save configuration must stay visible at the compact window size.");
        Capture(main, outputPath + ".settings-compact.png");
        checks.Add("compact-settings-save-visible");
        var sections = main.GetVisualDescendants().OfType<Control>().Where(control => control.Name is "ConnectionSettings" or "CredentialSettings" or "ConcurrencySettings" or "RoleSettings").ToDictionary(control => control.Name!);
        double Bottom(Control control) => control.TranslatePoint(default, main)!.Value.Y + control.Bounds.Height;
        var connectionsBottom = Bottom(sections["ConnectionSettings"]);
        var concurrencyBottom = Bottom(sections["ConcurrencySettings"]);
        var rolesTop = sections["RoleSettings"].TranslatePoint(default, main)!.Value.Y;
        if (concurrencyBottom > connectionsBottom + 1 || connectionsBottom > rolesTop + 1)
            throw new InvalidOperationException($"Settings sections overlap: connections bottom {connectionsBottom}, concurrency bottom {concurrencyBottom}, roles top {rolesTop}.");
        foreach (var (name, card) in new[] { ("OpenCode Go 凭据", "CredentialSettings"), ("全局并发上限", "ConcurrencySettings") })
        {
            var editor = main.GetVisualDescendants().OfType<Control>().First(control => AutomationProperties.GetName(control) == name);
            if (Bottom(editor) > Bottom(sections[card]) - 16)
                throw new InvalidOperationException($"Settings editor {name} is clipped by its card.");
        }
        checks.Add("compact-settings-sections-do-not-overlap");
        main.Width = width;
        main.Height = height;
        tabs.SelectedIndex = 0;

        if (app.State.Tasks.FirstOrDefault() is { } task)
        {
            var result = new ResultWindow(task.Id, app.State);
            try
            {
                result.Show(main);
                await Task.Delay(500);
                Capture(result, outputPath + ".result.png");
                var editor = result.GetVisualDescendants().OfType<TextBox>().Single();
                var viewport = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
                if (viewport.Bounds.Height < editor.Bounds.Height - 8)
                    throw new InvalidOperationException("The result editor must fill its available height.");
                checks.Add("result-window-opened");
            }
            finally { result.Close(); }
        }
    }

    private static void CheckTaskRows(Window window)
    {
        foreach (var row in window.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "TaskDataRow"))
        {
            var point = row.TranslatePoint(default, window);
            if (row.Bounds.Height < 48 || point is not { } p || p.X < 200 || p.X + row.Bounds.Width > window.Bounds.Width)
                throw new InvalidOperationException("Two-line task cards must fit within the workspace.");
        }
    }

    private static void CheckInspector(Window window, AgentDock.Core.TaskSummary task)
    {
        var text = window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Name is not null).ToDictionary(block => block.Name!);
        if (text["SelectedTaskTitle"].Text != task.Title || text["SelectedTaskProject"].Text != task.Project ||
            text["SelectedTaskInput"].Text != Fmt.Tokens(task.InputTokens) || text["SelectedTaskOutput"].Text != Fmt.Tokens(task.OutputTokens))
            throw new InvalidOperationException("Task inspector must show the selected task and reported usage.");
    }

    private static object Sample(string name)
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            name,
            at = DateTimeOffset.Now,
            elapsedFromProcessStartMs = SafeMillis(process),
            workingSetBytes = SafeLong(() => process.WorkingSet64),
            privateBytes = SafeLong(() => process.PrivateMemorySize64),
            peakWorkingSetBytes = SafeLong(() => process.PeakWorkingSet64),
            threads = SafeLong(() => (long)process.Threads.Count),
            managedBytes = GC.GetTotalMemory(false)
        };
    }

    private static object ProcessInfo()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            id = Environment.ProcessId,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            managedBytesAtStart = GC.GetTotalMemory(false),
            peakWorkingSetBytes = SafeLong(() => process.PeakWorkingSet64)
        };
    }

    private static long? SafeMillis(Process process)
    {
        try { return (long)(DateTime.Now - process.StartTime).TotalMilliseconds; }
        catch { return null; }
    }

    private static long SafeLong(Func<long> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private static void Write(string path, object payload)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
