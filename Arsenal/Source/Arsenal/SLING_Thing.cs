using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Custom Thing class for SLING cargo craft.
    /// Supports custom naming, cargo container for physical hauling,
    /// and loading state management.
    /// </summary>
    public class SLING_Thing : Building, IThingHolder
    {
        private string customName;
        private static int slingCounter = 1;

        /// <summary>
        /// Sets the sling counter to a specific value.
        /// Called after game load to prevent duplicate names.
        /// </summary>
        public static void SetCounter(int value)
        {
            slingCounter = System.Math.Max(1, value);
        }

        // Cargo container - holds items being transported
        private ThingOwner<Thing> cargoContainer;

        // Loading state
        private bool isLoading;
        private Dictionary<ThingDef, int> targetCargo = new Dictionary<ThingDef, int>();

        public const int MAX_CARGO_CAPACITY = 750;

        #region Properties

        public string CustomName
        {
            get => customName;
            set => customName = value;
        }

        public override string Label
        {
            get
            {
                if (!string.IsNullOrEmpty(customName))
                    return customName;
                return base.Label;
            }
        }

        public bool IsLoading => isLoading;
        public ThingOwner CargoContainer => cargoContainer;
        public int CurrentCargoCount => cargoContainer?.TotalStackCount ?? 0;
        public int RemainingCapacity => MAX_CARGO_CAPACITY - CurrentCargoCount;

        #endregion

        #region IThingHolder

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public ThingOwner GetDirectlyHeldThings()
        {
            return cargoContainer;
        }

        #endregion

        #region Lifecycle

        public SLING_Thing()
        {
            cargoContainer = new ThingOwner<Thing>(this, false);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            if (cargoContainer == null)
                cargoContainer = new ThingOwner<Thing>(this, false);

            // Assign name only if not already named (preserves name through transit)
            if (string.IsNullOrEmpty(customName))
            {
                customName = "SLING-" + slingCounter.ToString("D2");
                slingCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Don't drop cargo when despawning for transit
            base.DeSpawn(mode);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // Drop all cargo if destroyed
            if (mode != DestroyMode.Vanish && cargoContainer != null)
            {
                cargoContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            }
            base.Destroy(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref isLoading, "isLoading", false);
            Scribe_Deep.Look(ref cargoContainer, "cargoContainer", this);
            Scribe_Collections.Look(ref targetCargo, "targetCargo", LookMode.Def, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (cargoContainer == null)
                    cargoContainer = new ThingOwner<Thing>(this, false);
                if (targetCargo == null)
                    targetCargo = new Dictionary<ThingDef, int>();
            }
        }

        #endregion

        #region Loading System

        /// <summary>
        /// Starts loading mode - SLING will accept hauled items matching target cargo.
        /// </summary>
        public void StartLoading(Dictionary<ThingDef, int> cargo)
        {
            isLoading = true;
            targetCargo = new Dictionary<ThingDef, int>(cargo);
            Log.Message($"[SLING] {Label}: Started loading. Target: {string.Join(", ", cargo.Select(c => $"{c.Key.label}x{c.Value}"))}");
        }

        /// <summary>
        /// Stops loading mode and returns whether loading was successful.
        /// </summary>
        public bool CompleteLoading()
        {
            isLoading = false;
            bool success = IsLoadingComplete();
            Log.Message($"[SLING] {Label}: Loading complete. Success: {success}, Loaded: {CurrentCargoCount}");
            return success;
        }

        /// <summary>
        /// Cancels loading and drops any loaded cargo.
        /// </summary>
        public void CancelLoading()
        {
            isLoading = false;
            targetCargo.Clear();
            if (Map != null && cargoContainer.Count > 0)
            {
                cargoContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            }
        }

        /// <summary>
        /// Checks if target cargo has been fully loaded.
        /// </summary>
        public bool IsLoadingComplete()
        {
            foreach (var target in targetCargo)
            {
                int loaded = GetLoadedAmount(target.Key);
                if (loaded < target.Value)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the amount of a specific resource currently loaded.
        /// </summary>
        public int GetLoadedAmount(ThingDef def)
        {
            int total = 0;
            foreach (Thing t in cargoContainer)
            {
                if (t.def == def)
                    total += t.stackCount;
            }
            return total;
        }

        /// <summary>
        /// Gets remaining amount needed for a specific resource.
        /// </summary>
        public int GetRemainingNeeded(ThingDef def)
        {
            if (!targetCargo.ContainsKey(def))
                return 0;
            return Mathf.Max(0, targetCargo[def] - GetLoadedAmount(def));
        }

        /// <summary>
        /// Checks if SLING wants more of this item type.
        /// </summary>
        public bool WantsItem(ThingDef def)
        {
            if (!isLoading) return false;
            return GetRemainingNeeded(def) > 0;
        }

        /// <summary>
        /// Tries to add an item to the cargo. Returns the amount actually added.
        /// Handles items that are in pawn carry trackers.
        /// </summary>
        public int TryAddCargo(Thing item)
        {
            if (item == null || item.Destroyed) return 0;
            if (!isLoading) return 0;
            if (!WantsItem(item.def)) return 0;

            int remaining = GetRemainingNeeded(item.def);
            int toAdd = Mathf.Min(remaining, item.stackCount, RemainingCapacity);

            if (toAdd <= 0) return 0;

            if (toAdd >= item.stackCount)
            {
                // Take the whole stack - use TryAddOrTransfer to handle items in other containers
                if (cargoContainer.TryAddOrTransfer(item, true))
                {
                    return toAdd;
                }
            }
            else
            {
                // Split the stack first
                Thing split = item.SplitOff(toAdd);
                if (cargoContainer.TryAddOrTransfer(split, true))
                {
                    return toAdd;
                }
                else
                {
                    // Failed to add, merge back
                    item.TryAbsorbStack(split, true);
                }
            }

            return 0;
        }

        /// <summary>
        /// Gets all cargo as a dictionary for transit.
        /// </summary>
        public Dictionary<ThingDef, int> GetCargoManifest()
        {
            var manifest = new Dictionary<ThingDef, int>();
            foreach (Thing t in cargoContainer)
            {
                if (manifest.ContainsKey(t.def))
                    manifest[t.def] += t.stackCount;
                else
                    manifest[t.def] = t.stackCount;
            }
            return manifest;
        }

        /// <summary>
        /// Transfers all cargo out of the container (for unloading at destination).
        /// </summary>
        public void UnloadAllCargo(IntVec3 dropCell, Map map)
        {
            cargoContainer.TryDropAll(dropCell, map, ThingPlaceMode.Near);
        }

        /// <summary>
        /// Loads cargo from a manifest (used when arriving from transit).
        /// </summary>
        public void LoadCargoFromManifest(Dictionary<ThingDef, int> manifest)
        {
            foreach (var kvp in manifest)
            {
                int remaining = kvp.Value;
                while (remaining > 0)
                {
                    int toCreate = Mathf.Min(remaining, kvp.Key.stackLimit);
                    Thing t = ThingMaker.MakeThing(kvp.Key);
                    t.stackCount = toCreate;
                    cargoContainer.TryAdd(t, false);
                    remaining -= toCreate;
                }
            }
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!str.NullOrEmpty())
                str += "\n";

            if (isLoading)
            {
                str += "Status: Loading cargo";
                str += $"\nLoaded: {CurrentCargoCount}/{MAX_CARGO_CAPACITY}";

                // Show loading progress
                foreach (var target in targetCargo)
                {
                    int loaded = GetLoadedAmount(target.Key);
                    str += $"\n  {target.Key.label}: {loaded}/{target.Value}";
                }
            }
            else if (CurrentCargoCount > 0)
            {
                str += $"Cargo: {CurrentCargoCount} items";
            }
            else
            {
                str += "Status: Ready for dispatch";
            }

            return str;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
                yield return g;

            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = $"DEV: Cargo={CurrentCargoCount}",
                    defaultDesc = "Shows current cargo count",
                    action = delegate
                    {
                        var manifest = GetCargoManifest();
                        string msg = $"{Label} cargo:\n";
                        foreach (var kvp in manifest)
                        {
                            msg += $"  {kvp.Key.label}: {kvp.Value}\n";
                        }
                        Log.Message(msg);
                    }
                };

                if (isLoading)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Complete Loading",
                        action = delegate { CompleteLoading(); }
                    };

                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Cancel Loading",
                        action = delegate { CancelLoading(); }
                    };
                }
            }
        }

        #endregion

        #region Static Helpers

        /// <summary>
        /// Assigns a name to this SLING. Call before spawning to set a specific name.
        /// </summary>
        public void AssignName(string name)
        {
            if (!string.IsNullOrEmpty(name))
                customName = name;
        }

        /// <summary>
        /// Assigns a new unique name to this SLING.
        /// </summary>
        public void AssignNewName()
        {
            customName = "SLING-" + slingCounter.ToString("D2");
            slingCounter++;
        }

        /// <summary>
        /// Resets the naming counter (called on game load).
        /// </summary>
        public static void ResetCounter()
        {
            slingCounter = 1;
        }

        /// <summary>
        /// Sets the counter to continue from existing SLINGs.
        /// </summary>
        public static void SetCounterFromExisting(int maxExisting)
        {
            if (maxExisting >= slingCounter)
                slingCounter = maxExisting + 1;
        }

        /// <summary>
        /// Gets the name from a SLING Thing (works with both SLING_Thing and generic Things).
        /// </summary>
        public static string GetSlingName(Thing sling)
        {
            if (sling is SLING_Thing slingThing)
                return slingThing.CustomName ?? sling.Label;
            return sling?.Label ?? "SLING";
        }

        #endregion
    }
}
