using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    public class Building_Hub : Building
    {
        private List<Thing> storedMissiles = new List<Thing>();
        private const int MAX_STORED = 10;
        private const float LAUNCH_RADIUS = 100f;

        private CompRefuelable refuelableComp;
        
        private string customName;
        private static int hubCounter = 1;
        
        private int pendingStrikeTile = -1;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            refuelableComp = GetComp<CompRefuelable>();
            if (!respawningAfterLoad)
            {
                ArsenalNetworkManager.RegisterHub(this);
                customName = "HUB-" + hubCounter.ToString("D2");
                hubCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            foreach (Thing m in storedMissiles.ToList())
                GenPlace.TryPlaceThing(m, Position, Map, ThingPlaceMode.Near);
            storedMissiles.Clear();
            ArsenalNetworkManager.DeregisterHub(this);
            base.DeSpawn(mode);
        }

        public override string Label => customName ?? base.Label;

        public void SetCustomName(string name) => customName = name;

        public int GetStoredMissileCount() => storedMissiles.Count;
        public bool CanStoreMissile() => storedMissiles.Count < MAX_STORED;

        public bool StoreMissile(Thing missile)
        {
            if (!CanStoreMissile()) return false;
            if (missile.Spawned) missile.DeSpawn();
            storedMissiles.Add(missile);
            RefuelMissile(missile);
            return true;
        }

        // Load a missile from the ground into the HUB
        public void LoadMissileFromGround(Thing missile)
        {
            if (!CanStoreMissile())
            {
                Messages.Message("HUB is full.", this, MessageTypeDefOf.RejectInput);
                return;
            }
            
            if (missile.Spawned)
                missile.DeSpawn();
            
            storedMissiles.Add(missile);
            RefuelMissile(missile);
            
            Messages.Message("Missile loaded into " + Label, this, MessageTypeDefOf.PositiveEvent);
        }

        private void RefuelMissile(Thing missile)
        {
            if (refuelableComp == null || !refuelableComp.HasFuel) return;
            CompMissileFuel fc = missile.TryGetComp<CompMissileFuel>();
            if (fc == null) return;
            float needed = fc.FuelCapacity - fc.Fuel;
            float transfer = Mathf.Min(needed, refuelableComp.Fuel);
            if (transfer > 0)
            {
                refuelableComp.ConsumeFuel(transfer);
                fc.Refuel(transfer);
            }
        }

        public void BeginTargeting()
        {
            if (storedMissiles.Count == 0)
            {
                Messages.Message("No missiles stored.", this, MessageTypeDefOf.RejectInput);
                return;
            }

            CameraJumper.TryJump(CameraJumper.GetWorldTarget(this));
            Find.WorldTargeter.BeginTargeting(
                OnWorldTargetSelected,
                true, 
                null, 
                false,
                null,
                delegate(GlobalTargetInfo target) 
                { 
                    if (!target.IsValid) return "Select target";
                    int dist = Find.WorldGrid.TraversalDistanceBetween(Map.Tile, target.Tile);
                    if (dist > LAUNCH_RADIUS)
                        return "Out of range (" + dist + "/" + LAUNCH_RADIUS + " tiles)";
                    return "Launch strike here (" + dist + " tiles)";
                }
            );
        }

        private bool OnWorldTargetSelected(GlobalTargetInfo target)
        {
            if (!target.IsValid)
                return false;

            int dist = Find.WorldGrid.TraversalDistanceBetween(Map.Tile, target.Tile);
            if (dist > LAUNCH_RADIUS)
            {
                Messages.Message("Target out of range (max " + LAUNCH_RADIUS + " tiles).", this, MessageTypeDefOf.RejectInput);
                return false;
            }

            MapParent mp = Find.WorldObjects.MapParentAt(target.Tile);
            
            if (mp != null)
            {
                Map targetMap = mp.Map;
                
                if (targetMap != null)
                {
                    pendingStrikeTile = target.Tile;
                    Current.Game.CurrentMap = targetMap;
                    CameraJumper.TryJump(targetMap.Center, targetMap);
                    
                    Messages.Message("Select precise impact location on the map.", MessageTypeDefOf.NeutralEvent);
                    
                    Find.Targeter.BeginTargeting(
                        new TargetingParameters
                        {
                            canTargetLocations = true,
                            canTargetSelf = false,
                            canTargetPawns = true,
                            canTargetBuildings = true
                        },
                        OnLocalTargetSelected,
                        null,
                        null,
                        null
                    );
                    return true;
                }
                else
                {
                    Messages.Message("Target location has no active map. Strike will hit random position.", MessageTypeDefOf.NeutralEvent);
                    LaunchStrikeAtPosition(target.Tile, IntVec3.Invalid);
                    return true;
                }
            }
            else
            {
                LaunchStrikeAtPosition(target.Tile, IntVec3.Invalid);
                return true;
            }
        }

        private void OnLocalTargetSelected(LocalTargetInfo localTarget)
        {
            if (pendingStrikeTile >= 0 && localTarget.IsValid)
            {
                LaunchStrikeAtPosition(pendingStrikeTile, localTarget.Cell);
                pendingStrikeTile = -1;
            }
        }

        private void LaunchStrikeAtPosition(int targetTile, IntVec3 targetCell)
        {
            if (storedMissiles.Count == 0) return;

            Thing missile = storedMissiles[0];
            storedMissiles.RemoveAt(0);

            int dist = Find.WorldGrid.TraversalDistanceBetween(Map.Tile, targetTile);

            // Create strike world object
            WorldObject_MissileStrike strike = 
                (WorldObject_MissileStrike)WorldObjectMaker.MakeWorldObject(ArsenalDefOf.Arsenal_MissileStrike);
            strike.Tile = Map.Tile;
            strike.destinationTile = targetTile;
            strike.targetCell = targetCell;
            strike.arrivalTick = Find.TickManager.TicksGame + (dist * 50);
            strike.sourceHubLabel = Label;

            // Spawn takeoff skyfaller
            MissileLaunchingSkyfaller skyfaller = (MissileLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_MissileLaunching);
            skyfaller.missileStrike = strike;
            
            GenSpawn.Spawn(skyfaller, Position, Map);

            Messages.Message(Label + ": Missile strike launched! ETA: " + (dist * 50).ToStringTicksToPeriod(), this, MessageTypeDefOf.TaskCompletion);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this HUB.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate 
                { 
                    Find.WindowStack.Add(new Dialog_RenameHub(this)); 
                }
            };

            // Load missile button - only show if there's a missile nearby and we have space
            if (CanStoreMissile())
            {
                Thing nearbyMissile = FindNearbyMissile();
                if (nearbyMissile != null)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "Load Missile",
                        defaultDesc = "Load a nearby cruise missile into the HUB.",
                        action = delegate
                        {
                            Thing m = FindNearbyMissile();
                            if (m != null)
                                LoadMissileFromGround(m);
                        }
                    };
                }
            }

            if (storedMissiles.Count > 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Launch Strike",
                    defaultDesc = "Launch a cruise missile at a target within " + LAUNCH_RADIUS + " tiles.\n\nIf target has an active map, you can select precise impact location.",
                    action = BeginTargeting
                };

                yield return new Command_Action
                {
                    defaultLabel = "Eject Missile",
                    defaultDesc = "Remove a missile from storage.",
                    action = delegate
                    {
                        if (storedMissiles.Count > 0)
                        {
                            Thing m = storedMissiles[0];
                            storedMissiles.RemoveAt(0);
                            GenPlace.TryPlaceThing(m, Position, Map, ThingPlaceMode.Near);
                        }
                    }
                };
            }
        }

        private Thing FindNearbyMissile()
        {
            foreach (Thing t in Map.listerThings.ThingsOfDef(ArsenalDefOf.Arsenal_CruiseMissile))
            {
                if (t.Position.DistanceTo(Position) <= 10f)
                    return t;
            }
            return null;
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();
            str += "\nStored missiles: " + storedMissiles.Count + " / " + MAX_STORED;
            str += "\nStrike range: " + LAUNCH_RADIUS + " tiles";
            return str;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Collections.Look(ref storedMissiles, "storedMissiles", LookMode.Deep);
            if (storedMissiles == null) storedMissiles = new List<Thing>();
        }
    }

    public class Dialog_RenameHub : Window
    {
        private Building_Hub hub;
        private string newName;

        public Dialog_RenameHub(Building_Hub h)
        {
            hub = h;
            newName = h.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename HUB");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                hub.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}