using System;
using System.Globalization;
using System.Text;
using Rewired;
using UnityEngine;

namespace SplitCoopMod
{
    /// <summary>
    /// Gives one instance one input device, and keeps it listening while another window has focus.
    ///
    /// Two separate problems, both fatal to split-screen on their own.
    ///
    /// The first is sharing: every instance is a full copy of the game and Rewired in each of them
    /// sees every controller on the machine, so without this a single stick nudge moves a character
    /// in all of them at once. Rewired is built around players owning controllers, so the fix is to
    /// take the gamepads away from every player, hand player 0 the chosen one, and turn
    /// auto-assignment off so it is not undone when a controller is re-detected.
    ///
    /// The second is focus. Only one window can be focused, and Rewired defaults to
    /// <c>ignoreInputWhenAppNotInFocus</c>, which would leave every player but one holding a dead
    /// controller. Unity's own <c>runInBackground</c> is set alongside it, because a window that is
    /// not simulating cannot act on input it does receive.
    /// </summary>
    internal static class InputIsolation
    {
        /// <summary>
        /// -srcontroller:
        ///   xinput:N   the Xbox-style pad in Windows XInput slot N (0-3) - what the launcher sends
        ///   keyboard   keyboard and mouse, no gamepads
        ///   claim      the player will press a button on their pad in game (see SeatClaim)
        ///   N          Rewired's joystick index, for starting instances by hand
        /// </summary>
        internal static string Requested;

        /// <summary>-srlistcontrollers: log what Rewired sees and which index each one is.</summary>
        internal static bool ListOnly;

        internal static Action<string> Trace;

        private static bool backgroundInputSet;
        private static bool applied;
        private static bool listed;
        private static int attempts;

        internal static bool Wanted => !string.IsNullOrEmpty(Requested) || ListOnly;

        private enum Resolution { Keyboard, Joystick, NotPresent, Invalid }

        /// <summary>
        /// Retried until Rewired reports ready and the requested device is actually present.
        ///
        /// Controllers are enumerated a moment after the process starts, so the first few attempts
        /// legitimately find nothing; giving up at once would isolate nothing on a slow start.
        /// </summary>
        internal static void TryApply()
        {
            if (!Wanted)
                return;

            try
            {
                if (!ReInput.isReady)
                    return;

                AllowInputWhileUnfocused();

                if (ListOnly)
                {
                    if (!listed && ReInput.controllers.joystickCount > 0)
                    {
                        listed = true;
                        Trace(Describe());
                    }
                    else if (!listed && ++attempts > 120)
                    {
                        listed = true;
                        Trace("CONTROLLERS none detected");
                    }
                    return;
                }

                if (applied)
                    return;

                // Not while the game is still starting; controllers are left alone until the main
                // menu and network layer exist. See docs/how-it-works.md for what went wrong when
                // input was rearranged earlier.
                if (!GameInitialised())
                    return;

                // "claim" means nobody has chosen a device for this window yet: the player is about
                // to choose it by pressing a button on it. Until they do there is nothing to assign.
                if (SeatClaim.Enabled)
                {
                    SeatClaim.Announce();
                    SeatClaim.Tick();
                    return;
                }

                int index;
                switch (Resolve(out index))
                {
                    case Resolution.Invalid:
                        Say("-srcontroller '" + Requested + "' is not xinput:N, keyboard, claim or a joystick index; leaving input alone");
                        applied = true;
                        return;

                    case Resolution.NotPresent:
                        if (++attempts % 30 == 0)
                            Say("waiting for controller " + Requested + "; " + ReInput.controllers.joystickCount + " joystick(s) present");

                        if (attempts > 300)
                        {
                            Say("giving up: controller " + Requested + " never appeared");
                            applied = true;
                        }
                        return;

                    case Resolution.Keyboard:
                        Assign(true, -1);
                        break;

                    default:
                        Assign(false, index);
                        break;
                }

                applied = true;

                // Rewired re-assigns on a hotplug, which would hand this instance somebody else's
                // pad mid-session. Re-running the assignment on those events is cheaper and more
                // reliable than trying to prevent them.
                ReInput.ControllerConnectedEvent += OnControllerChanged;
                ReInput.ControllerDisconnectedEvent += OnControllerChanged;
            }
            catch (Exception e)
            {
                Say("could not isolate input: " + e.Message);
                applied = true;
            }
        }

        private static Resolution Resolve(out int index)
        {
            index = -1;

            if (Requested.Equals("keyboard", StringComparison.OrdinalIgnoreCase))
                return Resolution.Keyboard;

            if (Requested.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase))
            {
                int slot;
                if (!int.TryParse(Requested.Substring("xinput:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)
                    || slot < 0 || slot > 3)
                    return Resolution.Invalid;

                index = FindXInputJoystick(slot);
                return index >= 0 ? Resolution.Joystick : Resolution.NotPresent;
            }

            if (!int.TryParse(Requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                return Resolution.Invalid;

            return index >= 0 && index < ReInput.controllers.joystickCount ? Resolution.Joystick : Resolution.NotPresent;
        }

        /// <summary>
        /// Rewired's index for the pad in Windows XInput slot <paramref name="slot"/>, or -1.
        ///
        /// Rewired creates its XInput devices from slots 0-3 and reports each slot as the joystick's
        /// <c>systemId</c> - read from Rewired_Windows: the same number names the pad "XInput Gamepad
        /// slot+1". A launcher reading XInput directly sees the same slots, which is what makes it
        /// possible to choose controllers before any game is running. Unlike Rewired's index, the
        /// slot does not change when some other controller is plugged in.
        /// </summary>
        internal static int FindXInputJoystick(int slot)
        {
            for (int i = 0; i < ReInput.controllers.joystickCount; i++)
            {
                Joystick joystick = ReInput.controllers.Joysticks[i];
                if (joystick != null && IsXInput(joystick) && joystick.systemId.HasValue && joystick.systemId.Value == slot)
                    return i;
            }

            return -1;
        }

        internal static bool IsXInput(Joystick joystick)
        {
            string hardware = joystick.hardwareIdentifier ?? string.Empty;
            string name = joystick.name ?? string.Empty;

            return hardware.IndexOf("XInput", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.StartsWith("XInput", StringComparison.OrdinalIgnoreCase);
        }

        private static float initialisedSince = -1f;
        private static bool announcedWait;

        /// <summary>
        /// True once the game has finished starting, plus a few seconds to settle: the main menu
        /// exists and the network layer's Root has been created.
        /// </summary>
        private static bool GameInitialised()
        {
            bool ready;
            try
            {
                ready = GUIManager.instance != null
                        && MainMenu.Instance != null
                        && NetworkingManager.Instance != null
                        && NetworkingManager.Instance.NetworkManager != null
                        && NetworkingManager.Instance.NetworkManager.Root != null;
            }
            catch
            {
                ready = false;
            }

            if (!ready)
            {
                initialisedSince = -1f;

                if (!announcedWait)
                {
                    announcedWait = true;
                    Say("holding controller assignment until the game has finished starting");
                }

                return false;
            }

            if (initialisedSince < 0f)
                initialisedSince = Time.realtimeSinceStartup;

            return Time.realtimeSinceStartup - initialisedSince >= 3f;
        }

        private static void OnControllerChanged(ControllerStatusChangedEventArgs args)
        {
            try
            {
                if (Requested == null)
                    return;

                int index;
                switch (Resolve(out index))
                {
                    case Resolution.Keyboard:
                        Assign(true, -1);
                        break;
                    case Resolution.Joystick:
                        Assign(false, index);
                        break;
                    default:
                        Say("after a controller change, controller " + Requested + " is not present");
                        break;
                }
            }
            catch (Exception e)
            {
                Say("re-isolating after a controller change failed: " + e.Message);
            }
        }

        /// <summary>
        /// Takes the joystick a player just claimed by pressing a button on it.
        ///
        /// <see cref="Requested"/> is rewritten as well as assigned, so that the hotplug handler -
        /// which re-reads it whenever a controller comes or goes - keeps handing this window the same
        /// pad. An XInput pad is remembered by slot, which survives other pads being plugged in.
        /// </summary>
        internal static void AdoptJoystick(int index)
        {
            Joystick joystick = ReInput.controllers.Joysticks[index];

            Requested = IsXInput(joystick) && joystick.systemId.HasValue
                ? "xinput:" + joystick.systemId.Value.ToString(CultureInfo.InvariantCulture)
                : index.ToString(CultureInfo.InvariantCulture);

            Assign(false, index);
            applied = true;

            ReInput.ControllerConnectedEvent += OnControllerChanged;
            ReInput.ControllerDisconnectedEvent += OnControllerChanged;
        }

        private static void Assign(bool keyboard, int index)
        {
            // Off first, so nothing this method does gets undone a frame later.
            ReInput.configuration.autoAssignJoysticks = false;

            // Only gamepads are moved. An earlier version cleared every controller, keyboard and
            // mouse included, and the game does not survive player 0 losing its keyboard.
            foreach (Player player in ReInput.players.AllPlayers)
                player.controllers.ClearControllersOfType(ControllerType.Joystick);

            Player mine = ReInput.players.GetPlayer(0);
            mine.controllers.hasKeyboard = true;
            mine.controllers.hasMouse = true;

            if (keyboard)
            {
                Say("input isolated: keyboard and mouse, no gamepads");
            }
            else
            {
                Joystick joystick = ReInput.controllers.Joysticks[index];
                mine.controllers.AddController(joystick, removeFromOtherPlayers: true);
                Say("input isolated: joystick " + index + " (" + joystick.name
                    + (joystick.systemId.HasValue ? ", system id " + joystick.systemId.Value : string.Empty)
                    + ") only, keyboard and mouse left in place");
            }
        }

        /// <summary>-srkeepkeyboard: diagnostic. Leave keyboard and mouse active in a gamepad window.</summary>
        internal static bool KeepKeyboardInPadWindow;

        private static float nextEnforceAt;
        private static int enforcementReports;
        private static int keyboardPresses;
        private static int keyboardLeaks;
        private static int reportedPresses;
        private static float nextLeakReportAt;

        /// <summary>This window has been given a gamepad (by XInput slot, index, or an in-game claim).</summary>
        private static bool OwnsGamepad
        {
            get
            {
                if (!applied || string.IsNullOrEmpty(Requested))
                    return false;

                int unused;
                return Requested.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase)
                       || int.TryParse(Requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out unused);
            }
        }

        /// <summary>
        /// Stops the keyboard and mouse driving a window that belongs to a gamepad player.
        ///
        /// A gamepad window keeps the keyboard and mouse assigned to its players, because taking them
        /// away stopped the game starting at all. But every window also hears input while unfocused,
        /// so the keyboard player's keys and mouse were driving the gamepad player's game too.
        ///
        /// Instead of unassigning the devices, their controller maps are switched off - the devices
        /// stay where the game expects them and simply trigger no actions - for every Rewired player,
        /// since some of the game's input reads all players rather than player 0. The Rewired UI input
        /// module reads the mouse directly for menus rather than through maps, so its mouse input is
        /// switched off as well. Re-applied every second, because Rewired's key-rebinding screen can
        /// reload default maps, which come back enabled.
        /// </summary>
        internal static void Enforce()
        {
            if (!OwnsGamepad)
                return;

            try
            {
                if (!ReInput.isReady)
                    return;

                ProbeKeyboardLeaks();

                if (KeepKeyboardInPadWindow || Time.realtimeSinceStartup < nextEnforceAt)
                    return;

                nextEnforceAt = Time.realtimeSinceStartup + 1f;

                int maps = 0;
                foreach (Player player in ReInput.players.AllPlayers)
                {
                    maps += DisableMaps(player, ControllerType.Keyboard);
                    maps += DisableMaps(player, ControllerType.Mouse);
                }

                int modules = 0;
                foreach (var module in UnityEngine.Object.FindObjectsOfType<Rewired.Integration.UnityUI.RewiredStandaloneInputModule>())
                {
                    if (module.allowMouseInput)
                    {
                        module.allowMouseInput = false;
                        modules++;
                    }
                }

                if ((maps > 0 || modules > 0) && enforcementReports++ < 10)
                    Say("keyboard and mouse switched off for this gamepad window: " + maps + " map(s), "
                        + modules + " UI input module(s)");
            }
            catch (Exception e)
            {
                Say("could not switch off keyboard and mouse for this gamepad window: " + e.Message);
                KeepKeyboardInPadWindow = true;   // do not retry a failing call every second
            }
        }

        private static int DisableMaps(Player player, ControllerType type)
        {
            var maps = player.controllers.maps.GetMaps(type, 0);
            if (maps == null)
                return 0;

            int changed = 0;
            foreach (ControllerMap map in maps)
            {
                if (map != null && map.enabled)
                {
                    map.enabled = false;
                    changed++;
                }
            }

            return changed;
        }

        /// <summary>
        /// Counts keyboard presses this gamepad window hears, and how many of them also produced a game
        /// action on any player in the same frame. With the guard working, presses are still heard -
        /// the device is live - but none should reach an action.
        /// </summary>
        private static void ProbeKeyboardLeaks()
        {
            var keyboard = ReInput.controllers.Keyboard;
            if (keyboard != null && keyboard.GetAnyButtonDown())
            {
                keyboardPresses++;

                foreach (Player player in ReInput.players.AllPlayers)
                {
                    if (player.GetAnyButtonDown())
                    {
                        keyboardLeaks++;
                        break;
                    }
                }
            }

            if (keyboardPresses != reportedPresses && Time.realtimeSinceStartup >= nextLeakReportAt)
            {
                reportedPresses = keyboardPresses;
                nextLeakReportAt = Time.realtimeSinceStartup + 2f;
                Say("INPUTPROBE keyboard presses heard by this gamepad window: " + keyboardPresses
                    + ", that reached game actions: " + keyboardLeaks);
            }
        }

        /// <summary>
        /// Keeps this instance reading its controller while a different window holds focus.
        /// </summary>
        private static void AllowInputWhileUnfocused()
        {
            if (backgroundInputSet)
                return;

            backgroundInputSet = true;

            try
            {
                bool was = ReInput.configuration.ignoreInputWhenAppNotInFocus;
                ReInput.configuration.ignoreInputWhenAppNotInFocus = false;

                // The player loop has to keep running too, or there is nothing to act on the input.
                Application.runInBackground = true;

                Say("background input enabled (ignoreInputWhenAppNotInFocus was " + was + ")");
            }
            catch (Exception e)
            {
                Say("could not enable background input: " + e.Message);
            }
        }

        /// <summary>The joystick list, with the index and XInput slot each one answers to.</summary>
        private static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("CONTROLLERS ").Append(ReInput.controllers.joystickCount).Append(" joystick(s)");

            for (int i = 0; i < ReInput.controllers.joystickCount; i++)
            {
                Joystick j = ReInput.controllers.Joysticks[i];
                sb.Append(Environment.NewLine)
                  .Append("  index ").Append(i).Append(" : ").Append(j.name)
                  .Append("  [").Append(j.hardwareName).Append("]")
                  .Append(IsXInput(j) && j.systemId.HasValue ? "  xinput:" + j.systemId.Value : "  (not XInput)");
            }

            return sb.ToString();
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
