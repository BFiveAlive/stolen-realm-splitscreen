using System;
using UnityEngine;
using UnityEngine.UI;

namespace SplitCoopMod
{
    /// <summary>
    /// Keeps the character menu's Attributes panel only as tall as its contents in a tall window.
    ///
    /// The panel takes a flexible share of the left column's height
    /// (<c>CharacterMenuStyle.AttributesHeightShare</c>, 0.52), and its name and value columns are
    /// VerticalLayoutGroups that spread their five rows over whatever height that gives. The + and -
    /// button columns are not part of that layout: they are anchored at a fixed offset from the
    /// panel's top-right corner with fixed spacing.
    ///
    /// At 16:9 the two agree. <see cref="UiFit"/> gives a tall tile twice the logical height, so the
    /// panel stretched far taller than it needed, the rows spread out with it, and the buttons stayed
    /// bunched under Might - both as reported.
    ///
    /// So the panel is given a fixed height instead of a share: its fixed parts (title, level, the
    /// padding and the bar at the bottom) plus five rows at the buttons' own spacing. The rows then
    /// sit exactly where their buttons are, and the height the panel no longer takes goes to the
    /// Stats list below it. The game restores the share whenever it restyles the panel, so this is
    /// re-applied rather than set once.
    /// </summary>
    internal static class AttributePanelFit
    {
        internal static Action<string> Trace;

        private const float DesignAspect = 16f / 9f;

        private static float lastHeight = -1f;
        private static int reports;

        internal static void Tick()
        {
            if (Screen.height <= 0 || (float)Screen.width / Screen.height >= DesignAspect)
                return;

            var inventory = LoadableUIWindow<InventoryManager>.Instance;
            if (inventory == null || !inventory.gameObject.activeInHierarchy)
                return;

            RectTransform labels = inventory.AttributeLabelsHolder;
            RectTransform values = inventory.AttributesValuesMainHolder as RectTransform;
            RectTransform buttons = inventory.StatsButtonHolder != null ? inventory.StatsButtonHolder.transform as RectTransform : null;
            if (labels == null || values == null || buttons == null)
                return;

            // The Attributes panel is the button column's parent - the same object the game gives
            // its flexible height share in ApplyStyle.
            var panel = buttons.parent as RectTransform;
            var element = panel != null ? panel.GetComponent<LayoutElement>() : null;
            var rows = values.GetComponent<VerticalLayoutGroup>();
            if (element == null || rows == null)
                return;

            int rowCount = CountRows(values);
            float step = ButtonStep(buttons);
            if (rowCount < 1 || step <= 0f)
                return;

            // Everything in the panel that is not the rows: the space around the columns (title,
            // level text, the bar at the bottom) and the columns' own top and bottom padding.
            float chrome = panel.rect.height - values.rect.height;
            float wanted = chrome + rows.padding.top + rows.padding.bottom + rowCount * step - rows.spacing;

            if (wanted <= 0f)
                return;

            bool alreadyFixed = Mathf.Approximately(element.flexibleHeight, 0f)
                                && Mathf.Abs(element.preferredHeight - wanted) < 0.5f
                                && Mathf.Abs(element.minHeight - wanted) < 0.5f;
            if (alreadyFixed)
            {
                ReportAlignment(values, buttons);
                return;
            }

            element.flexibleHeight = 0f;
            element.minHeight = wanted;
            element.preferredHeight = wanted;

            if (panel.parent is RectTransform column)
                LayoutRebuilder.MarkLayoutForRebuild(column);

            if (reports++ < 12 && Mathf.Abs(wanted - lastHeight) >= 0.5f)
                Say("sized the Attributes panel to its contents: " + wanted.ToString("0")
                    + " tall (was " + panel.rect.height.ToString("0") + "), " + rowCount
                    + " rows " + step.ToString("0.0") + " apart to match their buttons");

            lastHeight = wanted;
        }

        private static bool alignmentReported;

        /// <summary>
        /// Once, a tick after the panel has been sized and laid out again: where the rows and their
        /// buttons actually ended up on screen, so the result is measured rather than assumed.
        /// </summary>
        private static void ReportAlignment(RectTransform values, RectTransform buttons)
        {
            if (alignmentReported || !buttons.gameObject.activeInHierarchy)
                return;

            float rowFirst, rowStep, buttonFirst, buttonStep;
            if (!WorldColumn(values, out rowFirst, out rowStep) || !WorldColumn(buttons, out buttonFirst, out buttonStep))
                return;

            alignmentReported = true;

            float scale = Mathf.Abs(values.lossyScale.y);
            if (scale <= 0f)
                scale = 1f;

            Say("attribute rows after sizing: rows " + (rowStep / scale).ToString("0.0") + " apart, buttons "
                + (buttonStep / scale).ToString("0.0") + " apart; first row centre "
                + ((rowFirst - buttonFirst) / scale).ToString("0.0") + " units from its button");
        }

        /// <summary>World y of the first visible child's centre, and the average step between them.</summary>
        private static bool WorldColumn(RectTransform column, out float first, out float step)
        {
            first = 0f;
            step = 0f;
            float last = 0f;
            int count = 0;

            foreach (Transform child in column)
            {
                if (!child.gameObject.activeSelf)
                    continue;

                var rect = child as RectTransform;
                if (rect == null)
                    continue;

                float y = rect.TransformPoint(rect.rect.center).y;
                if (count == 0)
                    first = y;
                last = y;
                count++;
            }

            if (count < 2)
                return false;

            step = Mathf.Abs(first - last) / (count - 1);
            return true;
        }

        private static int CountRows(RectTransform values)
        {
            int count = 0;
            foreach (Transform child in values)
            {
                if (child.gameObject.activeSelf)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Spacing of the + buttons, in the panel's units.
        ///
        /// Read from the children's local positions rather than their world positions, so it still
        /// works while the column is hidden - which it is whenever there are no points to spend, and
        /// the panel should be the right height then too.
        /// </summary>
        private static float ButtonStep(RectTransform buttons)
        {
            float first = 0f, last = 0f;
            int count = 0;

            foreach (Transform child in buttons)
            {
                var rect = child as RectTransform;
                if (rect == null)
                    continue;

                float y = rect.localPosition.y;
                if (count == 0)
                    first = y;
                last = y;
                count++;
            }

            if (count < 2)
                return 0f;

            return Mathf.Abs(first - last) / (count - 1) * Mathf.Abs(buttons.localScale.y);
        }

        private static void Say(string message)
        {
            if (Trace != null)
                Trace(message);
        }
    }
}
