using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

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
        try
        {
            if (!app.Main.IsVisible) app.Main.Show();
            await Task.Delay(900);
            mainShown = app.Main.IsVisible;
            Capture(app.Main, outputPath + ".main.png");
            stages.Add(Sample("main-shown"));

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
