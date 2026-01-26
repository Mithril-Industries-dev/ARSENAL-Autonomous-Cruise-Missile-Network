using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Skyfaller for SLING landing at a PERCH or waypoint.
    /// </summary>
    public class SlingLandingSkyfaller : Skyfaller
    {
        public Thing sling;
        public string slingName;
        public Dictionary<ThingDef, int> cargo = new Dictionary<ThingDef, int>();
        public Building_PERCH originPerch;
        public Building_PERCH destinationPerch;
        public Building_Hop targetHop;
        public int finalDestinationTile = -1;
        public bool isWaypointStop = false;
        public bool isCrashLanding = false;
        public bool isReturnFlight = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref sling, "sling");
            Scribe_Values.Look(ref slingName, "slingName");
            Scribe_Collections.Look(ref cargo, "cargo", LookMode.Def, LookMode.Value);
            Scribe_References.Look(ref originPerch, "originPerch");
            Scribe_References.Look(ref destinationPerch, "destinationPerch");
            Scribe_References.Look(ref targetHop, "targetHop");
            Scribe_Values.Look(ref finalDestinationTile, "finalDestinationTile", -1);
            Scribe_Values.Look(ref isWaypointStop, "isWaypointStop", false);
            Scribe_Values.Look(ref isCrashLanding, "isCrashLanding", false);
            Scribe_Values.Look(ref isReturnFlight, "isReturnFlight", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (cargo == null) cargo = new Dictionary<ThingDef, int>();
            }
        }

        protected override void Tick()
        {
            base.Tick();

            // Visual effects during landing - retro rockets firing
            if (Map != null)
            {
                // Frequent smoke trail
                if (this.IsHashIntervalTick(4))
                {
                    Vector3 smokePos = DrawPos + new Vector3(Rand.Range(-1.5f, 1.5f), 0, Rand.Range(-1f, 1f));
                    FleckMaker.ThrowSmoke(smokePos, Map, 2.5f);
                }

                // Engine fire glow
                if (this.IsHashIntervalTick(3))
                {
                    Vector3 firePos = DrawPos + new Vector3(Rand.Range(-0.8f, 0.8f), 0, -1f);
                    FleckMaker.ThrowFireGlow(firePos, Map, 1.5f);
                }

                // Occasional sparks from retro thrusters
                if (this.IsHashIntervalTick(8))
                {
                    FleckMaker.ThrowMicroSparks(DrawPos, Map);
                }

                // Heat shimmer effect
                if (this.IsHashIntervalTick(6))
                {
                    FleckMaker.ThrowHeatGlow(Position, Map, 2f);
                }
            }
        }

        protected override void Impact()
        {
            if (isCrashLanding)
            {
                HandleCrashLanding();
                return;
            }

            if (targetHop != null)
            {
                // Landing at HOP waypoint for refueling
                HandleHopWaypointLanding();
            }
            else if (isWaypointStop && destinationPerch != null)
            {
                // Landing at PERCH waypoint for refueling
                HandlePerchWaypointLanding();
            }
            else if (destinationPerch != null)
            {
                // Final destination landing
                HandleDestinationLanding();
            }
            else
            {
                // Fallback - drop cargo and SLING on ground
                HandleEmergencyLanding();
            }

            base.Impact();
        }

        private void HandleCrashLanding()
        {
            // SLING crashes, cargo scattered
            if (sling != null)
            {
                // SLING is damaged/destroyed in crash
                Messages.Message("SLING crashed! Craft destroyed.",
                    new TargetInfo(Position, Map), MessageTypeDefOf.NegativeEvent);
            }

            // Scatter some cargo (50% survival rate)
            if (cargo != null)
            {
                foreach (var kvp in cargo)
                {
                    int surviving = Mathf.FloorToInt(kvp.Value * 0.5f);
                    if (surviving > 0)
                    {
                        Thing dropped = ThingMaker.MakeThing(kvp.Key);
                        dropped.stackCount = surviving;
                        GenSpawn.Spawn(dropped, Position, Map);
                    }
                }
            }

            // Explosion effect
            GenExplosion.DoExplosion(
                center: Position,
                map: Map,
                radius: 2f,
                damType: DamageDefOf.Bomb,
                instigator: null,
                damAmount: 10,
                armorPenetration: -1f,
                explosionSound: null,
                weapon: null,
                projectile: null,
                intendedTarget: null,
                postExplosionSpawnThingDef: null,
                postExplosionSpawnChance: 0f,
                postExplosionSpawnThingCount: 0,
                postExplosionGasType: null,
                applyDamageToExplosionCellsNeighbors: false);
        }

        private void HandleHopWaypointLanding()
        {
            if (targetHop == null || !targetHop.Spawned)
            {
                HandleEmergencyLanding();
                return;
            }

            // Spawn SLING on HOP pad
            if (sling != null && !sling.Spawned)
            {
                GenSpawn.Spawn(sling, targetHop.Position, Map);
                sling.SetForbidden(true, false);
            }

            Messages.Message($"SLING landed at {targetHop.Label} for refueling",
                targetHop, MessageTypeDefOf.NeutralEvent);

            // Queue refueling and continuation
            // For now, use a simple delay before continuing
            // In full implementation, HOP would handle refueling cycle
            LongEventHandler.QueueLongEvent(() =>
            {
                if (sling != null && sling.Spawned)
                {
                    sling.DeSpawn(DestroyMode.Vanish);
                }
                SlingLogisticsManager.ContinueJourneyAfterRefuel(
                    sling, cargo, originPerch, destinationPerch,
                    targetHop.Map.Tile, isReturnFlight);
            }, "RefuelingSling", false, null);
        }

        private void HandlePerchWaypointLanding()
        {
            var localPerch = ArsenalNetworkManager.GetPerchAtTile(Map.Tile);
            if (localPerch == null)
            {
                HandleEmergencyLanding();
                return;
            }

            // Assign name before spawning
            if (sling is SLING_Thing slingThing && !string.IsNullOrEmpty(slingName))
            {
                slingThing.AssignName(slingName);
            }

            // Spawn SLING on PERCH pad
            if (sling != null && !sling.Spawned)
            {
                GenSpawn.Spawn(sling, localPerch.Position, Map);
                sling.SetForbidden(true, false);
            }

            Messages.Message($"{slingName ?? "SLING"} landed at {localPerch.Label} for refueling",
                localPerch, MessageTypeDefOf.NeutralEvent);

            // Start refueling process
            localPerch.AssignSling(sling);
            localPerch.StartRefuelingSling();

            // After refueling completes, continue journey
            // This would be handled by PERCH tick in full implementation
        }

        private void HandleDestinationLanding()
        {
            if (destinationPerch == null || !destinationPerch.Spawned)
            {
                HandleEmergencyLanding();
                return;
            }

            // IMPORTANT: Assign name BEFORE spawning to prevent SpawnSetup from assigning a new one
            if (sling is SLING_Thing slingThing)
            {
                if (!string.IsNullOrEmpty(slingName))
                {
                    slingThing.AssignName(slingName);
                }

                // Load cargo manifest into container (for physical unloading)
                if (cargo != null && cargo.Count > 0 && slingThing.CurrentCargoCount == 0)
                {
                    slingThing.LoadCargoFromManifest(cargo);
                }
            }

            // Spawn SLING on pad BEFORE calling ReceiveSling
            if (sling != null && !sling.Spawned)
            {
                GenSpawn.Spawn(sling, destinationPerch.Position, Map);
                sling.SetForbidden(true, false);
            }

            // Determine if this SLING needs to return after unloading
            Building_PERCH returnOrigin = null;
            if (!isReturnFlight && originPerch != null && originPerch != destinationPerch)
            {
                returnOrigin = originPerch;
            }

            // Deliver cargo to destination - PERCH will handle return after unloading
            destinationPerch.ReceiveSling(sling, cargo, returnOrigin, slingName);
        }

        private void HandleEmergencyLanding()
        {
            // Assign name before spawning
            if (sling is SLING_Thing slingThing && !string.IsNullOrEmpty(slingName))
            {
                slingThing.AssignName(slingName);
            }

            // Emergency landing - drop everything at current position
            if (sling != null && !sling.Spawned)
            {
                GenSpawn.Spawn(sling, Position, Map);
                sling.SetForbidden(false, false);
            }

            if (cargo != null)
            {
                foreach (var kvp in cargo)
                {
                    int remaining = kvp.Value;
                    while (remaining > 0)
                    {
                        int spawnCount = Mathf.Min(remaining, kvp.Key.stackLimit);
                        Thing item = ThingMaker.MakeThing(kvp.Key);
                        item.stackCount = spawnCount;

                        IntVec3 spawnCell = Position + GenRadial.RadialPattern[
                            Rand.Range(0, Mathf.Min(9, GenRadial.RadialPattern.Length))];
                        if (spawnCell.InBounds(Map) && spawnCell.Standable(Map))
                        {
                            GenSpawn.Spawn(item, spawnCell, Map);
                        }
                        else
                        {
                            GenSpawn.Spawn(item, Position, Map);
                        }
                        remaining -= spawnCount;
                    }
                }
            }

            Messages.Message("SLING made emergency landing - cargo dropped",
                new TargetInfo(Position, Map), MessageTypeDefOf.NeutralEvent);
        }
    }

    /// <summary>
    /// Skyfaller for SLING launching from a PERCH.
    /// Creates the traveling world object when leaving the map.
    /// </summary>
    public class SlingLaunchingSkyfaller : Skyfaller
    {
        public Thing sling;
        public string slingName;
        public Dictionary<ThingDef, int> cargo = new Dictionary<ThingDef, int>();
        public Building_PERCH originPerch;
        public Building_PERCH destinationPerch;
        public int destinationTile = -1;
        public bool isReturnFlight = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref sling, "sling");
            Scribe_Values.Look(ref slingName, "slingName");
            Scribe_Collections.Look(ref cargo, "cargo", LookMode.Def, LookMode.Value);
            Scribe_References.Look(ref originPerch, "originPerch");
            Scribe_References.Look(ref destinationPerch, "destinationPerch");
            Scribe_Values.Look(ref destinationTile, "destinationTile", -1);
            Scribe_Values.Look(ref isReturnFlight, "isReturnFlight", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (cargo == null) cargo = new Dictionary<ThingDef, int>();
            }
        }

        protected override void Tick()
        {
            base.Tick();

            // Visual effects during takeoff - main engine thrust
            if (Map != null)
            {
                // Heavy smoke trail during liftoff
                if (this.IsHashIntervalTick(3))
                {
                    Vector3 smokePos = DrawPos + new Vector3(Rand.Range(-2f, 2f), 0, Rand.Range(-1.5f, 0.5f));
                    FleckMaker.ThrowSmoke(smokePos, Map, 3f);
                }

                // Intense engine fire
                if (this.IsHashIntervalTick(2))
                {
                    Vector3 firePos = DrawPos + new Vector3(Rand.Range(-1f, 1f), 0, -0.5f);
                    FleckMaker.ThrowFireGlow(firePos, Map, 2f);
                }

                // Additional fire glow spread
                if (this.IsHashIntervalTick(4))
                {
                    Vector3 glowPos = DrawPos + new Vector3(Rand.Range(-1.5f, 1.5f), 0, Rand.Range(-1f, 0f));
                    FleckMaker.ThrowFireGlow(glowPos, Map, 1f);
                }

                // Sparks from exhaust
                if (this.IsHashIntervalTick(5))
                {
                    FleckMaker.ThrowMicroSparks(DrawPos + new Vector3(0, 0, -0.5f), Map);
                }

                // Ground dust kicked up
                if (this.IsHashIntervalTick(8))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        IntVec3 dustCell = Position + GenRadial.RadialPattern[Rand.Range(1, 9)];
                        if (dustCell.InBounds(Map))
                        {
                            FleckMaker.ThrowDustPuff(dustCell.ToVector3Shifted(), Map, 1.5f);
                        }
                    }
                }

                // Heat shimmer
                if (this.IsHashIntervalTick(6))
                {
                    FleckMaker.ThrowHeatGlow(Position, Map, 2.5f);
                }
            }
        }

        protected override void LeaveMap()
        {
            // Create the traveling world object
            if (destinationPerch != null && destinationTile >= 0)
            {
                var traveling = (WorldObject_TravelingSling)WorldObjectMaker.MakeWorldObject(
                    ArsenalDefOf.Arsenal_TravelingSling);
                traveling.Tile = Map.Tile;
                traveling.destinationTile = destinationTile;
                traveling.sling = sling;
                traveling.slingName = slingName;
                traveling.cargo = cargo ?? new Dictionary<ThingDef, int>();
                traveling.originPerch = originPerch;
                traveling.destinationPerch = destinationPerch;
                traveling.isReturnFlight = isReturnFlight;
                traveling.CalculateRoute();
                Find.WorldObjects.Add(traveling);
            }
            else if (sling != null)
            {
                // Fallback: drop SLING if no valid destination
                GenSpawn.Spawn(sling, Position, Map);
                Messages.Message("SLING launch aborted - no valid destination",
                    new TargetInfo(Position, Map), MessageTypeDefOf.NegativeEvent);
            }

            base.LeaveMap();
        }
    }
}
