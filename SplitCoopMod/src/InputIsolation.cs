using System;
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
    /// clear what it auto-assigned, hand player 0 the chosen device, and turn auto-assignment off
    /// so it is not undone when a controller is re-detected.
    ///
    /// The second is focus. Only one window can be focused, and Rewired defaults to
    /// <c>ignoreInputWhenAppNotInFocus</c>, which would leave every player but one holding a dead
    /// controller. Unity's own <c>runInBackground</c> is set alongside it, because a window that is
    /// not simulating cannot act on input it does receive.
    /// </summary>
    internal static class InputIsolation
    {
        /// <summary>-srcontroller: a joystick index, or "keyboard" for the mouse-and-keyboard seat.</summary>
        internal static string Requested;

        /// <summary>-srlistcontrollers: log what Rewired sees and which index each one is.</summary>
        internal static bool ListOnly;

        internal static Action<string> Trace;

        private static bool backgroundInputSet;
        private static bool applied;
        private static bool listed;
        private static int attempts;

        internal static bool Wanted => !string.IsNullOrEmpty(Requested) || ListOnly;

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

                // "claim" means nobody has chosen a device for this window yet: the player is about
                // to choose it by pressing a button on it. Until they do there is nothing to assign.
                if (SeatClaim.Enabled)
                {
                    SeatClaim.Announce();
                    SeatClaim.Tick();
                    return;
                }

                bool keyboard = Requested.Equals("keyboard", StringComparison.OrdinalIgnoreCase);
                int index = -1;

                if (!keyboard && !int.TryParse(Requested, out index))
                {
                    Say("-srcontroller '" + Requested + "' is neither a number nor \"keyboard\"; leaving input alone");
                    applied = true;
                    return;
                }

                if (!keyboard && (index < 0 || index >= ReInput.controllers.joystickCount))
                {
                    if (++attempts % 30 == 0)
                        Say("waiting for joystick " + index + "; " + ReInput.controllers.joystickCount + " present");

                    if (attempts > 300)
                    {
                        Say("giving up: joystick " + index + " never appeared");
                        applied = true;
                    }
                    return;
                }

                Assign(keyboard, index);
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

        private static void OnControllerChanged(ControllerStatusChangedEventArgs args)
        {
            try
            {
                if (Requested == null)
                    return;

                bool keyboard = Requested.Equals("keyboard", StringComparison.OrdinalIgnoreCase);
                int index;

                if (keyboard)
                {
                    Assign(true, -1);
                    return;
                }

                if (int.TryParse(Requested, out index)
                    && index >= 0 && index < ReInput.controllers.joystickCount)
                {
                    Assign(false, index);
                }
                else
                {
                    Say("after a controller change, joystick " + Requested + " is not present");
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
        /// <see cref="Requested"/> is rewritten to the index as well as assigned, so that the
        /// hotplug handler below - which re-reads it whenever a controller comes or goes - keeps
        /// handing this window the same pad rather than falling back to "claim" and unbinding it.
        /// </summary>
        internal static void AdoptJoystick(int index)
        {
            Requested = index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Assign(false, index);
            applied = true;

            ReInput.ControllerConnectedEvent += OnControllerChanged;
            ReInput.ControllerDisconnectedEvent += OnControllerChanged;
        }

        private static void Assign(bool keyboard, int index)
        {
            // Off first, so nothing this method does gets undone a frame later.
            ReInput.configuration.autoAssignJoysticks = false;

            foreach (Player player in ReInput.players.Players)
                player.controllers.ClearAllControllers();

            Player mine = ReInput.players.GetPlayer(0);

            if (keyboard)
            {
                mine.controllers.hasKeyboard = true;
                mine.controllers.hasMouse = true;
                Say("input isolated: keyboard and mouse only");
            }
            else
            {
                Joystick joystick = ReInput.controllers.Joysticks[index];
                mine.controllers.AddController(joystick, removeFromOtherPlayers: true);
                Say("input isolated: joystick " + index + " (" + joystick.name + ") only");
            }
        }

        /// <summary>
        /// Keeps this instance reading its controller while a different window holds focus.
        ///
        /// Without it only whichever window was clicked last responds, which for split-screen means
        /// every player but one is holding a dead pad. Set once, as early as Rewired allows.
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

        /// <summary>The joystick list, in the index order that -srcontroller expects.</summary>
        private static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("CONTROLLERS ").Append(ReInput.controllers.joystickCount).Append(" joystick(s)");

            for (int i = 0; i < ReInput.controllers.joystickCount; i++)
            {
                Joystick j = ReInput.controllers.Joysticks[i];
                sb.Append(Environment.NewLine)
                  .Append("  index ").Append(i).Append(" : ").Append(j.name)
                  .Append("  [").Append(j.hardwareName).Append("]");
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
