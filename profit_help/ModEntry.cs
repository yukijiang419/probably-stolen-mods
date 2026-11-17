using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace profit_help
{
    /// <summary>
    /// Entry point of the mod. The loader finds the one class implementing IMod, creates it,
    /// applies the [HarmonyPatch] classes of this assembly, then calls Init and OnEnable.
    /// All of that happens in the main menu: no save is loaded yet, so do not touch game
    /// state here. Subscribe to ModHook events instead (see ModdingGuide.md, "Hook Points").
    /// </summary>
    public class ModEntry : IMod
    {
        public static ModEntry Instance { get; private set; }
        public ModManifest Manifest { get; private set; }
        public ModLog Log { get; private set; }

        /// <summary>Whether the best deal may include contraband. Saved in the mod's config; a panel button toggles it.</summary>
        public ConfigEntry<bool> SellContraband { get; private set; }

        /// <summary>Whether items in the display case (showcase) may be recommended. A panel button toggles it.</summary>
        public ConfigEntry<bool> SellShowcase { get; private set; }

        /// <summary>Whether items whose label promises more than they really are may be recommended. A panel button toggles it.</summary>
        public ConfigEntry<bool> SellFakeLabels { get; private set; }

        /// <summary>Whether stolen goods (STOLEN_TAG) may be recommended. A panel button toggles it.</summary>
        public ConfigEntry<bool> SellStolen { get; private set; }

        /// <summary>Put the best deal on the counter by itself once per customer. A panel button does it on demand.</summary>
        public ConfigEntry<bool> AutoPlace { get; private set; }

        /// <summary>Key that hides/shows the panel. Rebindable in the game's Settings > Key bindings.</summary>
        public ConfigEntry<KeyCode> HideKey { get; private set; }

        /// <summary>Master switch for ExcludedTags.</summary>
        public ConfigEntry<bool> ExcludeTagsEnabled { get; private set; }

        /// <summary>Item types (the [..] tags in item tooltips) never recommended, comma separated ids.</summary>
        public ConfigEntry<string> ExcludedTags { get; private set; }

        public HashSet<string> GetExcludedTags()
        {
            var set = new HashSet<string>();
            if (ExcludeTagsEnabled == null || !ExcludeTagsEnabled.Value || string.IsNullOrEmpty(ExcludedTags?.Value))
            {
                return set;
            }
            foreach (string tag in ExcludedTags.Value.Split(','))
            {
                if (tag.Trim().Length > 0)
                {
                    set.Add(tag.Trim());
                }
            }
            return set;
        }

        /// <summary>Adds or removes one tag from ExcludedTags (saved right away), whatever the master switch says.</summary>
        public void SetTagExcluded(string tag, bool excluded)
        {
            var tags = new List<string>();
            foreach (string t in (ExcludedTags.Value ?? "").Split(','))
            {
                if (t.Trim().Length > 0 && t.Trim() != tag)
                {
                    tags.Add(t.Trim());
                }
            }
            if (excluded)
            {
                tags.Add(tag);
            }
            ExcludedTags.Set(string.Join(",", tags));
        }

        public bool IsTagSelected(string tag)
        {
            return Array.IndexOf((ExcludedTags?.Value ?? "").Split(','), tag) >= 0;
        }

        public void Init(ModManifest manifest)
        {
            Instance = this;
            Manifest = manifest;
            Log = new ModLog(manifest);
            Log.Log($"{manifest.Name} v{manifest.ModVersion} loaded.");
        }

        public void OnEnable()
        {
            // Runs right after Init, still in the main menu. Register hooks, items, loot edits,
            // options and configuration here. Examples:
            //   ModHook.OnGameLoadedInit += OnGameLoaded;
            //   ModHelper.AddLootEntry(Manifest.ID, "junkTable", "my_item", 10);
            SellContraband = ModConfig.For(Manifest.ID).Define("sell_contraband", true, "交易",
                () => "最佳组合包含违禁品",
                () => "关闭后不再推荐卖违禁品（只对愿意收违禁品的顾客有影响）。面板上的按钮也能切换。");
            SellShowcase = ModConfig.For(Manifest.ID).Define("sell_showcase", true, "交易",
                () => "最佳组合包含橱窗里的物品",
                () => "关闭后，橱窗（展示柜）里的物品和橱窗里容器装的东西都不推荐、不加框、不自动放上柜台。面板上的按钮也能切换。");
            SellFakeLabels = ModConfig.For(Manifest.ID).Define("sell_fake_labels", true, "交易",
                () => "最佳组合包含假标签物品",
                () => "假标签：标出来的比真实情况好（例如假货当真货卖），顾客又看不出来、会按标签价付钱。关闭后这类物品不推荐、不自动放。面板上的按钮也能切换。");
            SellStolen = ModConfig.For(Manifest.ID).Define("sell_stolen", true, "交易",
                () => "最佳组合包含赃物",
                () => "赃物：鼠标提示里有红字被盗信息的物品。关闭后赃物（以及装着赃物的容器）不推荐、不加框、不自动放上柜台。面板上的按钮也能切换。");
            AutoPlace = ModConfig.For(Manifest.ID).Define("auto_place", false, "交易",
                () => "顾客到柜台时自动放上推荐物品",
                () => "每位买东西的顾客到柜台时，自动把最佳组合放上柜台（每位顾客一次）。不开也可以随时点面板上的“放上柜台”。");
            ExcludeTagsEnabled = ModConfig.For(Manifest.ID).Define("exclude_tags_enabled", false, "交易",
                () => "不卖指定标签的物品",
                () => "开启后，带有所选标签的物品（或装着这类物品的容器）不会被推荐、不加框、也不会自动放上柜台。标签在面板上选。");
            ExcludedTags = ModConfig.For(Manifest.ID).Define("excluded_tags", "", "交易",
                () => "不卖的标签",
                tooltip: () => "物品类型 ID，用逗号分隔。建议直接在面板上勾选。");
            HideKey = ModConfig.For(Manifest.ID).DefineEnum("hide_key", KeyCode.F6, "按键",
                () => "隐藏/显示面板",
                () => "默认 F6。更方便的改法：游戏设置的「按键绑定」页里点这一项再按新键。");
            // Shown on the game's own key binding page next to the vanilla keys; the value lives in HideKey.
            ModHelper.AddGameOptionKeyBind(Manifest.ID, "keybind", "hide_key",
                () => "利润助手：隐藏/显示面板",
                () => HideKey.Value,
                key => HideKey.Set(key),
                () => "隐藏或显示右上角的最佳出售组合面板。");
            // The helper idles until a buying customer is at the counter, so it can live from the main menu on.
            helperObject = new GameObject("ProfitHelper");
            UnityEngine.Object.DontDestroyOnLoad(helperObject);
            helperObject.AddComponent<ProfitHelper>();
            ModHook.OnCreateTooltipLate += NoSale.AddTooltip;
            Log.Log("Enabled.");
        }

        public void OnDisable()
        {
            // Runs when the game shuts down. Unsubscribe what you subscribed in OnEnable.
            ModHook.OnCreateTooltipLate -= NoSale.AddTooltip;
            if (helperObject != null)
            {
                UnityEngine.Object.Destroy(helperObject);
                helperObject = null;
            }
            Log.Log("Disabled.");
        }

        private GameObject helperObject;

        // private void OnGameLoaded()
        // {
        //     // A save is loaded: game systems exist from here on.
        // }
    }
}
