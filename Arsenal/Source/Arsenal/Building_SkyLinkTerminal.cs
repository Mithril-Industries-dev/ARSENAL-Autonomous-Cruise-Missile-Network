using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// SKYLINK Terminal - connects local LATTICE to orbital satellite network.
    /// Must be placed within 15 tiles of a LATTICE hub.
    /// Passive receiver/transmitter - no player interaction required after placement.
    /// </summary>
    public class Building_SkyLinkTerminal : Building
    {
        private const float LATTICE_PROXIMITY_RANGE = 15f;

        private CompPowerTrader powerComp;
        private Building_Lattice linkedLattice;
        private int lastLatticeCheck = -999;
        private const int LATTICE_CHECK_INTERVAL = 120; // 2 seconds

        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;

        public bool IsOnline => IsPoweredOn && HasLatticeInRange && ArsenalNetworkManager.IsSatelliteInOrbit();

        public bool HasLatticeInRange
        {
            get
            {
                RefreshLatticeLink();
                return linkedLattice != null && linkedLattice.IsPoweredOn();
            }
        }

        public Building_Lattice LinkedLattice
        {
            get
            {
                RefreshLatticeLink();
                return linkedLattice;
            }
        }

        private void RefreshLatticeLink()
        {
            if (Find.TickManager.TicksGame - lastLatticeCheck < LATTICE_CHECK_INTERVAL)
                return;

            lastLatticeCheck = Find.TickManager.TicksGame;

            // Find LATTICE within range on same map
            linkedLattice = Map.listerBuildings.AllBuildingsColonistOfClass<Building_Lattice>()
                .FirstOrDefault(l => l.Position.DistanceTo(Position) <= LATTICE_PROXIMITY_RANGE);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();

            // Always register - static managers are reset on game load
            ArsenalNetworkManager.RegisterTerminal(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ArsenalNetworkManager.DeregisterTerminal(this);
            base.DeSpawn(mode);
        }

        public override void TickRare()
        {
            base.TickRare();
            RefreshLatticeLink();
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            // Satellite status
            if (!str.NullOrEmpty()) str += "\n";
            if (ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                str += "Satellite: IN ORBIT";
            }
            else
            {
                str += "<color=yellow>Satellite: NONE — Launch required</color>";
            }

            // LATTICE link status
            str += "\n";
            if (linkedLattice != null)
            {
                float dist = Position.DistanceTo(linkedLattice.Position);
                if (linkedLattice.IsPoweredOn())
                {
                    str += $"LATTICE Link: CONNECTED ({dist:F0} tiles)";
                }
                else
                {
                    str += $"<color=yellow>LATTICE Link: UNPOWERED ({dist:F0} tiles)</color>";
                }
            }
            else
            {
                str += "<color=red>LATTICE Link: NOT FOUND — Place within 15 tiles of LATTICE</color>";
            }

            // Overall network status
            str += "\n";
            if (IsOnline)
            {
                str += "Network Status: ONLINE — Global operations enabled";
            }
            else if (!IsPoweredOn)
            {
                str += "<color=yellow>Network Status: OFFLINE — No power</color>";
            }
            else if (!ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                str += "<color=yellow>Network Status: OFFLINE — No satellite</color>";
            }
            else
            {
                str += "<color=yellow>Network Status: OFFLINE — No LATTICE link</color>";
            }

            return str;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // Show LATTICE range indicator when selected
            if (Find.Selector.IsSelected(this))
            {
                yield return new Command_Action
                {
                    defaultLabel = "Show Range",
                    defaultDesc = "Shows the 15-tile range for LATTICE connection.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", false),
                    action = delegate
                    {
                        // This is a placeholder - the range is shown via DrawExtraSelectionOverlays
                    }
                };
            }
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();

            // Draw LATTICE proximity range
            GenDraw.DrawRadiusRing(Position, LATTICE_PROXIMITY_RANGE);

            // Draw line to linked LATTICE if found
            if (linkedLattice != null)
            {
                GenDraw.DrawLineBetween(DrawPos, linkedLattice.DrawPos,
                    linkedLattice.IsPoweredOn() ? SimpleColor.Green : SimpleColor.Yellow);
            }
        }
    }

    /// <summary>
    /// PlaceWorker ensuring Terminal is placed within range of a LATTICE.
    /// Shows warning but allows placement (LATTICE can be built later).
    /// </summary>
    public class PlaceWorker_NearLattice : PlaceWorker
    {
        private const float LATTICE_PROXIMITY_RANGE = 15f;

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // Check for LATTICE in range
            var lattice = map.listerBuildings.AllBuildingsColonistOfClass<Building_Lattice>()
                .FirstOrDefault(l => l.Position.DistanceTo(loc) <= LATTICE_PROXIMITY_RANGE);

            if (lattice == null)
            {
                // Check for LATTICE blueprints in range
                var blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                    .Where(b => b.def.entityDefToBuild?.defName == "Arsenal_LATTICE")
                    .Any(b => b.Position.DistanceTo(loc) <= LATTICE_PROXIMITY_RANGE);

                if (!blueprints)
                {
                    return new AcceptanceReport("No LATTICE within 15 tiles. Terminal will be inactive until LATTICE is built nearby.");
                }
            }

            return AcceptanceReport.WasAccepted;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            GenDraw.DrawRadiusRing(center, LATTICE_PROXIMITY_RANGE);
        }
    }
}
