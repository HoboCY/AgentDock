using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentDock.Desktop;

internal static class WindowPlacement
{
    public static void KeepOnScreen(Window window)
    {
        // This acceptance fix is scoped to the Windows windowing backend.
        if (!OperatingSystem.IsWindows()) return;
        var minimumWidth = window.MinWidth;
        var minimumHeight = window.MinHeight;
        void Schedule(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
        {
            if (!window.IsVisible || window.WindowState != WindowState.Normal) return;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var frame = window.FrameSize ?? window.ClientSize;
            var borderWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
            var borderHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
            var width = Math.Max(1, area.Width / scale - borderWidth - 16);
            var height = Math.Max(1, area.Height / scale - borderHeight - 16);
            window.MinWidth = Math.Min(minimumWidth, width);
            window.MinHeight = Math.Min(minimumHeight, height);
            window.Width = Math.Min(window.ClientSize.Width, width);
            window.Height = Math.Min(window.ClientSize.Height, height);
            var pixelWidth = (int)Math.Ceiling((window.Width + borderWidth) * scale);
            var pixelHeight = (int)Math.Ceiling((window.Height + borderHeight) * scale);
            window.Position = new PixelPoint(
                Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - pixelWidth)),
                Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - pixelHeight)));
        }, DispatcherPriority.Loaded);

        window.Opened += Schedule;
        window.ScalingChanged += Schedule;
        window.Screens.Changed += Schedule;
        window.Closed += (_, _) => window.Screens.Changed -= Schedule;
    }

    public static void BottomRight(Window window, double margin)
    {
        if (!OperatingSystem.IsWindows())
        {
            var primaryArea = window.Screens.Primary?.WorkingArea ?? default;
            if (primaryArea.Width <= 0) return;
            var height = window.Bounds.Height > 10 ? window.Bounds.Height : 64;
            window.Position = new PixelPoint(
                primaryArea.Right - (int)window.Width - (int)margin,
                primaryArea.Bottom - (int)height - (int)margin);
            return;
        }
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var size = window.FrameSize ?? window.ClientSize;
        window.Position = new PixelPoint(
            Math.Max(area.X, area.Right - (int)Math.Ceiling((size.Width + margin) * scale)),
            Math.Max(area.Y, area.Bottom - (int)Math.Ceiling((size.Height + margin) * scale)));
    }
}
