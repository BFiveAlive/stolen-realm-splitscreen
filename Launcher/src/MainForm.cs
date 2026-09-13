namespace SplitScreenLauncher;

/// <summary>
/// The whole launcher: choose a game, choose the players, press Launch, press a button.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly (string Text, TileLayout Layout)[] Layouts =
    [
        ("Automatic", TileLayout.Auto),
        ("Side by side", TileLayout.SideBySide),
        ("Stacked", TileLayout.Stacked),
        ("Grid", TileLayout.Grid)
    ];

    private static readonly string LogFile =
        Path.Combine(Path.GetTempPath(), "stolen-realm-splitscreen-launcher.log");

    private readonly SessionOptions options;
    private readonly SeatPanel panel;
    private readonly List<RadioButton> playerButtons = [];
    private readonly RadioButton campaignButton;
    private readonly RadioButton roguelikeButton;
    private readonly ComboBox layoutBox;
    private readonly TextBox gameDirBox;
    private readonly Button browseButton;
    private readonly Label hintLabel;
    private readonly Label statusLabel;
    private readonly Button launchButton;
    private readonly Button stopButton;
    private readonly Button skipButton;
    private readonly CheckBox logToggle;
    private readonly TextBox logBox;
    private readonly System.Windows.Forms.Timer liveTimer = new() { Interval = 250 };

    private Session? session;
    private CancellationTokenSource? launchCancel;
    private bool launching;
    private bool claimingDone;
    private bool minimisedAfterClaims;
    private Dictionary<string, string> verdicts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextVerdictAt;

    internal MainForm()
    {
        options = Settings.Load();

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Stolen Realm Split-Screen";
        Font = new Font("Segoe UI", 10f);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 720);
        MinimumSize = new Size(740, 600);

        // ---------------------------------------------------------------- header
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty
        };
        header.Controls.Add(new Label
        {
            Text = "Split-Screen Co-op", AutoSize = true, Margin = Padding.Empty,
            Font = new Font("Segoe UI Semibold", 20f), ForeColor = Theme.Text
        });
        header.Controls.Add(new Label
        {
            Text = "Every player gets their own window, camera and controller.",
            AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 14)
        });

        // ---------------------------------------------------------------- choices
        var playersFlow = Row();
        for (int n = 1; n <= 4; n++)
        {
            int count = n;
            var button = Toggle(n.ToString(), 46);
            button.CheckedChanged += (_, _) =>
            {
                if (button.Checked)
                    OnPlayerCount(count);
            };
            button.Checked = options.Seats.Count == n;
            playerButtons.Add(button);
            playersFlow.Controls.Add(button);
        }

        campaignButton = Toggle("Campaign", 112);
        roguelikeButton = Toggle("Roguelike", 112);
        campaignButton.CheckedChanged += (_, _) => { if (campaignButton.Checked) options.Mode = GameMode.Campaign; };
        roguelikeButton.CheckedChanged += (_, _) => { if (roguelikeButton.Checked) options.Mode = GameMode.Roguelike; };
        campaignButton.Checked = options.Mode == GameMode.Campaign;
        roguelikeButton.Checked = options.Mode == GameMode.Roguelike;

        var modeFlow = Row();
        modeFlow.Controls.Add(campaignButton);
        modeFlow.Controls.Add(roguelikeButton);

        layoutBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 150,
            BackColor = Theme.Surface, ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 0)
        };
        foreach (var (text, _) in Layouts)
            layoutBox.Items.Add(text);
        layoutBox.SelectedIndex = Math.Max(0, Array.FindIndex(Layouts, x => x.Layout == options.Layout));
        layoutBox.SelectedIndexChanged += (_, _) =>
        {
            options.Layout = Layouts[layoutBox.SelectedIndex].Layout;
            panel!.Invalidate();
        };

        var choices = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        choices.Controls.Add(Group("Players", playersFlow));
        choices.Controls.Add(Group("Game", modeFlow));
        choices.Controls.Add(Group("Screen layout", layoutBox));

        // ---------------------------------------------------------------- game folder
        gameDirBox = new TextBox
        {
            ReadOnly = true, TabStop = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface, ForeColor = Theme.Text, Text = options.GameDir,
            Margin = new Padding(0, 6, 8, 0)
        };
        browseButton = MakeButton("Browse…", 100, primary: false);
        browseButton.Height = 34;
        browseButton.Click += OnBrowse;

        var folderRow = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0)
        };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(new Label
        {
            Text = "Game folder", AutoSize = true, ForeColor = Theme.Muted,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0)
        }, 0, 0);
        folderRow.Controls.Add(gameDirBox, 1, 0);
        folderRow.Controls.Add(browseButton, 2, 0);

        // ---------------------------------------------------------------- seats
        panel = new SeatPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0), Options = options,
            StatusFor = seat => verdicts.TryGetValue(seat.Label, out string? v) ? v : null
        };
        panel.SeatClicked += OnSeatClicked;
        panel.SeatsSwapped += OnSeatsSwapped;

        hintLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 48, ForeColor = Theme.Muted, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 0)
        };

        // ---------------------------------------------------------------- bottom bar
        statusLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 40, AutoEllipsis = true, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Text = "Ready when you are."
        };

        logToggle = new CheckBox
        {
            Text = "Show log", AutoSize = true, ForeColor = Theme.Muted,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 14, 0)
        };

        skipButton = MakeButton("Skip remaining controllers", 220, primary: false);
        skipButton.Visible = false;
        skipButton.Click += (_, _) => FinishClaiming();

        stopButton = MakeButton("Stop", 90, primary: false);
        stopButton.Enabled = false;
        stopButton.Click += (_, _) =>
        {
            Session.StopGame();
            EndSession("Stopped. Every game window was closed.");
        };

        launchButton = MakeButton("Launch", 150, primary: true);
        launchButton.Click += OnLaunch;

        var bar = new TableLayoutPanel
        {
            ColumnCount = 5, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(statusLabel, 0, 0);
        bar.Controls.Add(logToggle, 1, 0);
        bar.Controls.Add(skipButton, 2, 0);
        bar.Controls.Add(stopButton, 3, 0);
        bar.Controls.Add(launchButton, 4, 0);

        logBox = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Height = 150, Visible = false, BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = new Font("Consolas", 9f),
            Margin = new Padding(0, 10, 0, 0)
        };
        logToggle.CheckedChanged += (_, _) => logBox.Visible = logToggle.Checked;

        // ---------------------------------------------------------------- assemble
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(22, 18, 22, 16)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(choices, 0, 1);
        root.Controls.Add(folderRow, 0, 2);
        root.Controls.Add(panel, 0, 3);
        root.Controls.Add(hintLabel, 0, 4);
        root.Controls.Add(bar, 0, 5);
        root.Controls.Add(logBox, 0, 6);
        Controls.Add(root);

        liveTimer.Tick += (_, _) => OnLiveTick();

        UpdateHint();

        string? problem = options.Problem();
        if (problem is not null)
            SetStatus(problem);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Settings.Save(options);
        liveTimer.Stop();
        launchCancel?.Cancel();
        base.OnFormClosing(e);
    }

    // -------------------------------------------------------------------- setup

    private void OnPlayerCount(int count)
    {
        if (session is not null)
            return;

        options.SetPlayerCount(count);
        panel?.Invalidate();
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the Stolen Realm folder - the one containing Stolen Realm.exe",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(options.GameDir) ? options.GameDir : string.Empty
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!File.Exists(Path.Combine(dialog.SelectedPath, "Stolen Realm.exe")))
        {
            MessageBox.Show(this, "That folder does not contain Stolen Realm.exe.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        options.GameDir = dialog.SelectedPath;
        gameDirBox.Text = options.GameDir;
        Settings.Save(options);
        SetStatus(options.Problem() ?? "Ready when you are.");
    }

    private void OnSeatClicked(Seat seat)
    {
        if (session is null)
        {
            // Only one seat can have the keyboard, so giving it to one takes it from the other.
            if (seat.Input == SeatInput.KeyboardAndMouse)
            {
                seat.Input = SeatInput.Controller;
            }
            else
            {
                foreach (var other in options.Seats)
                    other.Input = SeatInput.Controller;
                seat.Input = SeatInput.KeyboardAndMouse;
            }

            panel.Invalidate();
            return;
        }

        // Clicking a screen with no controller yet makes it the one listening - including after
        // "skip", in case someone turns up late with a pad.
        if (seat.NeedsClaim)
        {
            claimingDone = false;
            TopMost = true;
            skipButton.Visible = true;
            Arm(seat);
        }
    }

    private void OnSeatsSwapped(Seat a, Seat b)
    {
        (a.Tile, b.Tile) = (b.Tile, a.Tile);
        session?.PlaceWindows();
        panel.Invalidate();

        if (session is not null)
            Log($"Swapped player {a.Index + 1} and player {b.Index + 1}'s windows.");
    }

    // -------------------------------------------------------------------- launch

    private async void OnLaunch(object? sender, EventArgs e)
    {
        string? problem = options.Problem();
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (ModInstaller.EnsureInstalled(options) is { } installed)
                Log(installed);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy SplitCoopMod into the game folder: " + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (Session.GameIsRunning
            && MessageBox.Show(this, "Stolen Realm is already running. Close it and start split-screen?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (System.Diagnostics.Process.GetProcessesByName("steam").Length == 0)
            Log("Steam does not seem to be running. The game uses it for networking, so start Steam if the windows do not connect.");

        Settings.Save(options);

        foreach (var seat in options.Seats)
        {
            seat.ControllerName = null;
            seat.Ready = false;
        }

        verdicts.Clear();
        session = new Session(options, Log);
        launchCancel = new CancellationTokenSource();
        claimingDone = !options.Seats.Any(s => s.NeedsClaim);
        minimisedAfterClaims = false;

        SetLive(true);

        // Kept above the game windows while anyone still has to claim a controller: the windows
        // are about to cover the whole screen, and the prompt telling people what to press lives
        // here.
        TopMost = !claimingDone;
        liveTimer.Start();

        launching = true;
        try
        {
            await session.LaunchAsync(new Progress<string>(SetStatus), launchCancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log("Launch failed: " + ex.Message);
            Session.StopGame();
            EndSession("Launch failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            launching = false;
        }

        OnLiveTick();
    }

    private void OnLiveTick()
    {
        if (session is null)
            return;

        var claims = session.Claims;

        foreach (var seat in options.Seats)
        {
            if (!seat.Ready && seat.Input == SeatInput.Controller && claims.IsReady(seat.Index))
            {
                seat.Ready = true;
                Log($"Player {seat.Index + 1}'s game is listening for a controller.");
            }

            if (seat.Input == SeatInput.Controller && seat.ControllerName is null
                && claims.ClaimedController(seat.Index) is { } name)
            {
                seat.ControllerName = name;
                Log($"Player {seat.Index + 1} took {name}.");
                System.Media.SystemSounds.Asterisk.Play();

                if (panel.ArmedSeat == seat.Index)
                    panel.ArmedSeat = -1;
            }
        }

        if (!claimingDone && (panel.ArmedSeat < 0 || !options.Seats[panel.ArmedSeat].NeedsClaim))
        {
            var next = options.Seats.Where(s => s.NeedsClaim).OrderBy(s => s.Tile).FirstOrDefault();
            if (next is null)
                FinishClaiming();
            else
                Arm(next);
        }

        if (DateTime.UtcNow >= nextVerdictAt)
        {
            nextVerdictAt = DateTime.UtcNow.AddSeconds(2);
            verdicts = session.Verdicts();

            if (!launching)
            {
                if (session.StartedCount > 0 && !session.AnyRunning)
                {
                    EndSession("Every game window has closed.");
                    return;
                }

                SetStatus(string.Join("     ", options.Seats.Select(s =>
                    $"P{s.Index + 1}: {(verdicts.TryGetValue(s.Label, out string? v) ? v : "starting…")}")));
            }
        }

        if (!launching && claimingDone && !minimisedAfterClaims)
        {
            // Out of the way once there is nothing left to set up. It stays in the taskbar for
            // swapping seats or stopping the session.
            minimisedAfterClaims = true;
            WindowState = FormWindowState.Minimized;
        }

        UpdateHint();
        panel.Invalidate();
    }

    private void Arm(Seat seat)
    {
        if (session is null || panel.ArmedSeat == seat.Index)
            return;

        panel.ArmedSeat = seat.Index;
        session.Claims.Arm(seat.Index);
        Log($"Waiting for player {seat.Index + 1} to press a button on their controller.");
        UpdateHint();
        panel.Invalidate();
    }

    private void FinishClaiming()
    {
        claimingDone = true;
        panel.ArmedSeat = -1;
        session?.Claims.Disarm();
        TopMost = false;
        skipButton.Visible = false;

        var unclaimed = options.Seats.Where(s => s.NeedsClaim).ToList();
        Log(unclaimed.Count == 0
            ? "Every player has their input."
            : $"No controller for {string.Join(", ", unclaimed.Select(s => "player " + (s.Index + 1)))}; "
              + "those windows will respond to every controller until one is claimed.");

        UpdateHint();
        panel.Invalidate();
    }

    private void EndSession(string message)
    {
        liveTimer.Stop();
        launchCancel?.Cancel();
        session = null;
        claimingDone = false;
        panel.ArmedSeat = -1;
        TopMost = false;
        verdicts.Clear();

        foreach (var seat in options.Seats)
        {
            seat.ControllerName = null;
            seat.Ready = false;
        }

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        SetLive(false);
        SetStatus(message);
        Log(message);
        UpdateHint();
    }

    // -------------------------------------------------------------------- presentation

    private void SetLive(bool live)
    {
        foreach (var button in playerButtons)
            button.Enabled = !live;

        campaignButton.Enabled = roguelikeButton.Enabled = !live;
        layoutBox.Enabled = browseButton.Enabled = !live;
        launchButton.Enabled = !live;
        stopButton.Enabled = live;
        skipButton.Visible = live && !claimingDone;

        panel.Phase = live ? PanelPhase.Live : PanelPhase.Setup;
        panel.Invalidate();
    }

    private void UpdateHint()
    {
        if (session is null)
        {
            hintLabel.Text = "Click a screen to switch it between a controller and keyboard & mouse. "
                           + "Drag one screen onto another to swap them. Press Launch when everyone is ready.";
            return;
        }

        if (panel.ArmedSeat >= 0)
        {
            var seat = options.Seats[panel.ArmedSeat];
            hintLabel.Text = $"Player {seat.Index + 1}: pick up the controller you want and press any button on it. "
                           + "It will control the highlighted screen."
                           + (seat.Ready ? string.Empty : " That screen's game is still loading, and starts listening once it appears.");
            return;
        }

        hintLabel.Text = "Drag a screen onto another to swap those windows. "
                       + "Click a screen with no controller to give it one. Stop closes every game window.";
    }

    private void SetStatus(string text) => statusLabel.Text = text;

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(() => Log(message)));
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        logBox.AppendText(line + Environment.NewLine);

        try { File.AppendAllText(LogFile, line + Environment.NewLine); }
        catch { /* The on-screen log is the one that matters. */ }
    }

    private static FlowLayoutPanel Row() =>
        new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };

    private static Control Group(string caption, Control content)
    {
        var box = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Margin = new Padding(0, 0, 34, 0)
        };

        box.Controls.Add(new Label
        {
            Text = caption.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted,
            Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(1, 0, 0, 6)
        });
        box.Controls.Add(content);

        return box;
    }

    private static RadioButton Toggle(string text, int width)
    {
        var button = new RadioButton
        {
            Appearance = Appearance.Button, Text = text, TextAlign = ContentAlignment.MiddleCenter,
            Width = width, Height = 36, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 6, 0), BackColor = Theme.Surface, ForeColor = Theme.Text
        };

        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.CheckedBackColor = Theme.Accent;
        button.FlatAppearance.MouseOverBackColor = Theme.Hover;
        button.CheckedChanged += (_, _) => button.ForeColor = button.Checked ? Theme.Back : Theme.Text;

        return button;
    }

    private static Button MakeButton(string text, int width, bool primary)
    {
        var button = new Button
        {
            Text = text, Width = width, Height = 40, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
            BackColor = primary ? Theme.Accent : Theme.Surface,
            ForeColor = primary ? Theme.Back : Theme.Text
        };

        if (primary)
            button.Font = new Font("Segoe UI Semibold", 11f);

        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.Hover;

        // A flat button keeps its colours when disabled, and a gold Launch button that does nothing
        // while games are running reads as broken.
        if (primary)
        {
            button.EnabledChanged += (_, _) =>
            {
                button.BackColor = button.Enabled ? Theme.Accent : Theme.Surface;
                button.FlatAppearance.BorderColor = button.Enabled ? Theme.Accent : Theme.Border;
            };
        }

        return button;
    }
}
