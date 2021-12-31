using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;

namespace ChildToNPC.Integrations.ContentPatcher
{
    /// <summary>Handles integrating with the Content Patcher API.</summary>
    internal class ContentPatcherIntegration
    {
        /*********
        ** Fields
        *********/
        /// <summary>The current mod's manifest.</summary>
        private readonly IManifest Manifest;

        /// <summary>The Content Patcher API, or <c>null</c> if Content Patcher isn't installed.</summary>
        private readonly IContentPatcherAPI ContentPatcher;

        /// <summary>The ordinal prefixes for child token names.</summary>
        private readonly string[] Ordinals = { "First", "Second", "Third", "Fourth" }; // I'm stopping at four for now. If FamilyPlanning expands past four, I'll need to come back to this.

        /// <summary>The game tick when the child data was last updated.</summary>
        private int CacheTick = -1;

        /// <summary>A snapshot of the children converted to NPCs as of the last context update.</summary>
        private ChildData[] Cache;

        /// <summary>The total number of children, including those not converted to NPC yet.</summary>
        private int TotalChildren;

        private HashSet<NPC> AdultNPCsInFarmHouse;

        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="manifest">The current mod's manifest.</param>
        /// <param name="modRegistry">The SMAPI mod registry.</param>
        public ContentPatcherIntegration(IManifest manifest, IModRegistry modRegistry)
        {
            this.Manifest = manifest;
            this.ContentPatcher = modRegistry.GetApi<IContentPatcherAPI>("Pathoschild.ContentPatcher");
        }

        /// <summary>Register custom tokens with Content Patcher.</summary>
        public void RegisterTokens()
        {
            if (this.ContentPatcher == null)
                return;

            // aggregate tokens
            this
                .AddToken("NumberTotalChildren", () => this.Cache != null, () => this.TotalChildren.ToString(CultureInfo.InvariantCulture));

            // per-child tokens
            for (int i = 0; i < this.Ordinals.Length; i++)
            {
                string ordinal = this.Ordinals[i];
                int index = i;

                this
                    .AddToken($"{ordinal}ChildName", index, child => child.Name)
                    .AddToken($"{ordinal}ChildBirthday", index, child => child.Birthday)
                    .AddToken($"{ordinal}ChildBed", index, child => child.Bed)
                    .AddToken($"{ordinal}ChildGender", index, child => child.Gender)
                    .AddToken($"{ordinal}ChildParent", index, child => child.Parent);
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Register a token with Content Patcher.</summary>
        /// <param name="name">The token name.</param>
        /// <param name="isReady">Get whether the token is ready as of the last context update.</param>
        /// <param name="getValue">Get the token value as of the last context update.</param>
        private ContentPatcherIntegration AddToken(string name, Func<bool> isReady, Func<string> getValue)
        {
            this.ContentPatcher.RegisterToken(
                mod: this.Manifest,
                name: name,
                token: new ChildToken(
                    updateContext: this.UpdateContextIfNeeded,
                    isReady: isReady,
                    getValue: getValue
                )
            );

            return this;
        }

        /// <summary>Register a token with Content Patcher.</summary>
        /// <param name="name">The token name.</param>
        /// <param name="childIndex">The index of the child for which to add a token.</param>
        /// <param name="getValue">Get the token value.</param>
        private ContentPatcherIntegration AddToken(string name, int childIndex, Func<ChildData, string> getValue)
        {
            return this.AddToken(
                name: name,
                isReady: () => this.IsReady(childIndex),
                getValue: () =>
                {
                    ChildData child = this.GetChild(childIndex);
                    return child != null
                        ? getValue(child)
                        : null;
                }
            );
        }

        /// <summary>Get the cached data for a child.</summary>
        /// <param name="index">The child index.</param>
        private ChildData GetChild(int index)
        {
            if (this.Cache == null || index >= this.Cache.Length)
                return null;

            return this.Cache[index];
        }

        /// <summary>Get whether tokens for a given child should be marked ready.</summary>
        /// <param name="index">The child index.</param>
        private bool IsReady(int index)
        {
            return this.GetChild(index) != null;
        }

        /// <summary>Update all tokens for the current context.</summary>
        private bool UpdateContextIfNeeded()
        {
            // already updated this tick
            if (Game1.ticks == this.CacheTick)
                return false;
            this.CacheTick = Game1.ticks;

            // update context
            int oldTotal = this.TotalChildren;
            ChildData[] oldData = this.Cache;
            this.FetchNewData(out this.Cache, out this.TotalChildren, out bool updateNPCs);
            return
                oldTotal != this.TotalChildren
                || this.IsChanged(oldData, this.Cache)
                || updateNPCs;
        }

        /// <summary>Fetch the latest child data.</summary>
        /// <param name="data">The children converted to NPCs.</param>
        /// <param name="totalChildren">The total number of children, including those not converted to NPC yet.</param>
        private void FetchNewData(out ChildData[] data, out int totalChildren, out bool updateNPCs)
        {
            Child[] allChildren = ModEntry.GetAllChildrenForTokens().ToArray();
            Child[] convertibleChildren = allChildren.Where(c => ModEntry.IsOldEnough(c)).ToArray();

            totalChildren = allChildren.Length;

            updateNPCs = false;

            // Every time the game is saved, the children are re-added to the FarmHouse
            // So every morning, I check if there are children in the FarmHouse and remove them,
            // and I add their dopplegangers to the FarmHouse.
            FarmHouse farmHouse = ModEntry.GetSaveData(
                loading: save => save.locations.OfType<FarmHouse>().FirstOrDefault(p => p.Name == "FarmHouse"),
                loaded: () => Game1.getLocationFromName("FarmHouse") as FarmHouse,
                out bool loadingFromSave
            );
            if (farmHouse != null)
            {
                // Handle added NPCs.
                var oldAdultNPCsInFarmHouse = AdultNPCsInFarmHouse;
                AdultNPCsInFarmHouse = new HashSet<NPC>(farmHouse.getCharacters().Where(c => !(c is Child)));
                if (AdultNPCsInFarmHouse?.Count > oldAdultNPCsInFarmHouse?.Count)
                {
                    var addedAdultNPCs = AdultNPCsInFarmHouse.Except(oldAdultNPCsInFarmHouse);
                    // Identify the converted children so we can remove their originals.
                    var convertedChildNPCs = (from adult in addedAdultNPCs
                                              from child in convertibleChildren
                                              where ModEntry.IsCorrespondingNPC(child, adult)
                                              select new { adult, child });

                    foreach (var npc in convertedChildNPCs)
                    {
                        ModEntry.monitor.Log($"FetchNewData(): Detected child NPC {npc.adult.Name}");

                        //Add child to list & remove from farmHouse
                        ModEntry.children.Add(npc.child);
                        farmHouse.getCharacters().Remove(npc.child);

                        if (!ModEntry.copies.ContainsKey(npc.child.Name))
                        {
                            ModEntry.copies.Add(npc.child.Name, npc.adult);
                        }
                        else
                        {
                            ModEntry.monitor.Log($"FetchNewData(): ModEntry.copies already contains an entry for {npc.child.Name}. This is probably a bug.", LogLevel.Warn);
                        }

                        updateNPCs = true;

                        ModEntry.monitor.Log($"FetchNewData(): Converted child NPC {npc.child.Name}");

                        //Check if I've made this NPC before & set gift info
                        try
                        {
                            NPCFriendshipData childCopyFriendship = ModEntry.helper.Data.ReadJsonFile<NPCFriendshipData>(ModEntry.helper.Content.GetActualAssetKey("assets/data_" + npc.adult.Name + ".json", ContentSource.ModFolder));
                            if (childCopyFriendship != null)
                            {
                                Game1.player.friendshipData.TryGetValue(npc.child.Name, out Friendship childFriendship);
                                childFriendship.GiftsThisWeek = childCopyFriendship.GiftsThisWeek;
                                childFriendship.LastGiftDate = new WorldDate(childCopyFriendship.GetYear(), childCopyFriendship.GetSeason(), childCopyFriendship.GetDay());
                            }
                        }
                        catch (Exception) { }
                    }
                }
            }

            data = new ChildData[convertibleChildren.Length];
            for (int i = 0; i < data.Length; i++)
            {
                var child = convertibleChildren[i];
                data[i] = new ChildData
                {
                    Name = child.Name,
                    Gender = child.Gender == NPC.male ? "male" : "female",
                    Birthday = ModEntry.GetChildNPCBirthday(child),
                    Bed = ModEntry.GetBedSpot(i, allChildren),
                    Parent = ModEntry.GetChildNPCParent(child)
                };
            }
        }

        /// <summary>Get whether the cached data changed.</summary>
        /// <param name="oldData">The previous child data.</param>
        /// <param name="newData">The current child data.</param>
        private bool IsChanged(ChildData[] oldData, ChildData[] newData)
        {
            if (oldData == null || newData == null)
                return oldData != newData;

            if (oldData.Length != newData.Length)
                return true;

            for (int i = 0; i < oldData.Length; i++)
            {
                if (!oldData[i].IsEquivalentTo(newData[i]))
                    return true;
            }

            return false;
        }

        internal void ClearCache()
        {
            this.Cache = null;
        }
    }
}
