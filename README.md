# Stolen Realm Split-Screen

> [!NOTE]
> **This project was created with AI.** The code, tools and documentation were written with the help
> of an AI coding assistant (Claude, by Anthropic), directed and play-tested by a person. Review it as
> you would any third-party mod, and please report problems through the repository's issues.

Local split-screen co-op for [Stolen Realm](https://store.steampowered.com/app/1330000/), on one
PC, one screen, one controller each.

It runs one copy of the game per player, joins them into a single session over the loopback
address, pins each copy to one input device, and lays the windows out across the screen.

## Playing

Run **`StolenRealmSplitScreen.exe`**. No scripts, no command lines.

1. **Pick the game** (Campaign or Roguelike) and, if you like, the screen layout.
2. **Join.** Each player presses any button on their controller. Their pad rumbles and a tile
   appears for them on the picture of your monitors. Keyboard & mouse joins with **Enter**.
   Up to **six players**, matching Stolen Realm's party size.
3. **Choose where you sit**, with your own controller:

   | Control | Action |
   |---|---|
   | D-pad or left stick | move within your monitor, swapping places with the player beside you; at the edge, move onto the next monitor and share it |
   | LB / RB | move to the previous / next monitor |
   | A | ready |
   | B | not ready; press again to leave |
   | Start | once everyone is ready, launch without waiting |

   Keyboard: arrows move, Page Up/Down change monitor, Enter readies, Esc cancels or leaves.
   Mouse: drag a player onto another to swap, onto the edge of one to share that screen, or onto
   an empty monitor; click to ready; right-click to remove. **One each** and **All on main** are
   shortcuts when you have more than one monitor.
4. **Everyone ready starts a five-second countdown**, then the games launch with every
   controller already assigned. Nobody presses anything in the games to claim a pad.

Each monitor is split automatically for the players on it: side by side or stacked for two, a
2×2 grid for three or four, and 3×2 for five or six.

**Controllers 5 and 6, and non-Xbox pads.** Windows' XInput, which the launcher reads before the
games start, has four slots. Xbox, PowerA, most 8BitDo pads and anything shown as "XInput
compatible" use it. For a fifth or sixth player, or a PlayStation or other controller, use
**+ Other controller**: that player presses a button on their pad once their game has loaded,
and the launcher highlights whose turn it is.

Each window then reaches the game's party-select screen, where every player picks their own
character.

While playing, the launcher sits in the taskbar. Open it to swap two players' windows, give a
latecomer a controller, or **Stop**, which closes every game window at once.

The launcher remembers your last setup, finds the game through Steam's library list, and carries
the mod inside itself — on Launch it installs SplitCoopMod into the game, or updates it if it is
out of date.

**It installs BepInEx for you, too.** If BepInEx isn't in the game folder yet, pressing Launch
downloads BepInEx 5.4.23.5 (win_x64) from [BepInEx's official GitHub
release](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5), checks it against a pinned
SHA-256 checksum (the same one the Stolen Realm mod installer uses), and unpacks it into the game
folder before the games start. That needs an internet connection the first time only. If the
download or the check fails, nothing is installed; the launcher tells you why, and you can press
Launch again or install BepInEx yourself. BepInEx adds a loader beside the game's executable and
changes none of the game's own files.

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


## Requirements

- Stolen Realm on Steam, with Steam running (its networking library is the transport even for a
  direct-IP session)
- [BepInEx 5.4.23.5 (win_x64)](https://github.com/BepInEx/BepInEx/releases) in the game folder. The
  launcher installs it on your first Launch if it's missing, so this needs no action unless that
  download fails (it needs an internet connection that one time)
- One input device per player



## Known limitations

**Saves are shared, and the mod guards what it can.** Every window is the same install under the
same Windows user, so they share one save folder
(`%USERPROFILE%\AppData\LocalLow\Burst2Flame Entertainment\Stolen Realm\`) and one set of registry
settings. What that means in practice, read from the game's save code:

| File | Who writes it | Risk with several windows |
|---|---|---|
| `QuestSaveCampaign.json` / `QuestSaveRoguelike.json` | host only | none |
| `CharacterN.json` | the window that owns that character | none in normal play - each player saves their own |
| New character file numbers | whichever window creates one | two windows creating at once could pick the same `N`. **Guarded:** numbers are picked under a shared lock and reserved |
| `GlobalSaveData.json`, transmog data | every window | the game writes `X.temp` then swaps it in; simultaneous saves could collide. **Guarded:** saves take turns. Still last-writer-wins, since each window keeps its own copy in memory |
| Controller assignments (registry) | every window, when it closes | **this broke the game once.** A split-screen window's controller setup was saved and loaded by every later launch, modded or not, which left player 1 with no keyboard or mouse and the game unable to start. **Fixed:** split-screen windows never save their controller assignments |

The game's own `.backup` files and `tools\Backup-Saves.ps1` are still worth using before a session.

If the game ever starts throwing errors on every launch after a split-screen session, the saved
controller assignments are the first thing to reset: delete the
`RewiredSaveData_ControllerAssignments_*` value under
`HKEY_CURRENT_USER\Software\Burst2Flame Entertainment\Stolen Realm`, and the game rebuilds it.

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
