using System;
using HarmonyLib;

namespace no_junk
{
    /// <summary>
    /// Every find at the dumping ground (and the second one a metal scanner or event can add) comes from
    /// ScavHelper.SpawnDumpingGroundLoot, which rolls the "dumpingGroundTG" table group. Rolling it with
    /// "junkTable" weighted 0 leaves the other tables in their usual proportions, the same way the game
    /// skips junk when it clears debris (DebrisHelper.PullDumpingGround).
    /// </summary>
    [HarmonyPatch(typeof(ScavHelper), nameof(ScavHelper.SpawnDumpingGroundLoot))]
    internal static class ScavengePatch
    {
        private const string Group = "dumpingGroundTG";
        private const string JunkTable = "junkTable";

        private static bool Prefix(ref GameItem __result)
        {
            try
            {
                string id = LootRegistry.RollGroup(Group, (string tableId) => tableId == JunkTable ? 0f : 1f);
                if (string.IsNullOrEmpty(id))
                {
                    // Nothing but junk left in the group (another mod emptied the rest): keep the vanilla roll.
                    return true;
                }
                GameItem item = ItemSpawner.Spawn(id);
                if (item == null)
                {
                    return true;
                }
                __result = item;
                return false;
            }
            catch (Exception e)
            {
                ModEntry.Instance.Log.Error("No-junk roll failed, using the vanilla roll: " + e);
                return true;
            }
        }
    }
}
