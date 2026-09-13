using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SplitScreenLauncher;

internal enum PanelPhase { Setup, Live }

/// <summary>
/// A picture of the screen, cut into the tiles the windows will occupy.
///
/// It is the whole seating plan in one place: click a tile to change how that player plays, drag
/// one onto another to swap where they sit, and - once the games are running - watch each
/// player's controller appear on their tile as they press a button on it.
/// </summary>
internal sealed class SeatPanel : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 40 };
    private readonly Font titleFont = new("Segoe UI Semibold", 15f);
    private readonly Font bodyFont = new("Segoe UI", 10.5f);
    private readonly Font smallFont = new("Segoe UI", 9f);

    private Seat? pressed;
    private Point pressPoint;
    private Point dragPoint;
    private bool dragging;

    internal SeatPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Back;

        // Only the seat waiting for a button press animates; nothing else needs repainting.
        animation.Tick += (_, _) =>
        {
            if (Phase == PanelPhase.Live && ArmedSeat >= 0)
                Invalidate();
        };
        animation.Start();
    }

    internal SessionOptions? Options { get; set; }

    internal PanelPhase Phase { get; set; }

    /// <summary>The seat whose game is listening for a button press, or -1.</summary>
    internal int ArmedSeat { get; set; } = -1;

    /// <summary>A line of status under each seat once its game is running.</summary>
    internal Func<Seat, string?>? StatusFor { get; set; }

    internal event Action<Seat>? SeatClicked;

    internal event Action<Seat, Seat>? SeatsSwapped;

    private static Rectangle ScreenBounds => Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
            titleFont.Dispose();
            bodyFont.Dispose();
            smallFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ geometry

    private RectangleF ScreenArea()
    {
        Rectangle b = ScreenBounds;
        RectangleF area = RectangleF.Inflate(ClientRectangle, -12, -12);

        if (area.Width <= 0 || area.Height <= 0 || b.Width <= 0 || b.Height <= 0)
            return RectangleF.Empty;

        float scale = Math.Min(area.Width / b.Width, area.Height / b.Height);
        float w = b.Width * scale, h = b.Height * scale;

        return new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private List<(Seat Seat, RectangleF Rect)> SeatRects()
    {
        var result = new List<(Seat, RectangleF)>();
        if (Options is null)
            return result;

        RectangleF area = ScreenArea();
        if (area.IsEmpty)
            return result;

        Rectangle b = ScreenBounds;
        var tiles = TileMath.Compute(Options.Seats.Count, Options.EffectiveLayout, b);
        float sx = area.Width / b.Width, sy = area.Height / b.Height;

        foreach (var seat in Options.Seats)
        {
            if (seat.Tile < 0 || seat.Tile >= tiles.Count)
                continue;

            Rectangle t = tiles[seat.Tile];
            result.Add((seat, new RectangleF(
                area.X + (t.X - b.X) * sx, area.Y + (t.Y - b.Y) * sy, t.Width * sx, t.Height * sy)));
        }

        return result;
    }

    private Seat? SeatAt(Point point)
    {
        foreach (var (seat, rect) in SeatRects())
        {
            if (rect.Contains(point))
                return seat;
        }

        return null;
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        RectangleF area = ScreenArea();
        if (area.IsEmpty || Options is null)
            return;

        using (var bezel = RoundedRect(RectangleF.Inflate(area, 7, 7), 10))
        using (var brush = new SolidBrush(Theme.Bezel))
            g.FillPath(brush, bezel);

        double seconds = Environment.TickCount64 / 1000.0;

        foreach (var (seat, rect) in SeatRects())
        {
            bool source = dragging && pressed == seat;
            bool target = dragging && pressed is not null && pressed != seat && rect.Contains(dragPoint);
            DrawSeat(g, seat, RectangleF.Inflate(rect, -3, -3), seconds, source, target);
        }

        if (dragging && pressed is not null)
            DrawGhost(g, pressed);
    }

    private void DrawSeat(Graphics g, Seat seat, RectangleF r, double seconds, bool faded, bool dropTarget)
    {
        if (r.Width < 8 || r.Height < 8)
            return;

        Color colour = Theme.Player(seat.Index);
        bool armed = Phase == PanelPhase.Live && ArmedSeat == seat.Index;
        float pulse = armed ? (float)(0.5 + 0.5 * Math.Sin(seconds * 5)) : 0f;

        float tint = armed ? 0.16f + 0.16f * pulse : dropTarget ? 0.32f : 0.09f;

        using (var path = RoundedRect(r, 8))
        {
            using (var fill = new SolidBrush(Theme.Blend(Theme.Surface, colour, tint)))
                g.FillPath(fill, path);

            float width = armed ? 2f + 3f * pulse : dropTarget ? 4f : 2f;
            using var pen = new Pen(Color.FromArgb(faded ? 80 : 255, colour), width);
            g.DrawPath(pen, path);
        }

        var state = g.Save();
        g.SetClip(r);

        (string main, string? detail) = Describe(seat, armed);
        bool hasDevice = seat.Input == SeatInput.KeyboardAndMouse || seat.ControllerName is not null
                         || Phase == PanelPhase.Setup;

        Color iconColour = armed ? Theme.Blend(Theme.Muted, colour, pulse)
                           : hasDevice ? Theme.Text : Theme.Muted;
        if (faded)
            iconColour = Color.FromArgb(90, iconColour);

        float iconSize = Math.Clamp(Math.Min(r.Width, r.Height) * 0.3f, 30f, 96f);
        float iconHeight = iconSize * 0.6f;
        float titleH = titleFont.GetHeight(g), bodyH = bodyFont.GetHeight(g), smallH = smallFont.GetHeight(g);

        float total = titleH + 12 + iconHeight + 14 + bodyH + (detail is null ? 0 : 4 + smallH * 2);
        float y = r.Y + Math.Max(6, (r.Height - total) / 2);

        DrawCentred(g, $"Player {seat.Index + 1}", titleFont, faded ? Color.FromArgb(90, colour) : colour, r, y, titleH + 2);
        y += titleH + 12;

        var centre = new PointF(r.X + r.Width / 2, y + iconHeight / 2);
        if (seat.Input == SeatInput.KeyboardAndMouse)
            DrawKeyboard(g, centre, iconSize, iconColour);
        else
            DrawGamepad(g, centre, iconSize, iconColour);
        y += iconHeight + 14;

        DrawCentred(g, main, bodyFont, faded ? Theme.Muted : Theme.Text, r, y, bodyH + 2);
        y += bodyH + 4;

        if (detail is not null)
            DrawCentred(g, detail, smallFont, Theme.Muted, r, y, smallH * 2 + 2);

        g.Restore(state);
    }

    private (string Main, string? Detail) Describe(Seat seat, bool armed)
    {
        if (Phase == PanelPhase.Setup)
        {
            return seat.Input == SeatInput.KeyboardAndMouse
                ? ("Keyboard & mouse", "Click to use a controller instead")
                : ("Controller", "Click to use keyboard & mouse instead");
        }

        string? status = StatusFor?.Invoke(seat);

        if (seat.Input == SeatInput.KeyboardAndMouse)
            return ("Keyboard & mouse", status);

        if (seat.ControllerName is { } name)
            return (name, status);

        if (armed)
        {
            return ("Press any button",
                seat.Ready ? "on the controller for this screen" : "once this screen's game has loaded");
        }

        return ("No controller yet", "Click here, then press a button on one");
    }

    private void DrawGhost(Graphics g, Seat seat)
    {
        var card = new RectangleF(dragPoint.X - 80, dragPoint.Y - 26, 160, 52);
        Color colour = Theme.Player(seat.Index);

        using (var path = RoundedRect(card, 8))
        using (var fill = new SolidBrush(Color.FromArgb(235, Theme.Blend(Theme.Surface, colour, 0.25f))))
        using (var pen = new Pen(colour, 2f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var iconCentre = new PointF(card.X + 34, card.Y + card.Height / 2);
        if (seat.Input == SeatInput.KeyboardAndMouse)
            DrawKeyboard(g, iconCentre, 38, Theme.Text);
        else
            DrawGamepad(g, iconCentre, 38, Theme.Text);

        using var brush = new SolidBrush(Theme.Text);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString($"Player {seat.Index + 1}", bodyFont, brush,
            new RectangleF(card.X + 62, card.Y, card.Width - 66, card.Height), format);
    }

    private static void DrawCentred(Graphics g, string text, Font font, Color colour, RectangleF bounds, float y, float height)
    {
        using var brush = new SolidBrush(colour);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        g.DrawString(text, font, brush, new RectangleF(bounds.X + 8, y, bounds.Width - 16, height), format);
    }

    private static void DrawGamepad(Graphics g, PointF c, float size, Color colour)
    {
        float w = size, h = size * 0.6f;
        var body = new RectangleF(c.X - w / 2, c.Y - h / 2, w, h);

        using var pen = new Pen(colour, Math.Max(1.6f, size / 24f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(colour);

        using (var outline = RoundedRect(body, h * 0.46f))
            g.DrawPath(pen, outline);

        // D-pad on the left.
        float arm = h * 0.15f;
        var dpad = new PointF(body.X + w * 0.25f, c.Y - h * 0.06f);
        g.DrawLine(pen, dpad.X - arm, dpad.Y, dpad.X + arm, dpad.Y);
        g.DrawLine(pen, dpad.X, dpad.Y - arm, dpad.X, dpad.Y + arm);

        // Face buttons on the right.
        var face = new PointF(body.X + w * 0.75f, c.Y - h * 0.06f);
        float spread = h * 0.13f, radius = h * 0.055f;
        foreach (var (dx, dy) in new[] { (0f, -spread), (spread, 0f), (0f, spread), (-spread, 0f) })
            g.FillEllipse(brush, face.X + dx - radius, face.Y + dy - radius, radius * 2, radius * 2);

        // Two sticks.
        float stick = h * 0.12f;
        g.DrawEllipse(pen, c.X - w * 0.13f - stick, c.Y + h * 0.18f - stick, stick * 2, stick * 2);
        g.DrawEllipse(pen, c.X + w * 0.13f - stick, c.Y + h * 0.18f - stick, stick * 2, stick * 2);
    }

    private static void DrawKeyboard(Graphics g, PointF c, float size, Color colour)
    {
        float w = size * 0.74f, h = size * 0.42f;
        var board = new RectangleF(c.X - size / 2, c.Y - h / 2, w, h);

        using var pen = new Pen(colour, Math.Max(1.6f, size / 24f)) { LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(colour);

        using (var outline = RoundedRect(board, h * 0.14f))
            g.DrawPath(pen, outline);

        const int cols = 7, rows = 3;
        float pad = h * 0.16f;
        float cellW = (w - pad * 2) / cols, cellH = (h - pad * 2) / rows;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                // The bottom row is a space bar.
                if (row == rows - 1 && col > 1 && col < cols - 2)
                {
                    if (col == 2)
                        g.FillRectangle(brush, board.X + pad + cellW * 2 + cellW * 0.15f,
                            board.Y + pad + cellH * row + cellH * 0.2f, cellW * 3 - cellW * 0.3f, cellH * 0.6f);
                    continue;
                }

                g.FillRectangle(brush, board.X + pad + cellW * col + cellW * 0.2f,
                    board.Y + pad + cellH * row + cellH * 0.2f, cellW * 0.6f, cellH * 0.6f);
            }
        }

        // The mouse.
        float mw = size * 0.17f, mh = h * 1.05f;
        var mouse = new RectangleF(board.Right + size * 0.07f, c.Y - mh / 2, mw, mh);
        using (var body = RoundedRect(mouse, mw / 2))
            g.DrawPath(pen, body);
        g.DrawLine(pen, mouse.X + mw / 2, mouse.Y + mh * 0.12f, mouse.X + mw / 2, mouse.Y + mh * 0.38f);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0.5f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)) * 2;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        pressed = SeatAt(e.Location);
        pressPoint = e.Location;
        dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (pressed is not null && e.Button == MouseButtons.Left)
        {
            if (!dragging && (Math.Abs(e.X - pressPoint.X) > 6 || Math.Abs(e.Y - pressPoint.Y) > 6))
                dragging = true;

            if (dragging)
            {
                dragPoint = e.Location;
                Invalidate();
            }
        }

        Cursor = dragging ? Cursors.SizeAll : SeatAt(e.Location) is null ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        Seat? source = pressed;
        Seat? target = SeatAt(e.Location);
        bool wasDragging = dragging;

        pressed = null;
        dragging = false;
        Invalidate();

        if (source is null)
            return;

        if (wasDragging)
        {
            if (target is not null && target != source)
                SeatsSwapped?.Invoke(source, target);
        }
        else if (target == source)
        {
            SeatClicked?.Invoke(source);
        }
    }
}
