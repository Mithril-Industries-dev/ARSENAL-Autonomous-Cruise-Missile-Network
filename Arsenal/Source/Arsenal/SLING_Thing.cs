using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Custom Thing class for SLING cargo craft.
    /// Uses vanilla CompTransporter for cargo handling, providing familiar UI and hauling.
    /// Adds custom naming and autonomous loading state management.
    /// </summary>
    public class SLING_Thing : Building
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

        // Autonomous loading state (separate from vanilla loading UI)
        private bool isAutonomousLoading;
        private Dictionary<ThingDef, int> targetCargo = new Dictionary<ThingDef, int>();
        private Building_PerchBeacon pendingDestination;

        // Cached transporter component
        private CompTransporter cachedTransporter;

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

        /// <summary>
        /// The CompTransporter that handles cargo.
        /// </summary>
        public CompTransporter Transporter
        {
            get
            {
                if (cachedTransporter == null)
                    cachedTransporter = GetComp<CompTransporter>();
                return cachedTransporter;
            }
        }

        /// <summary>
        /// Direct access to cargo container via transporter.
        /// </summary>
        public ThingOwner CargoContainer => Transporter?.innerContainer;

        /// <summary>
        /// True if autonomous loading is in progress (logistics-driven).
        /// Separate from vanilla's loading UI.
        /// </summary>
        public bool IsLoading => isAutonomousLoading;

        /// <summary>
        /// Total items in cargo.
        /// </summary>
        public int CurrentCargoCount => CargoContainer?.TotalStackCount ?? 0;

        /// <summary>
        /// Remaining cargo capacity.
        /// </summary>
        public int RemainingCapacity => MAX_CARGO_CAPACITY - CurrentCargoCount;

        /// <summary>
        /// The destination for the current autonomous loading operation.
        /// </summary>
        public Building_PerchBeacon PendingDestination => pendingDestination;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // Cache transporter
            cachedTransporter = GetComp<CompTransporter>();

            // Assign name only if not already named (preserves name through transit)
            if (string.IsNullOrEmpty(customName))
            {
                customName = "SLING-" + slingCounter.ToString("D2");
                slingCounter++;
            }
        }

        public override void Tick()
        {
            base.Tick();

            // Check if autonomous loading is complete
            if (isAutonomousLoading && this.IsHashIntervalTick(60))
            {
                CheckAutonomousLoadingProgress();
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            base.DeSpawn(mode);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // CompTransporter handles dropping cargo on destruction
            base.Destroy(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref isAutonomousLoading, "isAutonomousLoading", false);
            Scribe_Collections.Look(ref targetCargo, "targetCargo", LookMode.Def, LookMode.Value);
            Scribe_References.Look(ref pendingDestination, "pendingDestination");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (targetCargo == null)
                    targetCargo = new Dictionary<ThingDef, int>();
            }
        }

        #endregion

        #region Autonomous Loading System

        /// <summary>
        /// Starts autonomous loading mode for logistics-driven cargo transfer.
        /// This uses vanilla's CompTransporter loading but tracks our own target manifest.
        /// </summary>
        public void StartLoading(Dictionary<ThingDef, int> cargo, Building_PerchBeacon destination = null)
        {
            isAutonomousLoading = true;
            targetCargo = new Dictionary<ThingDef, int>(cargo);
            pendingDestination = destination;

            // Set up the transporter for loading
            var transporter = Transporter;
            if (transporter != null)
            {
                // Add items to the transporter's transfer list
                foreach (var kvp in cargo)
                {
                    // Find items on map to add to loading list
                    if (Map != null)
                    {
                        var items = Map.listerThings.ThingsOfDef(kvp.Key)
                            .Where(t => !t.IsForbidden(Faction.OfPlayer) && t.Spawned)
                            .ToList();

                        int remaining = kvp.Value;
                        foreach (var item in items)
                        {
                            if (remaining <= 0) break;
                            int toAdd = Mathf.Min(remaining, item.stackCount);
                            TransferableOneWay transferable = new TransferableOneWay();
                            transferable.things.Add(item);
                            transporter.AddToTheToLoadList(transferable, toAdd);
                            remaining -= toAdd;
                        }
                    }
                }
            }

            Log.Message($"[SLING] {Label}: Started autonomous loading. Target: {string.Join(", ", cargo.Select(c => $"{c.Key.label}x{c.Value}"))}");
        }

        /// <summary>
        /// Simplified StartLoading that takes only cargo manifest.
        /// </summary>
        public void StartLoading(Dictionary<ThingDef, int> cargo)
        {
            StartLoading(cargo, null);
        }

        /// <summary>
        /// Checks loading progress and triggers dispatch when complete.
        /// </summary>
        private void CheckAutonomousLoadingProgress()
        {
            if (!isAutonomousLoading) return;

            // Check if we have enough cargo loaded
            if (IsLoadingComplete() || CurrentCargoCount > 0)
            {
                // Dispatch will be triggered by SlingLogisticsManager
                // Just log progress
                if (IsLoadingComplete())
                {
                    Log.Message($"[SLING] {Label}: Autonomous loading complete. Loaded: {CurrentCargoCount}");
                }
            }
        }

        /// <summary>
        /// Stops autonomous loading mode.
        /// </summary>
        public bool CompleteLoading()
        {
            isAutonomousLoading = false;
            bool success = IsLoadingComplete() || CurrentCargoCount > 0;
            Log.Message($"[SLING] {Label}: Loading finalized. Success: {success}, Loaded: {CurrentCargoCount}");
            return success;
        }

        /// <summary>
        /// Cancels loading and drops any loaded cargo.
        /// </summary>
        public void CancelLoading()
        {
            isAutonomousLoading = false;
            targetCargo.Clear();
            pendingDestination = null;

            var transporter = Transporter;
            if (transporter != null)
            {
                transporter.CancelLoad();
            }

            if (Map != null && CargoContainer != null && CargoContainer.Count > 0)
            {
                CargoContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            }
        }

        /// <summary>
        /// Checks if target cargo has been fully loaded.
        /// </summary>
        public bool IsLoadingComplete()
        {
            if (targetCargo == null || targetCargo.Count == 0)
                return CurrentCargoCount > 0;

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
            if (CargoContainer == null) return 0;

            int total = 0;
            foreach (Thing t in CargoContainer)
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
            if (!isAutonomousLoading) return false;
            return GetRemainingNeeded(def) > 0;
        }

        /// <summary>
        /// Gets all cargo as a dictionary for transit.
        /// </summary>
        public Dictionary<ThingDef, int> GetCargoManifest()
        {
            var manifest = new Dictionary<ThingDef, int>();
            if (CargoContainer == null) return manifest;

            foreach (Thing t in CargoContainer)
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
            CargoContainer?.TryDropAll(dropCell, map, ThingPlaceMode.Near);
        }

        /// <summary>
        /// Loads cargo from a manifest (used when arriving from transit).
        /// </summary>
        public void LoadCargoFromManifest(Dictionary<ThingDef, int> manifest)
        {
            if (CargoContainer == null) return;

            foreach (var kvp in manifest)
            {
                int remaining = kvp.Value;
                while (remaining > 0)
                {
                    int toCreate = Mathf.Min(remaining, kvp.Key.stackLimit);
                    Thing t = ThingMaker.MakeThing(kvp.Key);
                    t.stackCount = toCreate;
                    CargoContainer.TryAdd(t, false);
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

            if (isAutonomousLoading)
            {
                str += "Status: Loading cargo (autonomous)";
                str += $"\nLoaded: {CurrentCargoCount}/{MAX_CARGO_CAPACITY}";

                // Show loading progress
                foreach (var target in targetCargo)
                {
                    int loaded = GetLoadedAmount(target.Key);
                    str += $"\n  {target.Key.label}: {loaded}/{target.Value}";
                }

                if (pendingDestination != null)
                {
                    str += $"\nDestination: {pendingDestination.ZoneName ?? "Beacon Zone"}";
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

            // Load cargo button (like vanilla shuttles)
            if (!isAutonomousLoading && Transporter != null)
            {
                var loadCommand = new Command_LoadToTransporter
                {
                    transComp = Transporter,
                    defaultLabel = "Load Cargo",
                    defaultDesc = "Select items to load onto this SLING for transport.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", true)
                };
                yield return loadCommand;
            }

            // Launch button (when loaded and not in autonomous mode)
            if (!isAutonomousLoading && CurrentCargoCount > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Launch",
                    defaultDesc = "Launch this SLING to a destination.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchShip", true),
                    action = delegate
                    {
                        // Open destination picker
                        Find.WindowStack.Add(new Dialog_SelectSlingDestination(this));
                    }
                };
            }

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

                if (isAutonomousLoading)
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

    /// <summary>
    /// Dialog for selecting a destination for manual SLING launch.
    /// </summary>
    public class Dialog_SelectSlingDestination : Window
    {
        private SLING_Thing sling;
        private Vector2 scrollPosition;

        public Dialog_SelectSlingDestination(SLING_Thing sling)
        {
            this.sling = sling;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 500f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label($"Select destination for {sling.Label}");
            listing.GapLine();

            // Get all valid landing zones
            var beaconZones = ArsenalNetworkManager.GetConnectedBeaconZones()
                .Where(b => b.Map != sling.Map && b.HasSpaceForSling)
                .ToList();
            var perches = ArsenalNetworkManager.GetAllPerches()
                .Where(p => p.Map != null && p.Map != sling.Map && p.HasAvailableSlot)
                .ToList();

            Rect scrollRect = listing.GetRect(inRect.height - 80f);
            int totalCount = beaconZones.Count + perches.Count;
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, totalCount * 35f);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
            float y = 0;

            // List beacon zones
            foreach (var zone in beaconZones)
            {
                Rect rowRect = new Rect(0, y, viewRect.width, 30f);
                string label = $"{zone.ZoneName ?? "Beacon Zone"} (Tile {zone.Map.Tile})";
                if (Widgets.ButtonText(rowRect, label))
                {
                    LaunchToBeaconZone(zone);
                    Close();
                }
                y += 35f;
            }

            // List legacy perches
            foreach (var perch in perches)
            {
                Rect rowRect = new Rect(0, y, viewRect.width, 30f);
                string label = $"{perch.Label} (Tile {perch.Map.Tile})";
                if (Widgets.ButtonText(rowRect, label))
                {
                    LaunchToPerch(perch);
                    Close();
                }
                y += 35f;
            }

            if (totalCount == 0)
            {
                Widgets.Label(new Rect(0, 0, viewRect.width, 30f), "No available destinations");
            }

            Widgets.EndScrollView();
            listing.End();
        }

        private void LaunchToBeaconZone(Building_PerchBeacon destination)
        {
            if (sling == null || !sling.Spawned) return;

            var cargo = sling.GetCargoManifest();
            string slingName = sling.Label;
            Map sourceMap = sling.Map;
            IntVec3 launchPos = sling.Position;

            sling.DeSpawn(DestroyMode.Vanish);

            var launchingSkyfaller = (SlingLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLaunching);
            launchingSkyfaller.sling = sling;
            launchingSkyfaller.slingName = slingName;
            launchingSkyfaller.cargo = cargo;
            launchingSkyfaller.destinationBeaconZone = destination;
            launchingSkyfaller.destinationTile = destination.Map.Tile;
            launchingSkyfaller.isReturnFlight = false;

            GenSpawn.Spawn(launchingSkyfaller, launchPos, sourceMap);

            Messages.Message($"{slingName} launching to {destination.ZoneName ?? "destination"}",
                MessageTypeDefOf.PositiveEvent);
        }

        private void LaunchToPerch(Building_PERCH destination)
        {
            if (sling == null || !sling.Spawned) return;

            var cargo = sling.GetCargoManifest();
            string slingName = sling.Label;
            Map sourceMap = sling.Map;
            IntVec3 launchPos = sling.Position;

            sling.DeSpawn(DestroyMode.Vanish);

            var launchingSkyfaller = (SlingLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLaunching);
            launchingSkyfaller.sling = sling;
            launchingSkyfaller.slingName = slingName;
            launchingSkyfaller.cargo = cargo;
            launchingSkyfaller.destinationPerch = destination;
            launchingSkyfaller.destinationTile = destination.Map.Tile;
            launchingSkyfaller.isReturnFlight = false;

            GenSpawn.Spawn(launchingSkyfaller, launchPos, sourceMap);

            Messages.Message($"{slingName} launching to {destination.Label}",
                MessageTypeDefOf.PositiveEvent);
        }
    }
}
