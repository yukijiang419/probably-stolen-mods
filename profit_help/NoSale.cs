using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace profit_help
{
    /// <summary>
    /// "禁售": items the player marked never to sell. The mark is a tag on the item itself, so it is saved
    /// with the item (SaveManager keeps the item's tag state) and follows it wherever it is moved.
    /// A marked item, a container holding one, and everything inside a marked container or machine is never
    /// recommended, framed or auto-placed, and the game
    /// refuses to sell it (PlayerStore.CanSellThisItem).
    /// The right-click row is only added when Shop Assist is not enabled: it has its own "禁售" row.
    /// </summary>
    public static class NoSale
    {
        public const string Tag = "PROFIT_HELPER_NO_SALE_TAG";
        public const string ShopAssistId = "loyce_shop_assist";

        /// <summary>Marked 禁售 by this mod (the tag the right-click row toggles).</summary>
        public static bool IsMarked(GameItem item)
        {
            return item != null && item.IsTag(Tag);
        }

        /// <summary>Marked 禁售 by this mod or by Shop Assist.</summary>
        public static bool IsNoSale(GameItem item)
        {
            return IsMarked(item) || ShopAssistNoSale(item);
        }

        /// <summary>
        /// Must not be sold: the item is 禁售, something sold along inside it is, or it sits in a 禁售
        /// container or machine (at any depth; everything a 禁售 container holds is 禁售 too).
        /// </summary>
        public static bool IsBlocked(GameItem item)
        {
            if (item == null)
            {
                return false;
            }
            if (IsNoSale(item) || MarkedContainer(item) != null)
            {
                return true;
            }
            foreach (GameItem child in GraphUtils.FindAllChildrenType<GameItem>(item))
            {
                if (IsNoSale(child))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The nearest container or machine around the item that is 禁售, or null.</summary>
        public static GameItem MarkedContainer(GameItem item)
        {
            for (GameItem parent = ParentOf(item); parent != null; parent = ParentOf(parent))
            {
                if (IsNoSale(parent))
                {
                    return parent;
                }
            }
            return null;
        }

        private static bool shopAssistLooked;
        private static System.Reflection.MethodInfo shopAssistNoSaleHere;

        /// <summary>
        /// Shop Assist keeps its 禁售 marks in its own data, so they are read through its own check,
        /// ShopAssist.ItemProtection.NoSaleHere (item mark or its no-sale item rules). Looked up once by its
        /// literal name, which needs no extra mod capability; any failure just turns this off.
        /// </summary>
        private static bool ShopAssistNoSale(GameItem item)
        {
            if (!shopAssistLooked)
            {
                shopAssistLooked = true;
                if (ModLoader.IsModEnabled(ShopAssistId))
                {
                    shopAssistNoSaleHere = AccessTools.Method("ShopAssist.ItemProtection:NoSaleHere");
                    if (shopAssistNoSaleHere == null)
                    {
                        ModEntry.Instance?.Log.Warning("Shop Assist is enabled but its 禁售 check was not found; its marks are ignored.");
                    }
                }
            }
            if (shopAssistNoSaleHere == null || item == null)
            {
                return false;
            }
            try
            {
                return (bool)shopAssistNoSaleHere.Invoke(null, new object[] { item });
            }
            catch (Exception e)
            {
                shopAssistNoSaleHere = null;
                ModEntry.Instance?.Log.Warning("Reading Shop Assist's 禁售 failed, its marks are ignored from now on: " + e.Message);
                return false;
            }
        }

        private static GameItem ParentOf(GameItem item)
        {
            return item?.parentInventory == null ? null : GraphUtils.FindParentType<GameItem>(item.parentInventory);
        }

        public static void Toggle(GameItem item)
        {
            if (IsMarked(item))
            {
                item.DisableTag(Tag);
                return;
            }
            item.EnableTag(Tag);
            // Marked while on the counter: take it (or the container on the counter holding it) back.
            GameItem onCounter = CounterRoot(item);
            if (onCounter != null)
            {
                ModHelper.MoveItemToInventory(onCounter);
            }
        }

        /// <summary>The item itself, or the container holding it, that sits directly on a counter; null if none.</summary>
        private static GameItem CounterRoot(GameItem item)
        {
            for (GameItem g = item; g != null; g = ParentOf(g))
            {
                if (IsCounter(g.parentInventory))
                {
                    return g;
                }
            }
            return null;
        }

        public static bool IsCounter(GameInventory inventory)
        {
            EmporiumEntry e = EmporiumEntry.Instance;
            return inventory != null && e != null && (inventory == e.backInvinvElement || inventory == e.backInvinvElementCounter);
        }

        public static bool CanMark(GameItem item)
        {
            return item != null && GeneralHelper.IsItemOwned(item) && !(item is GameCharacterItem);
        }

        /// <summary>ModHook.OnCreateTooltipLate: say so in the item's tooltip.</summary>
        public static void AddTooltip(RichTextBuilder builder, GameItem item)
        {
            if (IsMarked(item))
            {
                builder.AddLine("禁售（利润助手）", ignoreEmpty: true, RenderHandler.ColorPalette.LightRed);
            }
            else
            {
                GameItem container = MarkedContainer(item);
                if (container != null)
                {
                    builder.AddLine($"禁售：所在的「{container.GetDisplayName(preferCustomName: true)}」已禁售", ignoreEmpty: true, RenderHandler.ColorPalette.LightRed);
                }
            }
        }
    }

    /// <summary>
    /// The game itself refuses to sell a blocked item, also when the player puts it on the counter by hand.
    /// Selling one item and the "sell all" button (NegociationUIManager.SellMultiple) both go through here.
    /// </summary>
    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.CanSellThisItem))]
    internal static class NoSaleSellGate
    {
        private static void Postfix(GameItem gameItem, ref bool __result)
        {
            if (__result && NoSale.IsBlocked(gameItem))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// Blocked items cannot be put on the counter at all: a drop there bounces back.
    /// The counter's own rule (EmporiumEntry.Start sets it) is wrapped, not combined with: the game ORs
    /// every function in the list, so an extra "no" next to its "yes" would never win.
    /// </summary>
    [HarmonyPatch(typeof(EmporiumEntry), "Start")]
    internal static class NoSaleCounterGate
    {
        private static void Postfix(EmporiumEntry __instance)
        {
            Wrap(__instance.backInvinvElement);
            Wrap(__instance.backInvinvElementCounter);
        }

        private static void Wrap(GameInventory counter)
        {
            if (counter == null)
            {
                return;
            }
            Func<GameItem, GameInventory, bool> vanilla = counter.mayInventoryAddItemFunc;
            counter.mayInventoryAddItemFunc = (GameItem item, GameInventory inventory) =>
                !NoSale.IsBlocked(item)
                && (vanilla == null || vanilla.InvokeAllReduce(item, inventory, InvokeFuncExtensions.BoolReduceOR));
        }
    }

    /// <summary>
    /// The negotiation panel's "sell N items" count, total and has-enough-budget check use this list.
    /// Without the filter they counted blocked items the sale would then skip.
    /// </summary>
    [HarmonyPatch(typeof(NegociationUIManager), "GetSellableBackInvItems")]
    internal static class NoSaleSellableList
    {
        private static void Postfix(List<GameItem> __result)
        {
            __result?.RemoveAll(NoSale.IsBlocked);
        }
    }

    /// <summary>
    /// The vending machine sells overnight from this list without asking CanSellThisItem. Filtering it keeps
    /// blocked items, and everything in a vending machine marked 禁售, in the machine.
    /// </summary>
    [HarmonyPatch(typeof(VendingMachineHelper), nameof(VendingMachineHelper.CheckEligibleItem))]
    internal static class NoSaleVending
    {
        private static void Postfix(List<GameItem> __result)
        {
            __result?.RemoveAll(NoSale.IsBlocked);
        }
    }

    /// <summary>
    /// Barter customers pick what they take from the player's items on the counter through this list
    /// (BarterOffer conditions); it has no other user in the game.
    /// </summary>
    [HarmonyPatch(typeof(EmporiumEntry), nameof(EmporiumEntry.GetOwnedItemsBack))]
    internal static class NoSaleBarter
    {
        private static void Postfix(List<GameItem> __result)
        {
            __result?.RemoveAll(NoSale.IsBlocked);
        }
    }

    /// <summary>
    /// Splitting a stack makes a brand new item (GameItem.GetSplitItem builds it from the directory), which
    /// would drop the mark: the split-off part keeps it.
    /// </summary>
    [HarmonyPatch(typeof(GameItem), nameof(GameItem.GetSplitItem))]
    internal static class NoSaleSplit
    {
        private static void Postfix(GameItem __instance, GameItem __result)
        {
            if (__result != null && NoSale.IsMarked(__instance))
            {
                __result.EnableTag(NoSale.Tag);
            }
        }
    }

    /// <summary>
    /// Stacking moves units into the target stack, which keeps only its own tags. When marked units join an
    /// unmarked stack the whole stack becomes 禁售: better to protect a few extra items than to sell one.
    /// </summary>
    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.StackItemUnchecked))]
    internal static class NoSaleStack
    {
        private static void Prefix(GameItem item, GameItem targetItem)
        {
            if (targetItem != null && NoSale.IsMarked(item) && !NoSale.IsMarked(targetItem))
            {
                targetItem.EnableTag(NoSale.Tag);
            }
        }
    }

    /// <summary>
    /// Adds a "禁售 / 取消禁售" row under the item right-click menu. Like the copy mod and Shop Assist, it
    /// wraps whatever the menu currently shows instead of rebuilding it, so it works next to other mods
    /// that extend the same menu, whatever order they load in.
    /// </summary>
    [HarmonyPatch]
    internal static class NoSaleMenu
    {
        private const string MenuTag = "ContextMenuNode";

        /// <summary>
        /// Our row, per menu window. The menu is built again whenever a handler initializes: loading a save
        /// from the main menu rebuilds the scene, and other mods (multiplayer ones) create or re-initialize
        /// handlers of their own. One shared button would then point at another menu's row, or at one
        /// already destroyed, so every patch looks up the row of the window it is working on.
        /// </summary>
        private static readonly Dictionary<PixelWindow, RichTextElement> rowsByWindow = new Dictionary<PixelWindow, RichTextElement>();

        private static string Label(GameItem item)
        {
            return NoSale.IsMarked(item) ? "取消禁售" : "禁售";
        }

        /// <summary>The live 禁售 row of this menu window, or null (none added, or destroyed with its scene).</summary>
        private static RichTextElement RowOf(PixelWindow window)
        {
            if (window == null || !rowsByWindow.TryGetValue(window, out RichTextElement row))
            {
                return null;
            }
            if (row?.handler == null)
            {
                rowsByWindow.Remove(window);
                return null;
            }
            return row;
        }

        /// <summary>Menu windows our row was added to, so it can be put back when another mod takes it out.</summary>
        private static readonly HashSet<PixelWindow> wanted = new HashSet<PixelWindow>();

        /// <summary>Whether the row is still shown inside that window (another mod may have rebuilt its content).</summary>
        private static bool IsInMenu(RichTextElement row, PixelWindow window)
        {
            return row?.handler != null && window?.handler != null && row.handler.transform.IsChildOf(window.handler.transform);
        }

        /// <summary>Forgets the rows of windows whose scene is gone.</summary>
        private static void Prune()
        {
            var dead = new List<PixelWindow>();
            foreach (KeyValuePair<PixelWindow, RichTextElement> pair in rowsByWindow)
            {
                if (pair.Key.handler == null || pair.Value?.handler == null)
                {
                    dead.Add(pair.Key);
                }
            }
            foreach (PixelWindow window in dead)
            {
                rowsByWindow.Remove(window);
            }
            wanted.RemoveWhere(window => window.handler == null);
        }

        /// <summary>
        /// Puts our row back if another mod replaced the menu's content after we wrapped it (some rebuild it
        /// in their own OnEventInit postfix, others later). Runs right before the menu is laid out and shown.
        /// </summary>
        private static void EnsureRow(PixelWindow window, RenderHandler.ColorPalette disabledColor)
        {
            if (window == null || !wanted.Contains(window) || IsInMenu(RowOf(window), window))
            {
                return;
            }
            TryAddRow(window, disabledColor);
        }

        // Last of all OnEventInit postfixes: menu mods that rebuild the content (contextWindow.Attach of a
        // grid of their own) run first, and our wrapper goes around whatever they built.
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ItemContextHandler), "OnEventInit")]
        private static void AddRow(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            // An exception escaping OnEventInit leaves the handler uninitialized: no right-click menu at all,
            // vanilla rows included. Whatever goes wrong here must only cost our row.
            try
            {
                TryAddRow(___contextWindow, ___textDisabledColor);
            }
            catch (Exception e)
            {
                ModEntry.Instance.Log.Error("Could not add the 禁售 row; the menu is left as the game built it: " + e);
            }
        }

        private static void TryAddRow(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            Prune();
            if (ModLoader.IsModEnabled(NoSale.ShopAssistId))
            {
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
                rowsByWindow.Remove(___contextWindow);
                UnityEngine.Object.Destroy(existing.handler.gameObject);
            }
            PixelElement current = ___contextWindow?.childElement;
            if (current == null || !___contextWindow.Detach(current))
            {
                ModEntry.Instance.Log.Warning("Context menu could not be extended; the 禁售 row was not added.");
                return;
            }
            var button = new RichTextElement();
            button.handler.gameObject.name = "nodeButtonProfitHelperNoSale";
            button.SetText("禁售", 10000, ___textDisabledColor);
            var rows = new GridPixelElement(1, 2);
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
                rowsByWindow[___contextWindow] = button;
                if (!wanted.Add(___contextWindow))
                {
                    ModEntry.Instance.Log.Log("禁售 row added again: another mod had rebuilt the right-click menu.");
                }
            }
            catch (Exception e)
            {
                rows.Detach(current);
                ___contextWindow.Detach(rows);
                ___contextWindow.Attach(current);
                ___contextWindow.Validate();
                ModEntry.Instance.Log.Error("Could not add the 禁售 row: " + e);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemContextHandler), "ResetButtonColor")]
        private static void ResetColor(PixelWindow ___contextWindow, GameItem ___currentItem, RenderHandler.ColorPalette ___textDisabledColor)
        {
            RichTextElement row = RowOf(___contextWindow);
            if (row == null)
            {
                return;
            }
            try
            {
                row.SetText(Label(___currentItem), 10000, ___textDisabledColor);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "ResetButtonColor", e);
            }
        }

        /// <summary>
        /// Our postfixes run inside the game's right-click handling: an exception escaping one of them would
        /// stop the whole menu from opening. Drop our row of that menu instead and log why.
        /// </summary>
        private static void GiveUp(PixelWindow window, string where, Exception e)
        {
            rowsByWindow.Remove(window);
            wanted.Remove(window);
            ModEntry.Instance.Log.Error($"The 禁售 row failed in {where} and is turned off for this menu: {e}");
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
                RenderHandler.ColorPalette color = NoSale.CanMark(___currentItem)
                    ? (selected ? ___textSelectColor : ___textColor)
                    : (selected ? ___textSelectDisabledColor : ___textDisabledColor);
                row.SetText(Label(___currentItem), 10000, color);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "UpdateSelectButtonColor", e);
            }
        }

        /// <summary>Same width for every visible row, including rows other mods added.</summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(ItemContextHandler), "EqualizeButtonWidths")]
        private static void EqualizeWidth(PixelWindow ___contextWindow, RenderHandler.ColorPalette ___textDisabledColor)
        {
            // The game calls this on every right-click, just before laying the menu out and showing it.
            try
            {
                EnsureRow(___contextWindow, ___textDisabledColor);
            }
            catch (Exception e)
            {
                GiveUp(___contextWindow, "EnsureRow", e);
            }
            RichTextElement row = RowOf(___contextWindow);
            if (row == null || ___contextWindow.handler == null)
            {
                return;
            }
            try
            {
                var rows = new List<RichTextElement>();
                foreach (TreeNodeRender node in ___contextWindow.handler.GetComponentsInChildren<TreeNodeRender>(includeInactive: false))
                {
                    if (node.element is RichTextElement r && !rows.Contains(r))
                    {
                        rows.Add(r);
                    }
                }
                row.SetFixedSize(-1, -1);
                int width = 0;
                foreach (RichTextElement r in rows)
                {
                    width = Math.Max(width, r.widthPixels);
                }
                foreach (RichTextElement r in rows)
                {
                    r.SetFixedSize(width, -1);
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
            GameItem item = ___currentItem;
            if (___currentInventory == null && NoSale.CanMark(item))
            {
                __instance.Stop();
                NoSale.Toggle(item);
                ProfitHelper.RequestRefresh();
            }
            return false;
        }
    }
}
