using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Central logistics coordinator for SLING/PERCH cargo system.
    /// Integrated with LATTICE for network-wide coordination.
    /// Supports both legacy Building_PERCH and new Building_PerchBeacon zones.
    /// </summary>
    public static class SlingLogisticsManager
    {
        private const int SCAN_INTERVAL = 120;  // 2 seconds
        private const float FUEL_PER_TILE = 1f;
        public const int MAX_CARGO_CAPACITY = 750;  // SLING cargo limit

        private static int tickCounter = 0;

        /// <summary>
        /// Called from LATTICE tick to process logistics.
        /// </summary>
        public static void Tick()
        {
            tickCounter++;
            if (tickCounter < SCAN_INTERVAL) return;
            tickCounter = 0;

            ProcessLogistics();
            ProcessBeaconZoneLogistics();
        }

        /// <summary>
        /// Main logistics processing loop.
        /// Distributes SLINGs across SINKs in round-robin fashion for fair distribution.
        /// Also redistributes SLINGs stuck at SINKs back to SOURCEs.
        /// </summary>
        private static void ProcessLogistics()
        {
            // First, redistribute any SLINGs stuck at SINK PERCHes
            RedistributeSlingsFromSinks();

            // Get all active SINKs with demand, sorted by priority
            var sinksWithDemand = ArsenalNetworkManager.GetAllPerches()
                .Where(p => p.role == PerchRole.SINK &&
                           p.HasNetworkConnection() &&
                           p.IsPoweredOn &&
                           p.HasDemand())
                .OrderBy(p => p.priority)
                .ToList();

            if (sinksWithDemand.Count == 0)
            {
                // Occasionally log this for debugging
                if (Find.TickManager.TicksGame % 600 == 0)
                {
                    var allSinks = ArsenalNetworkManager.GetSinkPerches();
                    if (allSinks.Count > 0)
                    {
                        Log.Message($"[SLING] No SINKs with demand. Total SINKs: {allSinks.Count}");
                    }
                }
                return;
            }

            // Get all SOURCE perches with SLINGs ready in slot 1 (staging slot)
            var availableSources = ArsenalNetworkManager.GetAllPerches()
                .Where(p => p.role == PerchRole.SOURCE &&
                           p.HasNetworkConnection() &&
                           p.IsPoweredOn &&
                           p.HasSlot1Sling &&    // Dispatch is from slot 1 (primary)
                           !p.Slot1Busy)         // Slot 1 not busy (not loading)
                .ToList();

            if (availableSources.Count == 0)
            {
                // Log why no sources available
                var allSources = ArsenalNetworkManager.GetSourcePerches();
                if (allSources.Count > 0 && Find.TickManager.TicksGame % 600 == 0)
                {
                    foreach (var src in allSources)
                    {
                        string reason = !src.HasNetworkConnection() ? "no network" :
                                       !src.IsPoweredOn ? "unpowered" :
                                       !src.HasSlot1Sling ? "no SLING in slot1" :
                                       src.Slot1Busy ? "slot1 busy (loading)" : "unknown";
                        Log.Message($"[SLING] SOURCE {src.Label}: unavailable - {reason}");
                    }
                }
                return;
            }

            Log.Message($"[SLING] Processing logistics: {sinksWithDemand.Count} sinks with demand, {availableSources.Count} sources ready");

            // Track demand per sink for round-robin distribution
            var sinkDemands = sinksWithDemand.ToDictionary(
                s => s,
                s => new Dictionary<ThingDef, int>(s.GetDemand()));

            // Round-robin distribution: dispatch one SLING per sink per round
            // This ensures fair distribution across all SINKs
            bool dispatchedAny = true;
            while (dispatchedAny && availableSources.Count > 0)
            {
                dispatchedAny = false;

                // Try to dispatch one SLING to each sink (in priority order)
                foreach (var sink in sinksWithDemand)
                {
                    if (availableSources.Count == 0) break;

                    var remainingDemand = sinkDemands[sink];
                    if (remainingDemand.Values.Sum() <= 0) continue;

                    // Find a SOURCE that can fulfill some demand for this sink
                    Building_PERCH bestSource = null;
                    Dictionary<ThingDef, int> bestCargo = null;

                    foreach (var resource in remainingDemand.Keys.ToList().OrderByDescending(r => remainingDemand[r]))
                    {
                        if (remainingDemand[resource] <= 0) continue;

                        // Find nearest SOURCE with this resource
                        var source = FindNearestSourceWithResource(sink, resource, availableSources);
                        if (source == null) continue;

                        // Check if route is viable
                        if (!IsRouteViable(source, sink)) continue;

                        // Calculate cargo to load
                        var cargoToLoad = CalculateCargo(source, sink, remainingDemand);
                        if (cargoToLoad.Count == 0) continue;

                        bestSource = source;
                        bestCargo = cargoToLoad;
                        break;
                    }

                    // Dispatch if we found a viable source
                    if (bestSource != null && bestCargo != null && bestSource.StartLoading(sink, bestCargo))
                    {
                        availableSources.Remove(bestSource);
                        dispatchedAny = true;

                        // Reduce remaining demand
                        foreach (var kvp in bestCargo)
                        {
                            if (remainingDemand.ContainsKey(kvp.Key))
                            {
                                remainingDemand[kvp.Key] -= kvp.Value;
                                if (remainingDemand[kvp.Key] <= 0)
                                    remainingDemand.Remove(kvp.Key);
                            }
                        }
                    }
                }
            }
        }

        #region Beacon Zone Logistics

        // Track pending beacon zone dispatches (SLING -> destination beacon zone)
        private static Dictionary<SLING_Thing, Building_PerchBeacon> pendingBeaconDispatches = new Dictionary<SLING_Thing, Building_PerchBeacon>();

        /// <summary>
        /// Processes logistics for beacon zone system (Building_PerchBeacon).
        /// This is the new system using 4-beacon landing zones.
        /// </summary>
        private static void ProcessBeaconZoneLogistics()
        {
            // First, check for SLINGs that have completed loading and dispatch them
            CheckAndDispatchLoadedSlings();

            // Get all valid beacon zones with SOURCE role that have idle SLINGs
            var sourceZones = ArsenalNetworkManager.GetSourceBeacons()
                .Where(b => b.HasNetworkConnection() && b.IsPoweredOn && b.HasAvailableSling)
                .ToList();

            if (sourceZones.Count == 0) return;

            // Get all valid beacon zones with SINK role that need resources
            var sinkZones = ArsenalNetworkManager.GetSinkBeacons()
                .Where(b => b.HasNetworkConnection() && b.IsPoweredOn && b.GetResourcesNeeded().Count > 0)
                .ToList();

            if (sinkZones.Count == 0)
            {
                if (Find.TickManager.TicksGame % 600 == 0)
                {
                    Log.Message($"[SLING] Beacon zones: {sourceZones.Count} sources with SLINGs, 0 sinks with demand");
                }
                return;
            }

            Log.Message($"[SLING] Processing beacon zone logistics: {sourceZones.Count} sources, {sinkZones.Count} sinks");

            foreach (var sourceZone in sourceZones)
            {
                // Get an idle SLING from this source zone (not loading)
                var idleSlings = sourceZone.GetIdleSlings();
                if (idleSlings.Count == 0) continue;

                var sling = idleSlings.First();

                // Skip if this SLING already has a pending dispatch
                if (pendingBeaconDispatches.ContainsKey(sling)) continue;

                var available = sourceZone.GetAvailableForExport();
                if (available.Count == 0) continue;

                // Find a sink that needs resources we have
                foreach (var sinkZone in sinkZones)
                {
                    var needed = sinkZone.GetResourcesNeeded();
                    if (needed.Count == 0) continue;

                    // Calculate cargo to load (intersection of available and needed)
                    var cargoToLoad = CalculateBeaconCargo(available, needed);
                    if (cargoToLoad.Count == 0) continue;

                    // Check route viability (fuel)
                    if (!IsBeaconRouteViable(sourceZone, sinkZone)) continue;

                    // Start loading the SLING
                    if (StartBeaconLoading(sourceZone, sinkZone, sling, cargoToLoad))
                    {
                        Log.Message($"[SLING] Beacon dispatch: {sling.Label} from {sourceZone.ZoneName} to {sinkZone.ZoneName}");
                        break; // Move to next source zone
                    }
                }
            }
        }

        /// <summary>
        /// Checks for SLINGs that have completed loading and dispatches them.
        /// </summary>
        private static void CheckAndDispatchLoadedSlings()
        {
            // Check all beacon zones for loading SLINGs
            var allZones = ArsenalNetworkManager.GetAllValidBeaconZones();
            var toRemove = new List<SLING_Thing>();

            foreach (var kvp in pendingBeaconDispatches.ToList())
            {
                var sling = kvp.Key;
                var destination = kvp.Value;

                if (sling == null || sling.Destroyed || destination == null || destination.Destroyed)
                {
                    toRemove.Add(sling);
                    continue;
                }

                // Check if loading is complete or has cargo (dispatch with what we have)
                if (sling.IsLoadingComplete() || (sling.CurrentCargoCount > 0 && !sling.IsLoading))
                {
                    DispatchSlingFromBeaconZone(sling, destination);
                    toRemove.Add(sling);
                }
            }

            foreach (var sling in toRemove)
            {
                pendingBeaconDispatches.Remove(sling);
            }
        }

        /// <summary>
        /// Dispatches a SLING from a beacon zone to its destination.
        /// </summary>
        private static void DispatchSlingFromBeaconZone(SLING_Thing sling, Building_PerchBeacon destination)
        {
            if (sling == null || !sling.Spawned || sling.Map == null) return;
            if (destination == null || destination.Map == null) return;

            // Complete the loading process
            sling.CompleteLoading();
            var cargo = sling.GetCargoManifest();
            string slingName = sling.Label;
            Map sourceMap = sling.Map;
            IntVec3 launchPos = sling.Position;

            // Despawn the SLING
            sling.DeSpawn(DestroyMode.Vanish);

            // Create launching skyfaller
            var launchingSkyfaller = (SlingLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLaunching);
            launchingSkyfaller.sling = sling;
            launchingSkyfaller.slingName = slingName;
            launchingSkyfaller.cargo = cargo;
            launchingSkyfaller.originPerch = null; // No legacy PERCH origin
            launchingSkyfaller.destinationPerch = null; // No legacy PERCH destination
            launchingSkyfaller.destinationBeaconZone = destination;
            launchingSkyfaller.destinationTile = destination.Map.Tile;
            launchingSkyfaller.isReturnFlight = false;

            GenSpawn.Spawn(launchingSkyfaller, launchPos, sourceMap);

            Messages.Message($"{slingName} launching from beacon zone to {destination.ZoneName} with {cargo.Values.Sum()} items",
                destination, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>
        /// Calculates cargo to load for beacon zone transfer.
        /// </summary>
        private static Dictionary<ThingDef, int> CalculateBeaconCargo(
            Dictionary<ThingDef, int> available,
            Dictionary<ThingDef, int> needed)
        {
            var cargo = new Dictionary<ThingDef, int>();
            int remainingCapacity = MAX_CARGO_CAPACITY;

            // Prioritize resources with highest need
            foreach (var kvp in needed.OrderByDescending(n => n.Value))
            {
                if (remainingCapacity <= 0) break;

                ThingDef resource = kvp.Key;
                int wantAmount = kvp.Value;

                if (!available.ContainsKey(resource)) continue;

                int canLoad = Mathf.Min(wantAmount, available[resource], remainingCapacity);
                if (canLoad > 0)
                {
                    cargo[resource] = canLoad;
                    remainingCapacity -= canLoad;
                }
            }

            return cargo;
        }

        /// <summary>
        /// Checks if route between beacon zones is viable (fuel).
        /// </summary>
        private static bool IsBeaconRouteViable(Building_PerchBeacon source, Building_PerchBeacon sink)
        {
            if (source.Map == null || sink.Map == null) return false;

            int distance = Find.WorldGrid.TraversalDistanceBetween(
                source.Map.Tile, sink.Map.Tile);

            // For beacon zones, we assume there's a fueling mechanism
            // If same tile, always viable
            if (distance == 0) return true;

            // For remote tiles, check if there's fuel available at source
            // TODO: Add fuel tracking to beacon zones
            return true; // For now, assume all routes are viable
        }

        /// <summary>
        /// Starts loading a SLING at a beacon zone for dispatch to another zone.
        /// </summary>
        private static bool StartBeaconLoading(
            Building_PerchBeacon sourceZone,
            Building_PerchBeacon sinkZone,
            SLING_Thing sling,
            Dictionary<ThingDef, int> cargoToLoad)
        {
            if (sling == null || sling.IsLoading) return false;

            // Start the SLING's loading process
            sling.StartLoading(cargoToLoad);

            // Track the pending dispatch so we dispatch to correct destination when complete
            pendingBeaconDispatches[sling] = sinkZone;

            Log.Message($"[SLING] {sling.Label} starting to load at {sourceZone.ZoneName} for {sinkZone.ZoneName}. " +
                       $"Cargo: {string.Join(", ", cargoToLoad.Select(c => $"{c.Key.label}x{c.Value}"))}");

            Messages.Message($"{sling.Label} loading cargo at {sourceZone.ZoneName} for delivery to {sinkZone.ZoneName}.",
                sourceZone, MessageTypeDefOf.NeutralEvent);

            return true;
        }

        /// <summary>
        /// Gets a beacon zone that can accept a landing SLING.
        /// </summary>
        public static Building_PerchBeacon GetBeaconZoneForLanding(int destinationTile)
        {
            return ArsenalNetworkManager.GetConnectedBeaconZones()
                .Where(b => b.Map != null && b.Map.Tile == destinationTile && b.HasSpaceForSling)
                .FirstOrDefault();
        }

        #endregion

        /// <summary>
        /// Redistributes SLINGs that are stuck at SINK PERCHes back to SOURCE PERCHes.
        /// This handles cases where SLINGs end up at SINKs due to cancelled loads or bugs.
        /// </summary>
        private static void RedistributeSlingsFromSinks()
        {
            // Find SINK PERCHes with idle SLINGs in slot1 (not unloading, not refueling)
            var sinksWithIdleSlings = ArsenalNetworkManager.GetAllPerches()
                .Where(p => p.role == PerchRole.SINK &&
                           p.HasNetworkConnection() &&
                           p.IsPoweredOn &&
                           p.HasSlot1Sling &&
                           !p.Slot1Busy)
                .ToList();

            if (sinksWithIdleSlings.Count == 0) return;

            // Find SOURCE PERCHes that need SLINGs (no SLING in slot1)
            var sourcesNeedingSlings = ArsenalNetworkManager.GetAllPerches()
                .Where(p => p.role == PerchRole.SOURCE &&
                           p.HasNetworkConnection() &&
                           p.IsPoweredOn &&
                           !p.HasSlot1Sling &&
                           p.HasAvailableSlot)
                .ToList();

            if (sourcesNeedingSlings.Count == 0) return;

            // Send SLINGs from SINKs to SOURCEs
            foreach (var sink in sinksWithIdleSlings)
            {
                if (sourcesNeedingSlings.Count == 0) break;

                // Find nearest SOURCE that needs a SLING
                Building_PERCH nearestSource = null;
                int nearestDist = int.MaxValue;

                foreach (var source in sourcesNeedingSlings)
                {
                    if (!IsRouteViable(sink, source)) continue;

                    int dist = Find.WorldGrid.TraversalDistanceBetween(
                        sink.Map.Tile, source.Map.Tile);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestSource = source;
                    }
                }

                if (nearestSource != null)
                {
                    // Send the idle SLING to the SOURCE (empty return flight)
                    var sling = sink.Slot1Sling;
                    string slingName = SLING_Thing.GetSlingName(sling);

                    InitiateReturnFlight(sling, slingName, sink, nearestSource);

                    // Clear the SLING from the sink's slot
                    sink.ClearSlot1();
                    sourcesNeedingSlings.Remove(nearestSource);

                    Log.Message($"[SLING] Redistributing {slingName} from SINK {sink.Label} to SOURCE {nearestSource.Label}");
                }
            }
        }

        /// <summary>
        /// Attempts to dispatch a SLING from a specific PERCH.
        /// Used for manual/dev dispatch testing.
        /// </summary>
        public static bool TryDispatchFromPerch(Building_PERCH source)
        {
            if (source == null || source.role != PerchRole.SOURCE) return false;
            if (!source.HasSlot1Sling || source.Slot1Busy) return false;

            // Find a SINK with demand
            var sink = ArsenalNetworkManager.GetAllPerches()
                .Where(p => p.role == PerchRole.SINK &&
                           p.HasNetworkConnection() &&
                           p.IsPoweredOn &&
                           p.HasDemand() &&
                           p != source)
                .OrderBy(p => p.priority)
                .FirstOrDefault();

            if (sink == null)
            {
                Messages.Message("No SINK with demand found", source, MessageTypeDefOf.RejectInput);
                return false;
            }

            var demand = sink.GetDemand();
            var cargoToLoad = CalculateCargo(source, sink, demand);

            if (cargoToLoad.Count == 0)
            {
                Messages.Message("No matching cargo available at SOURCE", source, MessageTypeDefOf.RejectInput);
                return false;
            }

            return source.StartLoading(sink, cargoToLoad);
        }

        /// <summary>
        /// Finds the nearest SOURCE perch that has the requested resource.
        /// </summary>
        private static Building_PERCH FindNearestSourceWithResource(
            Building_PERCH sink,
            ThingDef resource,
            List<Building_PERCH> availableSources)
        {
            Building_PERCH nearest = null;
            int nearestDist = int.MaxValue;

            foreach (var source in availableSources)
            {
                var available = source.GetAvailableResources();
                if (!available.ContainsKey(resource) || available[resource] <= 0)
                    continue;

                int dist = Find.WorldGrid.TraversalDistanceBetween(
                    source.Map.Tile,
                    sink.Map.Tile);

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = source;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Calculates the cargo to load based on SINK demand and SOURCE availability.
        /// </summary>
        private static Dictionary<ThingDef, int> CalculateCargo(
            Building_PERCH source,
            Building_PERCH sink,
            Dictionary<ThingDef, int> demand)
        {
            var cargo = new Dictionary<ThingDef, int>();
            var available = source.GetAvailableResources();
            int remainingCapacity = MAX_CARGO_CAPACITY;

            // Prioritize resources with highest demand
            foreach (var kvp in demand.OrderByDescending(d => d.Value))
            {
                if (remainingCapacity <= 0) break;

                ThingDef resource = kvp.Key;
                int needed = kvp.Value;

                if (!available.ContainsKey(resource)) continue;

                int canLoad = Mathf.Min(needed, available[resource], remainingCapacity);
                if (canLoad > 0)
                {
                    cargo[resource] = canLoad;
                    remainingCapacity -= canLoad;
                }
            }

            return cargo;
        }

        /// <summary>
        /// Checks if a route from source to sink is viable (fuel + waypoints).
        /// </summary>
        public static bool IsRouteViable(Building_PERCH source, Building_PERCH sink)
        {
            if (source.Map == null || sink.Map == null) return false;

            int distance = Find.WorldGrid.TraversalDistanceBetween(
                source.Map.Tile,
                sink.Map.Tile);

            float fuelNeeded = distance * FUEL_PER_TILE;
            float availableFuel = source.FuelLevel;

            // Direct route possible?
            if (availableFuel >= fuelNeeded)
                return true;

            // Check for waypoint route
            return CanReachWithWaypoints(source.Map.Tile, sink.Map.Tile, availableFuel);
        }

        /// <summary>
        /// Checks if destination can be reached using waypoint refueling.
        /// </summary>
        private static bool CanReachWithWaypoints(int fromTile, int toTile, float startingFuel)
        {
            const int maxHops = 10;  // Prevent infinite loops
            int currentTile = fromTile;
            float currentFuel = startingFuel;
            HashSet<int> visited = new HashSet<int> { fromTile };

            for (int hop = 0; hop < maxHops; hop++)
            {
                int distance = Find.WorldGrid.TraversalDistanceBetween(currentTile, toTile);

                // Can reach destination directly?
                if (distance <= currentFuel)
                    return true;

                // Find a waypoint within range that gets us closer
                var waypoint = FindBestWaypointToward(currentTile, toTile, currentFuel, visited);
                if (waypoint == null)
                    return false;

                int waypointTile = waypoint.Map.Tile;
                visited.Add(waypointTile);

                // "Move" to waypoint and refuel
                currentTile = waypointTile;
                currentFuel = GetWaypointFuelCapacity(waypoint);
            }

            return false;
        }

        private static Building FindBestWaypointToward(int fromTile, int toTile, float maxRange, HashSet<int> visited)
        {
            Building best = null;
            int bestProgress = int.MaxValue;

            // Check PERCHes
            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                if (perch.Map == null || !perch.HasFuel) continue;
                if (visited.Contains(perch.Map.Tile)) continue;

                int distTo = Find.WorldGrid.TraversalDistanceBetween(fromTile, perch.Map.Tile);
                if (distTo > maxRange) continue;

                int distFrom = Find.WorldGrid.TraversalDistanceBetween(perch.Map.Tile, toTile);
                if (distFrom < bestProgress)
                {
                    bestProgress = distFrom;
                    best = perch;
                }
            }

            // Check HOPs
            foreach (var hop in ArsenalNetworkManager.GetAllHops())
            {
                if (hop.Map == null || !hop.HasFuel) continue;
                if (visited.Contains(hop.Map.Tile)) continue;

                int distTo = Find.WorldGrid.TraversalDistanceBetween(fromTile, hop.Map.Tile);
                if (distTo > maxRange) continue;

                int distFrom = Find.WorldGrid.TraversalDistanceBetween(hop.Map.Tile, toTile);
                if (distFrom < bestProgress)
                {
                    bestProgress = distFrom;
                    best = hop;
                }
            }

            return best;
        }

        private static float GetWaypointFuelCapacity(Building waypoint)
        {
            if (waypoint is Building_PERCH perch)
                return Mathf.Min(perch.FuelLevel, 150f);  // SLING tank capacity
            if (waypoint is Building_Hop hop)
                return Mathf.Min(hop.GetAvailableFuel(), 150f);
            return 0f;
        }

        /// <summary>
        /// Calculates fuel cost for a journey between tiles.
        /// </summary>
        public static float CalculateFuelCost(int fromTile, int toTile)
        {
            int distance = Find.WorldGrid.TraversalDistanceBetween(fromTile, toTile);
            return distance * FUEL_PER_TILE;
        }

        /// <summary>
        /// Gets all SLINGs currently in the network.
        /// Includes SLINGs on legacy PERCHes, in beacon zones, and in transit.
        /// </summary>
        public static int GetTotalSlingCount()
        {
            int count = 0;

            // SLINGs on legacy PERCHes (count both slots)
            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                count += perch.SlingCount;
            }

            // SLINGs in beacon zones
            foreach (var beaconZone in ArsenalNetworkManager.GetAllValidBeaconZones())
            {
                count += beaconZone.GetDockedSlings().Count;
            }

            // SLINGs in transit
            foreach (var wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is WorldObject_TravelingSling)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Gets the maximum number of SLINGs allowed.
        /// Based on total landing zones: legacy PERCHes + beacon zones.
        /// </summary>
        public static int GetMaxSlingCount()
        {
            int legacyPerches = ArsenalNetworkManager.GetAllPerches().Count;
            int beaconZones = ArsenalNetworkManager.GetAllValidBeaconZones().Count;
            return legacyPerches + beaconZones;
        }

        /// <summary>
        /// Checks if a new SLING can be added to the network.
        /// </summary>
        public static bool CanAddSling()
        {
            return GetTotalSlingCount() < GetMaxSlingCount();
        }

        /// <summary>
        /// Gets all SLINGs currently in transit.
        /// </summary>
        public static List<WorldObject_TravelingSling> GetSlingsInTransit()
        {
            var slings = new List<WorldObject_TravelingSling>();
            foreach (var wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is WorldObject_TravelingSling sling)
                    slings.Add(sling);
            }
            return slings;
        }

        /// <summary>
        /// Handles SLING arriving after refueling at waypoint.
        /// </summary>
        public static void ContinueJourneyAfterRefuel(
            Thing sling,
            Dictionary<ThingDef, int> cargo,
            Building_PERCH origin,
            Building_PERCH destination,
            int currentTile,
            bool isReturnFlight)
        {
            // Create new traveling object to continue journey
            var traveling = (WorldObject_TravelingSling)WorldObjectMaker.MakeWorldObject(
                ArsenalDefOf.Arsenal_TravelingSling);

            traveling.Tile = currentTile;
            traveling.destinationTile = destination.Map.Tile;
            traveling.sling = sling;
            traveling.cargo = cargo ?? new Dictionary<ThingDef, int>();
            traveling.originPerch = origin;
            traveling.destinationPerch = destination;

            traveling.CalculateRoute();
            Find.WorldObjects.Add(traveling);
        }

        /// <summary>
        /// Initiates return flight for SLING after unloading.
        /// </summary>
        public static void InitiateReturnFlight(
            Thing sling,
            string slingName,
            Building_PERCH currentPerch,
            Building_PERCH originPerch)
        {
            if (sling == null || currentPerch == null || originPerch == null) return;
            if (currentPerch == originPerch) return;  // Already home

            // Despawn SLING from pad
            if (sling.Spawned)
            {
                sling.DeSpawn(DestroyMode.Vanish);
            }

            // Consume fuel for return trip
            float fuelCost = CalculateFuelCost(currentPerch.Map.Tile, originPerch.Map.Tile);
            var refuelComp = currentPerch.GetComp<CompRefuelable>();
            if (refuelComp != null)
            {
                refuelComp.ConsumeFuel(fuelCost);
            }

            // Create launching skyfaller for takeoff animation
            var launchingSkyfaller = (SlingLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLaunching);
            launchingSkyfaller.sling = sling;
            launchingSkyfaller.slingName = slingName;
            launchingSkyfaller.cargo = new Dictionary<ThingDef, int>();  // Empty return flight
            launchingSkyfaller.originPerch = currentPerch;  // Current location becomes origin
            launchingSkyfaller.destinationPerch = originPerch;  // Original source is now destination
            launchingSkyfaller.destinationTile = originPerch.Map.Tile;
            launchingSkyfaller.isReturnFlight = true;

            GenSpawn.Spawn(launchingSkyfaller, currentPerch.Position, currentPerch.Map);

            Messages.Message($"{slingName ?? "SLING"} returning to {originPerch.Label}", currentPerch, MessageTypeDefOf.NeutralEvent);
        }
    }

    /// <summary>
    /// Game component that drives the logistics tick.
    /// </summary>
    public class GameComponent_SlingLogistics : GameComponent
    {
        public GameComponent_SlingLogistics(Game game) { }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // Only tick if there's a powered LATTICE
            var lattice = ArsenalNetworkManager.GlobalLattice;
            if (lattice != null && lattice.IsPoweredOn())
            {
                SlingLogisticsManager.Tick();
            }
        }
    }
}
