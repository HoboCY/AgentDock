using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AgentDock.Desktop;

/// <summary>用代码绘制图标（圆角底 + A），不加载外部资源。</summary>
public static class Icons
{
    private static WindowIcon? _cached;

    public static WindowIcon Create()
    {
        if (_cached is not null) return _cached;
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var context = bitmap.CreateDrawingContext())
        {
            var background = new SolidColorBrush(Color.Parse("#2F6FEB"));
            context.DrawRectangle(background, null, new Rect(1, 1, 30, 30), 7, 7);
            var text = new FormattedText("A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 19, Brushes.White);
            context.DrawText(text, new Point(16 - text.Width / 2, 16 - text.Height / 2));
        }
        _cached = new WindowIcon(bitmap);
        return _cached;
    }
}
