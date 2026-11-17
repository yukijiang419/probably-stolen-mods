using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace item_copy
{
    /// <summary>
    /// Adds a "复制" row under the item right-click menu (ItemContextHandler).
    /// Other mods extend the same menu (Shop Assist adds "禁售"), in whatever order the mods load. So the
    /// menu is never taken apart: whatever the window currently shows is wrapped as-is in a 1x2 grid with
    /// our row below it, the way Shop Assist does it too. The other patches give our row the same colours,
    /// width and click handling the vanilla rows get.
    /// </summary>
    [HarmonyPatch]
    internal static class ContextMenuPatches
    {
        private const string Label = "复制";
        private const string MenuTag = "ContextMenuNode";

        /// <summary>
        /// Our row, per menu window. The menu is built again whenever a handler initializes: loading a save
        /// from the main menu rebuilds the scene, and other mods (multiplayer ones) create or re-initialize
        /// handlers of their own. One shared button would then point at another menu's row, or at one
        /// already destroyed, so every patch looks up the row of the window it is working on.
        /// </summary>
        private static readonly Dictionary<PixelWindow, RichTextElement> rows = new Dictionary<PixelWindow, RichTextElement>();

        /// <summary>The live copy row of this menu window, or null (none added, or destroyed with its scene).</summary>
        private static RichTextElement RowOf(PixelWindow window)
        {
            if (window == null || !rows.TryGetValue(window, out RichTextElement row))
            {
                return null;
            }
            if (row?.handler == null)
            {
                rows.Remove(window);
                return null;
            }
            return row;
        }

        /// <summary>Menu windows our row was added to, so it can be put back when another mod takes it out.</summary>
        private static readonly HashSet<PixelWindow> wanted = new HashSet<PixelWindow>();

        // A persistent UI error must not create a new wrapper and log an exception every frame.
        // Opening the menu still retries through EqualizeButtonWidths/OnEventPress.
        private static readonly HashSet<PixelWindow> failed = new HashSet<PixelWindow>();

        // Each retry wraps the menu once more, so a row that keeps failing is given up for good after a few.
        private const int MaxFailures = 3;
        private static readonly Dictionary<PixelWindow, int> failures = new Dictionary<PixelWindow, int>();

        /// <summary>Whether the row is still shown inside that window (another mod may have rebuilt its content).</summary>
        private static bool IsInMenu(RichTextElement row, PixelWindow window)
        {
            return row?.handler != null && window?.handler != null && row.handler.transform.IsChildOf(window.handler.transform);
        }

        /// <summary>Forgets the rows of windows whose scene is gone.</summary>
        private static void Prune()
        {
            var dead = new List<PixelWindow>();
            foreach (KeyValuePair<PixelWindow, RichTextElement> pair in rows)
            {
                if (pair.Key.handler == null || pair.Value?.handler == null)
                {
                    dead.Add(pair.Key);
                }
            }
            foreach (PixelWindow window in dead)
            {
                rows.Remove(window);
            }
            wanted.RemoveWhere(window => window.handler == null);
            failed.RemoveWhere(window => window.handler == null);
            var gone = new List<PixelWindow>();
            foreach (PixelWindow window in failures.Keys)
            {
                if (window.handler == null)
                {
                    gone.Add(window);
                }
            }
            foreach (PixelWindow window in gone)
            {
                failures.Remove(window);
            }
        }

        /// <summary>
        /// Puts our row back if another mod replaced the menu's content after we wrapped it (some rebuild it
        /// in their own OnEventInit postfix, others later). Runs right before the menu is laid out and shown.
        /// </summary>
        private static void EnsureCopyRow(PixelWindow window, RenderHandler.ColorPalette disabledColor)
        {
            if (window?.handler == null || IsInMenu(RowOf(window), window)
                || (failures.TryGetValue(window, out int count) && count >= MaxFailures))
            {
                return;
            }
            TryAddCopyRow(window, disabledColor);
        }

        // Some mods rebuild the menu in OnEventPress, after EqualizeButtonWidths has returned.
        // Check again after the open event, and on subsequent updates for deferred rebuilds.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventPress")]
        private static void RepairVisibleMenu(GameItem ___currentItem, PixelWindow ___contextWindow,
            RenderHandler.ColorPalette ___textColor, RenderHandler.ColorPalette ___textDisabledColor,
            RenderHandler.ColorPalette ___textSelectColor, RenderHandler.ColorPalette ___textSelectDisabledColor)
        {
            try
            {
                if (___currentItem == null || ___contextWindow?.handler == null || !___contextWindow.IsVisible()
                    || IsInMenu(RowOf(___contextWindow), ___contextWindow))
                {
                    return;
                }
                EqualizeWidth(___contextWindow, ___textDisabledColor);
                UpdateColor(null, ___currentItem, ___contextWindow, ___textColor, ___textDisabledColor,
                    ___textSelectColor, ___textSelectDisabledColor);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "RepairVisibleMenu", e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventUpdate")]
        private static void RepairUpdatedMenu(GameItem ___currentItem, PixelWindow ___contextWindow,
            RenderHandler.ColorPalette ___textColor, RenderHandler.ColorPalette ___textDisabledColor,
            RenderHandler.ColorPalette ___textSelectColor, RenderHandler.ColorPalette ___textSelectDisabledColor)
        {
            if (___contextWindow == null || failed.Contains(___contextWindow))
            {
                return;
            }
            RepairVisibleMenu(___currentItem, ___contextWindow, ___textColor, ___textDisabledColor,
                ___textSelectColor, ___textSelectDisabledColor);
        }

        // Last of all OnEventInit postfixes: menu mods that rebuild the content (contextWindow.Attach of a
        // grid of their own) run first, and our wrapper goes around whatever they built.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventInit")]
        private static void AddCopyRow(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            // An exception escaping OnEventInit leaves the handler uninitialized: no right-click menu at all,
            // vanilla rows included. Whatever goes wrong here must only cost our row.
            try
            {
                TryAddCopyRow(___contextWindow, ___textDisabledColor);
            }
            catch (Exception e)
            {
                ModEntry.Instance.Log.Error("Could not add the copy row; the menu is left as the game built it: " + e);
            }
        }

        private static void TryAddCopyRow(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            Prune();
            PixelElement current = ___contextWindow?.childElement;
            if (current == null)
            {
                // Marked failed so the per-frame repair does not log this every frame.
                failed.Add(___contextWindow);
                ModEntry.Instance.Log.Warning("Context menu has no content; the copy row was not added.");
                return;
            }
            RichTextElement existing = RowOf(___contextWindow);
            if (existing != null)
            {
                if (IsInMenu(existing, ___contextWindow))
                {
                    return;
                }
                // Taken out of the menu by another mod: drop the stray row, a new one wraps the current content.
                rows.Remove(___contextWindow);
                UnityEngine.Object.Destroy(existing.handler.gameObject);
            }
            var button = new RichTextElement();
            button.handler.gameObject.name = "nodeButtonCopy";
            button.SetText(Label, 10000, ___textDisabledColor);
            var grid = new GridPixelElement(1, 2);
            try
            {
                // Detach validates the window and can itself throw after changing childElement.
                // Keep it inside the rollback boundary too.
                if (!___contextWindow.Detach(current))
                {
                    UnityEngine.Object.Destroy(button.handler.gameObject);
                    UnityEngine.Object.Destroy(grid.handler.gameObject);
                    failed.Add(___contextWindow);
                    ModEntry.Instance.Log.Warning("Context menu could not be detached; the copy row was not added.");
                    return;
                }
                if (!grid.Attach(current) || !grid.Attach(button) || !___contextWindow.Attach(grid))
                {
                    throw new InvalidOperationException("Attaching the menu rows failed.");
                }
                grid.Validate();
                // Hover/close detection only counts nodes tagged like the vanilla menu.
                foreach (Transform t in ___contextWindow.handler.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    t.gameObject.tag = MenuTag;
                }
                ___contextWindow.Validate();
                rows[___contextWindow] = button;
                failed.Remove(___contextWindow);
                ModEntry.Instance.Log.Log(wanted.Add(___contextWindow)
                    ? "Copy row added to the right-click menu."
                    : "Copy row added again: another mod had rebuilt the right-click menu.");
            }
            catch (Exception e)
            {
                // Put the menu back exactly as it was.
                try
                {
                    grid.Detach(current);
                }
                finally
                {
                    // Even if detaching the failed wrapper throws, restore the original menu.
                    ___contextWindow.Attach(current);
                }
                ___contextWindow.Validate();
                UnityEngine.Object.Destroy(button.handler.gameObject);
                UnityEngine.Object.Destroy(grid.handler.gameObject);
                ModEntry.Instance.Log.Error("Could not add the copy row: " + e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "ResetButtonColor")]
        private static void ResetColor(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            RichTextElement row = RowOf(___contextWindow);
            if (row == null)
            {
                return;
            }
            try
            {
                row.SetText(Label, 10000, ___textDisabledColor);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "ResetButtonColor", e);
            }
        }

        /// <summary>
        /// Our postfixes run inside the game's right-click handling: an exception escaping one of them would
        /// stop the whole menu from opening. Hide our failed row and retry on the next menu check.
        /// </summary>
        private static void GiveUp(PixelWindow window, string where, Exception e)
        {
            // Hide the failed row before forgetting it, otherwise it remains visible but cannot be clicked.
            RichTextElement row = RowOf(window);
            if (row?.handler != null)
            {
                row.handler.gameObject.SetActive(false);
            }
            rows.Remove(window);
            if (window == null)
            {
                ModEntry.Instance.Log.Error($"The copy row failed in {where}: {e}");
                return;
            }
            failed.Add(window);
            failures.TryGetValue(window, out int count);
            failures[window] = ++count;
            ModEntry.Instance.Log.Error(count < MaxFailures
                ? $"The copy row failed in {where}; it will be retried when the menu opens: {e}"
                : $"The copy row failed in {where} {count} times and is turned off for this menu: {e}");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "UpdateSelectButtonColor")]
        private static void UpdateColor(RichTextElement selectedButton, GameItem ___currentItem, PixelWindow ___contextWindow,
            RenderHandler.ColorPalette ___textColor, RenderHandler.ColorPalette ___textDisabledColor,
            RenderHandler.ColorPalette ___textSelectColor, RenderHandler.ColorPalette ___textSelectDisabledColor)
        {
            RichTextElement row = RowOf(___contextWindow);
            if (row == null || ___currentItem == null)
            {
                return;
            }
            try
            {
                bool selected = selectedButton == row;
                RenderHandler.ColorPalette color = ItemCopier.CanCopy(___currentItem)
                    ? (selected ? ___textSelectColor : ___textColor)
                    : (selected ? ___textSelectDisabledColor : ___textDisabledColor);
                row.SetText(Label, 10000, color);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "UpdateSelectButtonColor", e);
            }
        }

        /// <summary>
        /// Gives every visible row of the menu the same width: the vanilla ones, ours and rows other mods
        /// added (they may run their own equalizing before or after this, without knowing about ours).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ItemContextHandler), "EqualizeButtonWidths")]
        private static void EqualizeWidth(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            // The game calls this on every right-click, just before laying the menu out and showing it.
            try
            {
                EnsureCopyRow(___contextWindow, ___textDisabledColor);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "EnsureCopyRow", e);
            }
            RichTextElement copyRow = RowOf(___contextWindow);
            if (copyRow == null || ___contextWindow.handler == null)
            {
                return;
            }
            try
            {
                var visible = new List<RichTextElement>();
                // The window is still hidden at this point. activeInHierarchy therefore excludes all rows.
                foreach (TreeNodeRender node in ___contextWindow.handler.GetComponentsInChildren<TreeNodeRender>(includeInactive: true))
                {
                    bool hidden = false;
                    for (Transform t = node.transform; t != ___contextWindow.handler.transform; t = t.parent)
                    {
                        if (!t.gameObject.activeSelf)
                        {
                            hidden = true;
                            break;
                        }
                    }
                    if (!hidden && node.element is RichTextElement row && !visible.Contains(row))
                    {
                        visible.Add(row);
                    }
                }
                copyRow.SetFixedSize(-1, -1);
                int width = 0;
                foreach (RichTextElement row in visible)
                {
                    width = Math.Max(width, row.widthPixels);
                }
                foreach (RichTextElement row in visible)
                {
                    row.SetFixedSize(width, -1);
                }
                // RichTextElement resizing does not validate its enclosing grids. Refresh from the
                // leaves up so nested wrappers added by other mods use the new widths and heights.
                TreeNodeRender[] nodes = ___contextWindow.handler.GetComponentsInChildren<TreeNodeRender>(includeInactive: true);
                for (int i = nodes.Length - 1; i >= 0; i--)
                {
                    if (nodes[i].element is GridPixelElement grid)
                    {
                        grid.Validate();
                    }
                }
                ___contextWindow.Validate();
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "EqualizeButtonWidths", e);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemContextHandler), "TriggerButton")]
        private static bool OnClick(ItemContextHandler __instance, RichTextElement button, GameItem ___currentItem,
            GameGridInventory ___currentInventory, PixelWindow ___contextWindow)
        {
            RichTextElement row = RowOf(___contextWindow);
            if (row == null || button != row)
            {
                return true;
            }
            try
            {
                GameItem item = ___currentItem;
                if (___currentInventory == null && ItemCopier.CanCopy(item))
                {
                    __instance.Stop();
                    ItemCopier.Copy(item);
                }
                return false;
            }
            catch (Exception e)
            {
                ModEntry.Instance.Log.Error($"Copying '{___currentItem?.identifier}' failed: {e}");
                return false;
            }
        }
    }
}
