using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// HERALD - Long-range communication relay.
    /// Connects remote map tiles to the MITHRIL LATTICE network.
    /// Required for HUBs and HOPs on remote tiles to communicate with LATTICE.
    /// Buildings on the LATTICE's home tile do NOT need a HERALD.
    /// </summary>
    public class Building_HERALD : Building
    {
        // Components
        private CompPowerTrader powerComp;

        // Custom name
        private string customName;
        private static int heraldCounter = 1;

        /// <summary>
        /// Sets the herald counter to a specific value.
        /// Called after game load to prevent duplicate names.
        /// </summary>
        public static void SetCounter(int value)
        {
            heraldCounter = System.Math.Max(1, value);
        }

        #region Properties

        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;

        /// <summary>
        /// HERALD is online if powered AND SKYLINK satellite is operational.
        /// </summary>
        public bool IsOnline => IsPoweredOn && ArsenalNetworkManager.IsLatticeConnectedToSkylink();

        /// <summary>
        /// Returns whether LATTICE network is accessible from this tile.
        /// Requires: HERALD powered + SKYLINK satellite + Terminal linked to LATTICE.
        /// </summary>
        public bool IsNetworkConnected
        {
            get
            {
                if (!IsPoweredOn)
                    return false;

                // Require SKYLINK connection
                if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
                    return false;

                var lattice = ArsenalNetworkManager.GlobalLattice;
                return lattice != null && lattice.IsPoweredOn();
            }
        }

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();

            if (!respawningAfterLoad)
            {
                customName = "HERALD-" + heraldCounter.ToString("D2");
                heraldCounter++;
            }

            ArsenalNetworkManager.RegisterHerald(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ArsenalNetworkManager.DeregisterHerald(this);
            base.DeSpawn(mode);
        }

        public override string Label => customName ?? base.Label;

        public void SetCustomName(string name)
        {
            customName = name;
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
                defaultDesc = "Rename this HERALD relay.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameHerald(this));
                }
            };
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!str.NullOrEmpty())
                str += "\n";

            // Power status
            if (!IsPoweredOn)
            {
                str += "Status: OFFLINE (no power)";
            }
            else
            {
                str += "Status: ONLINE";
            }

            // SKYLINK satellite status
            str += "\n";
            if (!ArsenalNetworkManager.IsSatelliteInOrbit())
            {
                str += "<color=yellow>SKYLINK: NO SATELLITE — Launch required</color>";
            }
            else if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
            {
                str += "<color=yellow>SKYLINK: NOT LINKED — Terminal required near LATTICE</color>";
            }
            else
            {
                str += "SKYLINK: CONNECTED";
            }

            // Network connection status
            var lattice = ArsenalNetworkManager.GlobalLattice;
            if (lattice == null)
            {
                str += "\nNetwork: DISCONNECTED — No LATTICE exists";
            }
            else if (!lattice.IsPoweredOn())
            {
                str += "\nNetwork: DISCONNECTED — LATTICE unpowered";
            }
            else if (!IsPoweredOn)
            {
                str += "\nNetwork: DISCONNECTED — HERALD unpowered";
            }
            else if (!ArsenalNetworkManager.IsLatticeConnectedToSkylink())
            {
                str += "\nNetwork: DISCONNECTED — No SKYLINK uplink";
            }
            else
            {
                str += "\nNetwork: CONNECTED via SKYLINK";
            }

            // Show what this tile can now access
            if (IsNetworkConnected)
            {
                str += "\n\nThis tile has network access.";
                str += "\nHUBs/HOPs here can communicate with LATTICE.";
            }

            return str;
        }

        #endregion

        #region Save/Load

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
        }

        #endregion
    }

    /// <summary>
    /// Dialog for renaming a HERALD.
    /// </summary>
    public class Dialog_RenameHerald : Window
    {
        private Building_HERALD herald;
        private string curName;

        public Dialog_RenameHerald(Building_HERALD herald)
        {
            this.herald = herald;
            this.curName = herald.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename HERALD");
            Text.Font = GameFont.Small;

            curName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), curName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                herald.SetCustomName(curName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
