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
                Position, Map, 2f,
                DamageDefOf.Bomb, null,
                10, -1f, null, null, null, null, null, 0f, 1, null, false, null, 0f, 1, 0f, false, null, null);
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

            // Spawn SLING on PERCH pad
            if (sling != null && !sling.Spawned)
            {
                GenSpawn.Spawn(sling, localPerch.Position, Map);
                sling.SetForbidden(true, false);
            }

            Messages.Message($"SLING landed at {localPerch.Label} for refueling",
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

            // Deliver cargo to destination
            destinationPerch.ReceiveSling(sling, cargo);

            // Spawn SLING on pad
            if (sling != null && !sling.Spawned)
            {
                GenSpawn.Spawn(sling, destinationPerch.Position, Map);
                sling.SetForbidden(true, false);
            }

            // If this is a delivery (not return), initiate return flight after unloading
            if (!isReturnFlight && originPerch != null && originPerch != destinationPerch)
            {
                // Queue return flight after unloading delay
                var origin = originPerch;
                var dest = destinationPerch;
                var s = sling;

                LongEventHandler.QueueLongEvent(() =>
                {
                    // Wait for unloading to complete then return
                    if (dest != null && !dest.IsBusy && s != null)
                    {
                        SlingLogisticsManager.InitiateReturnFlight(s, dest, origin);
                    }
                }, "SlingReturn", false, null);
            }
        }

        private void HandleEmergencyLanding()
        {
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
    /// </summary>
    public class SlingLaunchingSkyfaller : Skyfaller
    {
        public WorldObject_TravelingSling travelingSling;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref travelingSling, "travelingSling");
        }

        protected override void LeaveMap()
        {
            if (travelingSling != null)
            {
                Find.WorldObjects.Add(travelingSling);
            }
            base.LeaveMap();
        }
    }
}
