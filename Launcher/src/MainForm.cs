namespace SplitScreenLauncher;

/// <summary>
/// The whole launcher: everyone presses a button to join, moves their screen where they want it,
/// readies up, and the games start with every controller already assigned.
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

    private const int CountdownSeconds = 5;

    private readonly SessionOptions options;
    private readonly SeatPanel panel;
    private readonly PadPoller pads = new();
    private readonly Button addKeyboardButton;
    private readonly Button addOtherButton;
    private readonly Button clearButton;
    private readonly Label playerCountLabel;
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
    private readonly System.Windows.Forms.Timer padTimer = new() { Interval = 16 };
    private readonly List<Button> monitorButtons = [];

    private Session? session;
    private CancellationTokenSource? launchCancel;
    private bool launching;
    private bool claimingDone;
    private bool minimisedAfterClaims;
    private Dictionary<string, string> verdicts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextVerdictAt;
    private DateTime? countdownEndsAt;

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
        ClientSize = new Size(1000, 760);
        MinimumSize = new Size(780, 620);
        KeyPreview = true;

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
            Text = "Every player gets their own window, camera and controller. Up to six players.",
            AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 14)
        });

        // ---------------------------------------------------------------- players
        addKeyboardButton = SmallButton("+ Keyboard & mouse", 164);
        addKeyboardButton.Click += (_, _) => JoinKeyboard();

        addOtherButton = SmallButton("+ Other controller", 154);
        addOtherButton.Click += (_, _) => JoinOther();

        clearButton = SmallButton("Clear", 70);
        clearButton.Click += (_, _) =>
        {
            options.ClearSeats();
            LobbyChanged("Cleared every player.");
        };

        playerCountLabel = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(6, 9, 0, 0) };

        var playersFlow = Row();
        playersFlow.Controls.Add(addKeyboardButton);
        playersFlow.Controls.Add(addOtherButton);
        playersFlow.Controls.Add(clearButton);
        playersFlow.Controls.Add(playerCountLabel);

        // ---------------------------------------------------------------- game and layout
        campaignButton = Toggle("Campaign", 104);
        roguelikeButton = Toggle("Roguelike", 104);
        campaignButton.CheckedChanged += (_, _) => { if (campaignButton.Checked) options.Mode = GameMode.Campaign; };
        roguelikeButton.CheckedChanged += (_, _) => { if (roguelikeButton.Checked) options.Mode = GameMode.Roguelike; };
        campaignButton.Checked = options.Mode == GameMode.Campaign;
        roguelikeButton.Checked = options.Mode == GameMode.Roguelike;

        var modeFlow = Row();
        modeFlow.Controls.Add(campaignButton);
        modeFlow.Controls.Add(roguelikeButton);

        layoutBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 140,
            BackColor = Theme.Surface, ForeColor = Theme.Text, Margin = new Padding(0, 4, 0, 0)
        };
        foreach (var (text, _) in Layouts)
            layoutBox.Items.Add(text);
        layoutBox.SelectedIndex = Math.Max(0, Array.FindIndex(Layouts, x => x.Layout == options.Layout));
        layoutBox.SelectedIndexChanged += (_, _) =>
        {
            options.Layout = Layouts[layoutBox.SelectedIndex].Layout;
            Settings.Save(options);
            panel!.Invalidate();
        };

        var choices = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        choices.Controls.Add(Group("Players", playersFlow));
        choices.Controls.Add(Group("Game", modeFlow));
        choices.Controls.Add(Group("Screen layout", layoutBox));

        var displays = SeatLayout.Displays();
        if (displays.Count > 1)
        {
            var oneEach = SmallButton("One each", 96);
            var allOnMain = SmallButton("All on main", 108);
            monitorButtons.AddRange([oneEach, allOnMain]);

            oneEach.Click += (_, _) =>
            {
                SeatLayout.OneDisplayEach(options);
                SeatsRearranged("Gave each player their own display.");
            };
            allOnMain.Click += (_, _) =>
            {
                var main = SeatLayout.Displays().FirstOrDefault(d => d.Primary) ?? SeatLayout.Displays()[0];
                SeatLayout.AllOn(options, main.DeviceName);
                SeatsRearranged("Put every player on the main display.");
            };

            var monitorsFlow = Row();
            monitorsFlow.Controls.Add(oneEach);
            monitorsFlow.Controls.Add(allOnMain);
            choices.Controls.Add(Group($"Monitors ({displays.Count})", monitorsFlow));
        }

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
        panel.SeatRemoveRequested += seat => { if (session is null) RemovePlayer(seat); };
        panel.SeatsSwapped += (a, b) =>
        {
            SeatLayout.Swap(a, b);
            SeatsRearranged($"Swapped player {a.Index + 1} and player {b.Index + 1}.");
        };
        panel.SeatMovedBeside += (seat, target, after) =>
        {
            SeatLayout.MoveBeside(options, seat, target, after);
            SeatsRearranged($"Player {seat.Index + 1} now shares a screen with player {target.Index + 1}.");
        };
        panel.SeatMovedToDisplay += (seat, display) =>
        {
            SeatLayout.MoveTo(options, seat, display.DeviceName, int.MaxValue);
            SeatsRearranged($"Moved player {seat.Index + 1} to display {display.Number}.");
        };

        hintLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 52, ForeColor = Theme.Muted, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 0)
        };

        // ---------------------------------------------------------------- bottom bar
        statusLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, Height = 40, AutoEllipsis = true, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text
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
        launchButton.Click += (_, _) => StartLaunch();

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
            Height = 150, Visible = false, BorderStyle = BorderStyle.None, TabStop = false,
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

        pads.ButtonPressed += OnPadButton;
        pads.Disconnected += OnPadDisconnected;
        padTimer.Tick += (_, _) => OnPadTick();
        padTimer.Start();

        LobbyChanged(null);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Settings.Save(options);
        padTimer.Stop();
        liveTimer.Stop();
        launchCancel?.Cancel();
        base.OnFormClosing(e);
    }

    // -------------------------------------------------------------------- lobby: joining

    private void JoinPad(int slot)
    {
        if (options.AddSeat(SeatInput.Pad, slot) is { } seat)
        {
            PadPoller.Rumble(slot, 180);
            LobbyChanged($"Controller {slot + 1} joined as player {seat.Index + 1}.");
        }
        else if (options.Seats.Count >= SessionOptions.MaxPlayers)
        {
            LobbyChanged($"Controller {slot + 1} could not join: the party is full ({SessionOptions.MaxPlayers} players).");
        }
    }

    private void JoinKeyboard()
    {
        if (session is not null)
            return;

        if (options.AddSeat(SeatInput.KeyboardAndMouse) is { } seat)
            LobbyChanged($"Keyboard & mouse joined as player {seat.Index + 1}.");
        else
            LobbyChanged(options.Seats.Count >= SessionOptions.MaxPlayers
                ? "The party is full."
                : "Keyboard & mouse has already joined.");
    }

    private void JoinOther()
    {
        if (session is not null)
            return;

        if (options.AddSeat(SeatInput.Claim) is { } seat)
            LobbyChanged($"Added player {seat.Index + 1} with another controller; they press a button on it once their game loads.");
        else
            LobbyChanged("The party is full.");
    }

    private void RemovePlayer(Seat seat)
    {
        int number = seat.Index + 1;
        string what = seat.InputDescription;
        options.RemoveSeat(seat);
        LobbyChanged($"Player {number} ({what}) left.");
    }

    // -------------------------------------------------------------------- lobby: controllers

    private void OnPadTick()
    {
        pads.Poll();

        if (countdownEndsAt is not { } ends || session is not null)
            return;

        double remaining = (ends - DateTime.UtcNow).TotalSeconds;
        if (remaining <= 0)
        {
            countdownEndsAt = null;
            StartLaunch();
            return;
        }

        SetStatus($"Everyone is ready. Launching in {Math.Ceiling(remaining):0}…  (B or Esc to wait)");
    }

    private void OnPadButton(int slot, PadButton button)
    {
        if (session is not null)
            return;

        var seat = options.Seats.FirstOrDefault(s => s.Input == SeatInput.Pad && s.XInputSlot == slot);

        if (seat is null)
        {
            // Any button joins, except B - a player backing out should not land straight back in.
            if (button != PadButton.B)
                JoinPad(slot);
            return;
        }

        switch (button)
        {
            case PadButton.A:
            case PadButton.Start:
                if (!seat.LobbyReady)
                {
                    seat.LobbyReady = true;
                    PadPoller.Rumble(slot, 90, 30000);
                    LobbyChanged($"Player {seat.Index + 1} is ready.");
                }
                else if (button == PadButton.Start && options.AllReady)
                {
                    // Start with everyone ready skips the rest of the countdown.
                    countdownEndsAt = DateTime.UtcNow;
                }
                break;

            case PadButton.B:
                if (countdownEndsAt is not null || seat.LobbyReady)
                {
                    seat.LobbyReady = false;
                    LobbyChanged($"Player {seat.Index + 1} is not ready.");
                }
                else
                {
                    RemovePlayer(seat);
                }
                break;

            case PadButton.Left: MoveSeat(seat, -1, 0); break;
            case PadButton.Right: MoveSeat(seat, 1, 0); break;
            case PadButton.Up: MoveSeat(seat, 0, -1); break;
            case PadButton.Down: MoveSeat(seat, 0, 1); break;
            case PadButton.LeftShoulder: ChangeMonitor(seat, -1); break;
            case PadButton.RightShoulder: ChangeMonitor(seat, 1); break;
        }
    }

    private void OnPadDisconnected(int slot)
    {
        if (session is not null)
            return;

        if (options.Seats.FirstOrDefault(s => s.Input == SeatInput.Pad && s.XInputSlot == slot) is { } seat)
        {
            int number = seat.Index + 1;
            options.RemoveSeat(seat);
            LobbyChanged($"Controller {slot + 1} disconnected, so player {number} left. Press a button on it to join again.");
        }
    }

    /// <summary>
    /// The keyboard player's controls. Handled before any focused button sees the key, so Enter
    /// never also presses whichever button happens to have focus.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (session is null && ActiveControl is not TextBox)
        {
            var keyboard = options.Seats.FirstOrDefault(s => s.Input == SeatInput.KeyboardAndMouse);

            switch (keyData)
            {
                case Keys.Enter:
                    if (keyboard is null)
                        JoinKeyboard();
                    else if (!keyboard.LobbyReady)
                    {
                        keyboard.LobbyReady = true;
                        LobbyChanged($"Player {keyboard.Index + 1} is ready.");
                    }
                    else if (options.AllReady)
                        countdownEndsAt = DateTime.UtcNow;
                    return true;

                case Keys.Escape when keyboard is not null:
                    if (countdownEndsAt is not null || keyboard.LobbyReady)
                    {
                        keyboard.LobbyReady = false;
                        LobbyChanged($"Player {keyboard.Index + 1} is not ready.");
                    }
                    else
                    {
                        RemovePlayer(keyboard);
                    }
                    return true;

                case Keys.Left when keyboard is not null: MoveSeat(keyboard, -1, 0); return true;
                case Keys.Right when keyboard is not null: MoveSeat(keyboard, 1, 0); return true;
                case Keys.Up when keyboard is not null: MoveSeat(keyboard, 0, -1); return true;
                case Keys.Down when keyboard is not null: MoveSeat(keyboard, 0, 1); return true;
                case Keys.PageUp when keyboard is not null: ChangeMonitor(keyboard, -1); return true;
                case Keys.PageDown when keyboard is not null: ChangeMonitor(keyboard, 1); return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Moves a player's screen one step in a direction: into the neighbouring position, swapping
    /// with whoever is there, or onto the monitor on that side when there is nobody to swap with.
    /// </summary>
    private void MoveSeat(Seat seat, int dx, int dy)
    {
        if (SeatLayout.NeighbourInDirection(options, seat, dx, dy) is { } neighbour)
        {
            SeatLayout.Swap(seat, neighbour);
            SeatsRearranged($"Player {seat.Index + 1} swapped with player {neighbour.Index + 1}.");
        }
        else if (SeatLayout.DisplayInDirection(options, seat, dx, dy) is { } display)
        {
            SeatLayout.MoveTo(options, seat, display.DeviceName, int.MaxValue);
            SeatsRearranged($"Player {seat.Index + 1} moved to display {display.Number}.");
        }
    }

    private void ChangeMonitor(Seat seat, int step)
    {
        if (SeatLayout.CycleDisplay(seat, step) is { } display)
        {
            SeatLayout.MoveTo(options, seat, display.DeviceName, int.MaxValue);
            SeatsRearranged($"Player {seat.Index + 1} moved to display {display.Number}.");
        }
    }

    private void OnSeatClicked(Seat seat)
    {
        if (session is null)
        {
            // The mouse can ready anyone, for a player whose hands are busy or a seat added by
            // button. Other-controller seats are always ready, so clicking those does nothing.
            if (seat.Input != SeatInput.Claim)
            {
                seat.LobbyReady = !seat.LobbyReady;
                LobbyChanged($"Player {seat.Index + 1} is {(seat.LobbyReady ? "ready" : "not ready")}.");
            }
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

    /// <summary>
    /// After anything changes in the lobby. Everyone ready starts - or restarts - the countdown,
    /// so a last-second move never launches before the mover has seen where they ended up.
    /// </summary>
    private void LobbyChanged(string? message)
    {
        if (message is not null)
            Log(message);

        if (session is null)
        {
            if (options.AllReady)
                countdownEndsAt = DateTime.UtcNow.AddSeconds(CountdownSeconds);
            else
            {
                countdownEndsAt = null;
                int ready = options.Seats.Count(s => s.LobbyReady);
                SetStatus(options.Seats.Count == 0
                    ? "Waiting for players to join."
                    : $"{options.Seats.Count} of {SessionOptions.MaxPlayers} players joined, {ready} ready.");
            }
        }

        playerCountLabel.Text = $"{options.Seats.Count}/{SessionOptions.MaxPlayers}";
        addKeyboardButton.Enabled = session is null && !options.Seats.Any(s => s.Input == SeatInput.KeyboardAndMouse)
                                    && options.Seats.Count < SessionOptions.MaxPlayers;
        addOtherButton.Enabled = session is null && options.Seats.Count < SessionOptions.MaxPlayers;
        clearButton.Enabled = session is null && options.Seats.Count > 0;

        UpdateHint();
        panel.Invalidate();
    }

    /// <summary>
    /// After any rearrangement. With games running the windows follow straight away - including
    /// onto another monitor - so the picture and the screens never disagree.
    /// </summary>
    private void SeatsRearranged(string message)
    {
        session?.PlaceWindows();

        if (session is null)
            LobbyChanged(message);
        else
        {
            Log(message);
            panel.Invalidate();
        }
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
        LobbyChanged(null);
    }

    // -------------------------------------------------------------------- launch

    private async void StartLaunch()
    {
        if (session is not null)
            return;

        countdownEndsAt = null;

        string? problem = options.Problem();
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            LobbyChanged(null);
            return;
        }

        // A pad that was unplugged after joining would launch a window waiting for nothing.
        var missing = options.Seats.Where(s => s.Input == SeatInput.Pad && !pads.IsConnected(s.XInputSlot)).ToList();
        if (missing.Count > 0)
        {
            foreach (var seat in missing)
                options.RemoveSeat(seat);
            LobbyChanged("Removed players whose controller is no longer connected. Press a button on it to join again.");
            return;
        }

        try
        {
            if (GameSettingsRepair.RepairControllerAssignments() is { } repaired)
                Log(repaired);
        }
        catch (Exception ex)
        {
            Log("Could not check the game's saved controller assignments: " + ex.Message);
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

        if (options.Seats.Count >= 5)
            Log($"{options.Seats.Count} copies of the game use a lot of memory - roughly 2 GB each.");

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
        LobbyChanged($"Launching {options.Seats.Count} player(s): "
                     + string.Join(", ", options.Seats.Select(s => $"P{s.Index + 1} {s.InputDescription}")) + ".");

        // Above the game windows only while someone still has to claim a controller in game.
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

        foreach (var seat in options.Seats.Where(s => s.Input == SeatInput.Claim))
        {
            if (!seat.Ready && claims.IsReady(seat.Index))
            {
                seat.Ready = true;
                Log($"Player {seat.Index + 1}'s game is listening for a controller.");
            }

            if (seat.ControllerName is null && claims.ClaimedController(seat.Index) is { } name)
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
            // Out of the way once there is nothing left to set up.
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
              + "those windows will respond to any controller nobody else has.");

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

            // Back to the lobby unready, so the countdown does not immediately start again.
            seat.LobbyReady = seat.Input == SeatInput.Claim;
        }

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        SetLive(false);
        LobbyChanged(message);
        SetStatus(message);
    }

    // -------------------------------------------------------------------- presentation

    private void SetLive(bool live)
    {
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
            hintLabel.Text = options.Seats.Count == 0
                ? "Press any button on an Xbox-style controller to join (up to 4), or Enter for keyboard & mouse. "
                  + "Players 5 and 6, or a PlayStation or other controller, use + Other controller."
                : "Controller: D-pad or stick moves your screen, LB/RB changes monitor, A is ready, B cancels or leaves. "
                  + "Keyboard: Enter, arrows, Page Up/Down, Esc. Mouse: drag to move, click to ready, right-click to remove. "
                  + "When everyone is ready the games start.";
            return;
        }

        if (panel.ArmedSeat >= 0)
        {
            var seat = options.Seats[panel.ArmedSeat];
            hintLabel.Text = $"Player {seat.Index + 1}: press any button on your controller. It will control the highlighted screen."
                           + (seat.Ready ? string.Empty : " That screen's game is still loading, and starts listening once it appears.");
            return;
        }

        hintLabel.Text = "Drag a screen onto another to swap those windows. Stop closes every game window.";
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
            Margin = new Padding(0, 0, 30, 8)
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

    private static Button SmallButton(string text, int width)
    {
        var button = MakeButton(text, width, primary: false);
        button.Height = 36;
        button.Margin = new Padding(0, 0, 6, 0);
        button.UseMnemonic = false;
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
