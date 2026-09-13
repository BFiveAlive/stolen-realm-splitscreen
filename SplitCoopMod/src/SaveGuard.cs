using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SplitCoopMod
{
    /// <summary>
    /// Stops split-screen windows from damaging state that every copy of the game shares.
    ///
    /// All the windows are one install under one Windows user, so they share one save folder and
    /// one set of registry settings. Two things went wrong because of that:
    ///
    /// Controller assignments. Rewired saves which player owns which device to the registry, and
    /// loads it at the next start. Each split-screen window deliberately rewires player 0 to a
    /// single pad with no keyboard or mouse, and that got saved. Every later launch - modded or
    /// not, from Steam or not - then started with a player that had no usable input, and the game
    /// failed to initialise with thousands of errors a minute. A window's assignment is only
    /// right for that window and that session, so it is never saved.
    ///
    /// Simultaneous saves. The game saves by writing <c>X.temp</c> and swapping it in with
    /// File.Replace. GlobalSaveData.json and the transmog file are written by every window, all
    /// using the same temp name, so two windows saving at the same moment can overwrite each
    /// other's temp file or fail the swap. A machine-wide lock makes those saves take turns. It
    /// does not merge anything: each window still writes its own in-memory copy, so the last
    /// window to save a shared file is the one whose version is kept.
    /// </summary>
    internal static class SaveGuard
    {
        internal static Action<string> Trace;

        private const string MutexName = "Local\\StolenRealmSplitCoopSaves";

        private static Mutex mutex;
        private static int depth;
        private static bool owned;
        private static bool loggedSkip;

        internal static void Apply(Harmony harmony)
        {
            Patch(harmony,
                AccessTools.Method(typeof(Rewired.Data.UserDataStore_PlayerPrefs), "SaveControllerAssignments"),
                prefix: nameof(SkipControllerAssignmentSave));

            // Character saves are included even though each window saves only its own characters:
            // a character save also refreshes shared party state, and taking turns costs nothing.
            Patch(harmony, AccessTools.Method(typeof(Burst2Flame.GlobalSaveData), "Save", Type.EmptyTypes),
                prefix: nameof(EnterSave), finalizer: nameof(ExitSave));
            Patch(harmony, AccessTools.Method(typeof(TransmogData), "SaveTransmogs", Type.EmptyTypes),
                prefix: nameof(EnterSave), finalizer: nameof(ExitSave));
            Patch(harmony, AccessTools.Method(typeof(Character), "Save", new[] { typeof(bool) }),
                prefix: nameof(EnterSave), finalizer: nameof(ExitSave));

            // A new character takes the lowest CharacterN.json number not on disk, but the file is
            // only written later. Two windows creating characters together would both pick the same
            // N and one would overwrite the other. Picking happens under the lock, and each pick is
            // written down so the other windows skip it.
            Patch(harmony, AccessTools.Method(typeof(GameLogic), "GetCharacterFileIndices"),
                postfix: nameof(AddReservedIndices));
            Patch(harmony, AccessTools.Method(typeof(Character), "Load", new[] { typeof(int) }),
                prefix: nameof(BeginNewCharacterLoad), finalizer: nameof(EndNewCharacterLoad));
        }

        private static string ReservationDir =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stolen-realm-splitscreen", "character-reservations");

        private static void AddReservedIndices(System.Collections.Generic.List<int> __result)
        {
            if (__result == null)
                return;

            try
            {
                if (!System.IO.Directory.Exists(ReservationDir))
                    return;

                foreach (string file in System.IO.Directory.GetFiles(ReservationDir, "character-*.reserved"))
                {
                    // Old reservations are from finished sessions; by now the character either
                    // saved (and is on disk anyway) or was abandoned.
                    if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(12))
                    {
                        try { System.IO.File.Delete(file); } catch { }
                        continue;
                    }

                    string name = System.IO.Path.GetFileNameWithoutExtension(file).Substring("character-".Length);
                    if (int.TryParse(name, out int index) && !__result.Contains(index))
                        __result.Add(index);
                }
            }
            catch (Exception e)
            {
                Say("could not read character reservations: " + e.Message);
            }
        }

        private static void BeginNewCharacterLoad(int saveIndex, out bool __state)
        {
            __state = saveIndex == -1;
            if (__state)
                EnterSave();
        }

        private static Exception EndNewCharacterLoad(Character __instance, bool __state, Exception __exception)
        {
            if (!__state)
                return __exception;

            try
            {
                if (__exception == null && __instance != null && __instance.SaveIndex >= 0)
                {
                    System.IO.Directory.CreateDirectory(ReservationDir);
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(ReservationDir, "character-" + __instance.SaveIndex + ".reserved"),
                        System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                    Say("reserved character file number " + __instance.SaveIndex + " for a new character");
                }
            }
            catch (Exception e)
            {
                Say("could not reserve a character file number: " + e.Message);
            }
            finally
            {
                ExitSave(null);
            }

            return __exception;
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix = null, string finalizer = null,
            string postfix = null)
        {
            if (target == null)
            {
                // A game update renamed something. Say so; the game still saves normally.
                Say("save guard: a save method was not found, so it is unguarded ("
                    + (prefix ?? postfix ?? finalizer) + ")");
                return;
            }

            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(SaveGuard), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(SaveGuard), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(SaveGuard), finalizer));

            Say("save guard on " + target.DeclaringType.Name + "." + target.Name);
        }

        private static bool SkipControllerAssignmentSave(ref bool __result)
        {
            __result = true;

            if (!loggedSkip)
            {
                loggedSkip = true;
                Say("not saving this window's controller assignments (they are per window and per session)");
            }

            return false;
        }

        private static void EnterSave()
        {
            // Saves run on the main thread and can nest (a character save touching global data),
            // so only the outermost one takes the lock.
            if (depth++ > 0)
                return;

            try
            {
                if (mutex == null)
                    mutex = new Mutex(false, MutexName);

                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    // Another window was closed mid-save. The lock is ours now, and whatever it was
                    // writing is covered by the game's own .backup file.
                    owned = true;
                }

                if (!owned)
                    Say("another window held the save lock for 10s; saving anyway rather than losing this save");
            }
            catch (Exception e)
            {
                owned = false;
                Say("save lock unavailable, saving without it: " + e.Message);
            }
        }

        private static Exception ExitSave(Exception __exception)
        {
            if (--depth == 0 && owned)
            {
                owned = false;

                try { mutex.ReleaseMutex(); }
                catch (Exception e) { Say("could not release the save lock: " + e.Message); }
            }

            return __exception;
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
