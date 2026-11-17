using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace profit_help
{
    /// <summary>
    /// While a buying customer is at the counter, finds the set of the player's items that earns the
    /// most money within the customer's budget, frames those items and lists them on screen. The panel
    /// stays on screen in game (F6 hides it); its buttons put the items on the counter and set the filters.
    /// </summary>
    public class ProfitHelper : MonoBehaviour
    {
        private const float RefreshSeconds = 0.5f;
        private const int MaxDpCells = 2000000;
        private const float FrameThickness = 2f;
        private const string FrameName = "ProfitHelperFrame";

        private float nextRefresh;
        private bool visible = true;
        private string lastSignature;

        private static readonly Dictionary<GameItem, long> prices = new Dictionary<GameItem, long>();
        private static readonly HashSet<GameItem> exposedOnInspection = new HashSet<GameItem>();
        /// <summary>Candidates whose label promises more than they are and that the customer will not see through.</summary>
        private static readonly HashSet<GameItem> fakeLabels = new HashSet<GameItem>();
        // Tag -> how many of the items this customer would buy carry it (for the tag picker).
        private static readonly Dictionary<string, int> tagCounts = new Dictionary<string, int>();
        private bool tagPickerOpen;
        private Vector2 tagScroll;
        private readonly List<GameItem> bestItems = new List<GameItem>();
        private readonly Dictionary<GameItemElement, GameObject> frames = new Dictionary<GameItemElement, GameObject>();
        private long bestTotal;
        private long budget;
        private bool hasBudget;
        private int candidateCount;
        private bool active;
        private bool clientIsSecurity;
        private bool clientRefusesContraband;

        private const float PlaceIntervalSeconds = 0.3f;
        private const float PlaceMessageSeconds = 5f;
        private Coroutine placing;
        private StoreClientInstance autoPlacedVisit;
        /// <summary>Items the last placing round put on the counter; once one is sold, auto-place may refill.</summary>
        private readonly List<GameItem> lastPlaced = new List<GameItem>();
        private string placeMessage;
        private float placeMessageUntil;

        private GUIStyle boxStyle;

        private static KeyCode HideKey()
        {
            return ModEntry.Instance?.HideKey?.Value ?? KeyCode.F6;
        }

        private static bool refreshRequested;

        /// <summary>Recompute on the next frame, e.g. after an item was marked 禁售.</summary>
        public static void RequestRefresh()
        {
            refreshRequested = true;
        }

        private void Update()
        {
            if (refreshRequested)
            {
                refreshRequested = false;
                lastSignature = null;
                nextRefresh = 0f;
            }
            // The hide key (F6 unless rebound) only hides the panel; frames and auto-place keep working.
            if (Input.GetKeyDown(HideKey()))
            {
                visible = !visible;
            }
            if (Time.unscaledTime < nextRefresh)
            {
                return;
            }
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            try
            {
                Refresh();
            }
            catch (Exception e)
            {
                ModEntry.Instance?.Log.Error("Refresh failed: " + e);
                Clear();
            }
        }

        private void Refresh()
        {
            if (IsInGame())
            {
                NoSaleBadges.Refresh();
            }
            else
            {
                NoSaleBadges.Forget();
            }
            StoreClient client = GetBuyingClient();
            if (client == null)
            {
                Clear();
                CountOwnedTags();
                return;
            }

            List<GameItem> candidates = GetCandidates(client);
            clientIsSecurity = client.isSecurity;
            clientRefusesContraband = client.noAcceptingContraband;
            hasBudget = client.useClientBudget;
            budget = hasBudget ? Math.Max(0, client.GetBudget()) : long.MaxValue;

            // Only re-solve when something relevant changed (items, prices or budget).
            string signature = BuildSignature(client, candidates);
            if (signature == lastSignature)
            {
                PruneDeadFrames();
            }
            else
            {
                lastSignature = signature;
                candidateCount = candidates.Count;
                bestItems.Clear();
                bestItems.AddRange(Solve(candidates, client));
                bestTotal = 0;
                foreach (GameItem item in bestItems)
                {
                    bestTotal += Price(item);
                }
                active = true;
                UpdateFrames();
            }
            // Checked on every refresh, not only when the deal changes: a customer usually shows up (and the
            // deal is computed) while still walking in, and isClientArrived only turns true afterwards.
            TryAutoPlace(client);
        }

        /// <summary>No buying customer: the tag picker lists the tags of everything the player owns instead.</summary>
        private static void CountOwnedTags()
        {
            tagCounts.Clear();
            EmporiumEntry emporium = EmporiumEntry.Instance;
            if (!IsInGame())
            {
                return;
            }
            foreach (GameItem item in SellableUnits(emporium))
            {
                foreach (string tag in GetTagsDeep(item))
                {
                    tagCounts.TryGetValue(tag, out int n);
                    tagCounts[tag] = n + 1;
                }
            }
        }

        /// <summary>
        /// PlayerStore.Instance builds a new, empty store when there is none (main menu, during a scene load):
        /// never touch it before this says a store exists.
        /// </summary>
        private static bool IsInGame()
        {
            return EmporiumEntry.Instance != null && PlayerStore.HasInstance;
        }

        private void TryAutoPlace(StoreClient client)
        {
            if (ModEntry.Instance?.AutoPlace == null || !ModEntry.Instance.AutoPlace.Value)
            {
                return;
            }
            // The visit is the StoreClientInstance: the same StoreClient blueprint can come back.
            StoreClientInstance visit = PlayerStore.Instance.currentClientInstance;
            if (placing != null || !PlayerStore.Instance.isClientArrived || bestItems.Count == 0)
            {
                return;
            }
            if (visit != autoPlacedVisit)
            {
                autoPlacedVisit = visit;
                placing = StartCoroutine(PlaceBestItems(client));
                return;
            }
            // Same customer: refill once they bought something from the last round and the counter holds none
            // of the player's items any more. Requiring a sale keeps items the player took back off the
            // counter from being put straight back.
            if (lastPlaced.Exists(SoldOrGone) && EmporiumEntry.Instance.GetOwnedItemsBack().Count == 0
                && bestItems.Exists(item => !item.IsRootedInBackInv()))
            {
                placing = StartCoroutine(PlaceBestItems(client));
            }
        }

        private static bool SoldOrGone(GameItem item)
        {
            return item == null || (item is GameItemElement element && element.IsDestroyed()) || !GeneralHelper.IsItemOwned(item);
        }

        /// <summary>
        /// Moves the recommended items onto the counter one by one, through the same API a drag and drop
        /// ends in: the customer inspects each item, talks and the counter modifiers apply as usual. The
        /// pause between items lets each reaction play out.
        /// </summary>
        private System.Collections.IEnumerator PlaceBestItems(StoreClient client)
        {
            var toPlace = new List<GameItem>(bestItems);
            lastPlaced.Clear();
            int placed = 0;
            int notPlaced = 0;
            foreach (GameItem item in toPlace)
            {
                if (client == null || GetBuyingClient() != client)
                {
                    break;
                }
                if (item == null || (item is GameItemElement element && element.IsDestroyed()) || !GeneralHelper.IsItemOwned(item))
                {
                    continue;
                }
                if (item.IsRootedInBackInv())
                {
                    continue;
                }
                ModHelper.MoveItemToBackCounter(item);
                if (item.IsRootedInBackInv())
                {
                    placed++;
                    lastPlaced.Add(item);
                }
                else
                {
                    notPlaced++;
                }
                yield return new WaitForSecondsRealtime(PlaceIntervalSeconds);
            }
            placeMessage = notPlaced == 0
                ? $"已放上柜台 {placed} 件"
                : $"已放上柜台 {placed} 件，<color=#FF7777>{notPlaced} 件柜台放不下</color>";
            placeMessageUntil = Time.unscaledTime + PlaceMessageSeconds;
            lastSignature = null;
            nextRefresh = 0f;
            placing = null;
        }

        private static StoreClient GetBuyingClient()
        {
            if (!IsInGame())
            {
                return null;
            }
            PlayerStore store = PlayerStore.Instance;
            if (store.currentClientInstance == null)
            {
                return null;
            }
            if (MapUIManager.Instance != null && MapUIManager.Instance.isOutside)
            {
                return null;
            }
            StoreClient client = store.currentClientInstance.GetClientBlueprint();
            if (client == null || !client.IsClientBuying())
            {
                return null;
            }
            return client;
        }

        /// <summary>
        /// The owned things that can be sold one by one, from every store inventory (the hidden stash
        /// included, not the trash or the dossier). Every container that holds something (bags, cases,
        /// storage bays, at any depth) is opened up and its items count instead of it: putting the container
        /// itself on the counter would sell its contents along with it, so the two cannot both be offered.
        /// An empty container counts as an item. Things whose contents belong to them stay whole: guns with
        /// their parts, ammo boxes, and locked containers.
        /// The display case (showcase) can be left out entirely, contents of containers in it included.
        /// </summary>
        private static List<GameItem> SellableUnits(EmporiumEntry emporium)
        {
            bool sellShowcase = ModEntry.Instance?.SellShowcase?.Value ?? true;
            var units = new List<GameItem>();
            foreach (GameItem item in emporium.GetAllOwnedItemsShallow())
            {
                if (item == null || item.parentInventory == emporium.trashInvElement || item.parentInventory == emporium.docInvElement)
                {
                    continue;
                }
                if (!sellShowcase && item.parentInventory == emporium.showcaseElement)
                {
                    continue;
                }
                AddUnits(item, units);
            }
            return units;
        }

        private static void AddUnits(GameItem item, List<GameItem> units)
        {
            if (!GeneralHelper.IsItemOwned(item))
            {
                return;
            }
            var children = new List<GameItem>();
            if (IsOpenableContainer(item))
            {
                foreach (GameItem child in GraphUtils.FindAllChildrenType<GameItem>(item))
                {
                    // Only the container's own items; deeper ones are handled by their own container.
                    if (child.parentInventory != null && GraphUtils.FindParentType<GameItem>(child.parentInventory) == item)
                    {
                        children.Add(child);
                    }
                }
            }
            if (children.Count == 0)
            {
                units.Add(item);
                return;
            }
            foreach (GameItem child in children)
            {
                AddUnits(child, units);
            }
        }

        private static bool IsOpenableContainer(GameItem item)
        {
            return item.contentWindow != null
                && !item.IsTag("ADV_GUN_TAG")
                && !AmmoBoxHelper.IsAmmoBox(item)
                && !item.IsTag(AmmoBoxHelper.AMMO_BOX_TAG)
                && !LockHelper.IsCurrentlyLocked(item);
        }

        /// <summary>
        /// Owned items the customer would actually accept (same rules as PlayerStore.CanSellThisItem).
        /// Customers that refuse contraband (security, some scavs and lower-level citizens) already reject it
        /// in IsClientBuyingThisItem; on top of that the player can leave contraband out entirely.
        /// </summary>
        private static List<GameItem> GetCandidates(StoreClient client)
        {
            EmporiumEntry emporium = EmporiumEntry.Instance;
            var result = new List<GameItem>();
            bool sellContraband = ModEntry.Instance?.SellContraband?.Value ?? true;
            HashSet<string> excludedTags = ModEntry.Instance?.GetExcludedTags() ?? new HashSet<string>();
            bool sellFakeLabels = ModEntry.Instance?.SellFakeLabels?.Value ?? true;
            bool sellStolen = ModEntry.Instance?.SellStolen?.Value ?? true;
            prices.Clear();
            exposedOnInspection.Clear();
            fakeLabels.Clear();
            tagCounts.Clear();
            foreach (GameItem item in SellableUnits(emporium))
            {
                // Items the customer can see through are still sellable: placing them triggers the
                // inspection and the sale goes on at the exposed price, which PredictSalePrice uses.
                if (!client.IsClientBuyingThisItem(item))
                {
                    continue;
                }
                if (client.isRequireVariety && client.IsItemInVarietyList(item))
                {
                    continue;
                }
                HashSet<string> itemTags = GetTagsDeep(item);
                foreach (string tag in itemTags)
                {
                    tagCounts.TryGetValue(tag, out int n);
                    tagCounts[tag] = n + 1;
                }
                if (!sellContraband && HasContraband(item))
                {
                    continue;
                }
                if (!sellStolen && HasStolen(item))
                {
                    continue;
                }
                if (NoSale.IsBlocked(item))
                {
                    continue;
                }
                if (excludedTags.Overlaps(itemTags))
                {
                    continue;
                }
                long price = PredictSalePrice(item, client);
                if (price <= 0)
                {
                    continue;
                }
                // A fake label the customer will not see through: they pay the labelled price.
                // (Ones they will see through are sold at the real price and flagged [会被验出] instead.)
                if (VendingMachineHelper.IsMislabelled(item) && !exposedOnInspection.Contains(item))
                {
                    if (!sellFakeLabels)
                    {
                        continue;
                    }
                    fakeLabels.Add(item);
                }
                prices[item] = price;
                result.Add(item);
            }
            return result;
        }

        /// <summary>Item types (the [..] tags of the tooltip) of the item and everything sold along inside it.</summary>
        private static HashSet<string> GetTagsDeep(GameItem item)
        {
            var tags = new HashSet<string>(item.GetGameItemType());
            foreach (GameItem child in GraphUtils.FindAllChildrenType<GameItem>(item))
            {
                tags.UnionWith(child.GetGameItemType());
            }
            return tags;
        }

        /// <summary>The item itself or anything sold along inside it (a bag, a case) is contraband.</summary>
        private static bool HasContraband(GameItem item)
        {
            if (ContrabandHelper.IsContraband(item))
            {
                return true;
            }
            foreach (GameItem child in GraphUtils.FindAllChildrenType<GameItem>(item))
            {
                if (ContrabandHelper.IsContraband(child))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The item is stolen, or carries stolen goods inside it that would be sold along.</summary>
        private static bool HasStolen(GameItem item)
        {
            if (StolenHelper.IsStolenItem(item))
            {
                return true;
            }
            foreach (GameItem child in GraphUtils.FindAllChildrenType<GameItem>(item))
            {
                if (StolenHelper.IsStolenItem(child))
                {
                    return true;
                }
            }
            return false;
        }

        private static long Price(GameItem item)
        {
            return prices.TryGetValue(item, out long price) ? price : item.GetNegociatedValue();
        }

        /// <summary>
        /// The price the customer would pay once the item is on the counter. Placing an item there changes
        /// its price in several places, all simulated here for items that are not on the counter yet:
        ///  1. GameItem.onSlotAddItemFunc: the customer's special buying features (merchant discount,
        ///     premium buy, bribe donation...) and their contraband markup.
        ///  2. GameItem.OnAddedToWeightedArea: retail markup, talents, addiction, perfume, tips,
        ///     contraband/restricted markup, today's news events. We run the real method, so whatever other
        ///     mods patch into it (talent mods...) is included too.
        ///  3. The customer's onItemAddedToWeighted callbacks. Most of them also start dialogue, call
        ///     security or roll the RNG, so they are switched off; the price-only ones are re-added by hand
        ///     (AddClientPriceFeatures) and the haggle ones are run (RunBargainCallbacks).
        ///  4. Retail markup on the contents of a container only counts on the counter (IsApplyMarkUp).
        ///  5. The customer inspects the item and exposes what they can see through (fakes, damage...).
        /// Afterwards the feature lists and the misc value are put back exactly as they were.
        /// </summary>
        private static long PredictSalePrice(GameItem item, StoreClient client)
        {
            if (item.IsRootedInBackInv())
            {
                return item.GetNegociatedValue();
            }

            var touched = new List<GameItem> { item };
            touched.AddRange(GraphUtils.FindAllChildrenType<GameItem>(item));
            var saved = new List<List<ItemFeature>>(touched.Count);
            foreach (GameItem g in touched)
            {
                saved.Add(new List<ItemFeature>(g.itemFeatures));
            }
            long savedLateValue = item.lateUnitValue;
            Action<GameItem> savedOnWeighted = client.onItemAddedToWeighted;
            Action<GameItem> savedOnCounters = client.onItemAddedToCounters;
            try
            {
                client.onItemAddedToWeighted = null;
                client.onItemAddedToCounters = null;
                foreach (string featureId in client.clientItemFeatureBuying)
                {
                    if (ItemFeatureList.SpecialFeature.TryGetValue(featureId, out Func<ItemFeature> create))
                    {
                        item.AddItemFeature(create());
                    }
                }
                item.OnAddedToWeightedArea(ignoreOnItemAddedToWeighted: true);
                AddClientPriceFeatures(item, client);
                RunBargainCallbacks(item, savedOnWeighted);
                if (SimulateInspection(item, client))
                {
                    exposedOnInspection.Add(item);
                }
                if (PlayerStore.Instance.IsCurrentClientAcceptMarkup())
                {
                    // Contents get the markup through GetCurrentValue(forceMarkup) instead of IsApplyMarkUp;
                    // the difference is added as misc value, which is where the contents' value goes anyway.
                    long withMarkup = item.GetItemMiscValue(useMarkup: true, withChild: true, includeEvents: true, forceMarkup: true);
                    long without = item.GetItemMiscValue(useMarkup: true);
                    item.lateUnitValue += withMarkup - without;
                }
                return item.GetNegociatedValue();
            }
            finally
            {
                client.onItemAddedToWeighted = savedOnWeighted;
                client.onItemAddedToCounters = savedOnCounters;
                item.lateUnitValue = savedLateValue;
                for (int i = 0; i < touched.Count; i++)
                {
                    touched[i].itemFeatures.Clear();
                    touched[i].itemFeatures.AddRange(saved[i]);
                }
            }
        }

        /// <summary>
        /// Placing an item makes the customer inspect it and its contents (PlayerStore, after
        /// IsClientBuyingThisItem): every feature they can see through switches to its real value.
        /// Here those features are swapped for copies carrying the real value, without exposing the
        /// originals (exposing runs condition actions and costs reputation). Returns whether anything
        /// would be exposed.
        /// </summary>
        private static bool SimulateInspection(GameItem item, StoreClient client)
        {
            bool any = false;
            var inspected = new List<GameItem>(StoreClient.GetInspectableChildren(item)) { item };
            foreach (GameItem g in inspected)
            {
                for (int i = 0; i < g.itemFeatures.Count; i++)
                {
                    ItemFeature feature = g.itemFeatures[i];
                    if (!client.CanClientExposeThisFeature(feature))
                    {
                        continue;
                    }
                    var exposed = new ItemFeature(feature.featureType)
                    {
                        identifier = feature.identifier,
                        category = feature.category,
                        valueStage = feature.valueStage
                    };
                    exposed.SetPreExposedValueMod(feature.GetActualValueModifier());
                    g.itemFeatures[i] = exposed;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// A successful haggle (markup, discount, blackmail, threaten) changes the budget and registers an
        /// onItemAddedToWeighted callback that only adds the matching price feature to each owned item put
        /// on the counter afterwards. Those callbacks are safe to run, so run them; skipping them made items
        /// off the counter look cheaper than the customer would really pay.
        /// </summary>
        private static void RunBargainCallbacks(GameItem item, Action<GameItem> onWeighted)
        {
            if (onWeighted == null)
            {
                return;
            }
            foreach (Delegate d in onWeighted.GetInvocationList())
            {
                if (IsDeclaredIn(d, typeof(BargainUIManager)))
                {
                    ((Action<GameItem>)d)(item);
                }
            }
        }

        private static bool IsDeclaredIn(Delegate d, Type owner)
        {
            for (Type t = d.Method.DeclaringType; t != null; t = t.DeclaringType)
            {
                if (t == owner)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Price-only features that built-in customers add from their onItemAddedToWeighted callbacks.</summary>
        private static void AddClientPriceFeatures(GameItem item, StoreClient client)
        {
            if (client.isRequireVariety)
            {
                item.AddItemFeature(ItemFeatureList.VarietyBuyerBonus());
            }
            if (client.IsFaction("FACTION_TOURIST"))
            {
                item.AddItemFeature(ItemFeatureList.TouristTax());
            }
            if (client.identifier == "foodBankBuyer")
            {
                item.AddItemFeature(ItemFeatureList.DiscountSell50());
            }
            if (client.identifier == "shadyMerchant" || client.identifier == "wanted4Normal")
            {
                switch (ContrabandHelper.GetContrabandLevel(item))
                {
                    case 1:
                        item.AddItemFeature(ItemFeatureList.DiscountedLowContraband());
                        break;
                    case 2:
                        item.AddItemFeature(ItemFeatureList.DiscountedMidContraband());
                        break;
                    case 3:
                        item.AddItemFeature(ItemFeatureList.DiscountedHighContraband());
                        break;
                    case 4:
                        item.AddItemFeature(ItemFeatureList.DiscountedCriticalContraband());
                        break;
                }
            }
        }

        private string BuildSignature(StoreClient client, List<GameItem> candidates)
        {
            var sb = new StringBuilder();
            sb.Append(client.GetHashCode()).Append('|').Append(budget).Append('|');
            foreach (GameItem item in candidates)
            {
                sb.Append(item.GetHashCode()).Append(':').Append(Price(item)).Append(',');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Subset sum: the customer pays each item's negotiated price and every sale is deducted from the
        /// budget, so the best deal is the subset with the largest total that still fits in the budget.
        /// Customers that want variety buy at most one item per identifier (group knapsack).
        /// </summary>
        private static List<GameItem> Solve(List<GameItem> candidates, StoreClient client)
        {
            if (!client.useClientBudget)
            {
                return OnePerGroup(candidates, client);
            }

            long cap = Math.Max(0, client.GetBudget());
            var groups = new List<List<GameItem>>();
            var groupIndex = new Dictionary<string, int>();
            foreach (GameItem item in candidates)
            {
                if (Price(item) > cap)
                {
                    continue;
                }
                if (client.isRequireVariety)
                {
                    if (!groupIndex.TryGetValue(item.identifier, out int g))
                    {
                        g = groups.Count;
                        groupIndex[item.identifier] = g;
                        groups.Add(new List<GameItem>());
                    }
                    groups[g].Add(item);
                }
                else
                {
                    groups.Add(new List<GameItem> { item });
                }
            }
            if (groups.Count == 0 || cap == 0)
            {
                return new List<GameItem>();
            }

            // Large budgets are solved on a coarser scale. Weights round up and the capacity rounds down,
            // so the answer always fits in the real budget (it may miss the exact optimum by a few credits).
            long unit = Math.Max(1, (cap + MaxDpCells - 1) / MaxDpCells);
            int scaledCap = (int)(cap / unit);

            // reachedBy[s] = item that first reached scaled sum s; predecessors were reached by earlier groups.
            var reachedBy = new GameItem[scaledCap + 1];
            var reachedWeight = new int[scaledCap + 1];
            var reachable = new bool[scaledCap + 1];
            reachable[0] = true;
            foreach (List<GameItem> group in groups)
            {
                var weights = new int[group.Count];
                for (int i = 0; i < group.Count; i++)
                {
                    weights[i] = (int)((Price(group[i]) + unit - 1) / unit);
                }
                for (int s = scaledCap; s > 0; s--)
                {
                    if (reachable[s])
                    {
                        continue;
                    }
                    for (int i = 0; i < group.Count; i++)
                    {
                        int w = weights[i];
                        if (w <= s && reachable[s - w])
                        {
                            reachable[s] = true;
                            reachedBy[s] = group[i];
                            reachedWeight[s] = w;
                            break;
                        }
                    }
                }
            }

            int best = scaledCap;
            while (best > 0 && !reachable[best])
            {
                best--;
            }
            var chosen = new List<GameItem>();
            for (int s = best; s > 0; s -= reachedWeight[s])
            {
                chosen.Add(reachedBy[s]);
            }
            return chosen;
        }

        /// <summary>No budget: every accepted item can be sold; with variety, keep the priciest of each kind.</summary>
        private static List<GameItem> OnePerGroup(List<GameItem> candidates, StoreClient client)
        {
            if (!client.isRequireVariety)
            {
                return new List<GameItem>(candidates);
            }
            var best = new Dictionary<string, GameItem>();
            foreach (GameItem item in candidates)
            {
                if (!best.TryGetValue(item.identifier, out GameItem current) || Price(item) > Price(current))
                {
                    best[item.identifier] = item;
                }
            }
            return new List<GameItem>(best.Values);
        }

        private void UpdateFrames()
        {
            var wanted = new HashSet<GameItemElement>();
            foreach (GameItem item in bestItems)
            {
                if (item is GameItemElement element && !element.IsDestroyed() && element.middle != null)
                {
                    wanted.Add(element);
                }
            }
            var stale = new List<GameItemElement>();
            foreach (var pair in frames)
            {
                if (!wanted.Contains(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }
            foreach (GameItemElement element in stale)
            {
                RemoveFrame(element);
            }
            foreach (GameItemElement element in wanted)
            {
                if (!frames.TryGetValue(element, out GameObject frame) || frame == null)
                {
                    frames[element] = CreateFrame(element);
                }
                else
                {
                    SizeFrame(frame, element);
                }
            }
        }

        private static GameObject CreateFrame(GameItemElement element)
        {
            var frame = new GameObject(FrameName, typeof(RectTransform));
            var rect = frame.GetComponent<RectTransform>();
            rect.SetParent(element.middle.rectTransform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            // Top, bottom, left, right edges.
            AddEdge(rect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, FrameThickness));
            AddEdge(rect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, FrameThickness));
            AddEdge(rect, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(FrameThickness, 0f));
            AddEdge(rect, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(FrameThickness, 0f));
            SizeFrame(frame, element);
            return frame;
        }

        private static void AddEdge(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
        {
            var edge = new GameObject("Edge", typeof(RectTransform), typeof(Image));
            var rect = edge.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            var image = edge.GetComponent<Image>();
            image.color = Color.white;
            image.raycastTarget = false;
        }

        private static void SizeFrame(GameObject frame, GameItemElement element)
        {
            var rect = frame.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(element.widthPixels, element.heightPixels);
            rect.SetAsLastSibling();
        }

        private void PruneDeadFrames()
        {
            var dead = new List<GameItemElement>();
            foreach (var pair in frames)
            {
                if (pair.Value == null || pair.Key.IsDestroyed())
                {
                    dead.Add(pair.Key);
                }
                else
                {
                    SizeFrame(pair.Value, pair.Key);
                }
            }
            foreach (GameItemElement element in dead)
            {
                RemoveFrame(element);
            }
        }

        private void RemoveFrame(GameItemElement element)
        {
            if (frames.TryGetValue(element, out GameObject frame) && frame != null)
            {
                Destroy(frame);
            }
            frames.Remove(element);
        }

        private void ClearFrames()
        {
            foreach (GameObject frame in frames.Values)
            {
                if (frame != null)
                {
                    Destroy(frame);
                }
            }
            frames.Clear();
        }

        private void Clear()
        {
            if (!active && frames.Count == 0)
            {
                return;
            }
            active = false;
            lastSignature = null;
            bestItems.Clear();
            ClearFrames();
        }

        private const float PanelWidth = 400f;

        private GUIStyle textStyle;
        private GUIStyle buttonStyle;
        private GUIStyle toggleStyle;

        private void OnGUI()
        {
            if (boxStyle == null)
            {
                boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 6, 6) };
                textStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, richText = true };
                textStyle.normal.textColor = Color.white;
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 17, richText = true, padding = new RectOffset(8, 8, 6, 6) };
                toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 17 };
                toggleStyle.normal.textColor = Color.white;
                toggleStyle.onNormal.textColor = new Color(1f, 0.65f, 0f);
            }

            // Shown all the time once a save is loaded, customer or not; F6 hides it.
            if (!visible || !IsInGame())
            {
                return;
            }

            GUILayout.BeginArea(new Rect(Screen.width - PanelWidth - 10f, 10f, PanelWidth, Screen.height - 20f));
            GUILayout.BeginVertical(boxStyle);
            DrawButtons();
            GUILayout.Label(BuildPanelText(), textStyle);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawButtons()
        {
            ModEntry mod = ModEntry.Instance;
            bool sellContraband = mod?.SellContraband?.Value ?? true;
            bool autoPlace = mod?.AutoPlace?.Value ?? false;

            GUILayout.Label($"<b>最佳出售组合</b>  <color=#AAAAAA>({HideKey()} 隐藏)</color>", textStyle);

            GUI.enabled = active && placing == null && bestItems.Count > 0;
            if (GUILayout.Button(placing != null ? "正在放上柜台…" : "放上柜台", buttonStyle))
            {
                placing = StartCoroutine(PlaceBestItems(GetBuyingClient()));
            }
            GUI.enabled = true;

            GUILayout.BeginHorizontal();
            if (mod?.SellContraband != null && GUILayout.Button(sellContraband ? "违禁品: <color=#FFA500>卖</color>" : "违禁品: 不卖", buttonStyle))
            {
                mod.SellContraband.Set(!sellContraband);
                lastSignature = null;
                nextRefresh = 0f;
            }
            bool sellShowcase = mod?.SellShowcase?.Value ?? true;
            if (mod?.SellShowcase != null && GUILayout.Button(sellShowcase ? "橱窗: <color=#FFA500>卖</color>" : "橱窗: 不卖", buttonStyle))
            {
                mod.SellShowcase.Set(!sellShowcase);
                lastSignature = null;
                nextRefresh = 0f;
            }
            bool sellFake = mod?.SellFakeLabels?.Value ?? true;
            if (mod?.SellFakeLabels != null && GUILayout.Button(sellFake ? "假标签: <color=#FF5555>卖</color>" : "假标签: 不卖", buttonStyle))
            {
                mod.SellFakeLabels.Set(!sellFake);
                lastSignature = null;
                nextRefresh = 0f;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (mod?.AutoPlace != null && GUILayout.Button(autoPlace ? "自动放上: <color=#7CFC00>开</color>" : "自动放上: 关", buttonStyle))
            {
                mod.AutoPlace.Set(!autoPlace);
                if (!autoPlace)
                {
                    // Switching it on applies to the customer already standing here too.
                    autoPlacedVisit = null;
                    nextRefresh = 0f;
                    lastSignature = null;
                }
            }
            bool sellStolen = mod?.SellStolen?.Value ?? true;
            if (mod?.SellStolen != null && GUILayout.Button(sellStolen ? "赃物: <color=#FF5555>卖</color>" : "赃物: 不卖", buttonStyle))
            {
                mod.SellStolen.Set(!sellStolen);
                lastSignature = null;
                nextRefresh = 0f;
            }
            GUILayout.EndHorizontal();
            DrawTagFilter(mod);
        }

        /// <summary>
        /// "Don't sell these tags": a master switch and a picker listing the tags of the items this
        /// customer would buy, plus any tag already picked. Picked items are left out of the deal, so they
        /// get no frame and are never put on the counter.
        /// </summary>
        private void DrawTagFilter(ModEntry mod)
        {
            if (mod?.ExcludeTagsEnabled == null || mod.ExcludedTags == null)
            {
                return;
            }
            bool enabled = mod.ExcludeTagsEnabled.Value;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(enabled ? "按标签不卖: <color=#7CFC00>开</color>" : "按标签不卖: 关", buttonStyle))
            {
                mod.ExcludeTagsEnabled.Set(!enabled);
                lastSignature = null;
                nextRefresh = 0f;
            }
            if (GUILayout.Button(tagPickerOpen ? "收起标签 ▲" : "选择标签 ▼", buttonStyle))
            {
                tagPickerOpen = !tagPickerOpen;
            }
            GUILayout.EndHorizontal();
            if (!tagPickerOpen)
            {
                return;
            }

            var tags = new List<string>(tagCounts.Keys);
            foreach (string t in (mod.ExcludedTags.Value ?? "").Split(','))
            {
                if (t.Trim().Length > 0 && !tagCounts.ContainsKey(t.Trim()))
                {
                    tags.Add(t.Trim());
                }
            }
            tags.Sort((a, b) => string.CompareOrdinal(TypeHelper.GetTypeDisplayName(a), TypeHelper.GetTypeDisplayName(b)));

            GUILayout.Label(enabled ? "勾选的标签不卖:" : "<color=#AAAAAA>勾选的标签不卖（总开关关着，暂不生效）:</color>", textStyle);
            if (tags.Count == 0)
            {
                GUILayout.Label(active ? "这位顾客要的物品里没有标签" : "你的物品里没有标签", textStyle);
                return;
            }
            tagScroll = GUILayout.BeginScrollView(tagScroll, GUILayout.Height(Mathf.Min(300f, 30f * tags.Count + 8f)));
            foreach (string tag in tags)
            {
                bool selected = mod.IsTagSelected(tag);
                tagCounts.TryGetValue(tag, out int count);
                string label = $"{TypeHelper.GetTypeDisplayName(tag)}  ({count} 件)";
                bool now = GUILayout.Toggle(selected, label, toggleStyle);
                if (now != selected)
                {
                    mod.SetTagExcluded(tag, now);
                    lastSignature = null;
                    nextRefresh = 0f;
                }
            }
            GUILayout.EndScrollView();
        }

        private string BuildPanelText()
        {
            var sb = new StringBuilder();
            if (placeMessage != null && Time.unscaledTime < placeMessageUntil)
            {
                sb.Append(placeMessage).Append('\n');
            }
            if (!active)
            {
                sb.Append("<color=#AAAAAA>现在没有来买东西的顾客</color>");
                return sb.ToString();
            }
            sb.Append("顾客预算: ").Append(hasBudget ? budget.ToString() : "不限").Append('\n');
            sb.Append("最多可得: <color=#7CFC00><b>").Append(bestTotal).Append("</b></color>");
            sb.Append("  (").Append(bestItems.Count).Append('/').Append(candidateCount).Append(" 件可卖)\n");
            if (clientIsSecurity)
            {
                sb.Append("<color=#FF5555><b>保安！柜台上别放违禁品</b></color>\n");
            }
            else
            {
                sb.Append("这位顾客: ").Append(clientRefusesContraband ? "不收违禁品" : "<color=#FFA500>收违禁品</color>").Append('\n');
            }
            int fakeCount = bestItems.FindAll(fakeLabels.Contains).Count;
            if (fakeCount > 0)
            {
                sb.Append("<color=#FF5555><b>注意：含 ").Append(fakeCount)
                  .Append(" 件假标签</b>（标的比实际好，顾客按标签价付钱）</color>\n");
            }
            if (bestItems.Count == 0)
            {
                sb.Append("没有顾客愿意买的物品");
            }
            int shown = 0;
            foreach (GameItem item in bestItems)
            {
                if (shown++ >= 15)
                {
                    sb.Append("… 还有 ").Append(bestItems.Count - 15).Append(" 件");
                    break;
                }
                sb.Append("• ");
                if (HasContraband(item))
                {
                    sb.Append("<color=#FFA500>[违禁]</color> ");
                }
                if (exposedOnInspection.Contains(item))
                {
                    sb.Append("<color=#FF7777>[会被验出]</color> ");
                }
                if (fakeLabels.Contains(item))
                {
                    sb.Append("<color=#FF5555>[假标签]</color> ");
                }
                if (HasStolen(item))
                {
                    sb.Append("<color=#FF5555>[赃物]</color> ");
                }
                sb.Append(item.GetDisplayName(preferCustomName: true)).Append("  ").Append(Price(item)).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private void OnDestroy()
        {
            ClearFrames();
        }
    }
}
