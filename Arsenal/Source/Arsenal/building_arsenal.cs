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
        private List<Building_Stable> cachedStables = new List<Building_Stable>();
        private List<Building_PERCH> cachedPerches = new List<Building_PERCH>();
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
        /// Gets all cells from storage adjacent to ARSENAL.
        /// For multi-cell storage buildings (shelves, MORIA, etc.), returns ALL cells of that building.
        /// </summary>
        public IEnumerable<IntVec3> GetAdjacentStorageCells()
        {
            HashSet<IntVec3> storageCells = new HashSet<IntVec3>();
            HashSet<Thing> processedBuildings = new HashSet<Thing>();

            foreach (IntVec3 adjacentCell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!adjacentCell.InBounds(Map)) continue;

                // Check if cell is part of a stockpile zone
                Zone zone = adjacentCell.GetZone(Map);
                if (zone is Zone_Stockpile stockpile)
                {
                    // Add ALL cells of this stockpile zone (not just the adjacent one)
                    foreach (IntVec3 zoneCell in stockpile.Cells)
                    {
                        storageCells.Add(zoneCell);
                    }
                    continue;
                }

                // Check for storage buildings (shelf, MORIA, etc.)
                foreach (Thing thing in adjacentCell.GetThingList(Map))
                {
                    // Check for Building_Storage (shelves, hoppers, etc.)
                    if (thing is Building_Storage storageBuilding && !processedBuildings.Contains(thing))
                    {
                        processedBuildings.Add(thing);
                        // Add ALL cells this storage building occupies
                        foreach (IntVec3 buildingCell in storageBuilding.AllSlotCellsList())
                        {
                            storageCells.Add(buildingCell);
                        }
                    }

                    // Check for MORIA (custom storage)
                    if (thing is Building_Moria moria && !processedBuildings.Contains(thing))
                    {
                        processedBuildings.Add(thing);
                        // Add all cells the MORIA occupies
                        foreach (IntVec3 moriaCell in moria.AllSlotCellsList())
                        {
                            storageCells.Add(moriaCell);
                        }
                    }
                }
            }

            return storageCells;
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

            ForceRefreshNetworkCache();
        }

        /// <summary>
        /// Forces a cache refresh regardless of the interval timer.
        /// Call this after events that change network state (e.g., deliveries completing).
        /// </summary>
        public void ForceRefreshNetworkCache()
        {
            // HUBs and HOPs are searched GLOBALLY (across all maps) for DAGGER network
            cachedHubs = ArsenalNetworkManager.GetAllHubs().ToList();
            cachedHops = ArsenalNetworkManager.GetAllHops().ToList();

            // QUIVERs, STABLEs, and LATTICE are LOCAL only (local systems)
            cachedQuivers = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Quiver>().ToList();
            cachedStables = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Stable>().ToList();
            cachedLattice = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Lattice>().FirstOrDefault();

            // PERCHes are searched GLOBALLY for SLING logistics
            cachedPerches = ArsenalNetworkManager.GetAllPerches().ToList();

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
        /// HUBs require network connectivity (HERALD on remote tiles) to accept DAGGERs.
        /// </summary>
        public Building GetBestDestinationFor(MithrilProductDef product)
        {
            if (product == null) return null;

            RefreshNetworkCache();

            if (product.destinationType == typeof(Building_Hub))
            {
                // HUBs must have network connectivity to receive DAGGERs
                return cachedHubs
                    .Where(h => !h.IsFull && h.IsPoweredOn() && h.HasNetworkConnection())
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
            else if (product.destinationType == typeof(Building_Stable))
            {
                // STABLEs require LATTICE for coordination
                if (cachedLattice == null || !cachedLattice.IsPoweredOn())
                    return null;

                // Calculate total STABLE capacity on this map
                int totalStableCapacity = cachedStables.Count * Building_Stable.MAX_MULE_CAPACITY;

                // Count all MULEs associated with this map (docked + spawned + in-transit)
                int totalMulesOnMap = 0;
                foreach (var stable in cachedStables)
                {
                    totalMulesOnMap += stable.DockedMuleCount;
                }
                // Add spawned MULEs on the map
                totalMulesOnMap += ArsenalNetworkManager.GetMulesOnMap(Map).Count();

                // Don't allow manufacturing if we're at or over capacity
                if (totalMulesOnMap >= totalStableCapacity)
                    return null;

                return cachedStables
                    .Where(s => s.HasSpace && s.IsPoweredOn())
                    .OrderByDescending(s => Building_Stable.MAX_MULE_CAPACITY - s.DockedMuleCount)
                    .FirstOrDefault();
            }
            else if (product.destinationType == typeof(Building_PERCH))
            {
                // PERCHes require network connectivity for SLING delivery
                // Fleet limit: total SLINGs cannot exceed total PERCHes
                if (!SlingLogisticsManager.CanAddSling())
                    return null;

                return cachedPerches
                    .Where(p => p.IsPoweredOn && p.HasNetworkConnection() && !p.HasSlingOnPad)
                    .OrderBy(p => p.priority)
                    .FirstOrDefault();
            }

            return null;
        }

        /// <summary>
        /// Gets all valid destinations for a product (for UI dropdown).
        /// Only shows HUBs with network connectivity for DAGGERs.
        /// </summary>
        public List<Building> GetAllDestinationsFor(MithrilProductDef product)
        {
            if (product == null) return new List<Building>();

            RefreshNetworkCache();

            if (product.destinationType == typeof(Building_Hub))
            {
                // Only show HUBs with network connectivity
                return cachedHubs
                    .Where(h => h.HasNetworkConnection())
                    .Cast<Building>()
                    .ToList();
            }
            else if (product.destinationType == typeof(Building_Quiver))
                return cachedQuivers.Cast<Building>().ToList();
            else if (product.destinationType == typeof(Building_Stable))
                return cachedStables.Cast<Building>().ToList();
            else if (product.destinationType == typeof(Building_PERCH))
                return cachedPerches.Where(p => p.HasNetworkConnection()).Cast<Building>().ToList();

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
                    .Where(h => h.IsPoweredOn() && h.HasFuel && h.HasNetworkConnection())
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

            // Force cache refresh so next destination selection uses current HUB fill levels
            ForceRefreshNetworkCache();
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
                Thing missileItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_CruiseMissile);

                // Re-validate destination at delivery time (HOP/HERALD may have gone offline)
                if (targetHub == null || !CanReachHub(targetHub))
                {
                    // Original destination unreachable - try to find alternative
                    Building_Hub alternativeHub = FindAlternativeHub(targetHub);

                    if (alternativeHub != null && CanReachHub(alternativeHub))
                    {
                        // Found alternative - launch there instead
                        LaunchMissileToHub(missileItem, alternativeHub);
                        Messages.Message(Label + " Line " + (line.index + 1) + ": DAGGER re-routed to " + alternativeHub.Label +
                            " (original destination unreachable).", this, MessageTypeDefOf.NeutralEvent);
                    }
                    else if (targetHub != null)
                    {
                        // No alternative found - queue missile for original target
                        missileQueue.Add(new QueuedMissile { missile = missileItem, targetHub = targetHub });
                        Messages.Message(Label + " Line " + (line.index + 1) + ": DAGGER queued - " +
                            targetHub.Label + " unreachable (HOP/HERALD offline?).", this, MessageTypeDefOf.NeutralEvent);
                    }
                    else
                    {
                        // No destination at all - drop missile
                        GenPlace.TryPlaceThing(missileItem, Position, Map, ThingPlaceMode.Near);
                        Messages.Message(Label + ": DAGGER completed but no valid destination.", this, MessageTypeDefOf.NeutralEvent);
                    }
                    return;
                }

                // Original destination is reachable - launch
                LaunchMissileToHub(missileItem, targetHub);
                Messages.Message(Label + " Line " + (line.index + 1) + ": DAGGER launched to " + targetHub.Label,
                    this, MessageTypeDefOf.PositiveEvent);
            }
            else if (product.destinationType == typeof(Building_Stable))
            {
                // MULE - spawn at ARSENAL and pathfind to STABLE
                Building_Stable targetStable = destination as Building_Stable;
                if (targetStable == null || !targetStable.HasSpace)
                {
                    // No valid target - drop MULE item
                    Thing muleItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_MULE_Item);
                    GenPlace.TryPlaceThing(muleItem, Position, Map, ThingPlaceMode.Near);
                    Messages.Message(Label + ": MULE completed but no valid STABLE available.", this, MessageTypeDefOf.NeutralEvent);
                    return;
                }

                // Create MULE pawn and spawn at ARSENAL position
                PawnGenerationRequest request = new PawnGenerationRequest(
                    kind: ArsenalDefOf.Arsenal_MULE_Kind,
                    faction: Faction.OfPlayer,
                    forceGenerateNewPawn: true
                );
                MULE_Pawn mule = (MULE_Pawn)PawnGenerator.GeneratePawn(request);
                mule.SetHomeStable(targetStable);

                // Find a spawn cell near the ARSENAL
                IntVec3 spawnCell = Position;
                foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
                {
                    if (cell.InBounds(Map) && cell.Walkable(Map) && !cell.Fogged(Map))
                    {
                        spawnCell = cell;
                        break;
                    }
                }

                // Spawn the MULE and have it drive to the STABLE
                GenSpawn.Spawn(mule, spawnCell, Map);
                mule.InitializeForDelivery(targetStable);

                Messages.Message(Label + " Line " + (line.index + 1) + ": MULE manufacturing complete. Delivering to " + targetStable.Label,
                    this, MessageTypeDefOf.PositiveEvent);
            }
            else if (product.destinationType == typeof(Building_PERCH))
            {
                // SLING - spawn at PERCH pad via skyfaller
                Building_PERCH targetPerch = destination as Building_PERCH;
                if (targetPerch == null || !targetPerch.HasNetworkConnection())
                {
                    // No valid target - drop SLING item
                    Thing slingItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_SLING);
                    GenPlace.TryPlaceThing(slingItem, Position, Map, ThingPlaceMode.Near);
                    Messages.Message(Label + ": SLING completed but no valid PERCH available.", this, MessageTypeDefOf.NeutralEvent);
                    return;
                }

                // Check fleet capacity
                if (!SlingLogisticsManager.CanAddSling())
                {
                    Thing slingItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_SLING);
                    GenPlace.TryPlaceThing(slingItem, Position, Map, ThingPlaceMode.Near);
                    Messages.Message(Label + ": SLING completed but fleet is at capacity (need more PERCHes).", this, MessageTypeDefOf.NeutralEvent);
                    return;
                }

                // Create SLING item and deliver to PERCH
                Thing sling = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_SLING);

                // Assign name to the SLING immediately
                string newSlingName = null;
                if (sling is SLING_Thing slingThing)
                {
                    slingThing.AssignNewName();
                    newSlingName = slingThing.CustomName;
                }

                // If PERCH is on the same map, spawn landing skyfaller
                if (targetPerch.Map == Map)
                {
                    var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(ArsenalDefOf.Arsenal_SlingLanding);
                    skyfaller.sling = sling;
                    skyfaller.slingName = newSlingName;
                    skyfaller.destinationPerch = targetPerch;
                    skyfaller.isWaypointStop = false;
                    GenSpawn.Spawn(skyfaller, targetPerch.Position, Map);
                }
                else
                {
                    // Different map - create traveling world object
                    var traveling = (WorldObject_TravelingSling)WorldObjectMaker.MakeWorldObject(ArsenalDefOf.Arsenal_TravelingSling);
                    traveling.Tile = Map.Tile;
                    traveling.destinationTile = targetPerch.Map.Tile;
                    traveling.sling = sling;
                    traveling.slingName = newSlingName;
                    traveling.cargo = new System.Collections.Generic.Dictionary<ThingDef, int>();
                    traveling.destinationPerch = targetPerch;
                    traveling.CalculateRoute();
                    Find.WorldObjects.Add(traveling);
                }

                Messages.Message(Label + " Line " + (line.index + 1) + ": " + (newSlingName ?? "SLING") + " delivered to " + targetPerch.Label,
                    this, MessageTypeDefOf.PositiveEvent);
            }
        }

        /// <summary>
        /// Tries to find an alternative HUB destination when the original becomes unreachable.
        /// </summary>
        private Building_Hub FindAlternativeHub(Building_Hub originalHub)
        {
            RefreshNetworkCache();

            // Find any reachable HUB that isn't full
            return cachedHubs
                .Where(h => h != originalHub)
                .Where(h => !h.IsFull && h.IsPoweredOn() && h.HasNetworkConnection())
                .Where(h => CanReachHub(h))
                .OrderBy(h => h.priority)
                .ThenByDescending(h => h.EmptySlots)
                .FirstOrDefault();
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

        /// <summary>
        /// Public wrapper for CanReachHub, used by ManufacturingLine.
        /// </summary>
        public bool CanReachHubPublic(Building_Hub hub)
        {
            return CanReachHub(hub);
        }

        private bool CanReachHub(Building_Hub hub)
        {
            if (hub?.Map == null) return false;

            // HUB must have network connectivity
            if (!hub.HasNetworkConnection()) return false;

            int destTile = hub.Map.Tile;
            int dist = Find.WorldGrid.TraversalDistanceBetween(Map.Tile, destTile);

            // Direct range check
            if (dist <= 100f)
                return true;

            // Need to trace a valid route through connected HOPs
            // Use GetRouteToHub to verify a valid path exists
            var route = GetRouteToHub(destTile);
            return route != null && route.Count > 0;
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

        /// <summary>
        /// Calculates route to destination tile through connected HOPs.
        /// Returns null if destination is unreachable.
        /// </summary>
        public List<int> GetRouteToHub(int destinationTile)
        {
            List<int> route = new List<int>();
            float fuel = 100f;
            int current = Map.Tile;
            HashSet<int> visitedTiles = new HashSet<int>(); // Prevent infinite loops

            while (current != destinationTile)
            {
                if (visitedTiles.Contains(current))
                {
                    // Stuck in a loop - route is impossible
                    return null;
                }
                visitedTiles.Add(current);

                int directDist = Find.WorldGrid.TraversalDistanceBetween(current, destinationTile);
                if (directDist <= fuel)
                {
                    // Can reach destination directly
                    route.Add(destinationTile);
                    break;
                }

                // Need a HOP to extend range
                Building_Hop bestHop = FindBestAvailableHop(current, destinationTile, fuel);

                if (bestHop == null)
                {
                    // No valid HOP found and destination is out of range - ROUTE IS IMPOSSIBLE
                    return null;
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
                // HOP must have network connectivity (HERALD on remote tiles)
                if (!hop.HasNetworkConnection()) continue;

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
                        ForceRefreshNetworkCache();
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
