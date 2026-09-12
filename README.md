# Stolen Realm Split-Screen

Local split-screen co-op for [Stolen Realm](https://store.steampowered.com/app/1330000/), on one
PC, one screen, one controller each.

It runs one copy of the game per player, joins them into a single session over the loopback
address, pins each copy to one input device, and lays the windows out across the screen.

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

## Requirements

- Stolen Realm on Steam, with Steam running (its networking library is the transport even for a
  direct-IP session)
- [BepInEx 5.4.23.5 (win_x64)](https://github.com/BepInEx/BepInEx/releases) installed in the game
  folder
- .NET SDK 8 to build the mod
- PowerShell 7 (`pwsh`) for the launcher
- One input device per player

## Install

```powershell
dotnet build SplitCoopMod\SplitCoopMod.csproj -c Release
```

The build copies `SplitCoopMod.dll` into `BepInEx\plugins\SplitCoopMod\`. Override the game path
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
| **Gamepad isolation** | **not verified** — no controller was attached to the test machine. The code path runs and reports `waiting for joystick 0; 0 present`, which is the correct behaviour with none plugged in, but binding a real pad has not been exercised. |
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
SplitCoopMod/     BepInEx mod: the command-line arguments and input isolation
tools/            launcher, controller list, session stop, save backup, test harnesses
nucleus/          optional Nucleus Co-op handler
docs/             how the game's multiplayer and UI actually work
```

## Licence

MIT, for this repository's own code. It ships no game files and no third-party binaries.
