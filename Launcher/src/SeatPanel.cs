using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SplitScreenLauncher;

internal enum PanelPhase { Setup, Live }

/// <summary>
/// A picture of every monitor, cut into the tiles the windows will occupy.
///
/// In the lobby, each player who joins appears as a tile they move around with their own
/// controller. The mouse works too: drag a player onto another to swap, onto the edge of one to
/// share that screen, onto an empty monitor to go there; click to toggle ready; right-click to
/// remove. Once the games are running, players claiming a non-Xbox controller in game see it
/// appear on their tile.
/// </summary>
internal sealed class SeatPanel : Control
{
    private enum DropKind { None, Swap, Beside, Display }

    private readonly record struct Drop(DropKind Kind, Seat? Target, bool After, Display? Display, RectangleF Preview);

    private static readonly Color ReadyColour = Color.FromArgb(110, 204, 124);

    private readonly System.Windows.Forms.Timer animation = new() { Interval = 40 };
    private readonly Font titleFont = new("Segoe UI Semibold", 15f);
    private readonly Font bodyFont = new("Segoe UI", 10.5f);
    private readonly Font smallFont = new("Segoe UI", 9f);
    private readonly Font compactTitleFont = new("Segoe UI Semibold", 11f);
    private readonly Font compactBodyFont = new("Segoe UI", 9f);
    private readonly Font compactSmallFont = new("Segoe UI", 8f);
    private readonly Font labelFont = new("Segoe UI Semibold", 8.5f);
    private readonly Font promptFont = new("Segoe UI Semibold", 13f);

    private Seat? pressed;
    private Point pressPoint;
    private Point dragPoint;
    private bool dragging;

    internal SeatPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Back;

        // The empty lobby's join prompt and a seat waiting for a claim both pulse.
        animation.Tick += (_, _) =>
        {
            if ((Phase == PanelPhase.Live && ArmedSeat >= 0) || (Phase == PanelPhase.Setup && Options?.Seats.Count == 0))
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

    internal event Action<Seat>? SeatRemoveRequested;

    /// <summary>Two players traded places.</summary>
    internal event Action<Seat, Seat>? SeatsSwapped;

    /// <summary>A player was dropped on the edge of another's tile: (moved, beside, after it).</summary>
    internal event Action<Seat, Seat, bool>? SeatMovedBeside;

    /// <summary>A player was dropped on free space on a display.</summary>
    internal event Action<Seat, Display>? SeatMovedToDisplay;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
            foreach (var font in new[] { titleFont, bodyFont, smallFont, compactTitleFont, compactBodyFont, compactSmallFont, labelFont, promptFont })
                font.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Maps screen pixels into the control, fitting every display in at once.</summary>
    private sealed class Frame
    {
        private readonly Rectangle desktop;
        private readonly float scale;
        private readonly PointF origin;

        internal Frame(List<Display> displays, Rectangle client)
        {
            Displays = displays;
            desktop = displays.Select(d => d.Bounds).Aggregate(Rectangle.Union);

            RectangleF area = RectangleF.Inflate(client, -14, -14);
            if (area.Width <= 0 || area.Height <= 0 || desktop.Width <= 0 || desktop.Height <= 0)
                return;

            scale = Math.Min(area.Width / desktop.Width, area.Height / desktop.Height);
            origin = new PointF(area.X + (area.Width - desktop.Width * scale) / 2,
                                area.Y + (area.Height - desktop.Height * scale) / 2);
            Valid = true;
        }

        internal List<Display> Displays { get; }

        internal bool Valid { get; }

        internal RectangleF Map(Rectangle r) => new(
            origin.X + (r.X - desktop.X) * scale, origin.Y + (r.Y - desktop.Y) * scale,
            r.Width * scale, r.Height * scale);
    }

    private Frame CurrentFrame() => new(SeatLayout.Displays(), ClientRectangle);

    private List<(Seat Seat, RectangleF Rect)> SeatRects(Frame frame)
    {
        var result = new List<(Seat, RectangleF)>();
        if (Options is null || !frame.Valid)
            return result;

        foreach (var (seat, tile) in SeatLayout.Compute(Options, frame.Displays))
            result.Add((seat, frame.Map(tile)));

        return result;
    }

    private Seat? SeatAt(Point point)
    {
        foreach (var (seat, rect) in SeatRects(CurrentFrame()))
        {
            if (rect.Contains(point))
                return seat;
        }

        return null;
    }

    /// <summary>
    /// What letting go here would do: the middle of another player's tile swaps with them, its
    /// outer band puts the dragged player beside them, and free space on a display adds them there.
    /// </summary>
    private Drop DropAt(Point point)
    {
        if (pressed is null || Options is null)
            return default;

        var frame = CurrentFrame();

        foreach (var (seat, rect) in SeatRects(frame))
        {
            if (!rect.Contains(point))
                continue;

            if (seat == pressed)
                return default;

            float dx = (point.X - rect.X) / rect.Width;
            float dy = (point.Y - rect.Y) / rect.Height;
            float nearest = Math.Min(Math.Min(dx, 1 - dx), Math.Min(dy, 1 - dy));

            if (nearest >= 0.22f)
                return new Drop(DropKind.Swap, seat, false, null, rect);

            RectangleF half;
            bool after;
            if (nearest == dx) { after = false; half = rect with { Width = rect.Width / 2 }; }
            else if (nearest == 1 - dx) { after = true; half = rect with { X = rect.X + rect.Width / 2, Width = rect.Width / 2 }; }
            else if (nearest == dy) { after = false; half = rect with { Height = rect.Height / 2 }; }
            else { after = true; half = rect with { Y = rect.Y + rect.Height / 2, Height = rect.Height / 2 }; }

            return new Drop(DropKind.Beside, seat, after, null, half);
        }

        foreach (var display in frame.Displays)
        {
            RectangleF rect = frame.Map(display.Bounds);
            if (rect.Contains(point))
                return new Drop(DropKind.Display, null, false, display, rect);
        }

        return default;
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var frame = CurrentFrame();
        if (!frame.Valid || Options is null)
            return;

        var seats = SeatRects(frame);
        double seconds = Environment.TickCount64 / 1000.0;
        Drop drop = dragging ? DropAt(dragPoint) : default;
        bool lobbyEmpty = Phase == PanelPhase.Setup && Options.Seats.Count == 0;

        foreach (var display in frame.Displays)
        {
            RectangleF rect = RectangleF.Inflate(frame.Map(display.Bounds), -4, -4);

            using (var bezel = RoundedRect(rect, 8))
            using (var brush = new SolidBrush(Theme.Bezel))
                g.FillPath(brush, bezel);

            bool empty = !Options.Seats.Any(s => SeatLayout.DisplayOf(s, frame.Displays) == display);
            if (!empty)
                continue;

            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            if (lobbyEmpty)
            {
                float pulse = (float)(0.5 + 0.5 * Math.Sin(seconds * 3));
                using var brush = new SolidBrush(Theme.Blend(Theme.Muted, Theme.Accent, pulse));
                g.DrawString("Press any button on a controller to join\n\nor press Enter for keyboard & mouse",
                    promptFont, brush, rect, format);
            }
            else
            {
                using var brush = new SolidBrush(Theme.Muted);
                g.DrawString(Phase == PanelPhase.Setup ? "No players\nMove one here with LB / RB, or drag" : "No players",
                    bodyFont, brush, rect, format);
            }
        }

        foreach (var (seat, rect) in seats)
        {
            bool source = dragging && pressed == seat;
            bool swapTarget = drop.Kind == DropKind.Swap && drop.Target == seat;
            DrawSeat(g, seat, RectangleF.Inflate(rect, -7, -7), seconds, source, swapTarget);
        }

        if (drop.Kind is DropKind.Beside or DropKind.Display && pressed is not null)
        {
            Color colour = Theme.Player(pressed.Index);
            using var path = RoundedRect(RectangleF.Inflate(drop.Preview, -7, -7), 8);
            using var fill = new SolidBrush(Color.FromArgb(70, colour));
            using var pen = new Pen(colour, 3f) { DashStyle = DashStyle.Dash };
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        // Display labels last, so no tile covers them. One monitor needs no label at all.
        if (frame.Displays.Count > 1)
        {
            foreach (var display in frame.Displays)
                DrawDisplayLabel(g, display, frame.Map(display.Bounds));
        }

        if (dragging && pressed is not null)
            DrawGhost(g, pressed);
    }

    private void DrawDisplayLabel(Graphics g, Display display, RectangleF rect)
    {
        SizeF size = g.MeasureString(display.Label, labelFont);
        var chip = new RectangleF(rect.X + 12, rect.Y + 10, size.Width + 14, size.Height + 6);

        using (var path = RoundedRect(chip, chip.Height / 2))
        using (var fill = new SolidBrush(Color.FromArgb(215, Theme.Bezel)))
            g.FillPath(fill, path);

        using var brush = new SolidBrush(Theme.Muted);
        g.DrawString(display.Label, labelFont, brush, chip.X + 7, chip.Y + 3);
    }

    private void DrawSeat(Graphics g, Seat seat, RectangleF r, double seconds, bool faded, bool dropTarget)
    {
        if (r.Width < 8 || r.Height < 8)
            return;

        Color colour = Theme.Player(seat.Index);
        bool armed = Phase == PanelPhase.Live && ArmedSeat == seat.Index;
        bool ready = Phase == PanelPhase.Setup && seat.LobbyReady;
        float pulse = armed ? (float)(0.5 + 0.5 * Math.Sin(seconds * 5)) : 0f;

        float tint = armed ? 0.16f + 0.16f * pulse : dropTarget ? 0.32f : ready ? 0.2f : 0.09f;

        using (var path = RoundedRect(r, 8))
        {
            using (var fill = new SolidBrush(Theme.Blend(Theme.Surface, colour, tint)))
                g.FillPath(fill, path);

            float width = armed ? 2f + 3f * pulse : dropTarget ? 4f : ready ? 3.5f : 2f;
            using var pen = new Pen(Color.FromArgb(faded ? 80 : 255, colour), width);
            g.DrawPath(pen, path);
        }

        var state = g.Save();
        g.SetClip(r);

        bool compact = r.Height < 210 || r.Width < 170;
        Font title = compact ? compactTitleFont : titleFont;
        Font body = compact ? compactBodyFont : bodyFont;
        Font small = compact ? compactSmallFont : smallFont;

        if (ready)
            DrawReadyBadge(g, r, compact);

        (string main, string? detail) = Describe(seat, armed);
        bool hasDevice = seat.Input != SeatInput.Claim || seat.ControllerName is not null || Phase == PanelPhase.Setup;

        Color iconColour = armed ? Theme.Blend(Theme.Muted, colour, pulse)
                           : hasDevice ? Theme.Text : Theme.Muted;
        if (faded)
            iconColour = Color.FromArgb(90, iconColour);

        float iconSize = Math.Clamp(Math.Min(r.Width, r.Height) * 0.3f, compact ? 22f : 30f, 96f);
        float iconHeight = iconSize * 0.6f;
        float gap = compact ? 6 : 12;
        float titleH = title.GetHeight(g), bodyH = body.GetHeight(g), smallH = small.GetHeight(g);

        float total = titleH + gap + iconHeight + gap + bodyH + (detail is null ? 0 : 4 + smallH * 2);
        float y = r.Y + Math.Max(4, (r.Height - total) / 2);

        DrawCentred(g, $"Player {seat.Index + 1}", title, faded ? Color.FromArgb(90, colour) : colour, r, y, titleH + 2);
        y += titleH + gap;

        var centre = new PointF(r.X + r.Width / 2, y + iconHeight / 2);
        if (seat.Input == SeatInput.KeyboardAndMouse)
            DrawKeyboard(g, centre, iconSize, iconColour);
        else
            DrawGamepad(g, centre, iconSize, iconColour);
        y += iconHeight + gap;

        DrawCentred(g, main, body, faded ? Theme.Muted : Theme.Text, r, y, bodyH + 2);
        y += bodyH + 4;

        if (detail is not null)
            DrawCentred(g, detail, small, Theme.Muted, r, y, smallH * 2 + 2);

        g.Restore(state);
    }

    private void DrawReadyBadge(Graphics g, RectangleF r, bool compact)
    {
        Font font = compact ? compactSmallFont : labelFont;
        const string text = "READY";
        SizeF size = g.MeasureString(text, font);
        var pill = new RectangleF(r.Right - size.Width - 22, r.Y + 10, size.Width + 12, size.Height + 4);

        using (var path = RoundedRect(pill, pill.Height / 2))
        using (var fill = new SolidBrush(ReadyColour))
            g.FillPath(fill, path);

        using var brush = new SolidBrush(Theme.Back);
        g.DrawString(text, font, brush, pill.X + 6, pill.Y + 2);
    }

    private (string Main, string? Detail) Describe(Seat seat, bool armed)
    {
        if (Phase == PanelPhase.Setup)
        {
            return seat.Input switch
            {
                SeatInput.Pad => (seat.InputDescription, seat.LobbyReady
                    ? "Ready  ·  B to cancel"
                    : "A: ready  ·  D-pad: move  ·  LB/RB: monitor  ·  B: leave"),
                SeatInput.KeyboardAndMouse => (seat.InputDescription, seat.LobbyReady
                    ? "Ready  ·  Esc to cancel"
                    : "Enter: ready  ·  Arrows: move  ·  Esc: leave"),
                _ => ("Other controller", "Press a button on it once this game loads  ·  right-click to remove")
            };
        }

        string? status = StatusFor?.Invoke(seat);

        if (seat.Input != SeatInput.Claim)
            return (seat.InputDescription, status);

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

        g.DrawString(text, font, brush, new RectangleF(bounds.X + 6, y, bounds.Width - 12, height), format);
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

        float arm = h * 0.15f;
        var dpad = new PointF(body.X + w * 0.25f, c.Y - h * 0.06f);
        g.DrawLine(pen, dpad.X - arm, dpad.Y, dpad.X + arm, dpad.Y);
        g.DrawLine(pen, dpad.X, dpad.Y - arm, dpad.X, dpad.Y + arm);

        var face = new PointF(body.X + w * 0.75f, c.Y - h * 0.06f);
        float spread = h * 0.13f, radius = h * 0.055f;
        foreach (var (dx, dy) in new[] { (0f, -spread), (spread, 0f), (0f, spread), (-spread, 0f) })
            g.FillEllipse(brush, face.X + dx - radius, face.Y + dy - radius, radius * 2, radius * 2);

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

        if (e.Button == MouseButtons.Right)
        {
            if (Phase == PanelPhase.Setup && SeatAt(e.Location) is { } toRemove)
                SeatRemoveRequested?.Invoke(toRemove);
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        Seat? source = pressed;
        Drop drop = dragging ? DropAt(e.Location) : default;
        bool wasDragging = dragging;
        Seat? under = wasDragging ? null : SeatAt(e.Location);

        pressed = null;
        dragging = false;
        Invalidate();

        if (source is null)
            return;

        if (!wasDragging)
        {
            if (under == source)
                SeatClicked?.Invoke(source);
            return;
        }

        switch (drop.Kind)
        {
            case DropKind.Swap when drop.Target is not null:
                SeatsSwapped?.Invoke(source, drop.Target);
                break;
            case DropKind.Beside when drop.Target is not null:
                SeatMovedBeside?.Invoke(source, drop.Target, drop.After);
                break;
            case DropKind.Display when drop.Display is not null:
                SeatMovedToDisplay?.Invoke(source, drop.Display);
                break;
        }
    }
}
