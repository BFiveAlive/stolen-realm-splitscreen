# How Stolen Realm's multiplayer and UI actually work

Everything below was read out of the game's own assembly (`Assembly-CSharp.dll`, unobfuscated,
Unity 2022.3 Mono) or measured at runtime. It is the reasoning behind the design in the README.

## Why one process cannot show two players

The interface assumes exactly one local view, in three separate ways.

**One selection.** `GameLogic.instance.CurrentlySelectedCharacter` — 284 references. The whole UI
reads it: skill bar, targeting, tooltips, character sheet.

**One of everything else.** 88 distinct singleton manager types, among them
`GUIManager.instance` (742 references), `CurrentCharacterUI.Instance` (127) and
`CameraController.instance` (80) — the camera itself is a singleton.

**One of each window, enforced by the type system.**

```csharp
public class LoadableUIWindow<T> : UIWindow
{
    public static T Instance { get; set; }   // one slot per window TYPE
}
```

34 window types resolve through a static `Instance` — inventory, skill tree, shop, crafting,
stash, transmog. There is nowhere to put a second inventory, so two players cannot both have one
open.

Making that per-player means threading a player identity through roughly 1,200 call sites that
have no parameter for it. Harmony cannot invent context that the call sites never had.

## What the game already does for couch co-op

More than it appears to.

```csharp
public virtual int ControllerPlayerId { get; set; }                       // on Character
public Player ControllerPlayer => ReInput.players.GetPlayer(ControllerPlayerId);
```

Input is **Rewired**, which is built around players owning controllers. At the party-select screen
`ToggleSelectedCharacter(int controllerPlayerId)` assigns whichever controller pressed the button
as that character's owner, and `NetworkingManager.MyPartyControllerPlayerIdToCharacters` maps
controller to character. Disconnecting a pad transfers ownership and reconnecting hands it back.

But it is all one view. `VirtualInput.DefaultPlayer` resolves to the Rewired player owning the
*currently selected* character, and `GetButtonDownAndSwitchToPlayerCharacter` switches that global
selection to whoever last pressed something. So stock couch co-op is several controllers taking
turns driving one screen. Out of combat the camera even centres on the **average** position of all
local players, which is what forces everyone to stay in frame together.

## Turns belong to a team, not a character

```csharp
public bool IsMyTurn => GameLogic.instance.currentTeamTurnIndex == TeamIndex;
```

`CurrentTurnCharacters(teamIndex)` returns every character on the active team. There is no
initiative order. `Acting` and `Moving` are per-character, and the only place the game consults
them globally is the turn transition:

```csharp
// StartNewTurnSequence - waits for animations before flipping the turn
while (Time.time < maxWaitingTime && (Root.AnyActingCharactersInBattle || Root.AnyMovingCharactersInBattle))
    yield return new WaitForEndOfFrame();
```

That is not an input gate. Nothing stops a second character being commanded while the first is
mid-action — except that there is only one selection and one skill bar to command it with.

## The direct-IP path

The game ships LAN multiplayer, gated behind a menu.

```csharp
public void Host()
{
    EnsureSteamConnection();
    NetAddress address = NetAddress.AnyIp(9055);
    SocketManager = SteamNetworkingSockets.CreateNormalSocket(address, this);
    ServerType = ServerType.Server;
}

public void HostMultiplayerIP(bool useSaveData)
{
    ...
    NetworkingManager.Instance.NetworkManager.Host();
    Root.UsingSteam = false;              // <- explicitly not the Steam relay
    Root.PlayingMultiplayer = true;
}
```

A plain UDP socket, and identity is a host-assigned `NetworkId` (`IsServer => NetworkId == 0`), not
a SteamID. Steam's networking library is still the transport, so Steam must be running, but two
processes under one login are perfectly distinguishable to the game.

One gate matters: `SteamNetworkingSockets` refuses unauthenticated peers by default, which is every
peer in a same-machine session. The game's own helper lifts it:

```csharp
public static void AllowDirectIP()   // MultiplayerFeatureTest
{
    // reflection into Facepunch.Steamworks:
    //   SteamNetworkingUtils.Internal.SetGlobalConfigValueInt32(NetConfig.IP_AllowWithoutAuth, 2)
}
```

### The developers left a test harness in the build

`MultiplayerFeatureTest` drives hosting and joining and reports `MPRUN HOST READY` /
`MPRUN JOIN READY`, and there is a shipped `-autoclient <ip>` command-line flag. Those were the
obvious things to call — and `HostIP`/`JoinIP` turned out to be `async` over UniTask and never got
past their first `await` here, emitting none of their own output. So the mod walks the same steps
synchronously instead, calling exactly the methods those routines call.

## Two traps worth knowing

**Objects this mod creates are never ticked.** A timer on the plugin itself never fires: its
GameObject is destroyed during the first scene load — `OnDisable` and `OnDestroy` run and `Start`
never does. Creating a fresh `DontDestroyOnLoad` object does not help either; its `Start` never
ran. Neither failure throws anything. Meanwhile the game's own components tick normally, which is
why the mod hangs its clock on a Harmony postfix on `GUIManager.Update`.

Anything relying on Unity's player loop from mod-owned objects — including UniTask continuations —
should be assumed broken here until proven otherwise.

**Each instance needs its own Unity log.** They all write
`%USERPROFILE%\AppData\LocalLow\...\Player.log` otherwise, and "which instance failed" becomes
unreadable. `-logFile <path>` per instance fixes it; the mod additionally writes a per-process
trace under `BepInEx\splitcoop-logs\`.

## Why the UI breaks in a tile, and what fixes it

The canvases are authored against 800x600 and match on **height**:

```
SCALER 'GUI Manager' mode=ScaleWithScreenSize ref=800x600 match=1.00 -> canvas=2.400
```

So the canvas scale is the same 2.4 at 1280x1440 as at 2560x1440, and text is not being shrunk.
What changes is the logical width the layout has: 2560/2.4 = 1067 units on a normal screen, but
1280/2.4 = 533 in a tile. Half the horizontal room, same everything else.

Two visible consequences, both measured rather than guessed:

- The party list is an `AutoExpandGridLayoutGroup` with `constraint=FixedColumnCount count=2`. It
  does not draw cells at `cellSize`; it stretches them to fill the row -
  `(holderWidth - spacing * (columns - 1)) / columns`. At 2560x1440 the holder is 442 units and
  each card gets 247. In a tile the holder was 176 and the cards were far below anything their
  contents fit in, so names rendered as "TRB" and "Health" as "Hea".
- The skill tree simply ran off the edge: four of its eleven trees and the whole left-hand column
  of skills were outside the screen, and the header labels drew on top of each other.

### The correction

Raise the reference height so the logical width returns to its 16:9 value:

```
referenceHeight = authoredHeight * (16/9) / screenAspect      // 600 -> 1200 at 1280x1440
```

The character list holder then measures 442 in a tile - the same number as at 2560x1440 - and every
horizontal layout lands where it was designed to. The extra goes into logical height, which a tall
window has to spare.

Two things worth knowing about the approach that did **not** work. Changing
`matchWidthOrHeight` to match width instead is the obvious move and is not enough: it yields 800
logical units, better than 533 but still short of 1067, and the party cards came out at 211 units
where the game gives them 247 - still overlapping. And the authored `cellSize.x` (163.8) is not the
width the card contents need; it is only what the *Flexible* branch uses to count cells, so it
makes a poor threshold.

### The Attributes panel

The canvas correction adds logical height to a tall window, and one panel took it badly. In
`InventoryManager.ApplyStyle` the character menu gives the Attributes panel a flexible height share
of the left column:

```csharp
component.flexibleHeight = Style.AttributesHeightShare;   // 0.52; Stats takes 0.6, the bars 0.04
```

Its name and value columns are VerticalLayoutGroups with fixed top and bottom padding, so the five
rows spread over whatever height the panel gets. The + column is anchored separately, at
`Style.AddButtonsOffset` from the panel's top-right with its own fixed spacing. At 16:9 the two agree.
With twice the logical height the panel roughly doubled, the rows spread about twice as far, and the
buttons stayed bunched under Might. The game's own tooltip on `AttributeRowsBottomPadding` describes
the same effect: raise the height share without the padding and "the five rows ... spread out".

In a window narrower than 16:9, `AttributePanelFit` replaces the share with a fixed height:

```
height = (panel height - column height)          // title, level text, bar
       + top padding + bottom padding
       + rows * button spacing - layout spacing
```

The button spacing is read from the + column's children in local space, so it works while that
column is hidden (no points to spend). The panel is then only as tall as its contents, each row sits
level with its button, and the Stats list below takes the height back. `ApplyStyle` restores the
share whenever it runs, so the fit is re-applied from `UiFit`'s twice-a-second tick.

### Screenshots

`ScreenCapture.CaptureScreenshot` from inside the game is the only way to see any of this. A
desktop capture through GDI (`CopyFromScreen`) returns the wallpaper, because Unity renders into a
DirectX surface GDI cannot read - which looks exactly like a black or missing window and is easy to
misread as the game having failed.

## The controller change that broke the game

After the first real session with two gamepads, Stolen Realm stopped starting properly on the
test machine - every launch, with every mod disabled, and launched through Steam. About 14,000
`NullReferenceException`s a minute, every one of them tracing back to
`NetworkingManager.Instance.NetworkManager.Root` never being created. In the session itself, the
second player's window had been broken the same way from the start, which is what produced a
character with ~1,000 health on one screen and ~120 in play, and a stats panel whose + buttons all
sat under Might.

Two things combined:

**Removing player 0's keyboard and mouse.** Each window used to clear every controller from
every Rewired player and then give player 0 one gamepad - which also took player 0's keyboard
and mouse away. The game does not survive that before it has set up its session. Measured
directly, with the crash reporter swallowing the original exception each time:

| Window | Keyboard and mouse | When the pad was assigned | Result |
|---|---|---|---|
| pad window | removed | 8-12s, during start-up | broken, every frame |
| pad window | removed | 17s, after the main menu but before hosting | broken, right after "chose campaign mode" |
| pad window | removed | 24s, after its session had formed | fine |
| keyboard window | kept | any time | fine |
| pad window | **kept** | after the main menu | **fine: hosted, session formed, character creation opened, 0 errors** |

So windows now move only gamepads: every player's joysticks are cleared, player 0 gets its own
pad, and the keyboard and mouse stay where the game put them. As a precaution controllers are
also left alone until the main menu and the network Root exist, plus a few seconds.

**Persistence.** Rewired's `UserDataStore_PlayerPrefs.SaveControllerAssignments` writes who owns
which device to `HKCU\Software\Burst2Flame Entertainment\Stolen Realm` and loads it at the next
start. A split-screen window's "player 0 has one pad and no keyboard" was saved, so every later
launch recreated the broken start. Split-screen windows now never save controller assignments,
and the launcher deletes the damaged value if it finds it. Deleting that one value was the whole
repair - saves were untouched, and setting aside the day's save files first made no difference.

## Saves with several windows

Read from the game's save code. Every save is `WriteAllText(X.temp)` then
`File.Replace(X.temp, X, X.backup)`, which is atomic for a single writer.

- **Quest files** (`QuestSaveCampaign.json`, `QuestSaveRoguelike.json`): `SaveQuestState` returns
  unless `NetworkingManager.Instance.IsServer`. Only the host writes them.
- **Character files**: `Character.Save` returns unless the character is in this window's
  `AllMyCharacters`. Players write different files.
- **New character numbers**: `Character.Load(-1)` takes the lowest number not on disk, and the
  file is written later, so two windows creating characters together could choose the same one.
  The mod picks under a machine-wide mutex and records each pick for the other windows.
- **`GlobalSaveData.json` and transmog data**: written by every window with the same temp name.
  Simultaneous saves could collide, so the mod makes them take turns under the same mutex. That
  prevents collisions but not lost updates: each window serialises its own in-memory copy, so the
  last window to save a shared file wins.

## Choosing controllers before any game starts

The launcher reads Xbox-style pads itself, through Windows XInput, so players can join and pick
their screens before a single game window exists. That only helps if the pad a player pressed A on
in the launcher is provably the pad their game window gets, and it is. The link is XInput's four
slots:

- **Windows** numbers connected XInput pads by slot, 0-3. The launcher polls `XInputGetState` for
  each slot, so it knows the slot of every pad that presses a button.
- **Rewired**, the game's input library, creates its XInput devices from those same slots. In
  `Rewired_Windows.dll` it builds four devices in a loop, `for (int i = 0; i < 4; i++)`, each
  wrapping XInput user index `i`. The device reports that index as the joystick's `systemId`
  and names the pad `"XInput " + subType + " " + (i + 1)`, which is why pads show up as
  "XInput Gamepad 1" and "XInput Gamepad 2".

So the launcher passes `-srcontroller xinput:N`, and the mod gives the window the Rewired joystick
that is XInput with `systemId == N`. Measured on the test machine with two pads: Windows reported
slots 0 and 1 connected, and the game listed `index 0 : XInput Gamepad 1  xinput:0` and
`index 1 : XInput Gamepad 2  xinput:1`.

Matching by slot is also sturdier than Rewired's joystick index. The index depends on detection
order and shifts when another controller is plugged in; the slot does not.

The limits come from XInput. It has four slots, so a fifth or sixth pad, or any non-XInput
controller such as a DualSense, has no slot to match. Those reach the game through Raw Input or
DirectInput with identifiers the launcher cannot line up with Rewired's, so they keep the in-game
press-a-button claim below. The launcher also writes the slots it handed out to
`xinput-reserved.txt`, so a window claiming in game never takes a pad another window already owns.

## Claiming a controller by pressing a button on it

Giving each window a pad by index works, but it needs someone to enumerate the controllers first
and type the right numbers, and Windows' numbering is not Rewired's. Pressing a button on the pad
you are holding is how every couch game does it, so the launcher does that instead.

The catch is that the launcher cannot see which pad a press came from. Rewired owns the devices,
and Rewired runs inside each game. So the claiming happens in the games, and the launcher only
takes turns, through a directory both sides can read:

```
turn.txt      launcher -> games   the seat that should listen next, or -1
ready-N.txt   game N -> launcher  loaded, and can hear its controllers
seat-N.txt    game N -> launcher  index=, name=, hardware= of the pad it took
```

The seat whose turn it is polls `Joystick.GetAnyButtonDown()` on every joystick no `seat-*.txt`
has taken, binds the first one pressed, and writes its seat file. Only one seat listens at a time,
so two windows cannot both claim one press, and every window excludes what the others took
because it reads their files rather than trusting anything in its own memory.

Two details that matter:

- **The poll runs every frame.** `GetAnyButtonDown` is true for the single frame the button goes
  down. The file reads are throttled to four a second; sampling the button at that rate would miss
  most presses.
- **Every window must be hearing input while unfocused**, since the launcher holds focus during
  claiming. That is the `ignoreInputWhenAppNotInFocus` fix above, and this would not work without
  it.

Joystick indices are compared across processes, which assumes every instance enumerates the pads
in the same order. They are the same program reading the same device list at the same time, and
the index-based `-srcontroller <n>` has always relied on the same thing.

The launcher also carries the mod as an embedded resource and copies it into the game when the
installed one differs. The two halves of this exchange are only useful together, and this makes
it impossible to pair a launcher with a mod too old to answer it.
