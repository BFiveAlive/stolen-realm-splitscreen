// Nucleus Co-op handler for Stolen Realm.
//
// UNTESTED. Nucleus was not installed on the machine this was developed on, so this file is
// written from the handler API and the arguments the mod actually accepts, but it has never been
// run. tools/Start-SplitScreen.ps1 is the path that has been exercised end to end; prefer it
// unless you specifically want Nucleus's input isolation or its window management.
//
// Note what this handler does NOT do, compared with most: no Steam emulator, no SteamID faking,
// no symlinked game copies for DRM reasons. Stolen Realm ships direct-IP multiplayer that sets
// Root.UsingSteam = false and identifies peers by a host-assigned NetworkId, so several instances
// under one Steam login simply work. The mod supplies the command line the menus otherwise
// require a human to click through.

Game.ExecutableName = "Stolen Realm.exe";
Game.SteamID = "1330000";
Game.GUID = "StolenRealm";
Game.GameName = "Stolen Realm";

// Each instance is a full client that talks to the others over loopback, so they can share one
// installation directory. Symlinking is what a Steam-emulator handler needs; this does not.
Game.SymlinkGame = false;
Game.HandlerInterval = 0;

// The game reads the window size from the command line, so Nucleus does not need to resize it
// afterwards - it only needs to place it.
Game.SupportsPositioning = true;
Game.ResizeWindowOnStart = false;
Game.SetWindowHook = true;

Game.LauncherTitle = "";
Game.MaxPlayers = 4;
Game.MaxPlayersOneMonitor = 4;

// Every instance drives its own Rewired assignment via -srcontroller, so Nucleus's own input
// isolation is belt-and-braces rather than the mechanism.
Game.SupportsKeyboard = true;
Game.KeyboardPlayerIndex = 0;

Game.Play = function () {
    var i = Context.PlayerID;             // 0-based, in window order
    var isHost = (i === 0);

    Context.ExePath = Context.ExeDirectory + "\\" + Game.ExecutableName;

    var args = [
        "-screen-fullscreen", "0",
        "-screen-width",  String(Context.Width),
        "-screen-height", String(Context.Height),
        "-popupwindow",
        "-logFile", Context.ExeDirectory + "\\BepInEx\\splitcoop-logs\\nucleus-p" + (i + 1) + ".log",
        "-srplayer", "p" + (i + 1),
        "-srmode", "campaign"
    ];

    // Player 1 hosts; everyone else dials it. The host needs to be listening first, which is what
    // Nucleus's own start delay between instances is for - set it to 30s or more in the profile,
    // because the socket only opens once that instance has reached the main menu.
    if (isHost) {
        args.push("-srhost");
    } else {
        args.push("-srjoin", "127.0.0.1");
    }

    // Nucleus assigns gamepads per instance; tell the mod which one this seat owns so the other
    // instances ignore it. Keyboard seat is handled separately.
    if (Context.IsKeyboardPlayer) {
        args.push("-srcontroller", "keyboard");
    } else {
        args.push("-srcontroller", String(Context.GamepadId));
    }

    Context.StartArguments = args.join(" ");
};
