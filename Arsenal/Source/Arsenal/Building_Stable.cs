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
        private List<MULE_Pawn> dockedMules = new List<MULE_Pawn>();
        public const int MAX_MULE_CAPACITY = 10;

        // Power
        private CompPowerTrader powerComp;

        // Naming
        private string customName;
        private static int stableCounter = 1;

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
        public IReadOnlyList<MULE_Pawn> DockedMules => dockedMules;

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
                dockedMules = dockedMules ?? new List<MULE_Pawn>();
                dockedMules.RemoveAll(m => m == null);

                // Re-assign home stable to docked MULEs
                foreach (var mule in dockedMules)
                {
                    mule.homeStable = this;
                }
            }
        }

        #endregion

        #region Tick

        protected override void Tick()
        {
            base.Tick();

            // On remote tiles (no local LATTICE), check for deployment opportunities more frequently
            // This reduces the delay when MULEs return and new tasks are waiting
            bool isRemoteTile = ArsenalNetworkManager.GetLatticeOnMap(Map) == null;
            if (isRemoteTile && this.IsHashIntervalTick(60)) // Every second on remote tiles
            {
                // Only do the quick deploy check if we have ready MULEs
                if (AvailableMuleCount > 0)
                {
                    TryDeployForTasks();
                }
            }
        }

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
                        var battery = mule.BatteryComp;
                        if (battery == null) continue;

                        // Charge for 250 ticks worth (TickRare interval)
                        for (int i = 0; i < 250; i++)
                        {
                            battery.Charge();
                        }

                        // Update state based on charge level
                        if (!battery.IsFull && mule.state != MuleState.Charging)
                        {
                            mule.state = MuleState.Charging;
                        }
                        else if (battery.IsFull && mule.state == MuleState.Charging)
                        {
                            mule.state = MuleState.Idle;
                        }
                    }
                }
            }

            // Try to deploy available MULEs for tasks
            TryDeployForTasks();
        }

        /// <summary>
        /// Checks for available tasks and deploys ready MULEs to handle them.
        /// Works on both home tile (with LATTICE) and remote tiles (local scanning).
        /// </summary>
        private void TryDeployForTasks()
        {
            if (!IsPoweredOn()) return;

            // Get a ready MULE
            MULE_Pawn readyMule = dockedMules.FirstOrDefault(m =>
                m.state == MuleState.Idle && m.IsBatteryFull);

            if (readyMule == null) return;

            // Try to get a task from LATTICE (only if connected to network)
            if (HasNetworkConnection())
            {
                Building_Lattice lattice = ArsenalNetworkManager.GetConnectedLattice(Map);
                if (lattice != null)
                {
                    MuleTask task = lattice.RequestNewTaskForMule(readyMule);
                    if (task != null)
                    {
                        if (DeployMule(readyMule, task))
                        {
                            Log.Message($"[STABLE] {Label}: Deployed {readyMule.Label} for {task.taskType} at {task.targetCell}");
                        }
                        return;
                    }
                }
            }

            // Always try local scanning - works on remote tiles even without network connection
            // This allows MULEs to work autonomously on asteroids, outposts, etc.
            MuleTask localTask = ScanForLocalTask(readyMule);
            if (localTask != null)
            {
                if (DeployMule(readyMule, localTask))
                {
                    Log.Message($"[STABLE] {Label}: Deployed {readyMule.Label} for local {localTask.taskType} at {localTask.targetCell}");
                }
            }
        }

        /// <summary>
        /// Scans for local tasks when LATTICE has no pending tasks.
        /// Includes SLING loading as highest priority hauling task.
        /// </summary>
        private MuleTask ScanForLocalTask(MULE_Pawn mule)
        {
            if (Map == null) return null;

            // Mining tasks - iterate all mining designations
            foreach (var miningDes in Map.designationManager.AllDesignations
                .Where(d => d.def == DesignationDefOf.Mine && !d.target.HasThing))
            {
                IntVec3 cell = miningDes.target.Cell;
                Building mineable = cell.GetFirstMineable(Map);
                if (mineable == null) continue;

                // Skip if reserved by another pawn
                if (Map.reservationManager.IsReservedByAnyoneOf(mineable, Faction.OfPlayer)) continue;

                // Create task and check if MULE can accept it
                MuleTask task = MuleTask.CreateMiningTask(cell, miningDes);
                if (mule.CanAcceptTask(task))
                {
                    return task;
                }
            }

            // Hauling tasks
            var haulables = Map.listerHaulables.ThingsPotentiallyNeedingHauling();
            foreach (Thing item in haulables)
            {
                if (item == null || item.Destroyed || !item.Spawned) continue;
                if (item.IsForbidden(Faction.OfPlayer)) continue;

                // Skip if reserved by another pawn
                if (Map.reservationManager.IsReservedByAnyoneOf(item, Faction.OfPlayer)) continue;

                // Priority 1: Loading SLING (has timeout)
                SLING_Thing loadingSling = FindLoadingSlingForItem(item);
                if (loadingSling != null)
                {
                    return MuleTask.CreateSlingLoadTask(item, loadingSling);
                }

                // Priority 2: MORIA storage
                Building_Moria moria = ArsenalNetworkManager.GetNearestMoriaForItem(item, Map);
                if (moria != null)
                {
                    return MuleTask.CreateMoriaFeedTask(item, moria);
                }

                // Priority 3: Try to find a stockpile
                if (StoreUtility.TryFindBestBetterStoreCellFor(item, null, Map,
                    StoragePriority.Unstored, Faction.OfPlayer, out IntVec3 stockpile, true))
                {
                    return MuleTask.CreateHaulTask(item, null, stockpile);
                }
            }

            return null;
        }

        private SLING_Thing FindLoadingSlingForItem(Thing item)
        {
            // Loading happens on slot 1 (primary staging slot)
            foreach (var perch in ArsenalNetworkManager.GetPerchesOnMap(Map))
            {
                if (!perch.HasSlot1Sling) continue;

                var sling = perch.Slot1Sling as SLING_Thing;
                if (sling == null || !sling.IsLoading) continue;
                if (!sling.WantsItem(item.def)) continue;

                return sling;
            }
            return null;
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
        public MULE_Pawn GetAvailableMule(MuleTask task)
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
        public MULE_Pawn GetAvailableMule()
        {
            if (!IsPoweredOn()) return null;

            return dockedMules.FirstOrDefault(m =>
                m.state == MuleState.Idle && m.IsBatteryFull);
        }

        /// <summary>
        /// Docks a MULE into this STABLE.
        /// </summary>
        public bool DockMule(MULE_Pawn mule)
        {
            if (!CanAcceptMule()) return false;
            if (dockedMules.Contains(mule)) return true; // Already docked

            dockedMules.Add(mule);
            mule.homeStable = this;
            mule.state = MuleState.Charging;

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
        public bool DeployMule(MULE_Pawn mule, MuleTask task)
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
        public void ReleaseMule(MULE_Pawn mule)
        {
            if (!dockedMules.Contains(mule)) return;

            dockedMules.Remove(mule);

            if (Map != null && !mule.Spawned)
            {
                IntVec3 spawnCell = GetMuleSpawnCell();
                if (spawnCell.IsValid)
                {
                    GenSpawn.Spawn(mule, spawnCell, Map);
                    mule.state = MuleState.Idle;
                }
            }
        }

        /// <summary>
        /// Creates a new MULE pawn and docks it in this STABLE.
        /// Used when manufacturing MULEs.
        /// </summary>
        public MULE_Pawn CreateAndDockMule()
        {
            if (!HasSpace) return null;

            // Generate a new MULE pawn with simplified request
            PawnGenerationRequest request = new PawnGenerationRequest(
                kind: ArsenalDefOf.Arsenal_MULE_Kind,
                faction: Faction.OfPlayer,
                forceGenerateNewPawn: true
            );

            MULE_Pawn newMule = (MULE_Pawn)PawnGenerator.GeneratePawn(request);
            newMule.homeStable = this;
            newMule.state = MuleState.Idle;

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
                icon = ContentFinder<Texture2D>.Get("Arsenal/Arsenal_MULE", false),
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

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Deploy to Mine",
                    action = delegate
                    {
                        // Find a mining designation and deploy a MULE to it
                        var des = Map.designationManager.AllDesignations
                            .Where(d => d.def == DesignationDefOf.Mine && !d.target.HasThing)
                            .OrderBy(d => d.target.Cell.DistanceTo(Position))
                            .FirstOrDefault();

                        if (des == null)
                        {
                            Messages.Message("No mining designations found.", MessageTypeDefOf.RejectInput);
                            return;
                        }

                        IntVec3 cell = des.target.Cell;
                        Building mineable = cell.GetFirstMineable(Map);
                        if (mineable == null)
                        {
                            Messages.Message($"No mineable at {cell}.", MessageTypeDefOf.RejectInput);
                            return;
                        }

                        var mule = GetAvailableMule();
                        if (mule == null)
                        {
                            Messages.Message("No available MULE.", MessageTypeDefOf.RejectInput);
                            return;
                        }

                        MuleTask task = MuleTask.CreateMiningTask(cell, des);
                        if (DeployMule(mule, task))
                        {
                            Messages.Message($"Deployed {mule.Label} to mine at {cell}", MessageTypeDefOf.PositiveEvent);
                        }
                        else
                        {
                            Messages.Message("Deploy failed!", MessageTypeDefOf.RejectInput);
                        }
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Log All MULEs",
                    action = delegate
                    {
                        var allMules = ArsenalNetworkManager.GetAllMules();
                        Log.Warning($"=== ALL MULES ({allMules.Count()}) ===");
                        foreach (var m in allMules)
                        {
                            Log.Warning($"{m.Label}: state={m.state}, pos={m.Position}, spawned={m.Spawned}, battery={m.BatteryPercent:P0}");
                            if (m.CurrentTask != null)
                            {
                                Log.Warning($"  Task: {m.CurrentTask.taskType} at {m.CurrentTask.targetCell}");
                            }
                        }
                        Log.Warning($"=== END ===");
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Diagnose Tasks",
                    action = delegate
                    {
                        Log.Warning($"=== STABLE TASK DIAGNOSIS ({Label}) ===");
                        Log.Warning($"Map: {Map?.uniqueID}, Tile: {Map?.Tile}");
                        Log.Warning($"Powered: {IsPoweredOn()}, Network: {HasNetworkConnection()}");
                        Log.Warning($"Ready MULEs: {AvailableMuleCount}");

                        // Check LATTICE connection
                        var localLattice = ArsenalNetworkManager.GetLatticeOnMap(Map);
                        var connectedLattice = ArsenalNetworkManager.GetConnectedLattice(Map);
                        Log.Warning($"Local LATTICE: {localLattice?.Label ?? "NONE"}");
                        Log.Warning($"Connected LATTICE: {connectedLattice?.Label ?? "NONE"}");

                        if (connectedLattice != null)
                        {
                            Log.Warning($"LATTICE pending tasks: {connectedLattice.PendingMuleTaskCount}");
                        }

                        // Check mining designations
                        int miningCount = Map.designationManager.AllDesignations
                            .Count(d => d.def == DesignationDefOf.Mine && !d.target.HasThing);
                        Log.Warning($"Mining designations on this map: {miningCount}");

                        // Check haulables
                        var haulables = Map.listerHaulables.ThingsPotentiallyNeedingHauling();
                        Log.Warning($"Haulable items on this map: {haulables.Count}");

                        // Check stockpile zones
                        var zones = Map.zoneManager.AllZones.Where(z => z is Zone_Stockpile).Count();
                        Log.Warning($"Stockpile zones: {zones}");

                        // Try local scan
                        var readyMule = dockedMules.FirstOrDefault(m => m.state == MuleState.Idle && m.IsBatteryFull);
                        if (readyMule != null)
                        {
                            var localTask = ScanForLocalTask(readyMule);
                            Log.Warning($"Local scan result: {localTask?.taskType.ToString() ?? "NULL"}");
                        }
                        else
                        {
                            Log.Warning("No ready MULE available for local scan test");
                        }

                        Log.Warning($"=== END DIAGNOSIS ===");
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
