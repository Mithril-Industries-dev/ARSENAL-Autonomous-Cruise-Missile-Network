using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    public class MissileLaunchingSkyfaller : Skyfaller
    {
        public WorldObject_TravelingMissile travelingMissile;
        public WorldObject_MissileStrike missileStrike;

        private int initialTicks = -1;
        private Vector3 groundPos;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            groundPos = Position.ToVector3Shifted();
            initialTicks = ticksToImpact;
            
            // Initial launch effects - big burst at base
            if (map != null)
            {
                for (int i = 0; i < 12; i++)
                {
                    Vector3 offset = new Vector3(Rand.Range(-1.2f, 1.2f), 0, Rand.Range(-1.2f, 1.2f));
                    FleckMaker.ThrowSmoke(groundPos + offset, map, 3f);
                }
                for (int i = 0; i < 5; i++)
                {
                    FleckMaker.ThrowFireGlow(groundPos, map, 2.5f);
                }
                for (int i = 0; i < 4; i++)
                {
                    FleckMaker.ThrowMicroSparks(groundPos, map);
                }
            }
        }

        // Override DrawPos - missile goes UP (negative Z = toward top of screen in isometric view)
        public override Vector3 DrawPos
        {
            get
            {
                if (initialTicks <= 0) initialTicks = 120;
                
                // Progress from 0 (just spawned) to 1 (about to leave)
                float progress = 1f - ((float)ticksToImpact / (float)initialTicks);
                
                // NEGATIVE Z moves toward top-left of screen (up into sky visually)
                float zOffset = progress * -40f;
                
                // Y for draw order
                float yOffset = progress * 10f;
                
                return groundPos + new Vector3(0, yOffset, zOffset);
            }
        }

        protected override void Tick()
        {
            // Rocket exhaust effects - fire and smoke trailing below missile (positive Z = below in this case)
            if (Map != null && ticksToImpact % 3 == 0)
            {
                Vector3 currentPos = DrawPos;
                Vector3 exhaustPos = currentPos + new Vector3(0, -0.5f, 1f); // Below and behind (toward ground)
                
                FleckMaker.ThrowSmoke(exhaustPos, Map, 2f);
                
                if (ticksToImpact % 6 == 0)
                {
                    FleckMaker.ThrowFireGlow(exhaustPos, Map, 1.5f);
                }
                
                if (ticksToImpact % 9 == 0)
                {
                    FleckMaker.ThrowMicroSparks(exhaustPos, Map);
                }
            }
            
            base.Tick();
        }

        protected override void LeaveMap()
        {
            // Final smoke burst
            if (Map != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    FleckMaker.ThrowSmoke(DrawPos, Map, 2f);
                }
            }
            
            if (travelingMissile != null)
                Find.WorldObjects.Add(travelingMissile);
            if (missileStrike != null)
                Find.WorldObjects.Add(missileStrike);
            base.LeaveMap();
        }
    }

    public class MissileLandingSkyfaller : Skyfaller
    {
        public Thing missile;
        public Building_Hub targetHub;
        public Building_Hop targetHop;
        public int destinationTile = -1;
        public Building_Hub finalDestinationHub;

        private int initialTicks = -1;
        private Vector3 groundPos;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            groundPos = Position.ToVector3Shifted();
            initialTicks = ticksToImpact;
        }

        // Override DrawPos - missile comes DOWN from sky (starts at negative Z, ends at ground)
        public override Vector3 DrawPos
        {
            get
            {
                if (initialTicks <= 0) initialTicks = 120;
                
                // Progress from 0 (high up) to 1 (at ground)
                float progress = 1f - ((float)ticksToImpact / (float)initialTicks);
                
                // Start at -40 Z (top of screen), end at 0 (ground)
                float zOffset = (1f - progress) * -40f;
                
                // Y for draw order
                float yOffset = (1f - progress) * 10f;
                
                return groundPos + new Vector3(0, yOffset, zOffset);
            }
        }

        protected override void Tick()
        {
            // SpaceX-style retro-burn - fire pointing DOWN (exhaust toward ground = positive Z)
            if (Map != null && ticksToImpact % 2 == 0)
            {
                Vector3 currentPos = DrawPos;
                Vector3 exhaustPos = currentPos + new Vector3(0, -0.5f, 1.5f); // Exhaust toward ground
                
                // Intense smoke from retro burn - gets stronger as it lands
                float intensity = 1.5f + (1f - ((float)ticksToImpact / (float)initialTicks)) * 1.5f;
                FleckMaker.ThrowSmoke(exhaustPos, Map, intensity);
                
                if (ticksToImpact % 4 == 0)
                {
                    FleckMaker.ThrowFireGlow(exhaustPos, Map, 1.8f);
                }
                
                if (ticksToImpact % 6 == 0)
                {
                    FleckMaker.ThrowMicroSparks(exhaustPos, Map);
                }
            }
            
            base.Tick();
        }

        protected override void Impact()
        {
            // Landing dust cloud and final effects
            if (Map != null)
            {
                for (int i = 0; i < 15; i++)
                {
                    Vector3 offset = new Vector3(Rand.Range(-2f, 2f), 0, Rand.Range(-2f, 2f));
                    FleckMaker.ThrowSmoke(groundPos + offset, Map, 2.5f);
                }
                
                for (int i = 0; i < 4; i++)
                {
                    FleckMaker.ThrowFireGlow(groundPos, Map, 1.5f);
                }
                
                for (int i = 0; i < 8; i++)
                {
                    FleckMaker.ThrowMicroSparks(groundPos, Map);
                }
            }

            if (targetHub != null && targetHub.CanStoreMissile())
            {
                targetHub.StoreMissile(missile);
                Messages.Message("Cruise missile arrived at " + targetHub.Label, targetHub, MessageTypeDefOf.PositiveEvent);
            }
            else if (targetHop != null)
            {
                if (targetHop.CanAcceptMissile())
                {
                    targetHop.StartRefueling(missile, destinationTile, finalDestinationHub);
                }
                else
                {
                    Building_Hop alternateHopSameTile = ArsenalNetworkManager.GetAvailableHopAtTile(targetHop.Map.Tile);
                    
                    if (alternateHopSameTile != null)
                    {
                        alternateHopSameTile.StartRefueling(missile, destinationTile, finalDestinationHub);
                        Messages.Message("Using " + alternateHopSameTile.Label + " instead", alternateHopSameTile, MessageTypeDefOf.NeutralEvent);
                    }
                    else
                    {
                        Building_Hop alternateHop = FindAvailableHopExcludingTile(targetHop.Map.Tile, destinationTile, targetHop.Map.Tile);
                        
                        if (alternateHop != null)
                        {
                            WorldObject_TravelingMissile newTraveling = 
                                (WorldObject_TravelingMissile)WorldObjectMaker.MakeWorldObject(ArsenalDefOf.Arsenal_TravelingMissile);
                            
                            newTraveling.Tile = targetHop.Map.Tile;
                            newTraveling.destinationTile = destinationTile;
                            newTraveling.missile = missile;
                            newTraveling.destinationHub = finalDestinationHub;
                            newTraveling.CalculateRoute();

                            MissileLaunchingSkyfaller launchSkyfaller = (MissileLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                                ArsenalDefOf.Arsenal_MissileLaunching);
                            launchSkyfaller.travelingMissile = newTraveling;
                            
                            GenSpawn.Spawn(launchSkyfaller, Position, Map);
                            
                            Messages.Message("All HOPs busy here, rerouting to " + alternateHop.Label, targetHop, MessageTypeDefOf.NeutralEvent);
                        }
                        else
                        {
                            if (missile != null)
                                GenPlace.TryPlaceThing(missile, Position, Map, ThingPlaceMode.Near);
                            Messages.Message("All HOPs busy - missile grounded", targetHop, MessageTypeDefOf.NegativeEvent);
                        }
                    }
                }
            }
            else if (missile != null)
            {
                GenPlace.TryPlaceThing(missile, Position, Map, ThingPlaceMode.Near);
            }

            base.Impact();
        }

        private Building_Hop FindAvailableHopExcludingTile(int fromTile, int towardTile, int excludeTile)
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
                if (hopTile == excludeTile) continue;
                
                int distToHop = Find.WorldGrid.TraversalDistanceBetween(fromTile, hopTile);
                if (distToHop > 100f) continue;
                
                int distFromHop = Find.WorldGrid.TraversalDistanceBetween(hopTile, towardTile);
                
                if (distFromHop < bestScore)
                {
                    bestScore = distFromHop;
                    bestHop = hop;
                }
            }

            return bestHop;
        }
    }

    public class MissileStrikeSkyfaller : Skyfaller
    {
        public int explosionDamage = 333;
        public float explosionRadius = 16f;
        public string sourceHubLabel = "";

        private int initialTicks = -1;
        private Vector3 groundPos;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            groundPos = Position.ToVector3Shifted();
            initialTicks = ticksToImpact;
        }

        // Override DrawPos - missile comes DOWN FAST from sky
        public override Vector3 DrawPos
        {
            get
            {
                if (initialTicks <= 0) initialTicks = 80;
                
                // Progress from 0 (high up) to 1 (impact)
                float progress = 1f - ((float)ticksToImpact / (float)initialTicks);
                
                // Start at -60 Z (far at top of screen), end at ground
                float zOffset = (1f - progress) * -60f;
                
                // Y for draw order
                float yOffset = (1f - progress) * 15f;
                
                return groundPos + new Vector3(0, yOffset, zOffset);
            }
        }

        protected override void Tick()
        {
            // Incoming missile smoke trail (behind = positive Z since missile coming from negative Z)
            if (Map != null && ticksToImpact % 2 == 0)
            {
                Vector3 currentPos = DrawPos;
                Vector3 trailPos = currentPos + new Vector3(0, 0.5f, -1.5f); // Trail behind missile
                
                FleckMaker.ThrowSmoke(trailPos, Map, 1.8f);
                
                if (ticksToImpact % 4 == 0)
                {
                    FleckMaker.ThrowFireGlow(trailPos, Map, 1f);
                }
            }
            
            base.Tick();
        }

        protected override void Impact()
        {
            // Main explosion
            GenExplosion.DoExplosion(
                center: Position,
                map: Map,
                radius: explosionRadius,
                damType: DamageDefOf.Bomb,
                instigator: null,
                damAmount: explosionDamage,
                armorPenetration: -1f,
                explosionSound: null,
                weapon: null,
                projectile: null,
                intendedTarget: null,
                postExplosionSpawnThingDef: null,
                postExplosionSpawnChance: 0f,
                postExplosionSpawnThingCount: 0,
                postExplosionGasType: null,
                applyDamageToExplosionCellsNeighbors: true,
                preExplosionSpawnThingDef: null,
                preExplosionSpawnChance: 0f,
                preExplosionSpawnThingCount: 0,
                chanceToStartFire: 0.6f,
                damageFalloff: true
            );

            // Secondary explosions
            for (int i = 0; i < 4; i++)
            {
                IntVec3 secondaryCell = Position + GenRadial.RadialPattern[Rand.Range(1, 25)];
                if (secondaryCell.InBounds(Map))
                {
                    GenExplosion.DoExplosion(
                        center: secondaryCell,
                        map: Map,
                        radius: 5f,
                        damType: DamageDefOf.Flame,
                        instigator: null,
                        damAmount: 65,
                        armorPenetration: -1f,
                        explosionSound: null,
                        weapon: null,
                        projectile: null,
                        intendedTarget: null,
                        postExplosionSpawnThingDef: null,
                        postExplosionSpawnChance: 0f,
                        postExplosionSpawnThingCount: 0,
                        postExplosionGasType: null,
                        applyDamageToExplosionCellsNeighbors: false,
                        preExplosionSpawnThingDef: null,
                        preExplosionSpawnChance: 0f,
                        preExplosionSpawnThingCount: 0,
                        chanceToStartFire: 0.9f,
                        damageFalloff: true
                    );
                }
            }

            // Visual flash
            FleckMaker.Static(Position.ToVector3Shifted(), Map, FleckDefOf.ExplosionFlash, 35f);

            // Screen shake
            Find.CameraDriver.shaker.DoShake(5f);

            // Aftermath effects
            CreateCraterAndDebris();
            CreateLingeringFires();

            Messages.Message("Cruise missile impact from " + sourceHubLabel + "!",
                new TargetInfo(Position, Map), MessageTypeDefOf.ThreatBig);

            base.Impact();
        }

        private void CreateCraterAndDebris()
        {
            int debrisRadius = (int)(explosionRadius * 0.6f);
            
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(Position, debrisRadius, true))
            {
                if (!cell.InBounds(Map)) continue;
                
                float distFromCenter = cell.DistanceTo(Position);
                float debrisChance = 1f - (distFromCenter / debrisRadius);
                
                if (Rand.Chance(debrisChance * 0.7f))
                {
                    FilthMaker.TryMakeFilth(cell, Map, ThingDefOf.Filth_RubbleRock, Rand.RangeInclusive(1, 3));
                }
                
                if (Rand.Chance(debrisChance * 0.3f))
                {
                    FilthMaker.TryMakeFilth(cell, Map, ThingDefOf.Filth_RubbleBuilding, Rand.RangeInclusive(1, 2));
                }
            }
            
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(Position, explosionRadius * 0.4f, true))
            {
                if (!cell.InBounds(Map)) continue;
                
                if (Rand.Chance(0.5f))
                {
                    FilthMaker.TryMakeFilth(cell, Map, ThingDefOf.Filth_Ash, Rand.RangeInclusive(1, 2));
                }
            }
        }

        private void CreateLingeringFires()
        {
            int fireRadius = (int)(explosionRadius * 0.8f);
            int firesStarted = 0;
            int maxFires = 15;
            
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(Position, fireRadius, true))
            {
                if (firesStarted >= maxFires) break;
                if (!cell.InBounds(Map)) continue;
                
                float distFromCenter = cell.DistanceTo(Position);
                float fireChance = 0.4f * (1f - (distFromCenter / fireRadius));
                
                if (Rand.Chance(fireChance))
                {
                    Fire fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    fire.fireSize = Rand.Range(0.3f, 0.8f);
                    GenSpawn.Spawn(fire, cell, Map, Rot4.North);
                    firesStarted++;
                }
            }
            
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(Position, 3f, true))
            {
                if (!cell.InBounds(Map)) continue;
                
                if (Rand.Chance(0.7f))
                {
                    Fire fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    fire.fireSize = Rand.Range(0.5f, 1.0f);
                    GenSpawn.Spawn(fire, cell, Map, Rot4.North);
                }
            }
        }
    }
}