using System.Drawing.Drawing2D;

namespace CodexUsageTray;

internal sealed class ModernContextMenuStrip : ContextMenuStrip
{
    private static readonly Color MenuBackground = Color.FromArgb(31, 31, 31);
    private static readonly Color MenuForeground = Color.FromArgb(246, 246, 246);
    private readonly ModernToolStripRenderer _modernRenderer = new();
    private readonly HashSet<ToolStripDropDown> _styledDropDowns = [];
    private readonly Font _menuFont = new("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

    public ModernContextMenuStrip()
    {
        ShowImageMargin = false;
        BackColor = MenuBackground;
        ForeColor = MenuForeground;
        Font = _menuFont;
        Padding = new Padding(6);
        Renderer = _modernRenderer;
        DropShadowEnabled = true;
        StyleDropDown(this);
    }

    private void StyleDropDown(ToolStripDropDown dropDown)
    {
        if (!_styledDropDowns.Add(dropDown)) return;

        dropDown.BackColor = MenuBackground;
        dropDown.ForeColor = MenuForeground;
        dropDown.Font = Font;
        dropDown.Padding = new Padding(6);
        dropDown.Renderer = _modernRenderer;
        dropDown.ItemAdded += (_, args) =>
        {
            if (args.Item is not null) StyleItem(args.Item);
        };
        dropDown.Opened += (_, _) =>
        {
            StyleItems(dropDown);
            ApplyRoundedRegion(dropDown);
        };
        dropDown.SizeChanged += (_, _) =>
        {
            if (dropDown.Visible) ApplyRoundedRegion(dropDown);
        };
        StyleItems(dropDown);
    }

    private void StyleItems(ToolStrip toolStrip)
    {
        foreach (ToolStripItem item in toolStrip.Items)
        {
            StyleItem(item);
        }
    }

    private void StyleItem(ToolStripItem item)
    {
        item.BackColor = Color.Transparent;
        item.ForeColor = item.Enabled ? MenuForeground : Color.FromArgb(135, 135, 135);
        item.Margin = item is ToolStripSeparator
            ? new Padding(0, 4, 0, 4)
            : new Padding(0, 1, 0, 1);
        item.Padding = item is ToolStripSeparator
            ? Padding.Empty
            : new Padding(10, 6, 8, 6);

        if (item is ToolStripMenuItem menuItem)
        {
            StyleDropDown(menuItem.DropDown);
        }
    }

    private static void ApplyRoundedRegion(ToolStripDropDown dropDown)
    {
        if (dropDown.Width <= 0 || dropDown.Height <= 0) return;

        using var path = CreateRoundedRectangle(
            new Rectangle(0, 0, dropDown.Width, dropDown.Height),
            9);
        var previous = dropDown.Region;
        dropDown.Region = new Region(path);
        previous?.Dispose();
    }

    internal static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        var path = new GraphicsPath();
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var dropDown in _styledDropDowns)
            {
                var region = dropDown.Region;
                dropDown.Region = null;
                region?.Dispose();
            }
        }

        base.Dispose(disposing);
        if (disposing) _menuFont.Dispose();
    }
}

internal sealed class ModernToolStripRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color HoverBackground = Color.FromArgb(55, 55, 55);
    private static readonly Color BorderColor = Color.FromArgb(58, 58, 58);
    private static readonly Color SeparatorColor = Color.FromArgb(70, 70, 70);

    public ModernToolStripRenderer()
        : base(new ModernToolStripColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }

        var bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = ModernContextMenuStrip.CreateRoundedRectangle(bounds, 5);
        using var brush = new SolidBrush(HoverBackground);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(SeparatorColor);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(BorderColor);
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = ModernContextMenuStrip.CreateRoundedRectangle(bounds, 9);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled != false
            ? Color.FromArgb(225, 225, 225)
            : Color.FromArgb(125, 125, 125);
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? Color.FromArgb(246, 246, 246)
            : Color.FromArgb(135, 135, 135);
        base.OnRenderItemText(e);
    }
}

internal sealed class ModernToolStripColorTable : ProfessionalColorTable
{
    private static readonly Color Background = Color.FromArgb(31, 31, 31);
    private static readonly Color Hover = Color.FromArgb(55, 55, 55);
    private static readonly Color Accent = Color.FromArgb(0, 95, 184);

    public override Color ToolStripDropDownBackground => Background;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemBorder => Hover;
    public override Color MenuBorder => Color.FromArgb(58, 58, 58);
    public override Color CheckBackground => Accent;
    public override Color CheckSelectedBackground => Accent;
    public override Color CheckPressedBackground => Accent;
}
