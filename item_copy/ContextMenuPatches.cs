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

        private static RichTextElement copyButton;

        /// <summary>
        /// Loading a save from the main menu rebuilds the scene and with it the menu. OnEventInit calls
        /// ResetButtonColor before our postfix runs, so the button of the previous scene, already destroyed,
        /// must be forgotten first: touching it would throw and leave the whole right-click handler dead.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventInit")]
        private static void ForgetOldRow()
        {
            copyButton = null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventInit")]
        private static void AddCopyRow(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            copyButton = null;
            PixelElement current = ___contextWindow?.childElement;
            if (current == null)
            {
                ModEntry.Instance.Log.Warning("Context menu has no content; the copy row was not added.");
                return;
            }
            var button = new RichTextElement();
            button.handler.gameObject.name = "nodeButtonCopy";
            button.SetText(Label, 10000, ___textDisabledColor);
            var rows = new GridPixelElement(1, 2);
            if (!___contextWindow.Detach(current))
            {
                ModEntry.Instance.Log.Warning("Context menu could not be detached; the copy row was not added.");
                return;
            }
            try
            {
                if (!rows.Attach(current) || !rows.Attach(button) || !___contextWindow.Attach(rows))
                {
                    throw new InvalidOperationException("Attaching the menu rows failed.");
                }
                rows.Validate();
                // Hover/close detection only counts nodes tagged like the vanilla menu.
                foreach (Transform t in ___contextWindow.handler.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    t.gameObject.tag = MenuTag;
                }
                ___contextWindow.Validate();
                copyButton = button;
            }
            catch (Exception e)
            {
                // Put the menu back exactly as it was.
                rows.Detach(current);
                ___contextWindow.Detach(rows);
                ___contextWindow.Attach(current);
                ___contextWindow.Validate();
                ModEntry.Instance.Log.Error("Could not add the copy row: " + e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "ResetButtonColor")]
        private static void ResetColor(RenderHandler.ColorPalette ___textDisabledColor)
        {
            try
            {
                copyButton?.SetText(Label, 10000, ___textDisabledColor);
            }
            catch (Exception e)
            {
                GiveUp("ResetButtonColor", e);
            }
        }

        /// <summary>
        /// Our postfixes run inside the game's right-click handling: an exception escaping one of them would
        /// stop the whole menu from opening. Drop our row instead and log why, once.
        /// </summary>
        private static void GiveUp(string where, Exception e)
        {
            copyButton = null;
            ModEntry.Instance.Log.Error($"The copy row failed in {where} and is turned off until the next load: {e}");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "UpdateSelectButtonColor")]
        private static void UpdateColor(RichTextElement selectedButton, GameItem ___currentItem,
            RenderHandler.ColorPalette ___textColor, RenderHandler.ColorPalette ___textDisabledColor,
            RenderHandler.ColorPalette ___textSelectColor, RenderHandler.ColorPalette ___textSelectDisabledColor)
        {
            if (copyButton == null || ___currentItem == null)
            {
                return;
            }
            try
            {
                bool selected = selectedButton == copyButton;
                RenderHandler.ColorPalette color = ItemCopier.CanCopy(___currentItem)
                    ? (selected ? ___textSelectColor : ___textColor)
                    : (selected ? ___textSelectDisabledColor : ___textDisabledColor);
                copyButton.SetText(Label, 10000, color);
            }
            catch (Exception e)
            {
                GiveUp("UpdateSelectButtonColor", e);
            }
        }

        /// <summary>
        /// Gives every visible row of the menu the same width: the vanilla ones, ours and rows other mods
        /// added (they may run their own equalizing before or after this, without knowing about ours).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "EqualizeButtonWidths")]
        private static void EqualizeWidth(PixelWindow ___contextWindow)
        {
            if (copyButton == null || ___contextWindow?.handler == null)
            {
                return;
            }
            try
            {
                var rows = new List<RichTextElement>();
                foreach (TreeNodeRender node in ___contextWindow.handler.GetComponentsInChildren<TreeNodeRender>(includeInactive: false))
                {
                    if (node.element is RichTextElement row && !rows.Contains(row))
                    {
                        rows.Add(row);
                    }
                }
                copyButton.SetFixedSize(-1, -1);
                int width = 0;
                foreach (RichTextElement row in rows)
                {
                    width = Math.Max(width, row.widthPixels);
                }
                foreach (RichTextElement row in rows)
                {
                    row.SetFixedSize(width, -1);
                }
                ___contextWindow.Validate();
            }
            catch (Exception e)
            {
                GiveUp("EqualizeButtonWidths", e);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemContextHandler), "TriggerButton")]
        private static bool OnClick(ItemContextHandler __instance, RichTextElement button, GameItem ___currentItem, GameGridInventory ___currentInventory)
        {
            if (copyButton == null || button != copyButton)
            {
                return true;
            }
            GameItem item = ___currentItem;
            if (___currentInventory == null && ItemCopier.CanCopy(item))
            {
                __instance.Stop();
                try
                {
                    ItemCopier.Copy(item);
                }
                catch (Exception e)
                {
                    ModEntry.Instance.Log.Error($"Copying '{item.identifier}' failed: {e}");
                }
            }
            return false;
        }
    }
}
