using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// STABLE (Storage Terminal And Battery Loading Enclosure) - MULE storage, charging, and deployment hub.
    /// Stores up to 10 MULEs and charges them when powered.
    /// </summary>
    public class Building_Stable : Building
    {
        // Docked MULEs
        private List<MULE_Drone> dockedMules = new List<MULE_Drone>();
        public const int MAX_MULE_CAPACITY = 10;

        // Power
        private CompPowerTrader powerComp;

        // Naming
        private string customName;
        private static int stableCounter = 1;

        // Network
        private int lastNetworkCacheRefresh = -999;
        private const int NETWORK_CACHE_INTERVAL = 120;

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

        public int DockedMuleCount => dockedMules.Count;
        public int AvailableMuleCount => dockedMules.Count(m => m.state == MuleState.Idle && m.IsBatteryFull);
        public bool HasSpace => dockedMules.Count < MAX_MULE_CAPACITY;
        public override string Label => customName ?? base.Label;
        public IReadOnlyList<MULE_Drone> DockedMules => dockedMules;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            powerComp = GetComp<CompPowerTrader>();

            ArsenalNetworkManager.RegisterStable(this);

            if (!respawningAfterLoad)
            {
                customName = "STABLE-" + stableCounter.ToString("D2");
                stableCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Release all docked MULEs before despawning
            foreach (var mule in dockedMules.ToList())
            {
                ReleaseMule(mule);
            }

            ArsenalNetworkManager.DeregisterStable(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref customName, "customName");
            Scribe_Collections.Look(ref dockedMules, "dockedMules", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                dockedMules = dockedMules ?? new List<MULE_Drone>();
                dockedMules.RemoveAll(m => m == null);

                // Re-assign home stable to docked MULEs
                foreach (var mule in dockedMules)
                {
                    mule.SetHomeStable(this);
                }
            }
        }

        #endregion

        #region Tick

        public override void TickRare()
        {
            base.TickRare();

            // Clean up null references
            dockedMules.RemoveAll(m => m == null || m.Destroyed);

            // Charge docked MULEs if powered
            if (IsPoweredOn())
            {
                foreach (var mule in dockedMules)
                {
                    if (mule.state == MuleState.Charging || mule.state == MuleState.Idle)
                    {
                        // Charging is handled in MULE's tick, but we ensure state is correct
                        if (mule.BatteryPercent < 1f && mule.state != MuleState.Charging)
                        {
                            mule.SetState(MuleState.Charging);
                        }
                        else if (mule.IsBatteryFull && mule.state == MuleState.Charging)
                        {
                            mule.SetState(MuleState.Idle);
                        }
                    }
                }
            }
        }

        #endregion

        #region MULE Management

        /// <summary>
        /// Checks if this STABLE can accept a MULE (has space and power).
        /// </summary>
        public bool CanAcceptMule()
        {
            return HasSpace && !Destroyed;
        }

        /// <summary>
        /// Gets an available MULE that can handle the given task.
        /// </summary>
        public MULE_Drone GetAvailableMule(MuleTask task)
        {
            if (!IsPoweredOn()) return null;

            foreach (var mule in dockedMules)
            {
                if (mule.CanAcceptTask(task))
                {
                    return mule;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets any available MULE that is idle and fully charged.
        /// </summary>
        public MULE_Drone GetAvailableMule()
        {
            if (!IsPoweredOn()) return null;

            return dockedMules.FirstOrDefault(m =>
                m.state == MuleState.Idle && m.IsBatteryFull);
        }

        /// <summary>
        /// Docks a MULE into this STABLE.
        /// </summary>
        public bool DockMule(MULE_Drone mule)
        {
            if (!CanAcceptMule()) return false;
            if (dockedMules.Contains(mule)) return true; // Already docked

            dockedMules.Add(mule);
            mule.SetHomeStable(this);

            // Despawn the MULE from the map (it's now stored)
            if (mule.Spawned)
            {
                mule.DeSpawn(DestroyMode.Vanish);
            }

            return true;
        }

        /// <summary>
        /// Deploys a MULE from this STABLE to perform a task.
        /// </summary>
        public bool DeployMule(MULE_Drone mule, MuleTask task)
        {
            if (!dockedMules.Contains(mule)) return false;
            if (!mule.CanAcceptTask(task)) return false;

            // Spawn the MULE near the STABLE
            IntVec3 spawnCell = GetMuleSpawnCell();
            if (!spawnCell.IsValid) return false;

            dockedMules.Remove(mule);

            GenSpawn.Spawn(mule, spawnCell, Map);
            mule.AssignTask(task);

            return true;
        }

        /// <summary>
        /// Releases a MULE from storage without a task (spawns it nearby).
        /// </summary>
        public void ReleaseMule(MULE_Drone mule)
        {
            if (!dockedMules.Contains(mule)) return;

            dockedMules.Remove(mule);

            if (Map != null && !mule.Spawned)
            {
                IntVec3 spawnCell = GetMuleSpawnCell();
                if (spawnCell.IsValid)
                {
                    GenSpawn.Spawn(mule, spawnCell, Map);
                    mule.SetState(MuleState.Idle);
                }
            }
        }

        /// <summary>
        /// Creates a new MULE and docks it in this STABLE.
        /// Used when manufacturing MULEs.
        /// </summary>
        public MULE_Drone CreateAndDockMule()
        {
            if (!HasSpace) return null;

            MULE_Drone newMule = (MULE_Drone)ThingMaker.MakeThing(ArsenalDefOf.Arsenal_MULE_Drone);
            newMule.SetHomeStable(this);
            newMule.SetState(MuleState.Idle);

            dockedMules.Add(newMule);
            return newMule;
        }

        private IntVec3 GetMuleSpawnCell()
        {
            // Find a valid cell adjacent to the STABLE
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (cell.InBounds(Map) && cell.Walkable(Map) && !cell.Fogged(Map))
                {
                    return cell;
                }
            }

            // Fallback: try any nearby cell
            IntVec3 result;
            if (CellFinder.TryFindRandomCellNear(Position, Map, 5,
                c => c.Walkable(Map) && !c.Fogged(Map), out result))
            {
                return result;
            }

            return IntVec3.Invalid;
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty()) text += "\n";

            text += $"MULEs docked: {DockedMuleCount}/{MAX_MULE_CAPACITY}";
            text += $"\nReady for deployment: {AvailableMuleCount}";

            if (!IsPoweredOn())
            {
                text += "\n<color=#ff6666>No power - cannot charge MULEs</color>";
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
                defaultDesc = "Rename this STABLE.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameStable(this));
                }
            };

            // Show MULE status
            yield return new Command_Action
            {
                defaultLabel = $"MULEs: {DockedMuleCount}",
                defaultDesc = $"Docked: {DockedMuleCount}/{MAX_MULE_CAPACITY}\nReady: {AvailableMuleCount}\n\nClick to see MULE details.",
                icon = ContentFinder<Texture2D>.Get("Arsenal/MITHRIL_MULE", false),
                action = delegate
                {
                    // Show list of MULEs
                    string msg = $"{Label} MULE Status:\n\n";
                    if (dockedMules.Count == 0)
                    {
                        msg += "No MULEs docked.";
                    }
                    else
                    {
                        foreach (var mule in dockedMules)
                        {
                            msg += $"- {mule.Label}: {mule.state}, Battery: {mule.BatteryPercent:P0}\n";
                        }
                    }
                    Messages.Message(msg, this, MessageTypeDefOf.NeutralEvent);
                }
            };

            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Add MULE",
                    action = delegate
                    {
                        if (HasSpace)
                        {
                            CreateAndDockMule();
                            Messages.Message("MULE created and docked.", this, MessageTypeDefOf.PositiveEvent);
                        }
                        else
                        {
                            Messages.Message("STABLE is full.", this, MessageTypeDefOf.RejectInput);
                        }
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Deploy All",
                    action = delegate
                    {
                        foreach (var mule in dockedMules.ToList())
                        {
                            ReleaseMule(mule);
                        }
                    }
                };
            }
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
    /// Dialog for renaming a STABLE.
    /// </summary>
    public class Dialog_RenameStable : Window
    {
        private Building_Stable stable;
        private string newName;

        public Dialog_RenameStable(Building_Stable s)
        {
            stable = s;
            newName = s.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename STABLE");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                stable.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
