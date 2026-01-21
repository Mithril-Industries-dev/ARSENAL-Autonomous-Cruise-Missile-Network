using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// MORIA (Materials Orchestration and Resource Integration Archive) -
    /// Climate-controlled storage shelf for raw ores, refined materials, and components.
    /// Works like a vanilla shelf but requires power and network connection.
    /// MULEs can deliver resources here from mining and hauling tasks.
    /// </summary>
    public class Building_Moria : Building_Storage
    {
        // Power
        private CompPowerTrader powerComp;

        // Naming
        private string customName;
        private static int moriaCounter = 1;

        #region Properties

        public bool IsPoweredOn()
        {
            return powerComp == null || powerComp.PowerOn;
        }

        public bool HasNetworkConnection()
        {
            if (Map == null) return false;
            return ArsenalNetworkManager.IsTileConnected(Map.Tile);
        }

        public override string Label => customName ?? base.Label;

        /// <summary>
        /// Gets the count of items stored on this MORIA's cells.
        /// </summary>
        public int TotalStoredCount
        {
            get
            {
                if (Map == null) return 0;
                int count = 0;
                foreach (IntVec3 cell in AllSlotCells())
                {
                    foreach (Thing t in cell.GetThingList(Map))
                    {
                        if (t.def.category == ThingCategory.Item)
                        {
                            count += t.stackCount;
                        }
                    }
                }
                return count;
            }
        }

        /// <summary>
        /// Gets the number of unique item types stored.
        /// </summary>
        public int UniqueTypesCount
        {
            get
            {
                if (Map == null) return 0;
                HashSet<ThingDef> types = new HashSet<ThingDef>();
                foreach (IntVec3 cell in AllSlotCells())
                {
                    foreach (Thing t in cell.GetThingList(Map))
                    {
                        if (t.def.category == ThingCategory.Item)
                        {
                            types.Add(t.def);
                        }
                    }
                }
                return types.Count;
            }
        }

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            powerComp = GetComp<CompPowerTrader>();

            ArsenalNetworkManager.RegisterMoria(this);

            if (!respawningAfterLoad)
            {
                customName = "MORIA-" + moriaCounter.ToString("D2");
                moriaCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ArsenalNetworkManager.DeregisterMoria(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
        }

        #endregion

        #region Storage Acceptance

        /// <summary>
        /// Override to add power requirement for storage acceptance.
        /// </summary>
        public override bool Accepts(Thing t)
        {
            // Must be powered to accept items
            if (!IsPoweredOn())
            {
                return false;
            }

            return base.Accepts(t);
        }

        /// <summary>
        /// Checks if this MORIA can accept an item (for MULE delivery).
        /// </summary>
        public bool CanAcceptItem(Thing item)
        {
            if (item == null || item.Destroyed) return false;
            if (!IsPoweredOn()) return false;

            // Check storage filter
            if (!settings.AllowedToAccept(item)) return false;

            // Check if there's space on any cell
            foreach (IntVec3 cell in AllSlotCells())
            {
                if (cell.GetItemCount(Map) < cell.GetMaxItemsAllowedInCell(Map))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets a cell where an item can be placed.
        /// </summary>
        public IntVec3 GetStorageCell(Thing item)
        {
            if (!CanAcceptItem(item)) return IntVec3.Invalid;

            // Try to find a cell with existing stack of same type first
            foreach (IntVec3 cell in AllSlotCells())
            {
                foreach (Thing t in cell.GetThingList(Map))
                {
                    if (t.def == item.def && t.stackCount < t.def.stackLimit)
                    {
                        return cell;
                    }
                }
            }

            // Find an empty cell
            foreach (IntVec3 cell in AllSlotCells())
            {
                bool hasItem = false;
                foreach (Thing t in cell.GetThingList(Map))
                {
                    if (t.def.category == ThingCategory.Item)
                    {
                        hasItem = true;
                        break;
                    }
                }
                if (!hasItem)
                {
                    return cell;
                }
            }

            // Find any cell with room
            foreach (IntVec3 cell in AllSlotCells())
            {
                if (cell.GetItemCount(Map) < cell.GetMaxItemsAllowedInCell(Map))
                {
                    return cell;
                }
            }

            return IntVec3.Invalid;
        }

        /// <summary>
        /// Checks if this MORIA needs a specific resource.
        /// Used by LATTICE for task prioritization.
        /// </summary>
        public bool NeedsResource(ThingDef def)
        {
            if (!IsPoweredOn()) return false;
            if (!settings.AllowedToAccept(def)) return false;

            // Check if there's space
            foreach (IntVec3 cell in AllSlotCells())
            {
                if (cell.GetItemCount(Map) < cell.GetMaxItemsAllowedInCell(Map))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty()) text += "\n";

            text += $"Stored: {TotalStoredCount:N0} items ({UniqueTypesCount} types)";

            if (!IsPoweredOn())
            {
                text += "\n<color=#ff6666>No power - storage disabled</color>";
            }

            if (!HasNetworkConnection())
            {
                text += "\n<color=#ffaa00>No network connection</color>";
            }

            return text;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this MORIA.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameMoria(this));
                }
            };
        }

        #endregion

        #region Naming

        public void SetCustomName(string name)
        {
            customName = name;
        }

        #endregion
    }

    /// <summary>
    /// Dialog for renaming a MORIA.
    /// </summary>
    public class Dialog_RenameMoria : Window
    {
        private Building_Moria moria;
        private string newName;

        public Dialog_RenameMoria(Building_Moria m)
        {
            moria = m;
            newName = m.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename MORIA");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                moria.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
