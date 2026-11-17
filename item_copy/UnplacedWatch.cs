using HarmonyLib;

namespace item_copy
{
    /// <summary>
    /// GraphUtils.EmporiumTryAdd tries the bag, counter, showcase and the other store inventories in turn and,
    /// when none of them has room, destroys what is left through DestroyIfUnplaced with this context, only
    /// writing it to the log. While a copy is being placed this counts those units, so the player can be told.
    /// (A copy that merges into an existing stack is destroyed too, but without this context: it did fit.)
    /// </summary>
    [HarmonyPatch(typeof(GraphUtils), nameof(GraphUtils.DestroyIfUnplaced))]
    internal static class UnplacedWatch
    {
        private const string AllFull = "EmporiumTryAdd: every store inventory is full";

        private static bool watching;
        private static int lost;

        public static void Start()
        {
            watching = true;
            lost = 0;
        }

        /// <summary>Stops counting; returns the units the game threw away since Start.</summary>
        public static int Stop()
        {
            watching = false;
            return lost;
        }

        private static void Prefix(GameItem item, string context)
        {
            if (watching && context == AllFull && item != null && item.parentInventory == null && item.unitCount > 0)
            {
                lost += item.unitCount;
            }
        }
    }
}
