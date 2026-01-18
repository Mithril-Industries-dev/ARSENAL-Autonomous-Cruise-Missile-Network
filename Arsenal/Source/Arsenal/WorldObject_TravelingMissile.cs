using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    public class WorldObject_TravelingMissile : WorldObject
    {
        private const float SPEED = 0.02f;
        private const float FUEL_CAPACITY = 100f;
        
        public int destinationTile = -1;
        public Thing missile;
        public Building_Hub destinationHub;
        private int nextHopTile = -1;
        private float traveledPct = 0f;
        private int recalculateCounter = 0;
        private const int RECALCULATE_INTERVAL = 60;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref destinationTile, "destinationTile", -1);
            Scribe_Deep.Look(ref missile, "missile");
            Scribe_References.Look(ref destinationHub, "destinationHub");
            Scribe_Values.Look(ref nextHopTile, "nextHopTile", -1);
            Scribe_Values.Look(ref traveledPct, "traveledPct", 0f);
        }

        public void CalculateRoute()
        {
            RecalculateNextHop();
        }

        private void RecalculateNextHop()
        {
            CompMissileFuel fc = missile?.TryGetComp<CompMissileFuel>();
            float fuel = fc?.Fuel ?? FUEL_CAPACITY;
            float range = Mathf.Min(fuel, FUEL_CAPACITY);

            int directDist = Find.WorldGrid.TraversalDistanceBetween(Tile, destinationTile);
            
            if (directDist <= range)
            {
                nextHopTile = destinationTile;
                return;
            }

            Building_Hop bestHop = FindBestAvailableHop(Tile, destinationTile, range);
            
            if (bestHop != null)
                nextHopTile = bestHop.Map.Tile;
            else
                nextHopTile = destinationTile;
        }

        protected override void Tick()
        {
            base.Tick();

            recalculateCounter++;
            if (recalculateCounter >= RECALCULATE_INTERVAL)
            {
                recalculateCounter = 0;
                
                // Only recalculate if heading to a HOP tile (not final destination)
                if (nextHopTile != destinationTile)
                {
                    // Check if ANY HOP at target tile is available
                    Building_Hop availableHop = ArsenalNetworkManager.GetAvailableHopAtTile(nextHopTile);
                    
                    if (availableHop == null)
                    {
                        // No available HOP at target tile - find alternate at different tile
                        Building_Hop alternateHop = FindBestAvailableHopExcludingTile(Tile, destinationTile, FUEL_CAPACITY, nextHopTile);
                        if (alternateHop != null)
                        {
                            nextHopTile = alternateHop.Map.Tile;
                            Messages.Message("Missile rerouting to " + alternateHop.Label, this, MessageTypeDefOf.NeutralEvent);
                        }
                    }
                }
            }
            
            if (nextHopTile < 0)
            {
                ArriveAtDestination();
                return;
            }

            int dist = Find.WorldGrid.TraversalDistanceBetween(Tile, nextHopTile);
            
            if (dist <= 1)
            {
                Tile = nextHopTile;
                
                // Check if there are HOPs at this tile
                List<Building_Hop> hopsHere = ArsenalNetworkManager.GetAllHopsAtTile(Tile);
                if (hopsHere.Count > 0)
                {
                    // Find an available HOP at this tile
                    Building_Hop availableHop = hopsHere.FirstOrDefault(h => h.CanAcceptMissile());
                    
                    if (availableHop != null)
                    {
                        SpawnLandingAtHop(availableHop);
                        Destroy();
                        return;
                    }
                    else
                    {
                        // All HOPs at this tile are busy - find alternate at DIFFERENT tile
                        Building_Hop alternateHop = FindBestAvailableHopExcludingTile(Tile, destinationTile, FUEL_CAPACITY, Tile);
                        if (alternateHop != null)
                        {
                            nextHopTile = alternateHop.Map.Tile;
                            Messages.Message("All HOPs busy here, rerouting to " + alternateHop.Label, this, MessageTypeDefOf.NeutralEvent);
                            return;
                        }
                        else
                        {
                            // No alternate HOP anywhere - can we reach destination directly?
                            int directDist = Find.WorldGrid.TraversalDistanceBetween(Tile, destinationTile);
                            if (directDist <= FUEL_CAPACITY)
                            {
                                nextHopTile = destinationTile;
                                Messages.Message("No HOPs available, proceeding direct to destination", this, MessageTypeDefOf.NeutralEvent);
                                return;
                            }
                            else
                            {
                                // Stranded - wait here and keep checking
                                // Don't spam messages - only message once per 5 seconds
                                if (recalculateCounter == 0)
                                {
                                    Messages.Message("Missile waiting for HOP availability...", this, MessageTypeDefOf.NeutralEvent);
                                }
                                return;
                            }
                        }
                    }
                }

                // Reached final destination
                if (Tile == destinationTile)
                    ArriveAtDestination();
                else
                    RecalculateNextHop();
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
                    for (int i = 0; i < Find.WorldGrid.TilesCount; i++)
                    {
                        if (Find.WorldGrid.IsNeighbor(Tile, i))
                        {
                            int d = Find.WorldGrid.TraversalDistanceBetween(i, nextHopTile);
                            if (d < closestDist)
                            {
                                closestDist = d;
                                closest = i;
                            }
                        }
                    }
                    Tile = closest;
                }
            }
        }

        private void SpawnLandingAtHop(Building_Hop hop)
        {
            MissileLandingSkyfaller skyfaller = (MissileLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_MissileLanding);
            skyfaller.missile = missile;
            skyfaller.targetHop = hop;
            skyfaller.destinationTile = destinationTile;
            skyfaller.finalDestinationHub = destinationHub;
            
            GenSpawn.Spawn(skyfaller, hop.Position, hop.Map);
        }

        private void ArriveAtDestination()
        {
            Building_Hub hub = destinationHub;
            if (hub == null)
                hub = ArsenalNetworkManager.GetHubAtTile(destinationTile);
            
            if (hub != null && hub.Map != null && hub.CanStoreMissile())
            {
                MissileLandingSkyfaller skyfaller = (MissileLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                    ArsenalDefOf.Arsenal_MissileLanding);
                skyfaller.missile = missile;
                skyfaller.targetHub = hub;
                
                GenSpawn.Spawn(skyfaller, hub.Position, hub.Map);
            }
            else if (missile != null)
            {
                MapParent mp = Find.WorldObjects.MapParentAt(destinationTile);
                if (mp?.Map != null)
                {
                    IntVec3 dropSpot = DropCellFinder.RandomDropSpot(mp.Map);
                    
                    MissileLandingSkyfaller skyfaller = (MissileLandingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                        ArsenalDefOf.Arsenal_MissileLanding);
                    skyfaller.missile = missile;
                    
                    GenSpawn.Spawn(skyfaller, dropSpot, mp.Map);
                    
                    Messages.Message("Missile landed but no HUB available for storage.", 
                        new TargetInfo(dropSpot, mp.Map), MessageTypeDefOf.NeutralEvent);
                }
            }
            Destroy();
        }

        private Building_Hop FindBestAvailableHop(int fromTile, int towardTile, float maxRange)
        {
            List<Building_Hop> allHops = ArsenalNetworkManager.GetAllHops();
            
            Building_Hop bestHop = null;
            int bestScore = int.MaxValue;

            foreach (var hop in allHops)
            {
                if (hop.Map == null) continue;
                if (!hop.CanAcceptMissile()) continue;
                if (hop.GetAvailableFuel() < 50f) continue;
                
                int hopTile = hop.Map.Tile;
                int distToHop = Find.WorldGrid.TraversalDistanceBetween(fromTile, hopTile);
                
                if (distToHop > maxRange) continue;
                
                int distFromHop = Find.WorldGrid.TraversalDistanceBetween(hopTile, towardTile);
                
                if (distFromHop < bestScore)
                {
                    bestScore = distFromHop;
                    bestHop = hop;
                }
            }

            return bestHop;
        }

        // Find best available HOP excluding a specific tile (to avoid routing loops)
        private Building_Hop FindBestAvailableHopExcludingTile(int fromTile, int towardTile, float maxRange, int excludeTile)
        {
            List<Building_Hop> allHops = ArsenalNetworkManager.GetAllHops();
            
            Building_Hop bestHop = null;
            int bestScore = int.MaxValue;

            foreach (var hop in allHops)
            {
                if (hop.Map == null) continue;
                if (!hop.CanAcceptMissile()) continue;
                if (hop.GetAvailableFuel() < 50f) continue;
                
                int hopTile = hop.Map.Tile;
                
                // Skip HOPs at the excluded tile
                if (hopTile == excludeTile) continue;
                
                int distToHop = Find.WorldGrid.TraversalDistanceBetween(fromTile, hopTile);
                
                if (distToHop > maxRange) continue;
                
                int distFromHop = Find.WorldGrid.TraversalDistanceBetween(hopTile, towardTile);
                
                if (distFromHop < bestScore)
                {
                    bestScore = distFromHop;
                    bestHop = hop;
                }
            }

            return bestHop;
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();
            if (destinationHub != null)
                str += "\nDestination: " + destinationHub.Label;
            
            List<Building_Hop> hopsAtTarget = ArsenalNetworkManager.GetAllHopsAtTile(nextHopTile);
            if (hopsAtTarget.Count > 0)
            {
                int available = hopsAtTarget.Count(h => h.CanAcceptMissile());
                str += "\nNext stop: " + hopsAtTarget.Count + " HOPs (" + available + " available)";
            }
            else if (nextHopTile == destinationTile)
            {
                str += "\nRouting: DIRECT to destination";
            }
            
            int remaining = Find.WorldGrid.TraversalDistanceBetween(Tile, destinationTile);
            str += "\nRemaining: ~" + remaining + " tiles";
            
            return str;
        }
    }
}