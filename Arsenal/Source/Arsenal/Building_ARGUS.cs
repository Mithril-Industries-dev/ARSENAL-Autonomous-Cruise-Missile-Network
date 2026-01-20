using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// ARGUS - Autonomous threat detection sensor.
    /// Detects hostile pawns within range using line-of-sight checks.
    /// Reports threats to LATTICE for coordination.
    /// </summary>
    public class Building_ARGUS : Building
    {
        // Detection parameters
        private const int SCAN_INTERVAL = 60; // Ticks between scans (~1 second)
        private const float DETECTION_RADIUS = 45f;

        // Link to LATTICE
        private Building_Lattice linkedLattice;

        // Custom name
        private string customName;
        private static int argusCounter = 1;

        // Tracking for inspect panel
        private int lastScanThreatCount = 0;
        private bool threatDetectedThisScan = false;

        // Toggle for LOS overlay visualization
        private bool showLOSOverlay = false;

        #region Properties

        public bool IsPoweredOn => true; // ARGUS produces power, doesn't consume it
        public bool IsOnline => linkedLattice != null && linkedLattice.IsPoweredOn();
        public float DetectionRadius => DETECTION_RADIUS;
        public int ThreatsInRange => lastScanThreatCount;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            if (!respawningAfterLoad)
            {
                customName = "ARGUS-" + argusCounter.ToString("D2");
                argusCounter++;
            }

            FindAndRegisterWithLattice();
            ArsenalNetworkManager.RegisterArgus(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            linkedLattice?.UnregisterArgus(this);
            ArsenalNetworkManager.DeregisterArgus(this);
            base.DeSpawn(mode);
        }

        public override string Label => customName ?? base.Label;

        public void SetCustomName(string name)
        {
            customName = name;
        }

        #endregion

        #region Scanning

        protected override void Tick()
        {
            base.Tick();

            if (!this.IsHashIntervalTick(SCAN_INTERVAL))
                return;

            if (linkedLattice == null || !linkedLattice.IsPoweredOn())
            {
                // Try to find LATTICE if we don't have one
                FindAndRegisterWithLattice();
                lastScanThreatCount = 0;
                threatDetectedThisScan = false;
                return;
            }

            ScanForThreats();
        }

        private void ScanForThreats()
        {
            int threatCount = 0;
            bool detectedThreat = false;

            foreach (Pawn pawn in Map.mapPawns.AllPawnsSpawned)
            {
                // Skip non-hostiles
                if (!pawn.HostileTo(Faction.OfPlayer))
                    continue;

                // Skip dead or downed
                if (pawn.Dead || pawn.Downed)
                    continue;

                // Check distance
                float distance = pawn.Position.DistanceTo(this.Position);
                if (distance > DETECTION_RADIUS)
                    continue;

                // LOS check - this is what makes ARGUS different from old LATTICE detection
                // Walls, mountains, thick roofs block detection
                if (!GenSight.LineOfSight(this.Position, pawn.Position, Map))
                    continue;

                // Report to LATTICE
                linkedLattice.ReportThreat(pawn, this);
                threatCount++;
                detectedThreat = true;
            }

            lastScanThreatCount = threatCount;
            threatDetectedThisScan = detectedThreat;
        }

        #endregion

        #region LATTICE Registration

        private void FindAndRegisterWithLattice()
        {
            linkedLattice = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Lattice>().FirstOrDefault();
            linkedLattice?.RegisterArgus(this);
        }

        /// <summary>
        /// Called when LATTICE spawns to notify existing ARGUS sensors.
        /// </summary>
        public void OnLatticeAvailable(Building_Lattice lattice)
        {
            if (lattice != null && lattice.Map == Map)
            {
                linkedLattice = lattice;
            }
        }

        /// <summary>
        /// Called when LATTICE is destroyed.
        /// </summary>
        public void OnLatticeDestroyed()
        {
            linkedLattice = null;
        }

        #endregion

        #region UI

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this ARGUS sensor.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameArgus(this));
                }
            };

            // Toggle LOS overlay
            yield return new Command_Toggle
            {
                defaultLabel = "Show LOS",
                defaultDesc = "Toggle line-of-sight overlay. Green cells are visible, red cells are blocked by terrain or structures. Red lines show enemies in range but blocked. White lines show detected threats.",
                isActive = () => showLOSOverlay,
                toggleAction = delegate { showLOSOverlay = !showLOSOverlay; },
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", false)
            };

            // Debug gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Force Scan",
                    action = delegate
                    {
                        ScanForThreats();
                    }
                };
            }
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!str.NullOrEmpty())
                str += "\n";

            str += $"Detection radius: {DETECTION_RADIUS} tiles";

            // Status
            if (linkedLattice == null)
            {
                str += "\nStatus: OFFLINE (no LATTICE)";
            }
            else if (!linkedLattice.IsPoweredOn())
            {
                str += "\nStatus: OFFLINE (LATTICE unpowered)";
            }
            else if (threatDetectedThisScan)
            {
                str += "\n<color=red>Status: THREAT DETECTED</color>";
            }
            else
            {
                str += "\nStatus: ONLINE";
            }

            str += $"\nThreats in range: {lastScanThreatCount}";

            if (linkedLattice != null)
            {
                str += $"\nConnected to: {linkedLattice.Label}";
            }
            else
            {
                str += "\nConnected to: NO LATTICE";
            }

            return str;
        }

        // Draw detection radius and LOS visualization when selected
        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();

            // Draw detection radius ring
            GenDraw.DrawRadiusRing(Position, DETECTION_RADIUS);

            // Draw lines to threats in range (always shown when selected)
            DrawThreatLines();

            // Draw LOS coverage overlay if enabled
            if (showLOSOverlay)
            {
                DrawLOSOverlay();
            }
        }

        /// <summary>
        /// Draws lines from ARGUS to all hostile pawns in range.
        /// Red = detected (has LOS), White = blocked (no LOS)
        /// </summary>
        private void DrawThreatLines()
        {
            if (Map == null) return;

            foreach (Pawn pawn in Map.mapPawns.AllPawnsSpawned)
            {
                if (!pawn.HostileTo(Faction.OfPlayer)) continue;
                if (pawn.Dead || pawn.Downed) continue;

                float distance = pawn.Position.DistanceTo(Position);
                if (distance > DETECTION_RADIUS) continue;

                bool hasLOS = GenSight.LineOfSight(Position, pawn.Position, Map);

                // Draw line - red if has LOS (threat detected), white/gray if blocked
                if (hasLOS)
                {
                    GenDraw.DrawLineBetween(
                        Position.ToVector3Shifted(),
                        pawn.Position.ToVector3Shifted(),
                        SimpleColor.Red
                    );
                }
                else
                {
                    GenDraw.DrawLineBetween(
                        Position.ToVector3Shifted(),
                        pawn.Position.ToVector3Shifted(),
                        SimpleColor.White
                    );
                }
            }
        }

        /// <summary>
        /// Draws an overlay showing which cells have line-of-sight.
        /// Green = visible, Red = blocked
        /// </summary>
        private void DrawLOSOverlay()
        {
            if (Map == null) return;

            int radius = (int)DETECTION_RADIUS;

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(Position, radius, true))
            {
                if (!cell.InBounds(Map)) continue;
                if (cell == Position) continue;

                bool hasLOS = GenSight.LineOfSight(Position, cell, Map);

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

        #region Save/Load

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_References.Look(ref linkedLattice, "linkedLattice");
        }

        #endregion
    }

    /// <summary>
    /// Dialog for renaming an ARGUS.
    /// </summary>
    public class Dialog_RenameArgus : Window
    {
        private Building_ARGUS argus;
        private string curName;

        public Dialog_RenameArgus(Building_ARGUS argus)
        {
            this.argus = argus;
            this.curName = argus.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename ARGUS");
            Text.Font = GameFont.Small;

            curName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), curName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                argus.SetCustomName(curName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }

    /// <summary>
    /// PlaceWorker to show detection radius when placing ARGUS.
    /// </summary>
    public class PlaceWorker_ShowDetectionRadius : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            GenDraw.DrawRadiusRing(center, 45f); // ARGUS detection radius
        }
    }
}
