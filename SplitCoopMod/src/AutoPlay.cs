using System;
using System.Linq;
using UnityEngine;

namespace SplitCoopMod
{
    /// <summary>
    /// Walks an instance into a game and opens the menus, so the screens that only exist in play
    /// can be inspected at a split-screen window size.
    ///
    /// Diagnostic only, behind -srautoplay. The party screen can be reached by hosting, but the
    /// inventory and the skill tree cannot: they need a party accepted and a world loaded. Checking
    /// whether their layouts survive a narrow window otherwise means asking a person to play, and
    /// "probably fine" is not a useful answer about a screen nobody has looked at.
    ///
    /// It picks the first available character and accepts, which starts a real game. Back saves up
    /// before using it.
    /// </summary>
    internal static class AutoPlay
    {
        internal static bool Enabled;
        internal static Action<string> Trace;
        internal static Action<string> Shoot;

        private enum Step { WaitForList, Pick, Accept, WaitForGame, Settle, OpenCharacter, OpenSkills, Done }

        private static Step step = Step.WaitForList;
        private static float nextAt;
        private static int waits;

        internal static void Tick()
        {
            if (!Enabled || step == Step.Done || Time.realtimeSinceStartup < nextAt)
                return;

            nextAt = Time.realtimeSinceStartup + 1.5f;

            try
            {
                Advance();
            }
            catch (Exception e)
            {
                Say("autoplay failed at " + step + ": " + e.Message);
                step = Step.Done;
            }
        }

        private static void Advance()
        {
            var gui = GUIManager.instance;
            if (gui == null)
                return;

            switch (step)
            {
                case Step.WaitForList:
                {
                    var mgr = CharacterChoiceManager.Instance;

                    if (gui.CurrentGuiState != GUIState.ChoosingCharacter || mgr == null
                        || mgr.notInPartyHolder == null || mgr.notInPartyHolder.childCount == 0)
                    {
                        Bored("character list");
                        return;
                    }

                    // A few seconds after the cards appear: they are still being laid out, and the
                    // layout correction has to have run before anything is clicked.
                    Say("character list is up (" + mgr.notInPartyHolder.childCount + " cards)");
                    nextAt = Time.realtimeSinceStartup + 4f;
                    step = Step.Pick;
                    return;
                }

                case Step.Pick:
                {
                    var mgr = CharacterChoiceManager.Instance;
                    var first = mgr.notInPartyHolder.GetComponentsInChildren<CharacterChoiceItem>()
                        .FirstOrDefault(x => x != null && x.Character != null && !x.Character.HardcoreDeath);

                    if (first == null)
                    {
                        Say("no selectable character found");
                        step = Step.Done;
                        return;
                    }

                    mgr.selectedCharacterChoiceItem = first;
                    mgr.ToggleSelectedCharacter(0);
                    Say("added '" + first.Character.CharacterName + "' to the party");

                    nextAt = Time.realtimeSinceStartup + 3f;
                    step = Step.Accept;
                    return;
                }

                case Step.Accept:
                    CharacterChoiceManager.Instance.AcceptCharacterChoices();
                    Say("accepted the party");
                    nextAt = Time.realtimeSinceStartup + 5f;
                    step = Step.WaitForGame;
                    return;

                case Step.WaitForGame:
                    if (gui.CurrentGuiState == GUIState.ChoosingCharacter
                        || gui.CurrentGuiState == GUIState.InMainMenu)
                    {
                        Bored("the game to start");
                        return;
                    }

                    Say("in game, state=" + gui.CurrentGuiState);
                    nextAt = Time.realtimeSinceStartup + 8f;
                    step = Step.Settle;
                    return;

                case Step.Settle:
                    Shoot("InGame");
                    nextAt = Time.realtimeSinceStartup + 4f;
                    step = Step.OpenCharacter;
                    return;

                case Step.OpenCharacter:
                    EnsureSelectedCharacter();
                    CharacterMenusManager.Instance.OpenCharacterMenu();
                    Say("opened the character menu");

                    // Long enough for the window prefab to load: these are LoadableUIWindows, and
                    // the first open pulls the prefab in rather than showing something existing.
                    nextAt = Time.realtimeSinceStartup + 6f;
                    step = Step.OpenSkills;
                    return;

                case Step.OpenSkills:
                    Shoot("CharacterMenu");

                    EnsureSelectedCharacter();
                    CharacterMenusManager.Instance.OpenSkillTreeMenu();
                    Say("opened the skill tree");

                    nextAt = Time.realtimeSinceStartup + 8f;
                    step = Step.Done;

                    // Taken on the way out; the skill tree is the last screen this walks to.
                    Pending = "SkillTree";
                    return;
            }
        }

        /// <summary>A shot to take once the final screen has had time to lay out.</summary>
        internal static string Pending;
        private static float pendingAt;

        internal static void TickPending()
        {
            if (string.IsNullOrEmpty(Pending))
                return;

            if (pendingAt == 0f)
            {
                pendingAt = Time.realtimeSinceStartup + 8f;
                return;
            }

            if (Time.realtimeSinceStartup < pendingAt)
                return;

            Shoot(Pending);
            Pending = null;
        }

        /// <summary>
        /// The menus refuse to open without a selected character that belongs to this player.
        /// </summary>
        private static void EnsureSelectedCharacter()
        {
            var gl = GameLogic.instance;
            if (gl == null)
                return;

            if (gl.CurrentlySelectedCharacter == null
                || !gl.AllMyCharacters.Contains(gl.CurrentlySelectedCharacter))
            {
                var mine = gl.AllMyCharacters != null ? gl.AllMyCharacters.FirstOrDefault() : null;
                if (mine != null)
                {
                    gl.CurrentlySelectedCharacter = mine;
                    Say("selected '" + mine.CharacterName + "'");
                }
            }
        }

        private static void Bored(string what)
        {
            if (++waits % 20 == 0)
                Say("waiting for " + what + " (" + waits + ")");

            if (waits > 200)
            {
                Say("gave up waiting for " + what);
                step = Step.Done;
            }
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace("autoplay: " + message);
        }
    }
}
