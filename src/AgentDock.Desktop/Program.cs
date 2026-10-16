using Avalonia;

namespace AgentDock.Desktop;

public static class Program
{
    public static string? HomeOverride { get; private set; }
    public static string? SmokeOutputPath { get; private set; }
    public static bool TrayOnly { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tray": TrayOnly = true; break;
                case "--home" when i + 1 < args.Length: HomeOverride = args[++i]; break;
                case "--smoke-test" when i + 1 < args.Length: SmokeOutputPath = args[++i]; break;
            }
        }
        // 冒烟模式必须真实显示窗口，因此覆盖 --tray。
        if (SmokeOutputPath is not null) TrayOnly = false;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
