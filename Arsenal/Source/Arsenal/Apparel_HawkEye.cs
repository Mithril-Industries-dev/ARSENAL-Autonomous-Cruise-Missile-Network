using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// HAWKEYE Tactical Command Helmet - mobile sensor suite with two-way LATTICE integration.
    /// Provides:
    /// - Mobile ARGUS-like threat detection (30 tile radius)
    /// - Network awareness (wearer knows all network-detected threats)
    /// - DAGGER strike designation ability
    /// - QUIVER priority marking ability
    /// </summary>
    public class Apparel_HawkEye : Apparel
    {
        private CompHawkeyeSensor sensorComp;

        public CompHawkeyeSensor SensorComp
        {
            get
            {
                if (sensorComp == null)
                    sensorComp = GetComp<CompHawkeyeSensor>();
                return sensorComp;
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
        }

        public override void Notify_Equipped(Pawn pawn)
        {
            base.Notify_Equipped(pawn);

            // Register pawn with network
            ArsenalNetworkManager.RegisterHawkeyePawn(pawn);

            // Notify sensor comp
            SensorComp?.OnEquipped(pawn);
        }

        public override void Notify_Unequipped(Pawn pawn)
        {
            ArsenalNetworkManager.DeregisterHawkeyePawn(pawn);
            SensorComp?.OnUnequipped();

            base.Notify_Unequipped(pawn);
        }

        public override IEnumerable<Gizmo> GetWornGizmos()
        {
            foreach (Gizmo g in base.GetWornGizmos())
                yield return g;

            // Only show gizmos if HAWKEYE is operational
            if (SensorComp == null || !SensorComp.IsOperational)
                yield break;

            // Designate DAGGER Strike ability
            var daggerCmd = new Command_Target
            {
                defaultLabel = "DAGGER Strike",
                defaultDesc = "Designate a location for DAGGER cruise missile strike. System will select optimal HUB and launch.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", false),
                targetingParams = new TargetingParameters
                {
                    canTargetLocations = true,
                    canTargetPawns = false,
                    canTargetBuildings = false
                },
                action = delegate(LocalTargetInfo target)
                {
                    SensorComp?.DesignateDaggerStrike(target.Cell);
                }
            };
            if (!CanDesignateStrike())
            {
                daggerCmd.Disable(GetStrikeDisabledReason());
            }
            yield return daggerCmd;

            // Mark QUIVER Priority ability
            var markCmd = new Command_Target
            {
                defaultLabel = "Mark Priority",
                defaultDesc = "Mark an enemy pawn as priority target. DARTs will converge on the marked target until dead or 30 seconds expire.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", false),
                targetingParams = new TargetingParameters
                {
                    canTargetLocations = false,
                    canTargetPawns = true,
                    canTargetBuildings = false,
                    validator = (TargetInfo t) => t.Thing is Pawn p && p.HostileTo(Faction.OfPlayer) && !p.Dead
                },
                action = delegate(LocalTargetInfo target)
                {
                    if (target.Thing is Pawn targetPawn)
                    {
                        SensorComp?.MarkQuiverPriority(targetPawn);
                    }
                }
            };
            if (!CanMarkPriority())
            {
                markCmd.Disable(GetMarkDisabledReason());
            }
            yield return markCmd;

            // Network Status display
            yield return new Command_Action
            {
                defaultLabel = "Network Status",
                defaultDesc = $"SKYLINK: {ArsenalNetworkManager.GetSkylinkStatus()}\n\nThreats detected: {ArsenalNetworkManager.GetAllNetworkDetectedThreats().Count}",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", false),
                action = delegate
                {
                    var threats = ArsenalNetworkManager.GetAllNetworkDetectedThreats();
                    string msg = $"HAWKEYE Network Status\n\n";
                    msg += $"SKYLINK: {ArsenalNetworkManager.GetSkylinkStatus()}\n\n";
                    msg += $"Threats detected by network: {threats.Count}\n";
                    foreach (var threat in threats)
                    {
                        msg += $"- {threat.LabelShort} at {threat.Position}\n";
                    }
                    Messages.Message(msg, Wearer, MessageTypeDefOf.NeutralEvent);
                }
            };
        }

        private bool CanDesignateStrike()
        {
            // Need SKYLINK connection
            if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
                return false;

            // Need available HUBs with missiles
            var hubs = ArsenalNetworkManager.GetAllHubs();
            return hubs.Any(h => h.HasNetworkConnection() && h.StoredMissileCount > 0);
        }

        private string GetStrikeDisabledReason()
        {
            if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
                return "SKYLINK connection required";

            var hubs = ArsenalNetworkManager.GetAllHubs();
            if (!hubs.Any(h => h.HasNetworkConnection()))
                return "No connected HUBs";

            if (!hubs.Any(h => h.StoredMissileCount > 0))
                return "No missiles available in HUBs";

            return null;
        }

        private bool CanMarkPriority()
        {
            // Need SKYLINK connection
            if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
                return false;

            // Need LATTICE with active QUIVER system
            var lattice = ArsenalNetworkManager.GlobalLattice;
            if (lattice == null || !lattice.IsPoweredOn())
                return false;

            return true;
        }

        private string GetMarkDisabledReason()
        {
            if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
                return "SKYLINK connection required";

            var lattice = ArsenalNetworkManager.GlobalLattice;
            if (lattice == null || !lattice.IsPoweredOn())
                return "LATTICE offline";

            return null;
        }
    }

    /// <summary>
    /// Sensor component for HAWKEYE helmet - handles threat detection and ability execution.
    /// </summary>
    public class CompHawkeyeSensor : ThingComp
    {
        private const float DETECTION_RADIUS = 30f;
        private const int PRIORITY_MARK_DURATION_TICKS = 1800; // 30 seconds
        private const int PRIORITY_MARK_COOLDOWN_TICKS = 1800; // 30 seconds

        private Pawn wearer;
        private int lastPriorityMarkTick = -9999;

        // Currently marked priority target
        private static Pawn globalPriorityTarget;
        private static int priorityTargetExpireTick;

        public CompProperties_HawkeyeSensor Props => (CompProperties_HawkeyeSensor)props;

        public bool IsOperational
        {
            get
            {
                if (wearer == null || wearer.Dead || wearer.Downed)
                    return false;

                // Require SKYLINK connection
                return ArsenalNetworkManager.IsLatticeConnectedToSkylink();
            }
        }

        public static Pawn GlobalPriorityTarget
        {
            get
            {
                if (globalPriorityTarget != null)
                {
                    if (globalPriorityTarget.Dead || globalPriorityTarget.Destroyed ||
                        Find.TickManager.TicksGame > priorityTargetExpireTick)
                    {
                        globalPriorityTarget = null;
                    }
                }
                return globalPriorityTarget;
            }
        }

        public void OnEquipped(Pawn pawn)
        {
            wearer = pawn;
        }

        public void OnUnequipped()
        {
            wearer = null;
        }

        public override void CompTick()
        {
            base.CompTick();

            // Check if we need to expire priority target
            if (globalPriorityTarget != null && Find.TickManager.TicksGame > priorityTargetExpireTick)
            {
                globalPriorityTarget = null;
            }
        }

        /// <summary>
        /// Gets all hostile pawns detected within sensor range.
        /// </summary>
        public List<Pawn> GetDetectedThreats()
        {
            List<Pawn> threats = new List<Pawn>();

            if (!IsOperational || wearer?.Map == null)
                return threats;

            foreach (Pawn pawn in wearer.Map.mapPawns.AllPawnsSpawned)
            {
                if (!pawn.HostileTo(Faction.OfPlayer))
                    continue;
                if (pawn.Dead || pawn.Downed)
                    continue;

                float distance = pawn.Position.DistanceTo(wearer.Position);
                if (distance > DETECTION_RADIUS)
                    continue;

                // Check LOS
                if (GenSight.LineOfSight(wearer.Position, pawn.Position, wearer.Map))
                {
                    threats.Add(pawn);
                }
            }

            return threats;
        }

        /// <summary>
        /// Designates a DAGGER strike at the specified location.
        /// </summary>
        public void DesignateDaggerStrike(IntVec3 targetCell)
        {
            if (!IsOperational)
            {
                Messages.Message("HAWKEYE offline - cannot designate strike.", wearer, MessageTypeDefOf.RejectInput);
                return;
            }

            // Find best HUB with missiles that can reach target
            var hubs = ArsenalNetworkManager.GetAllHubs()
                .Where(h => h.HasNetworkConnection() && h.StoredMissileCount > 0)
                .OrderByDescending(h => h.StoredMissileCount)
                .ToList();

            if (hubs.Count == 0)
            {
                Messages.Message("No HUBs with available missiles.", wearer, MessageTypeDefOf.RejectInput);
                return;
            }

            Building_Hub selectedHub = hubs.First();

            // Request launch from HUB
            bool success = selectedHub.LaunchMissileAt(targetCell);
            if (success)
            {
                Messages.Message($"DAGGER strike designated via {wearer.LabelShort}'s HAWKEYE. Launching from {selectedHub.Label}.",
                    wearer, MessageTypeDefOf.PositiveEvent);
            }
            else
            {
                Messages.Message($"DAGGER launch failed - {selectedHub.Label} unable to launch.",
                    wearer, MessageTypeDefOf.RejectInput);
            }
        }

        /// <summary>
        /// Marks an enemy pawn as priority target for DART convergence.
        /// </summary>
        public void MarkQuiverPriority(Pawn target)
        {
            if (!IsOperational)
            {
                Messages.Message("HAWKEYE offline - cannot mark target.", wearer, MessageTypeDefOf.RejectInput);
                return;
            }

            // Check cooldown
            int ticksSinceLastMark = Find.TickManager.TicksGame - lastPriorityMarkTick;
            if (ticksSinceLastMark < PRIORITY_MARK_COOLDOWN_TICKS)
            {
                int secondsRemaining = (PRIORITY_MARK_COOLDOWN_TICKS - ticksSinceLastMark) / 60;
                Messages.Message($"Mark Priority on cooldown ({secondsRemaining}s remaining).", wearer, MessageTypeDefOf.RejectInput);
                return;
            }

            // Set the global priority target
            globalPriorityTarget = target;
            priorityTargetExpireTick = Find.TickManager.TicksGame + PRIORITY_MARK_DURATION_TICKS;
            lastPriorityMarkTick = Find.TickManager.TicksGame;

            Messages.Message($"{target.LabelShort} marked as priority target by {wearer.LabelShort}'s HAWKEYE. DARTs converging.",
                target, MessageTypeDefOf.NeutralEvent);

            // Notify LATTICE to redirect DARTs
            var lattice = ArsenalNetworkManager.GlobalLattice;
            if (lattice != null)
            {
                lattice.SetPriorityTarget(target);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref wearer, "wearer");
            Scribe_Values.Look(ref lastPriorityMarkTick, "lastPriorityMarkTick", -9999);

            // Static state - only save/load once
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_References.Look(ref globalPriorityTarget, "globalPriorityTarget");
                Scribe_Values.Look(ref priorityTargetExpireTick, "priorityTargetExpireTick", 0);
            }
        }
    }

    public class CompProperties_HawkeyeSensor : CompProperties
    {
        public float detectionRadius = 30f;

        public CompProperties_HawkeyeSensor()
        {
            compClass = typeof(CompHawkeyeSensor);
        }
    }
}
