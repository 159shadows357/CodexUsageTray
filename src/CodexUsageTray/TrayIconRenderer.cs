using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace CodexUsageTray;

public enum TrayIconStyle
{
    Ring,
    SolidBar,
    SegmentedBar,
    CodexPercent
}

public static class TrayIconRenderer
{
    private static readonly int[] SupportedSizes = [16, 20, 24, 32];
    private static readonly Color OutlineColor = Color.FromArgb(245, 245, 245);
    private static readonly Color TrackColor = Color.FromArgb(255, 38, 42, 50);
    private static readonly Color BackingColor = Color.FromArgb(255, 20, 23, 29);

    public static Icon Create(ConnectionStatus status, int? remainingPercent, TrayIconStyle style = TrayIconStyle.Ring)
    {
        var bytes = CreateIcoBytes(status, remainingPercent, style);
        using var stream = new MemoryStream(bytes, writable: false);
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    internal static byte[] CreateIcoBytes(ConnectionStatus status, int? remainingPercent, TrayIconStyle style)
    {
        var frames = new List<(int Size, byte[] Png)>(SupportedSizes.Length);
        foreach (var size in SupportedSizes)
        {
            using var bitmap = RenderFrame(status, remainingPercent, style, size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            frames.Add((size, png.ToArray()));
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        var imageOffset = 6 + (16 * frames.Count);
        foreach (var frame in frames)
        {
            writer.Write((byte)frame.Size);
            writer.Write((byte)frame.Size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frame.Png.Length);
            writer.Write(imageOffset);
            imageOffset += frame.Png.Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame.Png);
        }

        writer.Flush();
        return output.ToArray();
    }

    internal static Bitmap RenderFrame(ConnectionStatus status, int? remainingPercent, TrayIconStyle style, int size)
    {
        var geometry = Geometry.For(size);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = style == TrayIconStyle.Ring ? SmoothingMode.AntiAlias : SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        var knownQuota = remainingPercent.HasValue && status != ConnectionStatus.NotSignedIn;
        var remaining = status == ConnectionStatus.RateLimitReached
            ? 0
            : Math.Clamp(remainingPercent ?? 0, 0, 100);
        var color = StatusColor(status, remainingPercent);

        switch (style)
        {
            case TrayIconStyle.Ring:
                DrawRing(graphics, geometry.Ring, status, knownQuota, remaining, color, size);
                break;
            case TrayIconStyle.SolidBar:
                DrawSolidBar(graphics, geometry.BarBody, geometry.BarInner, status, knownQuota, remaining, color);
                break;
            case TrayIconStyle.SegmentedBar:
                DrawSegmentedBar(graphics, geometry.BarInner, status, knownQuota, remaining, color);
                break;
            case TrayIconStyle.CodexPercent:
                DrawPercentOnly(graphics, geometry.TextBody, status, knownQuota, remaining, color);
                break;
        }

        if (style == TrayIconStyle.CodexPercent && status == ConnectionStatus.DataStale)
        {
            DrawStaleBadge(graphics, geometry.TextBody, size);
        }
        else if (style != TrayIconStyle.CodexPercent)
        {
            var markBounds = style == TrayIconStyle.Ring ? geometry.Ring : geometry.BarBody;
            switch (status)
            {
                case ConnectionStatus.DataStale:
                    DrawStaleBadge(graphics, markBounds, size);
                    break;
                case ConnectionStatus.NotSignedIn:
                    DrawLock(graphics, markBounds, size);
                    break;
                case ConnectionStatus.CliNotFound:
                case ConnectionStatus.AppServerUnavailable:
                case ConnectionStatus.RateLimitReached:
                    DrawExclamation(graphics, markBounds, size);
                    break;
            }
        }

        return bitmap;
    }

    internal static int FillWidth(int innerWidth, int remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        return remaining == 0
            ? 0
            : Math.Max(1, (int)Math.Round(innerWidth * remaining / 100d, MidpointRounding.AwayFromZero));
    }

    private static void DrawRing(Graphics graphics, Rectangle bounds, ConnectionStatus status, bool knownQuota, int remaining, Color color, int size)
    {
        var stroke = size switch { <= 20 => 2f, 24 => 3f, _ => 4f };
        using var backing = new SolidBrush(BackingColor);
        using var track = new Pen(Color.FromArgb(180, OutlineColor), stroke);
        using var progress = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.FillEllipse(backing, bounds);
        graphics.DrawArc(track, bounds, -90, 360);

        if (status == ConnectionStatus.Loading || (status == ConnectionStatus.Connected && !knownQuota))
        {
            graphics.DrawArc(progress, bounds, -65, 110);
            return;
        }
        if (!knownQuota || remaining == 0) return;
        graphics.DrawArc(progress, bounds, -90, Math.Max(8, 360f * remaining / 100f));
    }

    private static void DrawSolidBar(Graphics graphics, Rectangle body, Rectangle inner, ConnectionStatus status, bool knownQuota, int remaining, Color color)
    {
        using var backing = new SolidBrush(BackingColor);
        using var track = new SolidBrush(TrackColor);
        using var outline = new Pen(OutlineColor, 1);
        using var fill = new SolidBrush(color);
        graphics.FillRectangle(backing, body);
        graphics.FillRectangle(track, inner);
        graphics.DrawRectangle(outline, body.X, body.Y, body.Width - 1, body.Height - 1);

        var width = status == ConnectionStatus.Loading || (status == ConnectionStatus.Connected && !knownQuota)
            ? Math.Max(2, inner.Width / 3)
            : knownQuota ? FillWidth(inner.Width, remaining) : 0;
        var x = status == ConnectionStatus.Loading || (status == ConnectionStatus.Connected && !knownQuota)
            ? inner.X + ((inner.Width - width) / 2)
            : inner.X;
        if (width > 0) graphics.FillRectangle(fill, x, inner.Y, width, inner.Height);
    }

    private static void DrawSegmentedBar(Graphics graphics, Rectangle inner, ConnectionStatus status, bool knownQuota, int remaining, Color color)
    {
        const int segmentCount = 4;
        const int gap = 1;
        using var track = new SolidBrush(TrackColor);
        using var fill = new SolidBrush(color);
        var fillEnd = inner.X + FillWidth(inner.Width, remaining);
        var available = inner.Width - ((segmentCount - 1) * gap);
        var baseWidth = Math.Max(1, available / segmentCount);
        var extra = Math.Max(0, available - (baseWidth * segmentCount));
        var x = inner.X;

        for (var index = 0; index < segmentCount; index++)
        {
            var segmentWidth = baseWidth + (index < extra ? 1 : 0);
            graphics.FillRectangle(track, x, inner.Y, segmentWidth, inner.Height);
            var indeterminate = status == ConnectionStatus.Loading || (status == ConnectionStatus.Connected && !knownQuota);
            var visibleWidth = indeterminate
                ? (index is 1 or 2 ? segmentWidth : 0)
                : knownQuota ? Math.Min(segmentWidth, Math.Max(0, fillEnd - x)) : 0;
            if (visibleWidth > 0) graphics.FillRectangle(fill, x, inner.Y, visibleWidth, inner.Height);
            x += segmentWidth + gap;
        }
    }

    private static void DrawPercentOnly(Graphics graphics, Rectangle bounds, ConnectionStatus status, bool knownQuota, int remaining, Color color)
    {
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var valueBrush = new SolidBrush(color);
        using var outlineBrush = new SolidBrush(Color.FromArgb(230, 7, 10, 14));
        var valueBounds = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var value = FormatPercentOnlyValue(status, knownQuota, remaining);

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var valueFont = CreateFittedFont(graphics, value, valueBounds, format);
        foreach (var offset in new[] { new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1) })
        {
            var outlineBounds = new RectangleF(
                valueBounds.X + offset.X,
                valueBounds.Y + offset.Y,
                valueBounds.Width,
                valueBounds.Height);
            graphics.DrawString(value, valueFont, outlineBrush, outlineBounds, format);
        }
        graphics.DrawString(value, valueFont, valueBrush, valueBounds, format);
    }

    internal static string FormatPercentOnlyValue(ConnectionStatus status, bool knownQuota, int remaining) => status switch
    {
        ConnectionStatus.Loading => "...",
        ConnectionStatus.NotSignedIn => "LOCK",
        ConnectionStatus.CliNotFound or ConnectionStatus.AppServerUnavailable => "!",
        ConnectionStatus.RateLimitReached => "0",
        _ when knownQuota => Math.Clamp(remaining, 0, 100).ToString(CultureInfo.InvariantCulture),
        _ => "--"
    };

    private static Font CreateFittedFont(Graphics graphics, string text, RectangleF bounds, StringFormat format)
    {
        var size = Math.Max(3f, bounds.Height + 1);
        while (size > 3f)
        {
            var candidate = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(text, candidate, int.MaxValue, format);
            if (measured.Width <= bounds.Width && measured.Height <= bounds.Height + 1) return candidate;
            candidate.Dispose();
            size -= 0.5f;
        }

        return new Font("Segoe UI", 3f, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static void DrawStaleBadge(Graphics graphics, Rectangle bounds, int size)
    {
        var badgeSize = size >= 24 ? 4 : 3;
        var badge = new Rectangle(bounds.Right - badgeSize, bounds.Y, badgeSize, badgeSize);
        using var warning = new SolidBrush(Color.FromArgb(255, 245, 166, 35));
        using var center = new SolidBrush(BackingColor);
        graphics.FillRectangle(warning, badge);
        graphics.FillRectangle(center, badge.X + (badgeSize / 2), badge.Y + (badgeSize / 2), 1, 1);
    }

    private static void DrawExclamation(Graphics graphics, Rectangle bounds, int size)
    {
        var unit = size >= 24 ? 2 : 1;
        var centerX = bounds.X + (bounds.Width / 2) - (unit / 2);
        var top = bounds.Y + Math.Max(1, unit);
        using var backing = new SolidBrush(BackingColor);
        using var warning = new SolidBrush(Color.FromArgb(255, 244, 83, 83));
        graphics.FillRectangle(backing, centerX - unit, bounds.Y + 1, unit * 3, bounds.Height - 2);
        graphics.FillRectangle(warning, centerX, top, unit, Math.Max(unit * 2, bounds.Height / 3));
        graphics.FillRectangle(warning, centerX, bounds.Bottom - (unit * 2), unit, unit);
    }

    private static void DrawLock(Graphics graphics, Rectangle bounds, int size)
    {
        var unit = size >= 24 ? 2 : 1;
        var lockWidth = size >= 24 ? unit * 5 : 6;
        var lockHeight = size >= 24 ? unit * 3 : 4;
        var x = bounds.X + ((bounds.Width - lockWidth) / 2);
        var y = bounds.Y + (bounds.Height / 2);
        using var backing = new SolidBrush(BackingColor);
        using var lockBrush = new SolidBrush(Color.FromArgb(255, 174, 181, 194));
        using var shackle = new Pen(Color.FromArgb(255, 174, 181, 194), unit);
        graphics.FillRectangle(backing, x - unit, y - (unit * 3), lockWidth + (unit * 2), lockHeight + (unit * 4));
        graphics.DrawRectangle(shackle, x + unit, y - (unit * 2), lockWidth - (unit * 2), unit * 3);
        graphics.FillRectangle(lockBrush, x, y, lockWidth, lockHeight);
        graphics.FillRectangle(backing, x + (lockWidth / 2), y + unit, unit, unit);
    }

    private static Color StatusColor(ConnectionStatus status, int? remaining) => status switch
    {
        ConnectionStatus.Connected when remaining <= 5 => Color.FromArgb(255, 244, 83, 83),
        ConnectionStatus.Connected when remaining <= 20 => Color.FromArgb(255, 245, 166, 35),
        ConnectionStatus.Connected => Color.FromArgb(255, 53, 201, 143),
        ConnectionStatus.RateLimitReached => Color.FromArgb(255, 244, 83, 83),
        ConnectionStatus.DataStale => Color.FromArgb(255, 245, 166, 35),
        ConnectionStatus.Loading => Color.FromArgb(255, 88, 166, 255),
        ConnectionStatus.NotSignedIn => Color.FromArgb(255, 174, 181, 194),
        _ => Color.FromArgb(255, 244, 83, 83)
    };

    private readonly record struct Geometry(Rectangle Ring, Rectangle BarBody, Rectangle BarInner, Rectangle TextBody)
    {
        public static Geometry For(int size) => size switch
        {
            16 => new(new(2, 2, 12, 12), new(1, 5, 14, 6), new(2, 6, 12, 4), new(0, 0, 16, 16)),
            20 => new(new(2, 2, 16, 16), new(1, 6, 18, 8), new(3, 8, 14, 4), new(0, 0, 20, 20)),
            24 => new(new(2, 2, 20, 20), new(1, 7, 22, 10), new(3, 9, 18, 6), new(0, 0, 24, 24)),
            32 => new(new(3, 3, 26, 26), new(2, 10, 28, 12), new(4, 12, 24, 8), new(0, 0, 32, 32)),
            _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Supported icon sizes are 16, 20, 24, and 32 pixels.")
        };
    }
}
