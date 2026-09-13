using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Rewired;
using UnityEngine;

namespace SplitCoopMod
{
    /// <summary>
    /// Lets a player claim this window by pressing a button on the pad they are holding.
    ///
    /// Assigning controllers by index works, but only if somebody first runs the game once to
    /// enumerate them and then types the right number for each window - and the number Windows
    /// shows is not the number Rewired uses, so getting it wrong silently gives two players the
    /// same pad. Nobody should have to do that to sit down and play.
    ///
    /// Rewired is the only thing on the machine that knows which pad is which here, and it lives
    /// inside the game, so the claiming has to happen inside the game too. The launcher supplies a
    /// directory and takes turns: it writes the seat it wants filled into <c>turn.txt</c>, the
    /// instance holding that seat watches for a button press on a pad no other seat has taken, and
    /// writes what it got back as <c>seat-N.txt</c>. One seat listens at a time, so two windows
    /// cannot both take the credit for one press.
    ///
    /// The exchange is deliberately plain text. It crosses a process boundary between a
    /// netstandard2.1 mod and a .NET 8 launcher, and a format both can parse without agreeing on a
    /// serializer is worth more here than structure.
    /// </summary>
    internal static class SeatClaim
    {
        internal static bool Enabled;

        /// <summary>-srclaimdir: where the launcher and the instances leave notes for each other.</summary>
        internal static string Dir;

        /// <summary>-srseat: which tile this window is, counting from 0.</summary>
        internal static int Seat = -1;

        internal static Action<string> Trace;

        private static bool announced;
        private static bool claimed;
        private static float nextReadAt;
        private static int currentTurn = -1;
        private static readonly HashSet<int> Taken = new HashSet<int>();
        private static int waits;

        /// <summary>
        /// Runs every frame once Rewired is up.
        ///
        /// The button poll cannot be throttled - <c>GetAnyButtonDown</c> is true for the single
        /// frame the button goes down, and a press sampled four times a second is a press mostly
        /// missed. Only the file reads are on a timer.
        /// </summary>
        internal static void Tick()
        {
            if (!Enabled || claimed || Seat < 0 || string.IsNullOrEmpty(Dir))
                return;

            try
            {
                if (Time.realtimeSinceStartup >= nextReadAt)
                {
                    nextReadAt = Time.realtimeSinceStartup + 0.25f;
                    ReadNotes();
                }

                if (currentTurn != Seat)
                    return;

                int index = FirstUnclaimedPress();
                if (index < 0)
                {
                    if (++waits % 600 == 0)
                        Say("seat " + Seat + " is listening; " + ReInput.controllers.joystickCount
                            + " joystick(s) present, " + Taken.Count + " already taken");
                    return;
                }

                Claim(index);
            }
            catch (Exception e)
            {
                Say("seat claiming failed: " + e.Message);
                Enabled = false;
            }
        }

        /// <summary>
        /// Says this window is listening, before any turn arrives.
        ///
        /// The launcher would otherwise have to guess whether a silent instance is still loading or
        /// has something wrong with it, and a game this size takes long enough to start that the
        /// difference matters to whoever is watching the screen.
        /// </summary>
        internal static void Announce()
        {
            if (!Enabled || announced || Seat < 0 || string.IsNullOrEmpty(Dir))
                return;

            announced = true;

            Write("ready-" + Seat + ".txt",
                "seat=" + Seat + Environment.NewLine +
                "joysticks=" + ReInput.controllers.joystickCount + Environment.NewLine);

            Say("seat " + Seat + " ready to claim a controller ("
                + ReInput.controllers.joystickCount + " joystick(s) visible)");
        }

        private static int FirstUnclaimedPress()
        {
            int count = ReInput.controllers.joystickCount;

            for (int i = 0; i < count; i++)
            {
                if (Taken.Contains(i))
                    continue;

                Joystick joystick = ReInput.controllers.Joysticks[i];
                if (joystick != null && joystick.GetAnyButtonDown())
                    return i;
            }

            return -1;
        }

        private static void Claim(int index)
        {
            Joystick joystick = ReInput.controllers.Joysticks[index];
            claimed = true;

            InputIsolation.AdoptJoystick(index);

            Write("seat-" + Seat + ".txt",
                "seat=" + Seat + Environment.NewLine +
                "index=" + index.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                "name=" + Sanitise(joystick != null ? joystick.name : "joystick " + index) + Environment.NewLine +
                "hardware=" + Sanitise(joystick != null ? joystick.hardwareName : string.Empty) + Environment.NewLine);

            Say("seat " + Seat + " claimed joystick " + index
                + " (" + (joystick != null ? joystick.name : "?") + ")");
        }

        /// <summary>
        /// Whose turn it is, and which joysticks other seats have already taken.
        ///
        /// The seat files are the authority on what is taken rather than anything held in memory:
        /// each window is a separate process and none of them can see another's variables, but they
        /// can all read the same directory.
        /// </summary>
        private static void ReadNotes()
        {
            if (!Directory.Exists(Dir))
                return;

            currentTurn = ReadInt(Path.Combine(Dir, "turn.txt"), -1);

            Taken.Clear();
            foreach (string file in Directory.GetFiles(Dir, "seat-*.txt"))
            {
                int seat = ReadKeyInt(file, "seat", -1);
                if (seat == Seat)
                    continue;

                int index = ReadKeyInt(file, "index", -1);
                if (index >= 0)
                    Taken.Add(index);
            }
        }

        private static int ReadInt(string path, int fallback)
        {
            try
            {
                if (!File.Exists(path))
                    return fallback;

                int value;
                return int.TryParse(Read(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) ? value : fallback;
            }
            catch
            {
                // The launcher may be mid-write; the next pass in a quarter second will get it.
                return fallback;
            }
        }

        private static int ReadKeyInt(string path, string key, int fallback)
        {
            try
            {
                foreach (string line in Read(path).Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith(key + "=", StringComparison.Ordinal))
                        continue;

                    int value;
                    if (int.TryParse(trimmed.Substring(key.Length + 1).Trim(),
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        return value;
                }
            }
            catch
            {
            }

            return fallback;
        }

        /// <summary>Opened shared: the launcher polls these files while the game writes them.</summary>
        private static string Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static void Write(string name, string body)
        {
            try
            {
                Directory.CreateDirectory(Dir);

                // Written beside and moved into place, so the launcher never reads half a file.
                string final = Path.Combine(Dir, name);
                string temp = final + ".tmp";

                File.WriteAllText(temp, body, Encoding.UTF8);
                if (File.Exists(final))
                    File.Delete(final);
                File.Move(temp, final);
            }
            catch (Exception e)
            {
                Say("could not write " + name + ": " + e.Message);
            }
        }

        /// <summary>Controller names are vendor strings; keep them on one line and out of the format.</summary>
        private static string Sanitise(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.Replace('\r', ' ').Replace('\n', ' ').Replace('=', ' ').Trim();
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
