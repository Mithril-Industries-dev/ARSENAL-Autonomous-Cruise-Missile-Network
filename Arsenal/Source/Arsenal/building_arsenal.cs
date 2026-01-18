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
    public class Building_Arsenal : Building
    {
        private int checkInterval = 10;
        private int ticksUntilCheck = 0;
        private bool isManufacturing = false;
        private int manufacturingTicksRemaining = 0;
        private const int MANUFACTURE_TIME = 48;

        public bool productionEnabled = true;
        public Dictionary<int, HubConfig> hubConfigs = new Dictionary<int, HubConfig>();

        // Queue system - missiles waiting for HOP availability
        private List<QueuedMissile> missileQueue = new List<QueuedMissile>();

        private string customName;
        private static int factoryCounter = 1;

        private CompRefuelable refuelableComp;
        private CompPowerTrader powerComp;

        private Sustainer manufacturingSustainer;

        // === DART MANUFACTURING ===
        public bool dartProductionEnabled = true;
        private bool isManufacturingDart = false;
        private int dartManufacturingTicksRemaining = 0;
        private const int DART_MANUFACTURE_TIME = 12; // Faster than missiles

        // DART resource costs (lighter than missiles)
        public const int DART_COST_PLASTEEL = 30;
        public const int DART_COST_COMPONENTS = 1;
        public const int DART_COST_CHEMFUEL = 20;

        // === INTERNAL STORAGE SYSTEM ===
        // Resource costs per missile
        public const int COST_PLASTEEL = 250;
        public const int COST_GOLD = 25;
        public const int COST_COMPONENTS = 5;
        public const int COST_CHEMFUEL = 300;
        
        // Current stored amounts
        public int storedPlasteel = 0;
        public int storedGold = 0;
        public int storedComponents = 0;
        public int storedChemfuel = 0;
        
        // Storage limit (in missiles worth, 1-25)
        public int storageLimitMissiles = 1;
        public const int MAX_STORAGE_MISSILES = 25;
        
        // Toggle for accepting deliveries
        public bool acceptDeliveries = true;

        public class HubConfig : IExposable
        {
            public int stockLimit = 0;
            public int priority = 1;
            
            public void ExposeData()
            {
                Scribe_Values.Look(ref stockLimit, "stockLimit", 0);
                Scribe_Values.Look(ref priority, "priority", 1);
            }
        }

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

        // Storage limit properties
        public int MaxPlasteel => COST_PLASTEEL * storageLimitMissiles;
        public int MaxGold => COST_GOLD * storageLimitMissiles;
        public int MaxComponents => COST_COMPONENTS * storageLimitMissiles;
        public int MaxChemfuel => COST_CHEMFUEL * storageLimitMissiles;

        // How much more can be stored
        public int PlateelNeeded => acceptDeliveries ? Mathf.Max(0, MaxPlasteel - storedPlasteel) : 0;
        public int GoldNeeded => acceptDeliveries ? Mathf.Max(0, MaxGold - storedGold) : 0;
        public int ComponentsNeeded => acceptDeliveries ? Mathf.Max(0, MaxComponents - storedComponents) : 0;
        public int ChemfuelNeeded => acceptDeliveries ? Mathf.Max(0, MaxChemfuel - storedChemfuel) : 0;

        public bool NeedsResources()
        {
            if (!acceptDeliveries) return false;
            return PlateelNeeded > 0 || GoldNeeded > 0 || ComponentsNeeded > 0 || ChemfuelNeeded > 0;
        }

        public int GetNeededCount(ThingDef def)
        {
            if (!acceptDeliveries) return 0;
            if (def == ThingDefOf.Plasteel) return PlateelNeeded;
            if (def == ThingDefOf.Gold) return GoldNeeded;
            if (def == ThingDefOf.ComponentSpacer) return ComponentsNeeded;
            if (def == ThingDefOf.Chemfuel) return ChemfuelNeeded;
            return 0;
        }

        public bool AcceptsResource(ThingDef def)
        {
            return GetNeededCount(def) > 0;
        }

        public int DepositResource(Thing thing)
        {
            if (thing == null) return 0;
            
            int needed = GetNeededCount(thing.def);
            if (needed <= 0) return 0;
            
            int toDeposit = Mathf.Min(thing.stackCount, needed);
            
            if (thing.def == ThingDefOf.Plasteel)
                storedPlasteel += toDeposit;
            else if (thing.def == ThingDefOf.Gold)
                storedGold += toDeposit;
            else if (thing.def == ThingDefOf.ComponentSpacer)
                storedComponents += toDeposit;
            else if (thing.def == ThingDefOf.Chemfuel)
                storedChemfuel += toDeposit;
            else
                return 0;
            
            return toDeposit;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            refuelableComp = GetComp<CompRefuelable>();
            powerComp = GetComp<CompPowerTrader>();
            
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
            
            // Drop stored resources
            DropStoredResources();
            
            ArsenalNetworkManager.DeregisterArsenal(this);
            base.DeSpawn(mode);
        }

        private void DropStoredResources()
        {
            if (storedPlasteel > 0)
            {
                Thing t = ThingMaker.MakeThing(ThingDefOf.Plasteel);
                t.stackCount = storedPlasteel;
                GenPlace.TryPlaceThing(t, Position, Map, ThingPlaceMode.Near);
                storedPlasteel = 0;
            }
            if (storedGold > 0)
            {
                Thing t = ThingMaker.MakeThing(ThingDefOf.Gold);
                t.stackCount = storedGold;
                GenPlace.TryPlaceThing(t, Position, Map, ThingPlaceMode.Near);
                storedGold = 0;
            }
            if (storedComponents > 0)
            {
                Thing t = ThingMaker.MakeThing(ThingDefOf.ComponentSpacer);
                t.stackCount = storedComponents;
                GenPlace.TryPlaceThing(t, Position, Map, ThingPlaceMode.Near);
                storedComponents = 0;
            }
            if (storedChemfuel > 0)
            {
                Thing t = ThingMaker.MakeThing(ThingDefOf.Chemfuel);
                t.stackCount = storedChemfuel;
                GenPlace.TryPlaceThing(t, Position, Map, ThingPlaceMode.Near);
                storedChemfuel = 0;
            }
        }

        public override string Label => customName ?? base.Label;

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

            yield return new Command_Toggle
            {
                defaultLabel = "Production",
                defaultDesc = productionEnabled ? "Production is ON. Click to stop." : "Production is OFF. Click to start.",
                isActive = () => productionEnabled,
                toggleAction = delegate { productionEnabled = !productionEnabled; },
                icon = ContentFinder<Texture2D>.Get("UI/Commands/DesirePower", false)
            };

            yield return new Command_Toggle
            {
                defaultLabel = "Accept Deliveries",
                defaultDesc = acceptDeliveries ? "Accepting resource deliveries. Click to stop." : "Not accepting deliveries. Click to start.",
                isActive = () => acceptDeliveries,
                toggleAction = delegate { acceptDeliveries = !acceptDeliveries; },
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", false)
            };

            yield return new Command_Action
            {
                defaultLabel = "Storage: " + storageLimitMissiles + "x",
                defaultDesc = "Set storage limit (1-25 missiles worth). Current: " + storageLimitMissiles + " missiles.",
                action = delegate { Find.WindowStack.Add(new Dialog_ConfigureStorage(this)); }
            };

            yield return new Command_Action
            {
                defaultLabel = "Configure HUBs",
                defaultDesc = "Set stock limits and priorities for HUB staging platforms.",
                action = delegate { Find.WindowStack.Add(new Dialog_ConfigureArsenal(this)); }
            };

            // DART production toggle (only if LATTICE system research is complete)
            if (ArsenalDefOf.Arsenal_DroneSwarm != null && ArsenalDefOf.Arsenal_DroneSwarm.IsFinished)
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "DART Production",
                    defaultDesc = dartProductionEnabled
                        ? "DART drone production is ON. Click to stop."
                        : "DART drone production is OFF. Click to start.",
                    isActive = () => dartProductionEnabled,
                    toggleAction = delegate { dartProductionEnabled = !dartProductionEnabled; },
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", false)
                };
            }
        }

        public override void TickRare()
        {
            base.TickRare();

            if (!CanOperate())
            {
                StopManufacturingSound();
                return;
            }

            // Try to launch queued missiles first
            TryLaunchQueuedMissiles();

            // Handle missile manufacturing
            if (productionEnabled)
            {
                if (isManufacturing)
                {
                    StartManufacturingSound();

                    manufacturingTicksRemaining--;
                    if (manufacturingTicksRemaining <= 0)
                    {
                        CompleteMissileManufacture();
                        isManufacturing = false;
                        StopManufacturingSound();
                    }
                }
                else
                {
                    ticksUntilCheck--;
                    if (ticksUntilCheck <= 0)
                    {
                        ticksUntilCheck = checkInterval;
                        CheckAndStartManufacturing();
                    }
                }
            }
            else
            {
                if (!isManufacturingDart)
                    StopManufacturingSound();
            }

            // Handle DART manufacturing (separate from missiles)
            if (dartProductionEnabled && ArsenalDefOf.Arsenal_DroneSwarm != null && ArsenalDefOf.Arsenal_DroneSwarm.IsFinished)
            {
                if (isManufacturingDart)
                {
                    StartManufacturingSound();

                    dartManufacturingTicksRemaining--;
                    if (dartManufacturingTicksRemaining <= 0)
                    {
                        CompleteDartManufacture();
                        isManufacturingDart = false;
                        if (!isManufacturing)
                            StopManufacturingSound();
                    }
                }
                else if (!isManufacturing) // Don't start DART if making missile
                {
                    CheckAndStartDartManufacturing();
                }
            }
        }

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

        private void CheckAndStartManufacturing()
        {
            Building_Hub targetHub = FindUnderstockedHub();
            if (targetHub == null)
                return;
            if (!HasRequiredResources())
                return;

            ConsumeResources();
            isManufacturing = true;
            manufacturingTicksRemaining = MANUFACTURE_TIME;
            
            Messages.Message(Label + " started manufacturing missile for " + targetHub.Label, 
                this, MessageTypeDefOf.PositiveEvent);
        }

        public Building_Hub FindUnderstockedHub()
        {
            var allHubs = ArsenalNetworkManager.GetAllHubs();
            var hubsWithDeficit = new List<(Building_Hub hub, int deficit, int priority)>();

            foreach (var hub in allHubs)
            {
                if (hub.Map == null) continue;
                
                // Use Map.Tile as key - must match Dialog_ConfigureArsenal
                int tile = hub.Map.Tile;
                if (!hubConfigs.TryGetValue(tile, out HubConfig config))
                    continue;
                
                if (config.stockLimit <= 0)
                    continue;
                
                int currentStock = hub.GetStoredMissileCount();
                
                if (currentStock < config.stockLimit)
                    hubsWithDeficit.Add((hub, config.stockLimit - currentStock, config.priority));
            }

            if (hubsWithDeficit.Count == 0)
                return null;

            hubsWithDeficit.Sort((a, b) => 
            {
                int priorityCompare = a.priority.CompareTo(b.priority);
                if (priorityCompare != 0) return priorityCompare;
                return b.deficit.CompareTo(a.deficit);
            });

            return hubsWithDeficit[0].hub;
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

        private bool HasRequiredResources()
        {
            return storedPlasteel >= COST_PLASTEEL &&
                   storedGold >= COST_GOLD &&
                   storedComponents >= COST_COMPONENTS &&
                   storedChemfuel >= COST_CHEMFUEL;
        }

        private void ConsumeResources()
        {
            storedPlasteel -= COST_PLASTEEL;
            storedGold -= COST_GOLD;
            storedComponents -= COST_COMPONENTS;
            storedChemfuel -= COST_CHEMFUEL;
            
            refuelableComp?.ConsumeFuel(50f);
        }

        private void CompleteMissileManufacture()
        {
            Thing missile = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_CruiseMissile);
            Building_Hub targetHub = FindUnderstockedHub();
            
            SoundDefOf.Building_Complete.PlayOneShot(this);
            
            if (targetHub == null)
            {
                GenPlace.TryPlaceThing(missile, Position, Map, ThingPlaceMode.Near);
                Messages.Message("No HUB available for missile delivery.", this, MessageTypeDefOf.NeutralEvent);
                return;
            }

            if (CanReachHub(targetHub))
            {
                LaunchMissileToHub(missile, targetHub);
            }
            else
            {
                missileQueue.Add(new QueuedMissile { missile = missile, targetHub = targetHub });
                Messages.Message(Label + ": Missile queued - waiting for available HOP.", this, MessageTypeDefOf.NeutralEvent);
            }
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

            Messages.Message(Label + ": Cruise missile launched to " + targetHub.Label, this, MessageTypeDefOf.PositiveEvent);
        }

        // === DART MANUFACTURING ===

        private void CheckAndStartDartManufacturing()
        {
            // Check if LATTICE exists on this map
            Building_Lattice lattice = ArsenalNetworkManager.GetLatticeOnMap(Map);
            if (lattice == null)
                return;

            // Check if any QUIVER needs DARTs
            Building_Quiver targetQuiver = lattice.GetQuiverForDelivery();
            if (targetQuiver == null)
                return;

            // Check if we have resources
            if (!HasDartResources())
                return;

            // Start manufacturing
            ConsumeDartResources();
            isManufacturingDart = true;
            dartManufacturingTicksRemaining = DART_MANUFACTURE_TIME;
        }

        private bool HasDartResources()
        {
            return storedPlasteel >= DART_COST_PLASTEEL &&
                   storedComponents >= DART_COST_COMPONENTS &&
                   storedChemfuel >= DART_COST_CHEMFUEL;
        }

        private void ConsumeDartResources()
        {
            storedPlasteel -= DART_COST_PLASTEEL;
            storedComponents -= DART_COST_COMPONENTS;
            storedChemfuel -= DART_COST_CHEMFUEL;

            refuelableComp?.ConsumeFuel(10f); // Less fuel than missile
        }

        private void CompleteDartManufacture()
        {
            Building_Lattice lattice = ArsenalNetworkManager.GetLatticeOnMap(Map);
            if (lattice == null)
            {
                // No LATTICE - drop DART item instead
                Thing dartItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_DART_Item);
                GenPlace.TryPlaceThing(dartItem, Position, Map, ThingPlaceMode.Near);
                Messages.Message(Label + ": DART completed but no LATTICE available.", this, MessageTypeDefOf.NeutralEvent);
                return;
            }

            Building_Quiver targetQuiver = lattice.GetQuiverForDelivery();
            if (targetQuiver == null)
            {
                // No QUIVER available - drop DART item
                Thing dartItem = ThingMaker.MakeThing(ArsenalDefOf.Arsenal_DART_Item);
                GenPlace.TryPlaceThing(dartItem, Position, Map, ThingPlaceMode.Near);
                Messages.Message(Label + ": DART completed but all QUIVERs full.", this, MessageTypeDefOf.NeutralEvent);
                return;
            }

            // Spawn DART flyer in Delivery state
            DART_Flyer dart = (DART_Flyer)ThingMaker.MakeThing(ArsenalDefOf.Arsenal_DART_Flyer);
            dart.InitializeForDelivery(targetQuiver, lattice);
            GenSpawn.Spawn(dart, Position, Map);

            SoundDefOf.Building_Complete.PlayOneShot(this);
        }

        public void SetCustomName(string name)
        {
            customName = name;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref isManufacturing, "isManufacturing", false);
            Scribe_Values.Look(ref manufacturingTicksRemaining, "manufacturingTicksRemaining", 0);
            Scribe_Values.Look(ref ticksUntilCheck, "ticksUntilCheck", 0);
            Scribe_Values.Look(ref productionEnabled, "productionEnabled", true);
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Collections.Look(ref hubConfigs, "hubConfigs", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref missileQueue, "missileQueue", LookMode.Deep);

            // Storage
            Scribe_Values.Look(ref storedPlasteel, "storedPlasteel", 0);
            Scribe_Values.Look(ref storedGold, "storedGold", 0);
            Scribe_Values.Look(ref storedComponents, "storedComponents", 0);
            Scribe_Values.Look(ref storedChemfuel, "storedChemfuel", 0);
            Scribe_Values.Look(ref storageLimitMissiles, "storageLimitMissiles", 1);
            Scribe_Values.Look(ref acceptDeliveries, "acceptDeliveries", true);

            // DART manufacturing
            Scribe_Values.Look(ref dartProductionEnabled, "dartProductionEnabled", true);
            Scribe_Values.Look(ref isManufacturingDart, "isManufacturingDart", false);
            Scribe_Values.Look(ref dartManufacturingTicksRemaining, "dartManufacturingTicksRemaining", 0);

            if (hubConfigs == null)
                hubConfigs = new Dictionary<int, HubConfig>();
            if (missileQueue == null)
                missileQueue = new List<QueuedMissile>();
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();
            str += "\nProduction: " + (productionEnabled ? "ACTIVE" : "STOPPED");

            // Storage status
            str += "\nStorage (" + storageLimitMissiles + "x): ";
            str += "Plasteel " + storedPlasteel + "/" + MaxPlasteel;
            str += " | Gold " + storedGold + "/" + MaxGold;
            str += "\n  Components " + storedComponents + "/" + MaxComponents;
            str += " | Chemfuel " + storedChemfuel + "/" + MaxChemfuel;

            if (!acceptDeliveries)
                str += "\nDeliveries: STOPPED";

            if (isManufacturing)
            {
                float progress = 1f - ((float)manufacturingTicksRemaining / MANUFACTURE_TIME);
                str += "\nManufacturing missile: " + (progress * 100f).ToString("F0") + "%";
            }
            else if (!HasRequiredResources())
            {
                str += "\nAwaiting resources...";
            }

            if (missileQueue.Count > 0)
            {
                str += "\nQueued missiles: " + missileQueue.Count + " (waiting for HOP)";
            }

            // DART status
            if (ArsenalDefOf.Arsenal_DroneSwarm != null && ArsenalDefOf.Arsenal_DroneSwarm.IsFinished)
            {
                str += "\nDARTs: " + (dartProductionEnabled ? "ACTIVE" : "STOPPED");
                if (isManufacturingDart)
                {
                    float dartProgress = 1f - ((float)dartManufacturingTicksRemaining / DART_MANUFACTURE_TIME);
                    str += " | Building: " + (dartProgress * 100f).ToString("F0") + "%";
                }
            }

            return str;
        }
    }

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

    public class Dialog_ConfigureStorage : Window
    {
        private Building_Arsenal arsenal;
        private int tempLimit;

        public Dialog_ConfigureStorage(Building_Arsenal a)
        {
            arsenal = a;
            tempLimit = a.storageLimitMissiles;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 320f);

        public override void DoWindowContents(Rect inRect)
        {
            float y = 0f;
            
            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 35), "Storage Limit");
            Text.Font = GameFont.Small;
            y += 45f;

            // Slider label
            Widgets.Label(new Rect(0, y, inRect.width, 24), 
                "Missiles worth of resources: " + tempLimit);
            y += 30f;
            
            // Slider
            tempLimit = (int)Widgets.HorizontalSlider(
                new Rect(0, y, inRect.width, 20), 
                tempLimit, 1f, Building_Arsenal.MAX_STORAGE_MISSILES, true);
            y += 35f;

            // Max storage info
            Widgets.Label(new Rect(0, y, inRect.width, 24), "Max storage at this limit:");
            y += 28f;
            
            Widgets.Label(new Rect(15, y, inRect.width - 15, 24), 
                "Plasteel: " + (Building_Arsenal.COST_PLASTEEL * tempLimit));
            y += 24f;
            
            Widgets.Label(new Rect(15, y, inRect.width - 15, 24), 
                "Gold: " + (Building_Arsenal.COST_GOLD * tempLimit));
            y += 24f;
            
            Widgets.Label(new Rect(15, y, inRect.width - 15, 24), 
                "Advanced Components: " + (Building_Arsenal.COST_COMPONENTS * tempLimit));
            y += 24f;
            
            Widgets.Label(new Rect(15, y, inRect.width - 15, 24), 
                "Chemfuel: " + (Building_Arsenal.COST_CHEMFUEL * tempLimit));
            y += 35f;

            // Buttons
            float buttonWidth = 100f;
            float buttonSpacing = 20f;
            float buttonsX = (inRect.width - (buttonWidth * 2 + buttonSpacing)) / 2f;
            
            if (Widgets.ButtonText(new Rect(buttonsX, y, buttonWidth, 35), "OK"))
            {
                arsenal.storageLimitMissiles = tempLimit;
                Close();
            }
            if (Widgets.ButtonText(new Rect(buttonsX + buttonWidth + buttonSpacing, y, buttonWidth, 35), "Cancel"))
            {
                Close();
            }
        }
    }
}