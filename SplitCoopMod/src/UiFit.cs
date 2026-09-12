using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;   // AutoExpandGridLayoutGroup is declared in this namespace by the game

namespace SplitCoopMod
{
    /// <summary>
    /// Drops grid layouts to the number of columns that actually fit the window.
    ///
    /// The game lays several lists out with <c>AutoExpandGridLayoutGroup</c> set to
    /// <c>FixedColumnCount</c>. A fixed count is fine at the shapes the game was designed for and
    /// wrong in a tiled window: measured on the party screen at 1280x1440, the holder had room for
    /// one 163.8-wide cell and the layout was placing two. The cards do not shrink to compensate -
    /// their contents simply overflow, which is why names came out as "TRB" and "Health" as "Hea".
    ///
    /// The fix is the game's own arithmetic. <c>AutoExpandGridLayoutGroup</c> already knows how to
    /// work out how many cells fit a width - that is exactly what its <c>Flexible</c> branch does -
    /// so this applies the same calculation to the fixed-count layouts and lowers the count when
    /// the columns do not fit.
    ///
    /// It only ever lowers the count below what the screen was authored with, and restores it as
    /// soon as there is room again, so at ordinary window sizes it changes nothing.
    /// </summary>
    internal static class UiFit
    {
        internal static bool Enabled;
        internal static Action<string> Trace;

        /// <summary>What each layout was authored with, so widening the window can restore it.</summary>
        private static readonly Dictionary<AutoExpandGridLayoutGroup, int> Authored =
            new Dictionary<AutoExpandGridLayoutGroup, int>();

        /// <summary>The same, for Unity's own grid, which other screens use.</summary>
        private static readonly Dictionary<GridLayoutGroup, int> AuthoredUnity =
            new Dictionary<GridLayoutGroup, int>();

        private static float nextAt;
        private static int changes;
        private static int matchChanges;

        internal static void Tick()
        {
            if (!Enabled || Time.realtimeSinceStartup < nextAt)
                return;

            // Twice a second: windows open and close and are laid out a frame later, so this has
            // to keep looking rather than run once, but it does not need to run every frame.
            nextAt = Time.realtimeSinceStartup + 0.5f;

            try
            {
                foreach (var scaler in UnityEngine.Object.FindObjectsOfType<CanvasScaler>())
                    FitScaler(scaler);

                foreach (var grid in UnityEngine.Object.FindObjectsOfType<AutoExpandGridLayoutGroup>())
                    Fit(grid);

                // The party screen is the only one using the game's own grid; the rest of the
                // interface uses Unity's, and a fixed column count is just as wrong there.
                foreach (var grid in UnityEngine.Object.FindObjectsOfType<GridLayoutGroup>())
                    FitUnity(grid);
            }
            catch (Exception e)
            {
                Say("layout fitting failed: " + e.Message);
                Enabled = false;
            }
        }

        private static void Fit(AutoExpandGridLayoutGroup grid)
        {
            if (grid == null || grid.constraint != AutoExpandGridLayoutGroup.Constraint.FixedColumnCount)
                return;

            var rect = grid.GetComponent<RectTransform>();
            if (rect == null)
                return;

            float width = rect.rect.width;

            // A zero width means it has not been laid out yet; acting on that would compute one
            // column for everything and stick.
            if (width <= 1f || grid.cellSize.x <= 0f)
                return;

            int authored;
            if (!Authored.TryGetValue(grid, out authored))
            {
                authored = grid.constraintCount;
                Authored[grid] = authored;
            }

            // This layout stretches its cells to fill the row rather than drawing them at
            // cellSize: the width each child actually gets is
            //     (holderWidth - spacing * (columns - 1)) / columns
            // so "how many cells fit" is the wrong question. What matters is how wide each one
            // ends up, and the authored cellSize.x is the width the card's contents were designed
            // around - below it, the name, level and Health/Mana rows start overlapping.
            //
            // So: the most columns that still leave each card at least its authored width. At an
            // ordinary window this keeps the authored count (the cards come out wider than
            // authored there); in a tiled window it drops to one and they become readable again.
            int target = 1;
            for (int n = authored; n >= 1; n--)
            {
                float each = (width - grid.padding.horizontal - grid.spacing.x * (n - 1)) / n;
                if (each >= grid.cellSize.x)
                {
                    target = n;
                    break;
                }
            }

            if (target == grid.constraintCount)
                return;

            grid.constraintCount = target;

            // Logged sparingly: this runs twice a second against every layout in the scene.
            if (changes++ < 40)
                Say("fitted '" + grid.name + "' to " + target + " column(s)"
                    + " (authored " + authored + ", holder " + width.ToString("0")
                    + ", each card "
                    + ((width - grid.padding.horizontal - grid.spacing.x * (target - 1)) / target).ToString("0")
                    + " vs authored cell " + grid.cellSize.x.ToString("0.#") + ")");
        }

        /// <summary>Aspect the game's UI is really laid out for. 16:9 is what it ships at.</summary>
        private const float DesignAspect = 16f / 9f;

        private static readonly Dictionary<CanvasScaler, Vector2> AuthoredRef =
            new Dictionary<CanvasScaler, Vector2>();

        /// <summary>
        /// Gives a narrow window the same horizontal room the UI gets on a 16:9 screen.
        ///
        /// The canvases are authored against 800x600 and match on height, so the logical width the
        /// layout has to work with is whatever the aspect ratio leaves: 1067 units at 16:9, but
        /// only 533 for two tiles on a 2560x1440 screen. Everything horizontal is then squeezed
        /// into half the room it expects - skill tree columns fall off the edge, header labels
        /// overlap, and party cards come out at 211 units where the game gives them 247.
        ///
        /// Rather than change which edge is matched - which trades the problem for a different one,
        /// since matching width yields 800 units, still short of 1067 - this keeps the match and
        /// raises the reference height until the logical width comes back to the 16:9 figure:
        ///
        ///     referenceHeight = authoredHeight * DesignAspect / screenAspect
        ///
        /// The surplus is spent on logical height, where a tall window has room going spare. The
        /// horizontal layout then measures exactly as it does on a normal screen.
        ///
        /// Only applied to windows narrower than 16:9, so an ordinary one is left alone.
        /// </summary>
        private static void FitScaler(CanvasScaler scaler)
        {
            if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                return;

            Vector2 authored;
            if (!AuthoredRef.TryGetValue(scaler, out authored))
            {
                authored = scaler.referenceResolution;
                AuthoredRef[scaler] = authored;
            }

            if (authored.x <= 0f || authored.y <= 0f || Screen.height <= 0)
                return;

            // Only canvases that scale by height are affected; one matching width already keeps
            // its horizontal extent.
            if (scaler.matchWidthOrHeight < 0.99f)
                return;

            float screenAspect = (float)Screen.width / Screen.height;
            float wantedHeight = screenAspect < DesignAspect
                ? authored.y * DesignAspect / screenAspect
                : authored.y;

            if (Mathf.Abs(scaler.referenceResolution.y - wantedHeight) < 0.5f)
                return;

            scaler.referenceResolution = new Vector2(authored.x, wantedHeight);

            if (matchChanges++ < 20)
                Say("canvas '" + scaler.name + "' reference height "
                    + authored.y.ToString("0") + " -> " + wantedHeight.ToString("0")
                    + " (aspect " + screenAspect.ToString("0.00")
                    + "; logical width now " + (Screen.width / (Screen.height / wantedHeight)).ToString("0") + ")");
        }

        /// <summary>The same correction for Unity's built-in grid.</summary>
        private static void FitUnity(GridLayoutGroup grid)
        {
            if (grid == null || grid.constraint != GridLayoutGroup.Constraint.FixedColumnCount)
                return;

            var rect = grid.GetComponent<RectTransform>();
            if (rect == null)
                return;

            float width = rect.rect.width;
            float step = grid.cellSize.x + grid.spacing.x;

            if (width <= 1f || step <= 0f)
                return;

            int authored;
            if (!AuthoredUnity.TryGetValue(grid, out authored))
            {
                authored = grid.constraintCount;
                AuthoredUnity[grid] = authored;
            }

            int fits = Mathf.Max(1, Mathf.FloorToInt((width - grid.padding.horizontal + grid.spacing.x + 0.001f) / step));
            int target = Mathf.Min(authored, fits);

            if (target == grid.constraintCount)
                return;

            grid.constraintCount = target;

            if (changes++ < 40)
                Say("fitted '" + grid.name + "' to " + target + " column(s)"
                    + " (authored " + authored + ", width " + width.ToString("0")
                    + ", cell " + grid.cellSize.x.ToString("0.#") + ", unity grid)");
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
