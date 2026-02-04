using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// HAWKEYE Tactical Command Helmet - mobile sensor suite with two-way LATTICE integration.
    /// Acts as a MOBILE ARGUS NODE - targets detected by HawkEye are valid for DART engagement.
    /// Provides:
    /// - Mobile ARGUS-like threat detection (30 tile radius)
    /// - Network awareness (wearer knows all network-detected threats)
    /// - DAGGER strike designation ability
    /// - DART target marking ability
    /// </summary>
    public class Apparel_HawkEye : Apparel
    {
        private CompHawkeyeSensor sensorComp;

        // Cached directional textures for proper rendering
        private static Graphic_Multi cachedGraphic;
        private static readonly Vector2 DRAW_SIZE = new Vector2(1f, 1f); // Normal size

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
            InitGraphic();
        }

        private void InitGraphic()
        {
            if (cachedGraphic == null)
            {
                cachedGraphic = (Graphic_Multi)GraphicDatabase.Get<Graphic_Multi>(
                    "Arsenal/MITHRIL_HAWKEYE",
                    ShaderDatabase.Cutout,
                    DRAW_SIZE,
                    Color.white);
            }
        }

        public override void DrawWornExtras()
        {
            if (Wearer == null || Wearer.Dead || !Wearer.Spawned)
                return;

            // Don't draw if pawn is not standing (lying down, etc.)
            if (Wearer.GetPosture() != PawnPosture.Standing)
                return;

            InitGraphic();
            if (cachedGraphic == null)
                return;

            // Get the correct rotation based on pawn's facing direction
            Rot4 facing = Wearer.Rotation;

            // Get the correct material for this direction (this respects _north, _south, _east, _west textures)
            Material mat = cachedGraphic.MatAt(facing);

            // Calculate draw position - on pawn's head
            Vector3 drawPos = Wearer.DrawPos;

            // Head offset - move up to head level
            // Y is render layer (depth), Z is vertical position on screen
            drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor(); // Render above pawn
            drawPos.z += 0.34f; // Vertical offset to head position

            // Create a matrix for proper positioning and scaling
            Vector3 scale = new Vector3(DRAW_SIZE.x, 1f, DRAW_SIZE.y);
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, Quaternion.identity, scale);

            // Draw the helmet with proper directional texture
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0);
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

            // MARK DART TARGET ability (formerly Mark Priority)
            var markCmd = new Command_Target
            {
                defaultLabel = "MARK DART TARGET",
                defaultDesc = "Mark an enemy pawn as priority target for DART convergence. All DARTs will focus fire on the marked target until dead or 30 seconds expire.\n\nTargets marked by HAWKEYE are valid for DART engagement even outside ARGUS sensor range.",
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
                        SensorComp?.MarkDartTarget(targetPawn);
                    }
                }
            };
            if (!CanMarkTarget())
            {
                markCmd.Disable(GetMarkDisabledReason());
            }
            yield return markCmd;

            // Toggle LOS overlay (like ARGUS)
            yield return new Command_Toggle
            {
                defaultLabel = "Show LOS",
                defaultDesc = "Toggle line-of-sight overlay. Green cells are visible to the HAWKEYE sensor, red cells are blocked. White lines show blocked enemies, red lines show detected threats.",
                isActive = () => SensorComp.ShowLOSOverlay,
                toggleAction = delegate { SensorComp.ShowLOSOverlay = !SensorComp.ShowLOSOverlay; },
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", false)
            };

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

        private bool CanMarkTarget()
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
    /// Acts as a MOBILE ARGUS NODE - threats detected by this sensor are valid for DART engagement.
    /// </summary>
    public class CompHawkeyeSensor : ThingComp
    {
        private const float DETECTION_RADIUS = 30f;
        private const int PRIORITY_MARK_DURATION_TICKS = 1800; // 30 seconds
        private const int PRIORITY_MARK_COOLDOWN_TICKS = 1800; // 30 seconds

        private Pawn wearer;
        private int lastPriorityMarkTick = -9999;

        // LOS overlay toggle
        public bool ShowLOSOverlay = false;

        // Currently marked priority target (shared across all HAWKEYE helmets)
        private static Pawn globalPriorityTarget;
        private static int priorityTargetExpireTick;

        public CompProperties_HawkeyeSensor Props => (CompProperties_HawkeyeSensor)props;

        public Pawn Wearer => wearer;
        public float DetectionRadius => DETECTION_RADIUS;

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
            ShowLOSOverlay = false;
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
        /// These targets are valid for DART engagement even outside stationary ARGUS range.
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
        /// Checks if a specific pawn is detected by this HAWKEYE sensor.
        /// Used by DART/QUIVER targeting to allow engagement of targets outside ARGUS range.
        /// </summary>
        public bool CanDetectTarget(Pawn target)
        {
            if (!IsOperational || wearer?.Map == null || target == null)
                return false;

            if (target.Map != wearer.Map)
                return false;

            if (!target.HostileTo(Faction.OfPlayer))
                return false;

            if (target.Dead || target.Downed)
                return false;

            float distance = target.Position.DistanceTo(wearer.Position);
            if (distance > DETECTION_RADIUS)
                return false;

            return GenSight.LineOfSight(wearer.Position, target.Position, wearer.Map);
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
        /// Targets marked by HAWKEYE are valid for engagement even outside ARGUS range.
        /// </summary>
        public void MarkDartTarget(Pawn target)
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
                Messages.Message($"MARK DART TARGET on cooldown ({secondsRemaining}s remaining).", wearer, MessageTypeDefOf.RejectInput);
                return;
            }

            // Set the global priority target
            globalPriorityTarget = target;
            priorityTargetExpireTick = Find.TickManager.TicksGame + PRIORITY_MARK_DURATION_TICKS;
            lastPriorityMarkTick = Find.TickManager.TicksGame;

            Messages.Message($"{target.LabelShort} marked as DART target by {wearer.LabelShort}'s HAWKEYE. DARTs converging.",
                target, MessageTypeDefOf.NeutralEvent);

            // Notify LATTICE to redirect DARTs
            var lattice = ArsenalNetworkManager.GlobalLattice;
            if (lattice != null)
            {
                lattice.SetPriorityTarget(target);
            }
        }

        #region LOS Visualization

        /// <summary>
        /// Draws the LOS overlay when enabled and wearer is selected.
        /// Called by MapComponent_HawkeyeOverlay.
        /// </summary>
        public void DrawLOSOverlay()
        {
            if (!ShowLOSOverlay || wearer == null || wearer.Map == null)
                return;

            // Draw detection radius ring
            GenDraw.DrawRadiusRing(wearer.Position, DETECTION_RADIUS);

            // Draw lines to threats in range
            DrawThreatLines();

            // Draw LOS coverage cells
            DrawLOSCells();
        }

        private void DrawThreatLines()
        {
            if (wearer?.Map == null) return;

            foreach (Pawn pawn in wearer.Map.mapPawns.AllPawnsSpawned)
            {
                if (!pawn.HostileTo(Faction.OfPlayer)) continue;
                if (pawn.Dead || pawn.Downed) continue;

                float distance = pawn.Position.DistanceTo(wearer.Position);
                if (distance > DETECTION_RADIUS) continue;

                bool hasLOS = GenSight.LineOfSight(wearer.Position, pawn.Position, wearer.Map);

                // Draw line - red if has LOS (threat detected), white if blocked
                if (hasLOS)
                {
                    GenDraw.DrawLineBetween(
                        wearer.Position.ToVector3Shifted(),
                        pawn.Position.ToVector3Shifted(),
                        SimpleColor.Red
                    );
                }
                else
                {
                    GenDraw.DrawLineBetween(
                        wearer.Position.ToVector3Shifted(),
                        pawn.Position.ToVector3Shifted(),
                        SimpleColor.White
                    );
                }
            }
        }

        private void DrawLOSCells()
        {
            if (wearer?.Map == null) return;

            int radius = (int)DETECTION_RADIUS;

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(wearer.Position, radius, true))
            {
                if (!cell.InBounds(wearer.Map)) continue;
                if (cell == wearer.Position) continue;

                bool hasLOS = GenSight.LineOfSight(wearer.Position, cell, wearer.Map);

                if (hasLOS)
                {
                    // Visible - draw green
                    CellRenderer.RenderCell(cell, SolidColorMaterials.SimpleSolidColorMaterial(new Color(0f, 1f, 0f, 0.15f)));
                }
                else
                {
                    // Blocked - draw red
                    CellRenderer.RenderCell(cell, SolidColorMaterials.SimpleSolidColorMaterial(new Color(1f, 0f, 0f, 0.15f)));
                }
            }
        }

        #endregion

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_References.Look(ref wearer, "wearer");
            Scribe_Values.Look(ref lastPriorityMarkTick, "lastPriorityMarkTick", -9999);
            Scribe_Values.Look(ref ShowLOSOverlay, "showLOSOverlay", false);

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

    /// <summary>
    /// MapComponent that handles drawing HAWKEYE LOS overlays.
    /// Draws overlays for pawns wearing HAWKEYE helmets when they are selected.
    /// </summary>
    public class MapComponent_HawkeyeOverlay : MapComponent
    {
        public MapComponent_HawkeyeOverlay(Map map) : base(map)
        {
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();

            // Draw overlays for selected pawns with HAWKEYE
            if (Find.Selector.SelectedObjects == null)
                return;

            foreach (object obj in Find.Selector.SelectedObjects)
            {
                if (obj is Pawn pawn && pawn.Map == map && !pawn.Dead)
                {
                    // Check if pawn is wearing HAWKEYE
                    var hawkeye = pawn.apparel?.WornApparel?.FirstOrDefault(a => a is Apparel_HawkEye) as Apparel_HawkEye;
                    if (hawkeye?.SensorComp != null && hawkeye.SensorComp.ShowLOSOverlay)
                    {
                        hawkeye.SensorComp.DrawLOSOverlay();
                    }
                }
            }
        }
    }
}
