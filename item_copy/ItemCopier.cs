using System.Collections.Generic;

namespace item_copy
{
    /// <summary>
    /// Makes an exact copy of an item with the game's own save code: SaveManager encodes the item and its
    /// contents the way a save does and decodes them into new items, and each feature (condition, fakes,
    /// markups, water purity...) is copied through ES3, the serializer the save file uses for them.
    /// The copies get new unique ids (features and remembered windows are keyed by them) and are owned.
    /// </summary>
    public static class ItemCopier
    {
        /// <summary>Store fixtures that are items too; there must only ever be one of each.</summary>
        public static bool CanCopy(GameItem item)
        {
            EmporiumEntry e = EmporiumEntry.Instance;
            if (item == null || e == null || !PlayerStore.HasInstance || item is GameCharacterItem)
            {
                return false;
            }
            return item != e.trashcan && item != e.dossier && item != e.drain && item != e.faucet && item != e.cassettePlayer;
        }

        public static GameItem Copy(GameItem source)
        {
            PlayerStore store = PlayerStore.Instance;
            var originals = new List<GameItem> { source };
            originals.AddRange(GraphUtils.FindAllChildrenType<GameItem>(source));
            var byId = new Dictionary<int, GameItem>();
            foreach (GameItem original in originals)
            {
                byId[original.GetUniqueID()] = original;
            }

            // EncodeNodes hands every feature to PlayerStore.savedItemFeatureList for the save file; take them back out.
            List<ItemFeature> pending = store.savedItemFeatureList;
            store.savedItemFeatureList = new List<ItemFeature>();
            List<SaveItemNode> nodes;
            try
            {
                nodes = SaveManager.EncodeNodes(source);
            }
            finally
            {
                store.savedItemFeatureList = pending;
            }
            GameItem copy = SaveManager.DecodeNodes(nodes);

            var copies = new List<GameItem> { copy };
            copies.AddRange(GraphUtils.FindAllChildrenType<GameItem>(copy));
            // Own tags and ids first: the load steps below change tags, which must not reach the original.
            var originalIds = new Dictionary<GameItem, int>();
            foreach (GameItem c in copies)
            {
                originalIds[c] = c.uniqueId;
                OwnTags(c);
                c.uniqueId = 0;
                c.GetUniqueID();
            }
            foreach (GameItem c in copies)
            {
                FinishLoading(c);
            }
            foreach (GameItem c in copies)
            {
                // Like PlayerStore.LoadGame: drop the factory's default features, then put the saved ones back.
                c.RemoveAllFeature();
                int newId = c.GetUniqueID();
                if (byId.TryGetValue(originalIds[c], out GameItem original))
                {
                    foreach (ItemFeature feature in original.itemFeatures)
                    {
                        ItemFeature clone = ES3.Deserialize<ItemFeature>(ES3.Serialize(feature));
                        clone.parentItemUniqueId = newId;
                        c.itemFeatures.Add(clone);
                    }
                }
                GeneralHelper.SetItemOwned(c, own: true);
            }
            GraphUtils.EmporiumTryAdd(copy);
            return copy;
        }

        /// <summary>
        /// The steps PlayerStore.DecodeSaveItem runs on every decoded item. DecodeNodes alone leaves the item as
        /// the factory built it apart from its data: finished wine would still open the empty bottle's brewing
        /// window, upgraded machines would have their base size, and so on.
        /// </summary>
        private static void FinishLoading(GameItem item)
        {
            MachineHelper.LoadUpgrade(item);
            SpriteHelper.UpdateWaterContainerSprite(item);
            GeneralHelper.LoadLockItem(item);
            PlantTraitList.LoadEdibleProduce(item);
            PlantTraitList.LoadTasteFeature(item);
            GeneralHelper.LoadNonOwnedToolTag(item);
            GeneralHelper.LoadHazardousWasteTag(item);
            ScavHelper.LoadScavTokenTag(item);
            LockHelper.LoadLockedContainer(item);
            MachineBrokenHelper.LoadBrokenMachine(item);
            MachineHelper.OnLoad(item);
            MachineTurboBoosterAdv.LoadTurboBoostEligibleTag(item);
            CurrentConverterHelper.OnLoad(item);
            item.onLoaded?.Invoke(item);
        }

        /// <summary>
        /// TagSystem.Clone copies the dictionary but not the TagStates in it, so a decoded item still shares
        /// every tag (a bucket's contents, charges, counters...) with the original; a real load never sees
        /// this because the nodes went through JSON. Give the copy its own TagStates, keeping state and
        /// modifiedState sharing an entry where they did.
        /// </summary>
        private static void OwnTags(GameItem item)
        {
            var clones = new Dictionary<TagState, TagState>();
            item.state = OwnTags(item.state, clones);
            item.modifiedState = OwnTags(item.modifiedState, clones);
        }

        private static TagSystem OwnTags(TagSystem tags, Dictionary<TagState, TagState> clones)
        {
            if (tags == null)
            {
                return null;
            }
            var own = new TagSystem();
            foreach (KeyValuePair<string, TagState> pair in tags.dict)
            {
                if (pair.Value == null)
                {
                    own.dict[pair.Key] = null;
                    continue;
                }
                if (!clones.TryGetValue(pair.Value, out TagState clone))
                {
                    clone = pair.Value.Clone();
                    clones[pair.Value] = clone;
                }
                own.dict[pair.Key] = clone;
            }
            return own;
        }
    }
}
