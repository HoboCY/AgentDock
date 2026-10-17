using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;

namespace AgentDock.Desktop;

/// <summary>Shared presentation helpers; control templates come from ShadUI.</summary>
internal static class Ui
{
    public static readonly IBrush Ink = Brush("#20312C");
    public static readonly IBrush Muted = Brush("#697A73");
    public static readonly IBrush Line = Brush("#DEE6E1");
    public static readonly IBrush Canvas = Brush("#F6F8F6");
    public static readonly IBrush Accent = Brush("#256D60");
    public static readonly IBrush Sidebar = Brush("#182B28");

    public static Control Symbol(string kind, IBrush? color = null, double size = 18) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(kind switch
        {
            "tasks" => "M4,3 L20,3 20,21 4,21 Z M8,8 L9,8 M12,8 L17,8 M8,12 L9,12 M12,12 L17,12 M8,16 L9,16 M12,16 L17,16",
            "settings" => "M4,6 L20,6 M4,12 L20,12 M4,18 L20,18 M8,3 L8,9 M16,9 L16,15 M10,15 L10,21",
            "window" => "M3,4 L21,4 21,20 3,20 Z M3,9 L21,9 M14,9 L14,20",
            "play" => "M7,4 L20,12 7,20 Z",
            "refresh" => "M20,10 A8,8 0 1 0 20,16 M20,4 L20,10 14,10",
            "arrow" => "M9,5 L16,12 9,19",
            _ => "M5,12 L10,17 20,6"
        }),
        Stroke = color ?? Muted, StrokeThickness = 1.65,
        Width = size, Height = size, Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    public static void ConfigureWindow(ShadUI.Window window)
    {
        window.TitleBarAnimationEnabled = false;
        window.TemplateApplied += (_, args) =>
        {
            foreach (var (part, name) in new[]
                     { ("PART_MinimizeButton", "最小化"), ("PART_MaximizeButton", "最大化或还原"), ("PART_CloseButton", "关闭窗口") })
                if (args.NameScope.Find<Button>(part) is { } button)
                    AutomationProperties.SetName(button, name);
        };
    }

    public static TextBlock Text(string text, string? style = null)
    {
        var block = new TextBlock { Text = text };
        if (style is not null) block.Classes.Add(style);
        return block;
    }

    public static Button Button(string text, string variant = "Outline") => new()
    {
        Content = text,
        Classes = { variant },
        VerticalAlignment = VerticalAlignment.Center
    };

    public static ShadUI.Card Card(Control content, Thickness? padding = null) => new()
    {
        Content = content,
        Padding = padding ?? new Thickness(20),
        BorderBrush = Line,
        CornerRadius = new CornerRadius(10),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
}
