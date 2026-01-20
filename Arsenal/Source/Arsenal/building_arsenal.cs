using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// ARSENAL Manufacturing Facility - Multi-line production system.
    /// Pulls resources from adjacent storage (no internal storage).
    /// Has 3 configurable manufacturing lines for DAGGER/DART production.
    /// </summary>
    public class Building_Arsenal : Building
    {
        // === MANUFACTURING LINES ===
        public List<ManufacturingLine> lines = new List<ManufacturingLine>();
        private const int NUM_LINES = 3;

        // === NETWORK CACHE ===
        private List<Building_Hub> cachedHubs = new List<Building_Hub>();
        private List<Building_Quiver> cachedQuivers = new List<Building_Quiver>();
        private List<Building_Hop> cachedHops = new List<Building_Hop>();
        private Building_Lattice cachedLattice;
        private int lastCacheRefresh = -999;
        private const int CACHE_REFRESH_INTERVAL = 120; // 2 seconds

        // === QUEUE SYSTEM ===
        private List<QueuedMissile> missileQueue = new List<QueuedMissile>();

        // === IDENTITY ===
        private string customName;
        private static int factoryCounter = 1;

        // === COMPONENTS ===
        private CompRefuelable refuelableComp;
        private CompPowerTrader powerComp;
        private Sustainer manufacturingSustainer;

        // === LEGACY COMPATIBILITY ===
        // These are kept for save migration only
        [Unsaved] private bool legacyMigrationDone = false;

        public class QueuedMissile : IExposable
        {
            public Thing missile;
            public Building_Hub targetHub;

            public void ExposeData()
            {
                Scribe_Deep.Look(ref missile, "missile");
                Scribe_References.Look(ref targetHub, "targetHub");
            }
        }

        #region Adjacent Storage System

        /// <summary>
        /// Gets all cells adjacent to ARSENAL that contain valid storage.
        /// </summary>
        public IEnumerable<IntVec3> GetAdjacentStorageCells()
        {
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;

                // Check if cell is part of a stockpile zone
                Zone zone = cell.GetZone(Map);
                if (zone is Zone_Stockpile)
                {
                    yield return cell;
                    continue;
                }

                // Check if cell has a storage building (shelf, etc.)
                foreach (Thing thing in cell.GetThingList(Map))
                {
                    if (thing is Building_Storage)
                    {
                        yield return cell;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Counts how much of a resource is available in adjacent storage.
        /// </summary>
        public int CountResourceAvailable(ThingDef def)
        {
            int count = 0;
            HashSet<IntVec3> checkedCells = new HashSet<IntVec3>();

            foreach (IntVec3 cell in GetAdjacentStorageCells())
            {
                if (checkedCells.Contains(cell)) continue;
                checkedCells.Add(cell);

                foreach (Thing thing in cell.GetThingList(Map))
                {
                    if (thing.def == def)
                        count += thing.stackCount;
                }
            }
            return count;
        }

        /// <summary>
        /// Checks if all resources for a cost list are available.
        /// </summary>
        public bool HasResourcesFor(List<ThingDefCountClass> costs)
        {
            if (costs == null) return false;

            foreach (var cost in costs)
            {
                if (CountResourceAvailable(cost.thingDef) < cost.count)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Consumes resources from adjacent storage. Returns false if insufficient.
        /// </summary>
        public bool ConsumeResources(List<ThingDefCountClass> costs)
        {
            // First verify we have everything
            if (!HasResourcesFor(costs))
                return false;

            // Then consume
            foreach (var cost in costs)
            {
                int remaining = cost.count;

                foreach (IntVec3 cell in GetAdjacentStorageCells())
                {
                    if (remaining <= 0) break;

                    List<Thing> things = cell.GetThingList(Map).ToList();
                    foreach (Thing thing in things)
                    {
                        if (remaining <= 0) break;

                        if (thing.def == cost.thingDef)
                        {
                            int take = Mathf.Min(remaining, thing.stackCount);
                            if (take >= thing.stackCount)
                            {
                                thing.Destroy();
                            }
                            else
                            {
                                thing.SplitOff(take).Destroy();
                            }
                            remaining -= take;
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Gets a dictionary of all available resources for UI display.
        /// </summary>
        public Dictionary<ThingDef, int> GetAllAvailableResources()
        {
            Dictionary<ThingDef, int> resources = new Dictionary<ThingDef, int>();
            HashSet<IntVec3> checkedCells = new HashSet<IntVec3>();

            foreach (IntVec3 cell in GetAdjacentStorageCells())
            {
                if (checkedCells.Contains(cell)) continue;
                checkedCells.Add(cell);

                foreach (Thing thing in cell.GetThingList(Map))
                {
                    if (thing.def.category == ThingCategory.Item)
                    {
                        if (resources.ContainsKey(thing.def))
                            resources[thing.def] += thing.stackCount;
                        else
                            resources[thing.def] = thing.stackCount;
                    }
                }
            }
            return resources;
        }

        /// <summary>
        /// Checks if ARSENAL has any adjacent storage.
        /// </summary>
        public bool HasAdjacentStorage()
        {
            return GetAdjacentStorageCells().Any();
        }

        #endregion

        #region Network Cache

        public void RefreshNetworkCache()
        {
            if (Find.TickManager.TicksGame - lastCacheRefresh < CACHE_REFRESH_INTERVAL)
                return;

            // HUBs and HOPs are searched GLOBALLY (across all maps) for DAGGER network
            cachedHubs = ArsenalNetworkManager.GetAllHubs().ToList();
            cachedHops = ArsenalNetworkManager.GetAllHops().ToList();

            // QUIVERs and LATTICE are LOCAL only (DART system is map-local)
            cachedQuivers = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Quiver>().ToList();
            cachedLattice = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Lattice>().FirstOrDefault();

            lastCacheRefresh = Find.TickManager.TicksGame;
        }

        public List<Building_Hub> CachedHubs
        {
            get
            {
                RefreshNetworkCache();
                return cachedHubs;
            }
        }

        public List<Building_Quiver> CachedQuivers
        {
            get
            {
                RefreshNetworkCache();
                return cachedQuivers;
            }
        }

        public List<Building_Hop> CachedHops
        {
            get
            {
                RefreshNetworkCache();
                return cachedHops;
            }
        }

        public Building_Lattice CachedLattice
        {
            get
            {
                RefreshNetworkCache();
                return cachedLattice;
            }
        }

        /// <summary>
        /// Gets the best destination for a product (least-full, respecting priority).
        /// </summary>
        public Building GetBestDestinationFor(MithrilProductDef product)
        {
            if (product == null) return null;

            RefreshNetworkCache();

            if (product.destinationType == typeof(Building_Hub))
            {
                return cachedHubs
                    .Where(h => !h.IsFull && h.IsPoweredOn())
                    .OrderBy(h => h.priority)
                    .ThenByDescending(h => h.EmptySlots)
                    .FirstOrDefault();
            }
            else if (product.destinationType == typeof(Building_Quiver))
            {
                // QUIVERs also require LATTICE to be online
                if (cachedLattice == null || !cachedLattice.IsPoweredOn())
                    return null;

                return cachedQuivers
                    .Where(q => !q.IsFull && !q.IsInert)
                    .OrderBy(q => q.Priority)
                    .ThenByDescending(q => q.EmptySlots)
                    .FirstOrDefault();
            }

            return null;
        }

        /// <summary>
        /// Gets all valid destinations for a product (for UI dropdown).
        /// </summary>
        public List<Building> GetAllDestinationsFor(MithrilProductDef product)
        {
            if (product == null) return new List<Building>();

            RefreshNetworkCache();

            if (product.destinationType == typeof(Building_Hub))
                return cachedHubs.Cast<Building>().ToList();
            else if (product.destinationType == typeof(Building_Quiver))
                return cachedQuivers.Cast<Building>().ToList();

            return new List<Building>();
        }

        /// <summary>
        /// Gets the chain of HOPs that extend a HUB's range.
        /// </summary>
        public List<Building_Hop> GetHopChainForHub(Building_Hub hub)
        {
            List<Building_Hop> chain = new List<Building_Hop>();
            HashSet<Building_Hop> visited = new HashSet<Building_Hop>();

            RefreshNetworkCache();

            // Start from HUB position, find reachable HOPs
            Vector3 currentPos = hub.Position.ToVector3();
            float currentRange = hub.BaseRange;

            while (true)
            {
                Building_Hop nextHop = cachedHops
                    .Where(h => !visited.Contains(h))
                    .Where(h => h.IsPoweredOn() && h.HasFuel)
                    .Where(h => Vector3.Distance(currentPos, h.Position.ToVector3()) <= currentRange)
                    .OrderBy(h => Vector3.Distance(currentPos, h.Position.ToVector3()))
                    .FirstOrDefault();

                if (nextHop == null) break;

                chain.Add(nextHop);
                visited.Add(nextHop);
                currentPos = nextHop.Position.ToVector3();
                currentRange = nextHop.RangeExtension;
            }

            return chain;
        }

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            refuelableComp = GetComp<CompRefuelable>();
            powerComp = GetComp<CompPowerTrader>();

            // Initialize lines if new
            if (lines == null || lines.Count == 0)
            {
                lines = new List<ManufacturingLine>();
                for (int i = 0; i < NUM_LINES; i++)
                {
                    lines.Add(new ManufacturingLine
                    {
                        index = i,
                        arsenal = this
                    });
                }
            }
            else
            {
                // Restore parent reference after load
                foreach (var line in lines)
                    line.arsenal = this;
            }

            if (!respawningAfterLoad)
            {
                ArsenalNetworkManager.RegisterArsenal(this);
                customName = "ARSENAL-" + factoryCounter.ToString("D2");
                factoryCounter++;
            }

            if (missileQueue == null)
                missileQueue = new List<QueuedMissile>();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            StopManufacturingSound();

            // Drop queued missiles
            foreach (var qm in missileQueue)
            {
                if (qm.missile != null)
                    GenPlace.TryPlaceThing(qm.missile, Position, Map, ThingPlaceMode.Near);
            }
            missileQueue.Clear();

            ArsenalNetworkManager.DeregisterArsenal(this);
            base.DeSpawn(mode);
        }

        public override string Label => customName ?? base.Label;

        public void SetCustomName(string name)
        {
            customName = name;
        }

        #endregion

        #region Manufacturing Tick

        public override void TickRare()
        {
            base.TickRare();

            if (!CanOperate())
            {
                StopManufacturingSound();
                return;
            }

            // Update line statuses
            foreach (var line in lines)
                line.UpdateStatus();

            // Try to launch queued missiles first
            TryLaunchQueuedMissiles();

            // Process manufacturing
            TickManufacturing();
        }

        private void TickManufacturing()
        {
            // Get active lines sorted by priority (for resource consumption order)
            var activeLines = lines
                .Where(l => l.status == LineStatus.Manufacturing)
                .OrderBy(l => l.priority)
                .ToList();

            if (activeLines.Count == 0)
            {
                StopManufacturingSound();
                return;
            }

            StartManufacturingSound();

            // Process ALL active lines - priority only affects resource consumption order
            foreach (var line in activeLines)
            {
                // Check if we need to consume resources (at start of production)
                if (!line.resourcesConsumed)
                {
                    if (!ConsumeResources(line.product.costList))
                    {
                        line.status = LineStatus.WaitingResources;
                        continue;
                    }

                    // Consume fuel
                    refuelableComp?.ConsumeFuel(line.product.fuelCost / line.product.workAmount);
                    line.resourcesConsumed = true;
                }

                // Progress production (TickRare is called every 250 ticks)
                line.progress += 250f;

                // Check completion
                if (line.progress >= line.product.workAmount)
                {
                    CompleteProduction(line);
                }
            }
        }

        private void CompleteProduction(ManufacturingLine line)
        {
            SpawnProductFlyer(line.product, line.currentDestination, line);

            // Play completion sound
            SoundDefOf.Building_Complete.PlayOneShot(this);

            // Reset line
            line.Reset();
        }

        private void SpawnProductFlyer(MithrilProductDef product, Building destination, ManufacturingLine line)
        {
            if (product.destinationType == typeof(Building_Quiver))
            {
                // DART - spawn delivery flyer
                Building_Quiver targetQuiver = destination as Building_Quiver;
                if (targetQuiver == null || CachedLattice == null)
                {
                    // No valid target - drop DART item
                    Thing dartItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_DART_Item);
                    GenPlace.TryPlaceThing(dartItem, Position, Map, ThingPlaceMode.Near);
                    Messages.Message(Label + ": DART completed but no valid destination.", this, MessageTypeDefOf.NeutralEvent);
                    return;
                }

                DART_Flyer dart = (DART_Flyer)ThingMaker.MakeThing(ArsenalDefOf.Arsenal_DART_Flyer);
                dart.InitializeForDelivery(targetQuiver, CachedLattice);
                GenSpawn.Spawn(dart, Position, Map);

                Messages.Message(Label + " Line " + (line.index + 1) + ": DART delivered to " + targetQuiver.Label,
                    this, MessageTypeDefOf.PositiveEvent);
            }
            else if (product.destinationType == typeof(Building_Hub))
            {
                // DAGGER - spawn cruise missile
                Building_Hub targetHub = destination as Building_Hub;
                if (targetHub == null)
                {
                    // No valid target - drop missile
                    Thing missile = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_CruiseMissile);
                    GenPlace.TryPlaceThing(missile, Position, Map, ThingPlaceMode.Near);
                    Messages.Message(Label + ": DAGGER completed but no valid destination.", this, MessageTypeDefOf.NeutralEvent);
                    return;
                }

                Thing missileItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_CruiseMissile);

                if (CanReachHub(targetHub))
                {
                    LaunchMissileToHub(missileItem, targetHub);
                    Messages.Message(Label + " Line " + (line.index + 1) + ": DAGGER launched to " + targetHub.Label,
                        this, MessageTypeDefOf.PositiveEvent);
                }
                else
                {
                    missileQueue.Add(new QueuedMissile { missile = missileItem, targetHub = targetHub });
                    Messages.Message(Label + " Line " + (line.index + 1) + ": DAGGER queued - waiting for HOP.",
                        this, MessageTypeDefOf.NeutralEvent);
                }
            }
        }

        #endregion

        #region Missile Queue & Launching

        private void TryLaunchQueuedMissiles()
        {
            if (missileQueue.Count == 0)
                return;

            for (int i = missileQueue.Count - 1; i >= 0; i--)
            {
                var qm = missileQueue[i];
                if (qm.missile == null || qm.targetHub == null)
                {
                    missileQueue.RemoveAt(i);
                    continue;
                }

                if (CanReachHub(qm.targetHub))
                {
                    LaunchMissileToHub(qm.missile, qm.targetHub);
                    missileQueue.RemoveAt(i);
                }
            }
        }

        private bool CanReachHub(Building_Hub hub)
        {
            if (hub?.Map == null) return false;

            int destTile = hub.Map.Tile;
            int dist = Find.WorldGrid.TraversalDistanceBetween(Map.Tile, destTile);

            if (dist <= 100f)
                return true;

            foreach (var hop in ArsenalNetworkManager.GetAllHops())
            {
                if (hop.Map == null) continue;
                if (!hop.CanAcceptMissile()) continue;
                if (hop.GetAvailableFuel() < 50f) continue;

                int distToHop = Find.WorldGrid.TraversalDistanceBetween(Map.Tile, hop.Map.Tile);
                if (distToHop <= 100f)
                    return true;
            }

            return false;
        }

        private void LaunchMissileToHub(Thing missile, Building_Hub targetHub)
        {
            WorldObject_TravelingMissile travelingMissile =
                (WorldObject_TravelingMissile)WorldObjectMaker.MakeWorldObject(ArsenalDefOf.Arsenal_TravelingMissile);

            travelingMissile.Tile = Map.Tile;
            travelingMissile.destinationTile = targetHub.Map.Tile;
            travelingMissile.missile = missile;
            travelingMissile.destinationHub = targetHub;
            travelingMissile.CalculateRoute();

            MissileLaunchingSkyfaller skyfaller = (MissileLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_MissileLaunching);
            skyfaller.travelingMissile = travelingMissile;

            GenSpawn.Spawn(skyfaller, Position, Map);
        }

        public List<int> GetRouteToHub(int destinationTile)
        {
            List<int> route = new List<int>();
            float fuel = 100f;
            int current = Map.Tile;

            while (current != destinationTile)
            {
                int directDist = Find.WorldGrid.TraversalDistanceBetween(current, destinationTile);
                if (directDist <= fuel)
                {
                    route.Add(destinationTile);
                    break;
                }

                Building_Hop bestHop = FindBestAvailableHop(current, destinationTile, fuel);

                if (bestHop == null)
                {
                    route.Add(destinationTile);
                    break;
                }

                route.Add(bestHop.Map.Tile);
                current = bestHop.Map.Tile;
                fuel = 100f;
            }

            return route;
        }

        private Building_Hop FindBestAvailableHop(int fromTile, int towardTile, float maxRange)
        {
            Building_Hop bestHop = null;
            int bestScore = int.MaxValue;

            foreach (var hop in ArsenalNetworkManager.GetAllHops())
            {
                if (hop.Map == null) continue;
                if (!hop.CanAcceptMissile()) continue;
                if (hop.GetAvailableFuel() < 50f) continue;

                int hopTile = hop.Map.Tile;
                int distToHop = Find.WorldGrid.TraversalDistanceBetween(fromTile, hopTile);

                if (distToHop > maxRange)
                    continue;

                int distFromHop = Find.WorldGrid.TraversalDistanceBetween(hopTile, towardTile);

                if (distFromHop < bestScore)
                {
                    bestScore = distFromHop;
                    bestHop = hop;
                }
            }

            return bestHop;
        }

        #endregion

        #region Sound

        private void StartManufacturingSound()
        {
            if (manufacturingSustainer == null || manufacturingSustainer.Ended)
            {
                SoundInfo info = SoundInfo.InMap(this, MaintenanceType.None);
                manufacturingSustainer = SoundDefOf.GeyserSpray.TrySpawnSustainer(info);
            }
        }

        private void StopManufacturingSound()
        {
            if (manufacturingSustainer != null && !manufacturingSustainer.Ended)
            {
                manufacturingSustainer.End();
            }
            manufacturingSustainer = null;
        }

        private bool CanOperate()
        {
            if (powerComp != null && !powerComp.PowerOn)
                return false;
            if (refuelableComp != null && !refuelableComp.HasFuel)
                return false;
            return true;
        }

        public bool IsPoweredOn()
        {
            return powerComp == null || powerComp.PowerOn;
        }

        /// <summary>
        /// Checks if ARSENAL has network connectivity (required for DAGGER shipment).
        /// Returns true if this tile has LATTICE access via direct connection or HERALD.
        /// </summary>
        public bool HasNetworkConnection()
        {
            if (Map == null) return false;
            return ArsenalNetworkManager.IsTileConnected(Map.Tile);
        }

        /// <summary>
        /// Gets network status message for UI.
        /// </summary>
        public string GetNetworkStatusMessage()
        {
            if (Map == null) return "OFFLINE — No map";
            return ArsenalNetworkManager.GetNetworkStatus(Map.Tile);
        }

        #endregion

        #region Gizmos

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this ARSENAL factory.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameArsenal(this));
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "Manager",
                defaultDesc = "Open ARSENAL Manager to configure manufacturing lines and view network status.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_ArsenalManager(this));
                }
            };

            // Debug gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Refresh Cache",
                    action = delegate
                    {
                        lastCacheRefresh = -999;
                        RefreshNetworkCache();
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Show Resources",
                    action = delegate
                    {
                        var resources = GetAllAvailableResources();
                        string msg = "Adjacent resources:\n";
                        foreach (var kvp in resources)
                            msg += $"{kvp.Key.label}: {kvp.Value}\n";
                        Messages.Message(msg, this, MessageTypeDefOf.NeutralEvent);
                    }
                };
            }
        }

        #endregion

        #region Save/Load

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref customName, "customName");
            Scribe_Collections.Look(ref lines, "lines", LookMode.Deep);
            Scribe_Collections.Look(ref missileQueue, "missileQueue", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Migration: if loading old save without lines, initialize them
                if (lines == null || lines.Count == 0)
                {
                    lines = new List<ManufacturingLine>();
                    for (int i = 0; i < NUM_LINES; i++)
                    {
                        lines.Add(new ManufacturingLine { index = i, arsenal = this });
                    }
                }

                if (missileQueue == null)
                    missileQueue = new List<QueuedMissile>();
            }
        }

        #endregion

        #region Inspect String

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!HasAdjacentStorage())
            {
                if (!str.NullOrEmpty()) str += "\n";
                str += "<color=red>No adjacent storage! Place stockpile or shelf next to ARSENAL.</color>";
            }

            // Show network status
            if (!str.NullOrEmpty()) str += "\n";
            if (HasNetworkConnection())
            {
                str += $"Network: {GetNetworkStatusMessage()}";
            }
            else
            {
                str += $"<color=yellow>Network: {GetNetworkStatusMessage()}</color>";
            }

            // Show active line count
            int activeCount = lines.Count(l => l.enabled);
            int manufacturingCount = lines.Count(l => l.status == LineStatus.Manufacturing);
            if (!str.NullOrEmpty()) str += "\n";
            str += $"Lines: {manufacturingCount} manufacturing / {activeCount} enabled";

            // Show queued missiles
            if (missileQueue.Count > 0)
            {
                str += $"\nQueued DAGGERs: {missileQueue.Count} (waiting for HOP)";
            }

            return str;
        }

        #endregion
    }

    #region Dialogs

    public class Dialog_RenameArsenal : Window
    {
        private Building_Arsenal arsenal;
        private string newName;

        public Dialog_RenameArsenal(Building_Arsenal a)
        {
            arsenal = a;
            newName = a.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename ARSENAL");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                arsenal.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }

    #endregion
}
