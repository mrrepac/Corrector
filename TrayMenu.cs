using System.ComponentModel;
using System.Drawing.Drawing2D;
using W = System.Windows;
using M = System.Windows.Media;

namespace Corrector;

// Keep the native tray menu's keyboard navigation, dismiss and submenu behavior.
internal sealed class TrayMenu : ContextMenuStrip
{
    readonly CardRenderer painter = new();
    readonly Font textFont = new("Segoe UI", 10);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ToolStripMenuItem? StatusItem { get => painter.StatusItem; set => painter.StatusItem = value; }

    internal TrayMenu()
    {
        Font = textFont;
        Renderer = painter;
        ShowImageMargin = true;
        DropShadowEnabled = true;
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        painter.ReadTheme();
        Style(this);
        base.OnOpening(e);
    }

    void Style(ToolStripDropDownMenu menu)
    {
        int D(int value) => (int)Math.Round(value * menu.DeviceDpi / 96d);
        menu.Padding = new Padding(D(5), D(6), D(5), D(6));
        menu.BackColor = painter.Surface;
        menu.ForeColor = painter.Ink;
        menu.Renderer = painter;
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is not ToolStripMenuItem command) continue;
            command.Padding = new Padding(0, D(6), D(12), D(6));
            if (command == StatusItem) command.Padding = new Padding(0, D(19), D(4), D(19));
            if (!command.HasDropDownItems || command.DropDown is not ToolStripDropDownMenu children) continue;
            children.Font = Font;
            children.SizeChanged -= RoundCorners;
            children.SizeChanged += RoundCorners;
            Style(children);
            RoundCorners(children, EventArgs.Empty);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RoundCorners(this, e);
    }

    static void RoundCorners(object? sender, EventArgs e)
    {
        if (sender is not ToolStrip menu || menu.Width < 2 || menu.Height < 2) return;
        using var path = CardRenderer.Rounded(new RectangleF(0, 0, menu.Width, menu.Height), 13f * menu.DeviceDpi / 96);
        menu.Region = new Region(path);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { textFont.Dispose(); painter.Dispose(); }
    }

    sealed class CardRenderer : ToolStripRenderer, IDisposable
    {
        readonly Font title = new("Segoe UI", 11, FontStyle.Bold);
        readonly Font symbol = new("Segoe MDL2 Assets", 10);
        internal ToolStripMenuItem? StatusItem;
        internal Color Surface, Ink;
        Color secondary, edge, tint, accent, disabled;

        internal void ReadTheme()
        {
            var resources = new W.ResourceDictionary();
            CardTheme.Apply(resources);
            Color Get(string key)
            {
                var color = ((M.SolidColorBrush)resources[key]).Color;
                return Color.FromArgb(color.R, color.G, color.B);
            }
            Surface = Get("CardSurface"); Ink = Get("CardText"); secondary = Get("CardSecondary");
            edge = Get("CardEdge"); tint = Get("CardTint"); accent = Get("CardAccent");
            disabled = SystemInformation.HighContrast ? SystemColors.GrayText :
                Color.FromArgb((secondary.R+Surface.R)/2, (secondary.G+Surface.G)/2, (secondary.B+Surface.B)/2);
        }

        internal static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            float d = Math.Min(2 * radius, Math.Min(rect.Width,rect.Height));
            var path = new GraphicsPath();
            path.AddArc(rect.Left,rect.Top,d,d,180,90); path.AddArc(rect.Right-d,rect.Top,d,d,270,90);
            path.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90); path.AddArc(rect.Left,rect.Bottom-d,d,d,90,90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Surface);
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip.Width < 2 || e.ToolStrip.Height < 2) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.ToolStrip.DeviceDpi / 96f;
            using var path = Rounded(new RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),13*scale);
            using var pen = new Pen(edge,scale);
            g.DrawPath(pen,path);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
            float s = e.ToolStrip!.DeviceDpi / 96f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Rounded(new RectangleF(2*s,s,e.Item.Width-4*s,e.Item.Height-2*s),8*s);
            using var brush = new SolidBrush(tint);
            e.Graphics.FillPath(brush,shape);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int D(int n) => (int)Math.Round(n * e.ToolStrip!.DeviceDpi / 96d);
            var row = new Rectangle(0,0,e.Item.Width,e.Item.Height);
            const TextFormatFlags plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            if (e.Item == StatusItem)
            {
                var badge = new Rectangle(D(9),(row.Height-D(34))/2,D(34),D(34));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var shape = Rounded(badge,D(11));
                using var brush = new SolidBrush(tint);
                e.Graphics.FillPath(brush,shape);
                TextRenderer.DrawText(e.Graphics,"C",title,badge,accent,plain|TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
                int x = badge.Right+D(12);
                var top = new Rectangle(x,0,row.Width-x-D(8),row.Height/2);
                var bottom = new Rectangle(x,top.Bottom,top.Width,row.Height-top.Height);
                TextRenderer.DrawText(e.Graphics,"Corrector",title,top,Ink,plain|TextFormatFlags.Bottom);
                TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,bottom,secondary,plain|TextFormatFlags.Top|TextFormatFlags.EndEllipsis);
                return;
            }
            bool active = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
            e.TextColor = !e.Item.Enabled ? disabled : active ? accent : Ink;
            if (e.Item.Tag is string glyph)
                TextRenderer.DrawText(e.Graphics,glyph,symbol,new Rectangle(D(8),0,D(22),row.Height),
                    !e.Item.Enabled ? disabled : active ? accent : secondary, plain|TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            var textBounds = new Rectangle(e.TextRectangle.Left+D(8), 0, e.TextRectangle.Width, row.Height);
            TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,textBounds,e.TextColor,
                TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|
                TextFormatFlags.EndEllipsis|(e.TextFormat & TextFormatFlags.HidePrefix));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = (int)Math.Round(10 * e.ToolStrip!.DeviceDpi / 96d);
            using var pen = new Pen(edge);
            e.Graphics.DrawLine(pen,inset,e.Item.Height/2,e.Item.Width-inset,e.Item.Height/2);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            float scale = (e.Item?.Owner?.DeviceDpi ?? 96) / 96f;
            float x = e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2f;
            float y = e.ArrowRectangle.Top + e.ArrowRectangle.Height / 2f;
            float dx = e.Direction == ArrowDirection.Left ? -2*scale : 2*scale;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(e.Item?.Selected == true ? accent : secondary, 1.4f*scale) { LineJoin = LineJoin.Round };
            e.Graphics.DrawLines(pen, new PointF[] { new(x-dx,y-3*scale),new(x+dx,y),new(x-dx,y+3*scale) });
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            int dpi = e.ToolStrip!.DeviceDpi;
            var rect = new Rectangle(8*dpi/96,0,22*dpi/96,e.Item.Height);
            TextRenderer.DrawText(e.Graphics,"\uE73E",symbol,rect,e.Item.Selected || !SystemInformation.HighContrast ? accent : Ink,
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        }
        public void Dispose() { title.Dispose(); symbol.Dispose(); }
    }
}
