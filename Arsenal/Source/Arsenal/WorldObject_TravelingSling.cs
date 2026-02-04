using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// World object representing a SLING cargo craft in transit between PERCHes.
    /// Handles waypoint refueling for long-range routes.
    /// </summary>
    public class WorldObject_TravelingSling : WorldObject
    {
        private const float SPEED = 0.004f;  // Cargo craft moves slower than missiles
        private const float FUEL_CAPACITY = 150f;  // SLING has larger fuel tank

        public int destinationTile = -1;
        public Thing sling;
        public string slingName;
        public Dictionary<ThingDef, int> cargo = new Dictionary<ThingDef, int>();
        public Building_PERCH originPerch;
        public Building_PERCH destinationPerch;
        public Building_PerchBeacon destinationBeacon; // New beacon zone system
        public bool isReturnFlight = false;

        private int nextWaypointTile = -1;
        private int previousTile = -1;
        private float traveledPct = 0f;
        private int recalculateCounter = 0;
        private const int RECALCULATE_INTERVAL = 60;

        private float currentFuel = FUEL_CAPACITY;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref destinationTile, "destinationTile", -1);
            Scribe_Deep.Look(ref sling, "sling");
            Scribe_Values.Look(ref slingName, "slingName");
            Scribe_Collections.Look(ref cargo, "cargo", LookMode.Def, LookMode.Value);
            Scribe_References.Look(ref originPerch, "originPerch");
            Scribe_References.Look(ref destinationPerch, "destinationPerch");
            Scribe_References.Look(ref destinationBeacon, "destinationBeacon");
            Scribe_Values.Look(ref isReturnFlight, "isReturnFlight", false);
            Scribe_Values.Look(ref nextWaypointTile, "nextWaypointTile", -1);
            Scribe_Values.Look(ref previousTile, "previousTile", -1);
            Scribe_Values.Look(ref traveledPct, "traveledPct", 0f);
            Scribe_Values.Look(ref currentFuel, "currentFuel", FUEL_CAPACITY);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (cargo == null) cargo = new Dictionary<ThingDef, int>();
            }
        }

        public void CalculateRoute()
        {
            // Initialize previousTile when starting travel
            if (previousTile < 0)
            {
                previousTile = Tile;
            }
            RecalculateNextWaypoint();
        }

        /// <summary>
        /// Override DrawPos to smoothly interpolate position between tiles
        /// </summary>
        public override Vector3 DrawPos
        {
            get
            {
                Vector3 currentPos = Find.WorldGrid.GetTileCenter(Tile);

                // If no valid waypoint or no travel progress, just use current position
                if (nextWaypointTile < 0 || traveledPct <= 0f)
                {
                    return currentPos;
                }

                // Find the next neighbor we'd move to (same logic as Tick)
                int targetNeighbor = Tile;
                int closestDist = Find.WorldGrid.TraversalDistanceBetween(Tile, nextWaypointTile);

                // Find neighbor tile closest to target (same approach as Tick method)
                for (int i = 0; i < Find.WorldGrid.TilesCount; i++)
                {
                    if (Find.WorldGrid.IsNeighbor(Tile, i))
                    {
                        int d = Find.WorldGrid.TraversalDistanceBetween(i, nextWaypointTile);
                        if (d < closestDist)
                        {
                            closestDist = d;
                            targetNeighbor = i;
                        }
                    }
                }

                if (targetNeighbor == Tile)
                {
                    return currentPos;
                }

                // Interpolate from current tile toward the neighbor we'll move to
                Vector3 targetPos = Find.WorldGrid.GetTileCenter(targetNeighbor);
                return Vector3.Lerp(currentPos, targetPos, traveledPct);
            }
        }

        private void RecalculateNextWaypoint()
        {
            float range = currentFuel;
            int directDist = Find.WorldGrid.TraversalDistanceBetween(Tile, destinationTile);

            // Can we reach destination directly?
            if (directDist <= range)
            {
                nextWaypointTile = destinationTile;
                return;
            }

            // Need a waypoint - find best PERCH or HOP along the route
            var waypoint = FindBestWaypoint(Tile, destinationTile, range);
            if (waypoint != null)
                nextWaypointTile = waypoint.Map.Tile;
            else
            {
                // No waypoint found - try to reach destination anyway (will run out of fuel)
                nextWaypointTile = destinationTile;
                Log.Warning($"[ARSENAL] SLING has no viable waypoint route to destination. May run out of fuel.");
            }
        }

        protected override void Tick()
        {
            base.Tick();

            recalculateCounter++;
            if (recalculateCounter >= RECALCULATE_INTERVAL)
            {
                recalculateCounter = 0;

                // Recalculate if heading to waypoint (not final destination)
                if (nextWaypointTile != destinationTile)
                {
                    // Check if waypoint is still valid
                    var perchAtWaypoint = ArsenalNetworkManager.GetPerchAtTile(nextWaypointTile);
                    var hopAtWaypoint = ArsenalNetworkManager.GetHopAtTile(nextWaypointTile);

                    if (perchAtWaypoint == null && hopAtWaypoint == null)
                    {
                        // Waypoint lost - find alternate
                        var alternate = FindBestWaypointExcludingTile(Tile, destinationTile, currentFuel, nextWaypointTile);
                        if (alternate != null)
                        {
                            nextWaypointTile = alternate.Map.Tile;
                            Messages.Message($"SLING rerouting to {alternate.Label}", this, MessageTypeDefOf.NeutralEvent);
                        }
                    }
                }
            }

            if (nextWaypointTile < 0)
            {
                ArriveAtDestination();
                return;
            }

            int dist = Find.WorldGrid.TraversalDistanceBetween(Tile, nextWaypointTile);

            if (dist <= 1)
            {
                // Consume fuel for this leg
                int legDistance = Find.WorldGrid.TraversalDistanceBetween(Tile, nextWaypointTile);
                currentFuel -= legDistance;

                previousTile = Tile;
                Tile = nextWaypointTile;

                // Check if we've reached final destination
                if (Tile == destinationTile)
                {
                    ArriveAtDestination();
                    return;
                }

                // Check for refueling at waypoint
                var perchHere = ArsenalNetworkManager.GetPerchAtTile(Tile);
                var hopHere = ArsenalNetworkManager.GetHopAtTile(Tile);

                if (perchHere != null && perchHere.HasFuel && perchHere.HasAvailableSlot)
                {
                    // Refuel at PERCH waypoint
                    LandForRefueling(perchHere);
                    return;
                }
                else if (hopHere != null && hopHere.CanAcceptMissile())
                {
                    // Refuel at HOP waypoint
                    LandForRefuelingAtHop(hopHere);
                    return;
                }

                // Continue to next waypoint
                RecalculateNextWaypoint();
            }
            else
            {
                // Move toward target
                traveledPct += SPEED;
                if (traveledPct >= 1f)
                {
                    traveledPct = 0f;
                    int closest = Tile;
                    int closestDist = dist;

                    // Find neighbor tile closest to target
                    for (int i = 0; i < Find.WorldGrid.TilesCount; i++)
                    {
                        if (Find.WorldGrid.IsNeighbor(Tile, i))
                        {
                            int d = Find.WorldGrid.TraversalDistanceBetween(i, nextWaypointTile);
                            if (d < closestDist)
                            {
                                closestDist = d;
                                closest = i;
                            }
                        }
                    }

                    // Consume fuel for movement
                    currentFuel -= 1f;
                    previousTile = Tile;
                    Tile = closest;

                    // Check if out of fuel
                    if (currentFuel <= 0)
                    {
                        HandleOutOfFuel();
                    }
                }
            }
        }

        private void LandForRefueling(Building_PERCH perch)
        {
            // Spawn landing skyfaller at an available slot position
            // Waypoint stops prefer slot 2 (incoming)
            IntVec3 landingPos = perch.Slot2Available ? perch.GetSlot2Position() : perch.GetSlot1Position();

            var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLanding);
            skyfaller.sling = sling;
            skyfaller.slingName = slingName;
            skyfaller.cargo = cargo;
            skyfaller.originPerch = originPerch;
            skyfaller.destinationPerch = destinationPerch;
            skyfaller.finalDestinationTile = destinationTile;
            skyfaller.isWaypointStop = true;
            skyfaller.isReturnFlight = isReturnFlight;

            GenSpawn.Spawn(skyfaller, landingPos, perch.Map);
            Destroy();
        }

        private void LandForRefuelingAtHop(Building_Hop hop)
        {
            // Use HOP's existing refueling infrastructure
            // SLING will be treated similarly to a missile for refueling purposes
            Messages.Message($"{slingName ?? "SLING"} landing at {hop.Label} for refueling", hop, MessageTypeDefOf.NeutralEvent);

            // Spawn landing skyfaller
            var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLanding);
            skyfaller.sling = sling;
            skyfaller.slingName = slingName;
            skyfaller.cargo = cargo;
            skyfaller.originPerch = originPerch;
            skyfaller.destinationPerch = destinationPerch;
            skyfaller.finalDestinationTile = destinationTile;
            skyfaller.isWaypointStop = true;
            skyfaller.targetHop = hop;
            skyfaller.isReturnFlight = isReturnFlight;

            GenSpawn.Spawn(skyfaller, hop.Position, hop.Map);
            Destroy();
        }

        private void ArriveAtDestination()
        {
            // Try beacon zone landing first (new system)
            if (destinationBeacon != null && destinationBeacon.Map != null && !destinationBeacon.Destroyed)
            {
                if (destinationBeacon.HasValidLandingZone && destinationBeacon.HasSpaceForSling)
                {
                    // Find landing spot within beacon zone
                    IntVec3 landingSpot = destinationBeacon.FindLandingSpot();
                    if (landingSpot.IsValid)
                    {
                        var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                            ArsenalDefOf.Arsenal_SlingLanding);
                        skyfaller.sling = sling;
                        skyfaller.slingName = slingName;
                        skyfaller.cargo = cargo;
                        skyfaller.destinationBeacon = destinationBeacon;
                        skyfaller.destinationTile = destinationTile;
                        skyfaller.isWaypointStop = false;
                        skyfaller.isReturnFlight = isReturnFlight;

                        GenSpawn.Spawn(skyfaller, landingSpot, destinationBeacon.Map);
                        Destroy();
                        return;
                    }
                }

                // Beacon zone full or invalid - try to find another beacon zone on same tile
                var alternateBeacon = FindAvailableBeaconZoneOnTile(destinationTile);
                if (alternateBeacon != null && alternateBeacon != destinationBeacon)
                {
                    IntVec3 landingSpot = alternateBeacon.FindLandingSpot();
                    if (landingSpot.IsValid)
                    {
                        Messages.Message($"{slingName ?? "SLING"} rerouted to {alternateBeacon.ZoneName} (original zone full)",
                            alternateBeacon, MessageTypeDefOf.NeutralEvent);

                        var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                            ArsenalDefOf.Arsenal_SlingLanding);
                        skyfaller.sling = sling;
                        skyfaller.slingName = slingName;
                        skyfaller.cargo = cargo;
                        skyfaller.destinationBeacon = alternateBeacon;
                        skyfaller.destinationTile = destinationTile;
                        skyfaller.isWaypointStop = false;
                        skyfaller.isReturnFlight = isReturnFlight;

                        GenSpawn.Spawn(skyfaller, landingSpot, alternateBeacon.Map);
                        Destroy();
                        return;
                    }
                }
            }

            // Fall back to legacy PERCH system
            Building_PERCH landingPerch = null;

            if (destinationPerch != null && destinationPerch.Map != null && !destinationPerch.Destroyed)
            {
                if (destinationPerch.HasAvailableSlot)
                {
                    // Destination has an available slot
                    landingPerch = destinationPerch;
                }
                else
                {
                    // Destination full - find alternate PERCH on same tile with available slot
                    landingPerch = FindAvailablePerchOnTile(destinationTile);
                    if (landingPerch != null && landingPerch != destinationPerch)
                    {
                        Messages.Message($"{slingName ?? "SLING"} rerouted to {landingPerch.Label} (original pad full)",
                            landingPerch, MessageTypeDefOf.NeutralEvent);
                    }
                }
            }

            // If no landing perch found on destination tile, search elsewhere
            if (landingPerch == null)
            {
                landingPerch = FindAvailablePerchOnTile(destinationTile);
            }

            if (landingPerch != null)
            {
                // Land at the available PERCH - get the appropriate slot position
                // Incoming with cargo goes to slot 2, return flights go to slot 1
                bool hasCargoToUnload = cargo != null && cargo.Count > 0;
                IntVec3 landingPos = landingPerch.GetAvailableSlotPosition();
                if (hasCargoToUnload && landingPerch.Slot2Available)
                {
                    landingPos = landingPerch.GetSlot2Position();
                }
                else if (!hasCargoToUnload && landingPerch.Slot1Available)
                {
                    landingPos = landingPerch.GetSlot1Position();
                }

                var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                    ArsenalDefOf.Arsenal_SlingLanding);
                skyfaller.sling = sling;
                skyfaller.slingName = slingName;
                skyfaller.cargo = cargo;
                skyfaller.originPerch = originPerch;
                skyfaller.destinationPerch = landingPerch;
                skyfaller.isWaypointStop = false;
                skyfaller.isReturnFlight = isReturnFlight;

                GenSpawn.Spawn(skyfaller, landingPos, landingPerch.Map);
            }
            else if (originPerch != null && !originPerch.Destroyed && originPerch.Map != null && originPerch.HasAvailableSlot)
            {
                // No available pad at destination - return to origin
                Messages.Message($"{slingName ?? "SLING"} returning to {originPerch.Label} - no available pad at destination",
                    MessageTypeDefOf.NeutralEvent);

                // Reverse the journey
                destinationPerch = originPerch;
                originPerch = null;
                destinationBeacon = null; // Clear beacon destination
                destinationTile = destinationPerch.Map.Tile;
                isReturnFlight = true;
                cargo = new Dictionary<ThingDef, int>(); // Empty cargo on return
                CalculateRoute();
                return; // Don't destroy - continue traveling
            }
            else
            {
                // No alternate found and can't return - crash land
                MapParent mp = Find.WorldObjects.MapParentAt(destinationTile);
                if (mp?.Map != null)
                {
                    IntVec3 dropSpot = DropCellFinder.RandomDropSpot(mp.Map);
                    var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                        ArsenalDefOf.Arsenal_SlingLanding);
                    skyfaller.sling = sling;
                    skyfaller.slingName = slingName;
                    skyfaller.cargo = cargo;
                    skyfaller.isCrashLanding = true;

                    GenSpawn.Spawn(skyfaller, dropSpot, mp.Map);

                    Messages.Message($"{slingName ?? "SLING"} crash-landing - no landing zone available at destination",
                        new TargetInfo(dropSpot, mp.Map), MessageTypeDefOf.NegativeEvent);
                }
            }

            Destroy();
        }

        private void HandleOutOfFuel()
        {
            // SLING ran out of fuel mid-flight
            MapParent mp = Find.WorldObjects.MapParentAt(Tile);
            if (mp?.Map != null)
            {
                IntVec3 dropSpot = DropCellFinder.RandomDropSpot(mp.Map);
                var skyfaller = (SlingLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                    ArsenalDefOf.Arsenal_SlingLanding);
                skyfaller.sling = sling;
                skyfaller.slingName = slingName;
                skyfaller.cargo = cargo;
                skyfaller.isCrashLanding = true;

                GenSpawn.Spawn(skyfaller, dropSpot, mp.Map);

                Messages.Message($"{slingName ?? "SLING"} ran out of fuel and crash-landed!",
                    new TargetInfo(dropSpot, mp.Map), MessageTypeDefOf.NegativeEvent);
            }
            else
            {
                // No map at this tile - SLING and cargo lost
                Messages.Message($"{slingName ?? "SLING"} ran out of fuel over uninhabited territory. Craft and cargo lost.",
                    MessageTypeDefOf.NegativeEvent);
            }

            Destroy();
        }

        private Building FindBestWaypoint(int fromTile, int towardTile, float maxRange)
        {
            Building best = null;
            int bestScore = int.MaxValue;

            // Check PERCHes
            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                if (perch.Map == null) continue;
                if (!perch.HasFuel || !perch.HasAvailableSlot) continue;

                int perchTile = perch.Map.Tile;
                int distToPerch = Find.WorldGrid.TraversalDistanceBetween(fromTile, perchTile);

                if (distToPerch > maxRange) continue;

                int distFromPerch = Find.WorldGrid.TraversalDistanceBetween(perchTile, towardTile);

                if (distFromPerch < bestScore)
                {
                    bestScore = distFromPerch;
                    best = perch;
                }
            }

            // Check HOPs
            foreach (var hop in ArsenalNetworkManager.GetAllHops())
            {
                if (hop.Map == null) continue;
                if (!hop.CanAcceptMissile()) continue;

                int hopTile = hop.Map.Tile;
                int distToHop = Find.WorldGrid.TraversalDistanceBetween(fromTile, hopTile);

                if (distToHop > maxRange) continue;

                int distFromHop = Find.WorldGrid.TraversalDistanceBetween(hopTile, towardTile);

                if (distFromHop < bestScore)
                {
                    bestScore = distFromHop;
                    best = hop;
                }
            }

            return best;
        }

        private Building FindBestWaypointExcludingTile(int fromTile, int towardTile, float maxRange, int excludeTile)
        {
            Building best = null;
            int bestScore = int.MaxValue;

            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                if (perch.Map == null) continue;
                if (perch.Map.Tile == excludeTile) continue;
                if (!perch.HasFuel || !perch.HasAvailableSlot) continue;

                int perchTile = perch.Map.Tile;
                int distToPerch = Find.WorldGrid.TraversalDistanceBetween(fromTile, perchTile);

                if (distToPerch > maxRange) continue;

                int distFromPerch = Find.WorldGrid.TraversalDistanceBetween(perchTile, towardTile);

                if (distFromPerch < bestScore)
                {
                    bestScore = distFromPerch;
                    best = perch;
                }
            }

            foreach (var hop in ArsenalNetworkManager.GetAllHops())
            {
                if (hop.Map == null) continue;
                if (hop.Map.Tile == excludeTile) continue;
                if (!hop.CanAcceptMissile()) continue;

                int hopTile = hop.Map.Tile;
                int distToHop = Find.WorldGrid.TraversalDistanceBetween(fromTile, hopTile);

                if (distToHop > maxRange) continue;

                int distFromHop = Find.WorldGrid.TraversalDistanceBetween(hopTile, towardTile);

                if (distFromHop < bestScore)
                {
                    bestScore = distFromHop;
                    best = hop;
                }
            }

            return best;
        }

        private Building_PERCH FindAvailablePerchOnTile(int tile)
        {
            // Try to find a PERCH on the specified tile with an available slot
            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                if (perch.Map == null || perch.Destroyed) continue;
                if (perch.Map.Tile != tile) continue;
                if (!perch.HasAvailableSlot) continue; // Skip full pads (both slots occupied)
                if (!perch.IsPoweredOn) continue;

                return perch;
            }

            // No available PERCH on the target tile
            return null;
        }

        private Building_PerchBeacon FindAvailableBeaconZoneOnTile(int tile)
        {
            // Try to find a beacon zone on the specified tile with space for a SLING
            foreach (var beacon in ArsenalNetworkManager.GetAllValidBeaconZones())
            {
                if (beacon.Map == null || beacon.Destroyed) continue;
                if (beacon.Map.Tile != tile) continue;
                if (!beacon.HasSpaceForSling) continue;
                if (!beacon.IsPoweredOn) continue;

                return beacon;
            }

            // No available beacon zone on the target tile
            return null;
        }

        private Building_PERCH FindNearestAvailablePerch(int nearTile)
        {
            Building_PERCH nearest = null;
            int nearestDist = int.MaxValue;

            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                if (perch.Map == null || perch.Destroyed) continue;
                if (!perch.HasAvailableSlot) continue; // Skip full pads (both slots occupied)
                if (!perch.IsPoweredOn) continue;

                int dist = Find.WorldGrid.TraversalDistanceBetween(nearTile, perch.Map.Tile);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = perch;
                }
            }

            return nearest;
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!string.IsNullOrEmpty(slingName))
                str += $"\n{slingName}";

            if (destinationBeacon != null)
                str += $"\nDestination: {destinationBeacon.ZoneName ?? "Beacon Zone"}";
            else if (destinationPerch != null)
                str += $"\nDestination: {destinationPerch.Label}";

            if (cargo != null && cargo.Count > 0)
            {
                str += "\nCargo: ";
                str += string.Join(", ", cargo.Select(c => $"{c.Key.label} x{c.Value}"));
            }
            else
            {
                str += "\nCargo: Empty (return flight)";
            }

            str += $"\nFuel: {currentFuel:F0} / {FUEL_CAPACITY:F0}";

            int remaining = Find.WorldGrid.TraversalDistanceBetween(Tile, destinationTile);
            str += $"\nRemaining: ~{remaining} tiles";

            return str;
        }
    }
}
