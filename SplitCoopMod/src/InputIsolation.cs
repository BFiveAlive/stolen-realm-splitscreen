using System;
using Rewired;
using UnityEngine;

namespace SplitCoopMod
{
    /// <summary>
    /// Gives one instance one input device, so each window answers only to its own player.
    ///
    /// This is what makes the whole arrangement playable. Every instance is a full copy of the
    /// game and Rewired in each of them sees every controller plugged into the machine, so without
    /// this a single stick nudge moves a character in all of them at once.
    ///
    /// Rewired makes the fix cheap because it is built around players owning controllers: clear
    /// what it auto-assigned, hand the chosen device to player 0, and turn auto-assignment off so
    /// it does not undo the work when a controller is re-detected.
    ///
    /// Everything is assigned to Rewired player 0 rather than spread across players: inside one
    /// instance there is only one human, and the game reads its "which player is this" from the
    /// character's ControllerPlayerId, which stays 0 when only player 0 owns hardware.
    /// </summary>
    internal static class InputIsolation
    {
        /// <summary>-srcontroller: a joystick index, or "keyboard" for the mouse-and-keyboard seat.</summary>
        internal static string Requested;

        internal static Action<string> Trace;

        private static bool applied;
        private static int attempts;

        internal static bool Wanted => !string.IsNullOrEmpty(Requested);

        /// <summary>
        /// Retried until Rewired reports ready and the requested device is actually present.
        ///
        /// Controllers are enumerated a moment after the process starts, so the first few attempts
        /// legitimately find nothing; giving up immediately would isolate nothing on a slow start.
        /// </summary>
        internal static void TryApply()
        {
            if (applied || !Wanted)
                return;

            try
            {
                if (!ReInput.isReady)
                    return;

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

                applied = true;
            }
            catch (Exception e)
            {
                Say("could not isolate input: " + e.Message);
                applied = true;
            }
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
