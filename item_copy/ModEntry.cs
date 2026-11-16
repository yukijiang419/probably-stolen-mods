using System;
using UnityEngine;
using HarmonyLib;

namespace item_copy
{
    /// <summary>
    /// Entry point of the mod. The loader finds the one class implementing IMod, creates it,
    /// applies the [HarmonyPatch] classes of this assembly (ContextMenuPatches), then calls Init and OnEnable.
    /// </summary>
    public class ModEntry : IMod
    {
        public static ModEntry Instance { get; private set; }
        public ModManifest Manifest { get; private set; }
        public ModLog Log { get; private set; }

        public void Init(ModManifest manifest)
        {
            Instance = this;
            Manifest = manifest;
            Log = new ModLog(manifest);
            Log.Log($"{manifest.Name} v{manifest.ModVersion} loaded.");
        }

        public void OnEnable()
        {
            Log.Log("Enabled.");
        }

        public void OnDisable()
        {
            Log.Log("Disabled.");
        }
    }
}
