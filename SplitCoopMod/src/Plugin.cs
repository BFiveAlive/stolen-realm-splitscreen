using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace SplitCoopMod
{
    /// <summary>
    /// Puts Stolen Realm's own direct-IP multiplayer on the command line.
    ///
    /// The game already ships everything needed for several copies on one PC to play together:
    /// <c>MainMenu.HostMultiplayerIP</c> opens a plain UDP socket on port 9055 and sets
    /// <c>Root.UsingSteam = false</c>, and <c>MultiplayerFeatureTest</c> is the developers' own
    /// harness that drives hosting and joining and reports the result. None of it is reachable
    /// without a person clicking through the menus, which is exactly what a split-screen launcher
    /// cannot do.
    ///
    /// So this mod adds no multiplayer of its own. It reads two arguments and calls the game's
    /// existing entry points:
    ///
    ///   -srhost              host a direct-IP session
    ///   -srjoin &lt;ip&gt;    join one
    ///   -srmode roguelike    optional; campaign is the default
    ///   -srplayer &lt;n&gt;   optional label used in this mod's log file name
    ///
    /// Nothing here touches Steam identity. Direct-IP play is a shipped feature and the peers are
    /// told apart by a host-assigned NetworkId, not by who is signed in, which is why two
    /// instances under one Steam login can sit in the same session at all.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "bfivealive.stolenrealm.splitcoopmod";
        public const string Name = "Split Co-op Mod";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal enum Role { None, Host, Join }

        private Role role = Role.None;
        private string hostIp;
        private bool roguelike;
        private string label;


        private void Awake()
        {
            Log = Logger;

            ParseCommandLine();

            if (role == Role.None && !InputIsolation.Wanted)
            {
                Trace("No -srhost, -srjoin, -srcontroller or -srlistcontrollers argument; staying out of the way.");
                enabled = false;
                return;
            }

            Trace(Name + " " + Version + " armed as " + role
                  + (role == Role.Join ? " -> " + hostIp : string.Empty)
                  + ", mode=" + (roguelike ? "roguelike" : "campaign"));

            StartRunner();
        }

        /// <summary>
        /// Hands the waiting over to a Harmony postfix on the game's own per-frame UI method.
        ///
        /// Objects created by this mod are not ticked in this game - both the plugin's own and a
        /// fresh DontDestroyOnLoad one were measured never reaching Start - so the only dependable
        /// clock available is the game's.
        /// </summary>
        private void StartRunner()
        {
            try
            {
                Runner.Role = role;
                Runner.HostIp = hostIp;
                Runner.Roguelike = roguelike;
                Runner.Trace = Trace;
                InputIsolation.Trace = Trace;

                new HarmonyLib.Harmony(Guid).PatchAll(typeof(Runner));
                Trace("Patched GUIManager.Update; waiting for the main menu.");
            }
            catch (Exception e)
            {
                Trace("Could not patch GUIManager.Update: " + e);
            }
        }

        private void ParseCommandLine()
        {
            string[] args;

            try { args = Environment.GetCommandLineArgs(); }
            catch (Exception e) { Trace("Could not read the command line: " + e.Message); return; }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;

                switch (arg)
                {
                    case "-srhost":
                        role = Role.Host;
                        break;

                    case "-srjoin":
                        if (!string.IsNullOrEmpty(next))
                        {
                            role = Role.Join;
                            hostIp = next;
                        }
                        break;

                    case "-srmode":
                        roguelike = string.Equals(next, "roguelike", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "-srplayer":
                        label = next;
                        break;

                    case "-srcontroller":
                        InputIsolation.Requested = next;
                        break;

                    case "-srlistcontrollers":
                        InputIsolation.ListOnly = true;
                        break;
                }
            }
        }

        // ---------------------------------------------------------------- per-instance logging

        private string traceFile;

        /// <summary>
        /// Writes to a file of this instance's own, as well as to the BepInEx log.
        ///
        /// Every instance in a split-screen session is the same install, so they all share one
        /// BepInEx/LogOutput.log and interleave into it - and when the interesting question is
        /// "which instance failed to join", an interleaved log is close to useless. The process id
        /// is in the name because it is the one thing guaranteed to differ.
        /// </summary>
        private void Trace(string message)
        {
            if (Log != null)
                Log.LogInfo(message);

            try
            {
                if (traceFile == null)
                {
                    string dir = Path.Combine(Paths.BepInExRootPath, "splitcoop-logs");
                    Directory.CreateDirectory(dir);

                    string who = string.IsNullOrEmpty(label)
                        ? System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                        : label + "-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

                    traceFile = Path.Combine(dir, "instance-" + who + ".log");
                    File.WriteAllText(traceFile, "=== " + Name + " " + Version + " ===" + Environment.NewLine);
                }

                File.AppendAllText(traceFile,
                    Time.realtimeSinceStartup.ToString("0.0", CultureInfo.InvariantCulture)
                    + "s  " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // A trace file is a convenience; never let it take the session down.
            }
        }
    }
}
