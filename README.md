# Stolen Realm Split-Screen

Local split-screen co-op for [Stolen Realm](https://store.steampowered.com/app/1330000/), on one
PC, one screen, one controller each.

It runs one copy of the game per player, joins them into a single session over the loopback
address, pins each copy to one input device, and lays the windows out across the screen.

## Playing

Run **`StolenRealmSplitScreen.exe`**. No scripts, no command lines.

1. **Pick the number of players and the game** — Campaign or Roguelike — and, if you like, the
   screen layout.
2. **Set up the seats.** The picture shows every monitor you have, with one tile per player.
   Click a player to switch them between a controller and keyboard & mouse. Then drag:
   - onto the **middle** of another player to swap places, even across monitors;
   - onto the **edge** of another player to share that screen with them, on that side;
   - onto an **empty monitor** (or the empty corner of a three-player grid) to go there.

   So two players and two monitors can have one each or share one, and three players can split
   one monitor while the third has the other to themselves. With more than one monitor there are
   also **One each** and **All on main** shortcuts. Each monitor is split automatically for the
   players on it, side by side on a landscape screen and stacked on a portrait one.
3. **Press Launch.** The games start, join each other and fill their tiles.
4. **Claim your controllers.** The launcher stays on top and highlights one screen at a time:
   whoever is sitting there presses any button on their pad, and it appears on their tile. Click
   a different tile to fill that one first. Once everyone has a controller the launcher gets out
   of the way.

Each window then reaches the game's party-select screen, where every player picks their own
character.

While playing, the launcher sits in the taskbar. Open it to swap two players' windows, give a
latecomer a controller, or **Stop**, which closes every game window at once.

The launcher remembers your last setup, finds the game through Steam's library list, and carries
the mod inside itself — on Launch it installs SplitCoopMod into the game, or updates it if it is
out of date. The one thing it cannot do for you is install BepInEx.

The PowerShell scripts in `tools\` do the same job from a command line and are still there if you
prefer them:

```powershell
.\tools\Start-SplitScreen.ps1 -Players 2 -Controllers keyboard,0
```

## Why it works this way

Not by splitting the screen. By running two games.

Stolen Realm's interface is built around there being exactly one local view: one
`CurrentlySelectedCharacter`, one skill bar, one camera, and 34 UI window types that each exist
once — `LoadableUIWindow<T>` holds a `public static T Instance`, so the type system permits exactly
one inventory to be open. Giving two people two views inside one process would mean rewriting that
layer, not patching it.

Two processes get there for free. Each player has a real, whole client — own camera, own UI, own
selection — and the only thing left to arrange is that they share a world and don't fight over the
same gamepad.

**The session is the game's own direct-IP multiplayer.** `MainMenu.HostMultiplayerIP` opens a plain
UDP socket on port 9055 and sets `Root.UsingSteam = false`; peers are told apart by a host-assigned
`NetworkId`, not by who is signed in. That is why two instances under one Steam login can sit in
the same session. Nothing here emulates, patches or bypasses Steam, and no Steam emulator is
involved — an approach like Nucleus Co-op's would need one, and this does not.

### And it is not just a nicer camera

Combat turns in Stolen Realm belong to a **team**, not a character:

```csharp
public bool IsMyTurn => GameLogic.instance.currentTeamTurnIndex == TeamIndex;
```

Every character on the active team is up at once, and `Acting`/`Moving` are per-character — the
only global gate is a wait for animations before the turn flips. So the engine already permits two
players to move and cast simultaneously; in the stock game it is the single shared UI that forces
them to take turns. Give each player their own client and that restriction goes away.

## Making the UI survive a narrow window

A tiled window is a shape the game was never designed for. Two tiles on a 2560x1440 screen are
1280x1440 - an aspect of 0.89 where the interface expects 1.78 - and the result is not merely
small text. Party cards overlapped their own contents ("Health" drawn over "269/269"), and the
skill tree lost its left-hand column off the edge of the screen along with four of its eleven
trees.

The cause is how the canvases scale. They are authored against 800x600 and match on **height**, so
the logical width the layout gets is whatever the aspect ratio leaves over: 1067 units at 16:9, but
only 533 in a tile. Every horizontal layout is then trying to fit in half the room it was built
for.

The mod raises the canvas reference height until the logical width comes back to its 16:9 value:

```
referenceHeight = authoredHeight * (16/9) / screenAspect
```

At 1280x1440 that is 600 -> 1200, and the layout measures exactly as it does on a normal screen -
the character list holder comes out 442 units wide in a tile, the same 442 it has at 2560x1440. The
surplus is spent on logical height, where a tall window has room going spare, so the UI is drawn
smaller but is complete and correctly laid out rather than clipped.

This is a no-op at 16:9: measured at both 2560x1440 and 1280x720, nothing is changed and the holder
is 442 either way.

A second, narrower correction sits behind it as a fallback. Some lists are laid out with a fixed
column count and stretch their cells to fill the row, so a narrow holder yields cards thinner than
the width their contents need. Where that happens the mod lowers the column count until each card
is at least as wide as the layout's authored cell. With the canvas correction in place this rarely
has anything to do, but it covers aspect ratios that were not tested.

## Requirements

- Stolen Realm on Steam, with Steam running (its networking library is the transport even for a
  direct-IP session)
- [BepInEx 5.4.23.5 (win_x64)](https://github.com/BepInEx/BepInEx/releases) installed in the game
  folder
- One input device per player

Nothing else to play. The launcher is self-contained and needs no .NET install.

## Building

Needs the .NET 8 SDK and the game installed (the mod compiles against its assemblies).

```powershell
.\tools\Publish-Launcher.ps1
# -> dist\StolenRealmSplitScreen.exe
```

That builds the mod, embeds it in the launcher, and publishes one .exe. To build only the mod:

```powershell
dotnet build SplitCoopMod\SplitCoopMod.csproj -c Release
```

which also copies `SplitCoopMod.dll` into `BepInEx\plugins\SplitCoopMod\`. Override the game path
with `-p:GameDir="D:\...\Stolen Realm"` if yours is elsewhere.

## Use

```powershell
# two players: keyboard on the left, first gamepad on the right
.\tools\Start-SplitScreen.ps1 -Players 2 -Controllers keyboard,0

# four players, 2x2, roguelike
.\tools\Start-SplitScreen.ps1 -Players 4 -Controllers 0,1,2,3 -Roguelike

# stacked instead of side by side
.\tools\Start-SplitScreen.ps1 -Players 2 -Layout Stacked
```

Find your controller indices first, if you have not before — Windows' numbering is not
necessarily Rewired's, and guessing wrong silently gives two players the same pad:

```powershell
.\tools\Get-Controllers.ps1
#   CONTROLLERS 2 joystick(s)
#     index 0 : Xbox 360 Controller  [Controller (XBOX 360 For Windows)]
#     index 1 : DualSense Wireless Controller  [Wireless Controller]
```

And to close a session — several windows, only one focused, so quitting by hand is fiddly:

```powershell
.\tools\Stop-SplitScreen.ps1
```

The first instance hosts; the rest join `127.0.0.1`. Start order does not matter much: a client
that dials before the host is listening retries for about 90 seconds. Each lands on the party-select screen, where
every player picks their own character with their own controller — the game's existing couch co-op
flow, which already assigns a character to whichever controller claimed it.

The launcher prints each instance's verdict when it is done:

```
instance-p1-55448            connected
    SESSION OK  role=Host networkId=0 isServer=True gui=ChoosingCharacter
    input isolated: keyboard and mouse only
instance-p2-42972            connected
    SESSION OK  role=Join networkId=1 isServer=False gui=ChoosingCharacter
```

`networkId=1` on a client is the real proof: the host assigned it, so the session formed.

## The mod's arguments

Useful on their own if you would rather start instances by hand.

| Argument | Effect |
|---|---|
| `-srhost` | Host a direct-IP session once the main menu is up |
| `-srjoin <ip>` | Join one |
| `-srcontroller <n\|keyboard>` | Give this instance one joystick, or keyboard and mouse, and nothing else |
| `-srmode roguelike` | Roguelike instead of campaign |
| `-srplayer <label>` | Names this instance's log under `BepInEx\splitcoop-logs\` |
| `-srlistcontrollers` | Report the joysticks Rewired sees, with their indices, and do nothing else |
| `-srnofitui` | Leave the UI scaling alone (the corrections above are on by default for a split-screen instance) |
| `-srdumpui` | Log canvas scalers and grid layouts on each screen - how the UI problems above were found |
| `-srshots <dir>` | Have the game screenshot itself on each screen. A desktop grab returns the wallpaper here, because Unity draws into a DirectX surface GDI cannot read |
| `-srautoplay` | Diagnostic: pick the first character, accept, and open the menus, so the in-game screens can be inspected without playing. **Starts a real game - back saves up first** |

Without any of them the mod does nothing at all, so it is safe to leave installed.

## What is verified, and what is not

Measured on Windows 11, Stolen Realm (Unity 2022.3.62), BepInEx 5.4.23.5:

| | |
|---|---|
| Two instances run at once | yes — no single-instance guard in the game |
| Both keep full speed unfocused | yes — ~5s CPU per 8s wall, both in the background |
| They form a session over loopback | **yes** — host `networkId=0`, client `networkId=1` |
| Windows tile correctly | yes — verified by window rectangles, `0,0→1280,1440` and `1280,0→2560,1440` |
| Keyboard/mouse isolation | yes |
| Input survives losing focus | yes — `ignoreInputWhenAppNotInFocus` defaults to **True**, and is turned off in every instance. Without this every window but the focused one ignores its controller, so this one mattered. |
| Client retries a too-early connect | yes — forced by starting the client 45s before the host: first attempt failed, retried twice, joined at 62s |
| UI readable in a tile | yes — verified by screenshot at 1280x1440: party cards and the skill tree render as they do at native, after the canvas correction |
| UI untouched at 16:9 | yes — no canvas or column change at 2560x1440 or 1280x720 |
| Launcher, end to end | yes — driven through UI Automation clicking **Launch**: host reported its session at 39s and player 2 was started at that moment, both reached `SESSION OK` (`networkId=0` / `networkId=1`), windows tiled `0,0 1280x1440` and `1280,0 1280x1440`, launcher minimised itself once seats were filled |
| Claim handshake | yes — player 2's game wrote `ready-1.txt`, listened on the turn it was given (`2 joystick(s) present, 0 already taken`), and the launcher reacted to a seat file by showing the pad, clearing the turn and finishing |
| **A real button press claiming a pad** | **not verified** — two Xbox pads were connected and seen by the game, but nobody was there to press a button. The claim in the test was a simulated `seat-1.txt` in exactly the format the mod writes. |
| **Gamepad isolation** | **not verified** — the game now sees two connected Xbox pads, but binding one needs a person to press a button (or pick it by index) and then check that the other window ignores it. |
| **Actual play** | **not verified** — the tests reach the party-select screen and stop. Picking characters, entering combat and two people acting at once needs a human at each seat. |

Take the last two as untested rather than working.

## Known limitations

**Saves are shared.** All instances read and write the same
`%USERPROFILE%\AppData\LocalLow\Burst2Flame Entertainment\Stolen Realm\`. Two players picking two
different characters write two different files, which is the ordinary case, but nothing here
isolates them and simultaneous writes have not been stress-tested. Run `tools\Backup-Saves.ps1`
before a session. The game keeps its own `.backup` files too, but do not rely on that alone.

**One purchase, several players.** This runs several instances of a single copy through the game's
own LAN feature. That is a shipped feature being used as shipped, with nothing circumvented — but
whether you are comfortable seating more players than you own copies of the game is your call, and
worth a thought before publishing anything built on it.

**Per-instance overhead is real.** Each client is a full game: roughly 2 GB of memory and most of a
CPU core. Four players wants a machine with headroom.

**The host must be up before anyone joins** - but not before they start. Clients dial a socket that
only exists once the host has reached its menu, so the launcher gives the host a head start
(`-HostLeadSeconds`, default 30) and a client that dials too early retries for about 90 seconds.

## Nucleus Co-op

This project began as a Nucleus Co-op handler and stopped needing one. Nucleus's value is running
several instances with isolated input and settings, and the parts of that which Stolen Realm
actually needs — instances, a session, one device each — are all handled here without it, and
without the Steam-identity emulation a Nucleus handler for this game would otherwise require.

`nucleus/` holds a handler for anyone who would rather drive it from Nucleus anyway; it uses the
same mod and the same arguments.

## Layout

```
Launcher/         the launcher app: seating plan, controller claiming, window placement
SplitCoopMod/     BepInEx mod: the command-line arguments, input isolation, UI fitting
tools/            script launcher, publish, controller list, session stop, save backup, tests
nucleus/          optional Nucleus Co-op handler
docs/             how the game's multiplayer and UI actually work
```

## Licence

MIT, for this repository's own code. It ships no game files and no third-party binaries.
