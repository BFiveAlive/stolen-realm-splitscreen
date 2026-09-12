using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SplitCoopMod
{
    /// <summary>
    /// Reports how the UI is being scaled, and what the layouts are doing, at whatever window shape
    /// this instance was given.
    ///
    /// Split-screen hands the game aspect ratios it was never designed for - two windows side by
    /// side on a 2560x1440 screen is 1280x1440, which is taller than it is wide - and a canvas
    /// tuned for 16:9 responds by shrinking everything. Guessing at the fix from the outside is
    /// how you end up moving the wrong slider, so this prints the actual scale factors and grid
    /// constraints from inside the running game.
    ///
    /// Diagnostic only: it is behind -srdumpui and changes nothing.
    /// </summary>
    internal static class UiReport
    {
        internal static bool Enabled;

        /// <summary>-srshots &lt;dir&gt;: capture the framebuffer on each new GUI state.</summary>
        internal static string ShotDir;

        internal static Action<string> Trace;

        private static string lastState;
        private static int shotIndex;
        private static float nextPeriodicAt;
        private static int periodicCount;

        /// <summary>
        /// Dumps once per distinct GUI state, so the character screen, the inventory and the skill
        /// tree each get reported as they are opened rather than once at startup when none exist.
        /// </summary>
        internal static void Tick()
        {
            if (!Enabled)
                return;

            try
            {
                string state = GUIManager.instance != null
                    ? GUIManager.instance.CurrentGuiState.ToString()
                    : null;

                bool changed = state != null && state != lastState;

                // Also on a timer: the character list is populated a moment after the screen
                // opens, so a dump taken only on the state change sees an empty holder.
                bool periodic = state != null && periodicCount < 6
                                && Time.realtimeSinceStartup >= nextPeriodicAt;

                if (!changed && !periodic)
                    return;

                if (periodic)
                {
                    periodicCount++;
                    nextPeriodicAt = Time.realtimeSinceStartup + 10f;
                }

                lastState = state;
                Trace(Dump(state));

                // Captured on the periodic passes as well as on the state change: a screen is
                // still settling when it first opens, and any layout correction lands a moment
                // after that, so a shot taken only on the change shows the state before the fix.
                Shoot(state);
            }
            catch (Exception e)
            {
                if (Trace != null)
                    Trace("UI report failed: " + e.Message);
            }
        }

        private static string Dump(string state)
        {
            var sb = new StringBuilder();
            float aspect = (float)Screen.width / Screen.height;

            sb.Append("UIDUMP state=").Append(state)
              .Append(" screen=").Append(Screen.width).Append("x").Append(Screen.height)
              .Append(" aspect=").Append(aspect.ToString("0.00"));

            foreach (var scaler in Resources.FindObjectsOfTypeAll<CanvasScaler>()
                         .Where(s => s != null && s.gameObject.scene.IsValid()
                                     && s.gameObject.activeInHierarchy))
            {
                var canvas = scaler.GetComponent<Canvas>();

                sb.Append(Environment.NewLine)
                  .Append("  SCALER '").Append(scaler.name).Append("'")
                  .Append(" mode=").Append(scaler.uiScaleMode)
                  .Append(" ref=").Append(scaler.referenceResolution.x).Append("x").Append(scaler.referenceResolution.y)
                  .Append(" match=").Append(scaler.matchWidthOrHeight.ToString("0.00"))
                  .Append(" scaleFactor=").Append(scaler.scaleFactor.ToString("0.000"))
                  .Append(" -> canvas=").Append(canvas != null ? canvas.scaleFactor.ToString("0.000") : "?");
            }

            // Grid layouts are what turn a narrow window into unreadably small cards: a fixed
            // column count keeps the columns and shrinks each one.
            foreach (var grid in Resources.FindObjectsOfTypeAll<GridLayoutGroup>()
                         .Where(g => g != null && g.gameObject.scene.IsValid()
                                     && g.gameObject.activeInHierarchy))
            {
                var rect = grid.GetComponent<RectTransform>();

                sb.Append(Environment.NewLine)
                  .Append("  GRID '").Append(Path(grid.transform)).Append("'")
                  .Append(" constraint=").Append(grid.constraint)
                  .Append(" count=").Append(grid.constraintCount)
                  .Append(" cell=").Append(grid.cellSize.x).Append("x").Append(grid.cellSize.y)
                  .Append(" spacing=").Append(grid.spacing.x).Append(",").Append(grid.spacing.y)
                  .Append(" children=").Append(grid.transform.childCount)
                  .Append(" width=").Append(rect != null ? rect.rect.width.ToString("0") : "?");
            }

            DescribeCharacterSelect(sb);
            DescribeAutoGrids(sb);

            return sb.ToString();
        }

        /// <summary>
        /// Every AutoExpandGridLayoutGroup currently loaded, active or not.
        ///
        /// These are set up in prefabs rather than in code, so which screens use a fixed column
        /// count can only be found by looking at a running game - and a screen whose prefab has not
        /// been opened yet will not appear here at all, which is worth knowing when reading it.
        /// </summary>
        private static void DescribeAutoGrids(StringBuilder sb)
        {
            try
            {
                var grids = Resources.FindObjectsOfTypeAll<AutoExpandGridLayoutGroup>();
                sb.Append(Environment.NewLine).Append("  AUTOGRIDS loaded=").Append(grids.Length);

                foreach (var g in grids)
                {
                    if (g == null)
                        continue;

                    // Not filtered to loaded scenes: several of these live in prefabs that are
                    // instantiated only when their window is first opened, and those are exactly
                    // the screens worth knowing about before opening them.
                    var r = g.GetComponent<RectTransform>();

                    sb.Append(Environment.NewLine)
                      .Append("    '").Append(Path(g.transform)).Append("'")
                      .Append(" active=").Append(g.gameObject.activeInHierarchy)
                      .Append(" constraint=").Append(g.constraint)
                      .Append(" count=").Append(g.constraintCount)
                      .Append(" cell=").Append(g.cellSize.x.ToString("0.#"))
                      .Append(" width=").Append(r != null ? r.rect.width.ToString("0") : "?");
                }
            }
            catch (Exception e)
            {
                sb.Append(Environment.NewLine).Append("  AUTOGRIDS unavailable: ").Append(e.Message);
            }
        }

        /// <summary>
        /// The holder that the character cards are laid out in, and the size a card ends up.
        ///
        /// This is the screen that breaks first in a narrow window, so it gets reported directly
        /// rather than hunted for among every layout group in the scene.
        /// </summary>
        private static void DescribeCharacterSelect(StringBuilder sb)
        {
            try
            {
                var mgr = CharacterChoiceManager.Instance;
                if (mgr == null || mgr.notInPartyHolder == null)
                    return;

                Transform holder = mgr.notInPartyHolder;
                var rect = holder as RectTransform ?? holder.GetComponent<RectTransform>();

                sb.Append(Environment.NewLine)
                  .Append("  CHARSELECT holder='").Append(Path(holder)).Append("'")
                  .Append(" children=").Append(holder.childCount)
                  .Append(" width=").Append(rect != null ? rect.rect.width.ToString("0") : "?")
                  .Append(" height=").Append(rect != null ? rect.rect.height.ToString("0") : "?");

                foreach (var c in holder.GetComponents<Component>())
                    sb.Append(Environment.NewLine).Append("    component: ").Append(c.GetType().Name);

                // The game uses its own LayoutGroup subclass here, not Unity's GridLayoutGroup,
                // so it has to be asked for by its real type.
                var grid = holder.GetComponent<AutoExpandGridLayoutGroup>();
                if (grid != null)
                {
                    sb.Append(Environment.NewLine)
                      .Append("    AUTOGRID constraint=").Append(grid.constraint)
                      .Append(" count=").Append(grid.constraintCount)
                      .Append(" cell=").Append(grid.cellSize.x).Append("x").Append(grid.cellSize.y)
                      .Append(" spacing=").Append(grid.spacing.x).Append(",").Append(grid.spacing.y)
                      .Append(" padding=").Append(grid.padding.horizontal)
                      .Append(" allowOne=").Append(grid.AllowOne);

                    // What the Flexible branch would compute for the current width.
                    if (rect != null)
                    {
                        float avail = rect.rect.width - grid.padding.horizontal + grid.spacing.x;
                        int fit = Mathf.Max(1, Mathf.FloorToInt(avail / (grid.cellSize.x + grid.spacing.x)));
                        sb.Append(" -> columnsThatFit=").Append(fit);
                    }
                }

                if (holder.childCount > 0)
                {
                    var card = holder.GetChild(0) as RectTransform;
                    if (card != null)
                        sb.Append(Environment.NewLine)
                          .Append("    card[0] '").Append(card.name).Append("' ")
                          .Append(card.rect.width.ToString("0")).Append("x").Append(card.rect.height.ToString("0"));
                }
            }
            catch (Exception e)
            {
                sb.Append(Environment.NewLine).Append("  CHARSELECT unavailable: ").Append(e.Message);
            }
        }

        /// <summary>
        /// Captures through the game's own renderer.
        ///
        /// A desktop screengrab returns the wallpaper here: Unity draws into a DirectX surface that
        /// GDI's CopyFromScreen cannot read, which makes an outside-in screenshot silently useless
        /// for checking what the UI actually looks like.
        /// </summary>
        internal static void Shoot(string state)
        {
            if (string.IsNullOrEmpty(ShotDir))
                return;

            try
            {
                System.IO.Directory.CreateDirectory(ShotDir);

                string file = System.IO.Path.Combine(ShotDir,
                    string.Format("{0:00}-{1}-{2}x{3}.png", ++shotIndex, state, Screen.width, Screen.height));

                // Written asynchronously by Unity over the next frame or two.
                ScreenCapture.CaptureScreenshot(file);
                Trace("screenshot queued: " + file);
            }
            catch (Exception e)
            {
                Trace("screenshot failed: " + e.Message);
            }
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            int guard = 0;

            while (t.parent != null && guard++ < 6)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }

            return sb.ToString();
        }
    }
}
