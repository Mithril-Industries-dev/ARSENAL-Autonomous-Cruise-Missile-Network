using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Arsenal
{
    /// <summary>
    /// SKYLINK Launch Pad - manufactures satellites and executes orbital launches.
    /// 5x5 building, reusable for replacement satellites if original destroyed.
    /// </summary>
    public class Building_SkyLinkLaunchPad : Building
    {
        // Manufacturing state
        private bool isManufacturing;
        private float manufacturingProgress;
        private bool resourcesConsumed;
        private const float SATELLITE_WORK_AMOUNT = 240000f; // 4 days at normal speed

        // Launch state
        private bool hasBuiltSatellite;
        private bool isLaunching;

        // Components
        private CompPowerTrader powerComp;
        private Sustainer manufacturingSustainer;

        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;
        public bool IsManufacturing => isManufacturing;
        public float ManufacturingProgress => manufacturingProgress;
        public float ManufacturingProgressPercent => manufacturingProgress / SATELLITE_WORK_AMOUNT;
        public bool HasBuiltSatellite => hasBuiltSatellite;
        public bool CanLaunch => hasBuiltSatellite && !isLaunching && !ArsenalNetworkManager.IsSatelliteInOrbit();

        // Satellite cost
        private static readonly List<ThingDefCountClass> SatelliteCost = new List<ThingDefCountClass>
        {
            new ThingDefCountClass(ThingDefOf.Steel, 200),
            new ThingDefCountClass(ThingDefOf.Plasteel, 150),
            new ThingDefCountClass(ThingDefOf.Gold, 50),
            new ThingDefCountClass(ThingDefOf.ComponentIndustrial, 8),
            new ThingDefCountClass(ThingDefOf.ComponentSpacer, 6)
        };

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            StopManufacturingSound();
            base.DeSpawn(mode);
        }

        public override void TickRare()
        {
            base.TickRare();

            if (!IsPoweredOn)
            {
                StopManufacturingSound();
                return;
            }

            if (isManufacturing)
            {
                TickManufacturing();
            }
        }

        private void TickManufacturing()
        {
            if (!IsPoweredOn)
            {
                StopManufacturingSound();
                return;
            }

            StartManufacturingSound();

            // Progress manufacturing (TickRare = 250 ticks)
            manufacturingProgress += 250f;

            if (manufacturingProgress >= SATELLITE_WORK_AMOUNT)
            {
                CompleteManufacturing();
            }
        }

        private void CompleteManufacturing()
        {
            isManufacturing = false;
            hasBuiltSatellite = true;
            manufacturingProgress = 0f;
            StopManufacturingSound();

            SoundDefOf.Building_Complete.PlayOneShot(this);
            Messages.Message("SKYLINK satellite manufacturing complete. Ready for launch.",
                this, MessageTypeDefOf.PositiveEvent);
        }

        #region Resource Management

        /// <summary>
        /// Gets all cells adjacent to Launch Pad that contain valid storage.
        /// </summary>
        private IEnumerable<IntVec3> GetAdjacentStorageCells()
        {
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;

                Zone zone = cell.GetZone(Map);
                if (zone is Zone_Stockpile)
                {
                    yield return cell;
                    continue;
                }

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

        private int CountResourceAvailable(ThingDef def)
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

        private bool HasResourcesForSatellite()
        {
            foreach (var cost in SatelliteCost)
            {
                if (CountResourceAvailable(cost.thingDef) < cost.count)
                    return false;
            }
            return true;
        }

        private bool ConsumeResources()
        {
            if (!HasResourcesForSatellite())
                return false;

            foreach (var cost in SatelliteCost)
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

        #endregion

        #region Manufacturing Control

        public void StartManufacturing()
        {
            if (isManufacturing || hasBuiltSatellite)
                return;

            if (ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                Messages.Message("Cannot manufacture satellite - one is already in orbit.",
                    this, MessageTypeDefOf.RejectInput);
                return;
            }

            if (!HasResourcesForSatellite())
            {
                Messages.Message("Insufficient resources in adjacent storage for satellite manufacturing.",
                    this, MessageTypeDefOf.RejectInput);
                return;
            }

            if (!ConsumeResources())
            {
                Messages.Message("Failed to consume resources for satellite manufacturing.",
                    this, MessageTypeDefOf.RejectInput);
                return;
            }

            isManufacturing = true;
            resourcesConsumed = true;
            manufacturingProgress = 0f;

            Messages.Message("SKYLINK satellite manufacturing started. ETA: 4 days.",
                this, MessageTypeDefOf.PositiveEvent);
        }

        public void CancelManufacturing()
        {
            if (!isManufacturing)
                return;

            // Note: resources are not refunded
            isManufacturing = false;
            manufacturingProgress = 0f;
            StopManufacturingSound();

            Messages.Message("SKYLINK satellite manufacturing cancelled. Resources lost.",
                this, MessageTypeDefOf.NeutralEvent);
        }

        #endregion

        #region Launch

        public void LaunchSatellite()
        {
            if (!CanLaunch)
                return;

            if (ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                Messages.Message("Cannot launch - satellite already in orbit.",
                    this, MessageTypeDefOf.RejectInput);
                return;
            }

            hasBuiltSatellite = false;
            isLaunching = true;

            // Spawn the launching rocket visual
            SkyLinkLaunchingRocket rocket = (SkyLinkLaunchingRocket)ThingMaker.MakeThing(
                ThingDef.Named("Arsenal_SkyLinkLaunchingRocket"));
            rocket.launchPad = this;
            GenSpawn.Spawn(rocket, Position, Map);

            Messages.Message("SKYLINK satellite launching!",
                this, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>
        /// Called by the launching rocket when it reaches orbit.
        /// </summary>
        public void OnLaunchComplete()
        {
            isLaunching = false;

            // Create the satellite world object
            WorldObject_SkyLinkSatellite satellite = (WorldObject_SkyLinkSatellite)WorldObjectMaker.MakeWorldObject(
                WorldObjectDef.Named("Arsenal_SkyLinkSatellite"));
            satellite.Tile = Map.Tile; // Satellite is "over" this tile initially (doesn't really matter)
            Find.WorldObjects.Add(satellite);

            Messages.Message("SKYLINK satellite has achieved orbit! Global network operations enabled.",
                MessageTypeDefOf.PositiveEvent);
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

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            // Satellite status
            if (!str.NullOrEmpty()) str += "\n";
            if (ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                str += "Orbital Status: SATELLITE IN ORBIT";
            }
            else
            {
                str += "Orbital Status: NO SATELLITE";
            }

            // Manufacturing status
            str += "\n";
            if (isManufacturing)
            {
                str += $"Manufacturing: {ManufacturingProgressPercent:P0} complete";
                int ticksRemaining = (int)(SATELLITE_WORK_AMOUNT - manufacturingProgress);
                float hoursRemaining = ticksRemaining / 2500f;
                str += $" ({hoursRemaining:F1}h remaining)";
            }
            else if (hasBuiltSatellite)
            {
                str += "Status: SATELLITE READY FOR LAUNCH";
            }
            else if (isLaunching)
            {
                str += "Status: LAUNCHING...";
            }
            else
            {
                str += "Status: Idle";
            }

            // Resource availability
            if (!isManufacturing && !hasBuiltSatellite && !ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                str += "\n\nResources needed:";
                foreach (var cost in SatelliteCost)
                {
                    int available = CountResourceAvailable(cost.thingDef);
                    string color = available >= cost.count ? "green" : "red";
                    str += $"\n<color={color}>{cost.thingDef.label}: {available}/{cost.count}</color>";
                }
            }

            return str;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            if (!ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                if (!isManufacturing && !hasBuiltSatellite)
                {
                    // Start manufacturing
                    yield return new Command_Action
                    {
                        defaultLabel = "Manufacture Satellite",
                        defaultDesc = "Begin manufacturing a SKYLINK communications satellite.\n\nCost:\n- Steel: 200\n- Plasteel: 150\n- Gold: 50\n- Components: 8\n- Adv. Components: 6\n\nTime: 4 days",
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", false),
                        action = StartManufacturing,
                        disabled = !HasResourcesForSatellite(),
                        disabledReason = "Insufficient resources in adjacent storage"
                    };
                }
                else if (isManufacturing)
                {
                    // Cancel manufacturing
                    yield return new Command_Action
                    {
                        defaultLabel = "Cancel Manufacturing",
                        defaultDesc = "Cancel satellite manufacturing. Resources will NOT be refunded.",
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/Halt", false),
                        action = CancelManufacturing
                    };
                }
                else if (hasBuiltSatellite)
                {
                    // Launch satellite
                    yield return new Command_Action
                    {
                        defaultLabel = "Launch Satellite",
                        defaultDesc = "Launch the SKYLINK satellite into orbit. Once in orbit, global network operations will be enabled.",
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", false),
                        action = LaunchSatellite,
                        disabled = isLaunching,
                        disabledReason = "Launch in progress"
                    };
                }
            }
            else
            {
                // Info about satellite in orbit
                yield return new Command_Action
                {
                    defaultLabel = "Satellite Status",
                    defaultDesc = "A SKYLINK satellite is currently in orbit providing global network connectivity.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", false),
                    action = delegate
                    {
                        var satellite = ArsenalNetworkManager.GetOrbitalSatellite();
                        if (satellite != null)
                        {
                            Find.WindowStack.Add(new Dialog_InfoCard(satellite));
                        }
                    }
                };
            }
        }

        #endregion

        #region Save/Load

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref isManufacturing, "isManufacturing", false);
            Scribe_Values.Look(ref manufacturingProgress, "manufacturingProgress", 0f);
            Scribe_Values.Look(ref resourcesConsumed, "resourcesConsumed", false);
            Scribe_Values.Look(ref hasBuiltSatellite, "hasBuiltSatellite", false);
            Scribe_Values.Look(ref isLaunching, "isLaunching", false);
        }

        #endregion
    }
}
