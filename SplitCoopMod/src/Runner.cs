using System;
using Burst2Flame.Observable;
using HarmonyLib;
using UnityEngine;

namespace SplitCoopMod
{
    /// <summary>
    /// Waits for the main menu, then starts or joins a direct-IP session.
    ///
    /// Driven from a postfix on the game's own <c>GUIManager.Update</c>. Two earlier approaches
    /// were measured and both silently never ran: a timer on the plugin itself (its GameObject is
    /// destroyed during the first scene load - OnDisable and OnDestroy fire, Start never does) and
    /// a timer on a fresh DontDestroyOnLoad object (Start never fired there either). Neither threw.
    /// If the game is running its UI at all, a postfix on its UI update runs.
    ///
    /// The game's own <c>MultiplayerFeatureTest.HostIP/JoinIP</c> would otherwise be the obvious
    /// thing to call, and they were tried first: they are <c>async</c> over UniTask and never got
    /// past their first await here, emitting none of their own MPRUN output. So this walks the
    /// same steps synchronously, calling exactly the methods those routines call.
    /// </summary>
    [HarmonyPatch(typeof(GUIManager), "Update")]
    internal static class Runner
    {
        internal static Plugin.Role Role = Plugin.Role.None;
        internal static string HostIp;
        internal static bool Roguelike;
        internal static Action<string> Trace;

        private enum Step { WaitForMenu, ChooseMode, Start, Watch, Done }

        private static Step step = Step.WaitForMenu;
        private static bool announced;
        private static float nextAt;
        private static int waits;
        private static int polls;
        private static bool reportedSuccess;
        private static int retries;

        private static void Postfix()
        {
            // Runs regardless of role: an instance may be launched only to be pinned to one
            // controller, with a person driving the menus themselves.
            InputIsolation.TryApply();
            InputIsolation.Enforce();
            UiReport.Tick();
            UiFit.Tick();
            AutoPlay.Tick();
            AutoPlay.TickPending();

            if (Role == Plugin.Role.None || step == Step.Done)
                return;

            if (!announced)
            {
                announced = true;
                Say("running from GUIManager.Update as " + Role);
            }

            if (Time.realtimeSinceStartup < nextAt)
                return;

            try
            {
                Advance();
            }
            catch (Exception e)
            {
                Say("FAILED at step " + step + ": " + e.Message);
                step = Step.Done;
            }
        }

        private static void Advance()
        {
            switch (step)
            {
                case Step.WaitForMenu:
                    nextAt = Time.realtimeSinceStartup + 1f;

                    if (!AtMainMenu())
                    {
                        if (++waits % 15 == 0)
                            Say("still waiting for the main menu (" + waits + "s)");

                        if (waits > 180)
                        {
                            Say("FAILED: main menu never appeared");
                            step = Step.Done;
                        }
                        return;
                    }

                    Say("main menu is up");
                    step = Step.ChooseMode;
                    return;

                case Step.ChooseMode:
                    // The mode has to be chosen before hosting: it decides whether the session is
                    // campaign or roguelike, and the host's choice is what the client inherits.
                    if (Roguelike)
                        MainMenu.Instance.ChooseRoguelikeMode();
                    else
                        MainMenu.Instance.ChooseNormalMode();

                    Say("chose " + (Roguelike ? "roguelike" : "campaign") + " mode");

                    // The game's own routine waits here too; the menu swaps its controls out.
                    nextAt = Time.realtimeSinceStartup + 1f;
                    step = Step.Start;
                    return;

                case Step.Start:
                    // Steam's networking library is the transport even for a direct-IP session, so
                    // it has to be up before the socket is made. This does not sign anyone in
                    // differently - the peers are told apart by a host-assigned NetworkId.
                    if (!NetworkObserver<Root>.EnsureSteamConnection())
                    {
                        Say("FAILED: Steam networking did not initialise");
                        step = Step.Done;
                        return;
                    }

                    // Without this, SteamNetworkingSockets refuses an unauthenticated peer, which
                    // is every peer in a same-machine session. It is the game's own helper.
                    MultiplayerFeatureTest.AllowDirectIP();
                    Say("direct IP allowed");

                    if (Role == Plugin.Role.Host)
                    {
                        MainMenu.Instance.HostMultiplayerIP(useSaveData: false);
                        Say("hosting on port 9055");
                    }
                    else
                    {
                        NetworkingManager.Instance.NetworkManager.Connect(HostIp);
                        Say("connecting to " + HostIp);
                    }

                    nextAt = Time.realtimeSinceStartup + 2f;
                    step = Step.Watch;
                    return;

                case Step.Watch:
                    nextAt = Time.realtimeSinceStartup + 2f;
                    Watch();
                    return;
            }
        }

        /// <summary>
        /// Reports whether the session actually formed, rather than only that it was requested.
        ///
        /// The honest signal is the NetworkId: the host keeps 0 and hands every client a non-zero
        /// one, so a client still sitting at 0 has not been accepted no matter what the menus show.
        /// </summary>
        private static void Watch()
        {
            var net = NetworkingManager.Instance != null
                ? NetworkingManager.Instance.NetworkManager
                : null;

            if (net == null)
                return;

            int id = net.NetworkId;
            bool server = NetworkingManager.Instance.IsServer;
            string state = GUIManager.instance != null
                ? GUIManager.instance.CurrentGuiState.ToString()
                : "?";

            if (!reportedSuccess)
            {
                bool ok = Role == Plugin.Role.Host ? server : id != 0;

                if (ok)
                {
                    reportedSuccess = true;
                    Say("SESSION OK  role=" + Role + " networkId=" + id + " isServer=" + server
                        + " gui=" + state);
                }
                else if (Role == Plugin.Role.Join && polls > 0 && polls % 8 == 0 && retries < 5)
                {
                    // A client that dialled before the host was listening gets nothing and would
                    // otherwise sit there forever. How long the host takes to reach the menu varies
                    // with the machine, so retrying is more reliable than any fixed head start.
                    retries++;
                    Say("no network id yet; retrying the connection to " + HostIp
                        + " (attempt " + (retries + 1) + ")");

                    try { NetworkingManager.Instance.NetworkManager.Connect(HostIp); }
                    catch (Exception e) { Say("retry threw: " + e.Message); }
                }
            }

            if (++polls % 5 == 0)
                Say("status: networkId=" + id + " isServer=" + server + " gui=" + state);

            if (polls > 60)
            {
                if (!reportedSuccess)
                    Say("FAILED: no session after " + (polls * 2) + "s (networkId=" + id + ")");

                step = Step.Done;
            }
        }

        private static bool AtMainMenu()
        {
            try
            {
                return GUIManager.instance != null
                       && GUIManager.instance.CurrentGuiState == GUIState.InMainMenu
                       && MainMenu.Instance != null;
            }
            catch
            {
                // Asked before those singletons exist, which is normal for the first frames.
                return false;
            }
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
