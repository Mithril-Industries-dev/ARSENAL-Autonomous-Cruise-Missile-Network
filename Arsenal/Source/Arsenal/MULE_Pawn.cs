using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// MULE (Mobile Utility Logistics Engine) - Autonomous ground drone for mining and hauling.
    /// Now extends Pawn for proper pathfinding and job system integration.
    /// </summary>
    public class MULE_Pawn : Pawn
    {
        // Home STABLE
        public Building_Stable homeStable;

        // State tracking
        public MuleState state = MuleState.Idle;
        private MuleTask currentTask;
        private int idleTicks = 0;
        private const int MAX_IDLE_TICKS = 300; // 5 seconds of idle = go home

        // Naming
        private string customName;
        private static int muleCounter = 1;

        #region Properties

        public Comp_MuleBattery BatteryComp => GetComp<Comp_MuleBattery>();
        public float BatteryPercent => BatteryComp?.ChargePercent ?? 0f;
        public bool IsBatteryFull => BatteryComp?.IsFull ?? false;
        public bool IsBatteryDepleted => BatteryComp?.IsDepleted ?? true;
        public MuleTask CurrentTask => currentTask;

        public override string Label => customName ?? base.Label;
        public string CustomName => customName;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // Only assign a new name if this MULE doesn't have one yet
            // (prevents name changes when deploying from STABLE)
            if (string.IsNullOrEmpty(customName))
            {
                customName = "MULE-" + muleCounter.ToString("D2");
                muleCounter++;
            }

            // Set faction to player
            if (Faction == null)
            {
                SetFaction(Faction.OfPlayer);
            }

            ArsenalNetworkManager.RegisterMule(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Drop carried thing
            if (carryTracker?.CarriedThing != null)
            {
                carryTracker.TryDropCarriedThing(Position, ThingPlaceMode.Near, out _);
            }

            ArsenalNetworkManager.DeregisterMule(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref state, "muleState", MuleState.Idle);
            Scribe_Deep.Look(ref currentTask, "currentTask");
            Scribe_References.Look(ref homeStable, "homeStable");
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref idleTicks, "idleTicks", 0);
        }

        #endregion

        #region Tick

        protected override void Tick()
        {
            base.Tick();

            // Safety check - don't process if not spawned
            if (!Spawned || Map == null) return;

            // Battery drain while active (not when idle or charging)
            if (state != MuleState.Charging && state != MuleState.Idle && state != MuleState.Inert)
            {
                BatteryComp?.Drain();
            }

            // Check for depleted battery
            if (IsBatteryDepleted && state != MuleState.Inert)
            {
                EnterInertState();
                return;
            }

            // Check for low battery - need to return home (but not if already returning or delivering)
            if (BatteryComp != null && BatteryComp.NeedsRecharge &&
                state != MuleState.ReturningHome && state != MuleState.Charging &&
                state != MuleState.DeliveringToStable && state != MuleState.Inert)
            {
                ReturnToStable();
                return;
            }

            // State-specific ticks
            switch (state)
            {
                case MuleState.Idle:
                    TickIdle();
                    break;
                case MuleState.Charging:
                    TickCharging();
                    break;
                case MuleState.Inert:
                    TickInert();
                    break;
                case MuleState.DeliveringToStable:
                case MuleState.ReturningHome:
                    TickMovingToStable();
                    break;
                case MuleState.Deploying:
                case MuleState.Mining:
                case MuleState.Hauling:
                    TickWorking();
                    break;
            }
        }

        private void TickMovingToStable()
        {
            if (homeStable == null || homeStable.Destroyed)
            {
                // Find a new stable
                homeStable = ArsenalNetworkManager.GetNearestStableWithSpace(Position, Map);
                if (homeStable == null)
                {
                    state = MuleState.Idle;
                    return;
                }
                GoToStable();
                return;
            }

            // Check if we've arrived at the stable (adjacent or at interaction cell)
            if (Position.InHorDistOf(homeStable.Position, 2f) || Position == homeStable.InteractionCell)
            {
                // Check if our Goto job is done or we're close enough
                if (jobs?.curJob == null || jobs.curJob.def != JobDefOf.Goto)
                {
                    // We've arrived - dock at the stable
                    if (homeStable.DockMule(this))
                    {
                        Log.Message($"[MULE] {Label}: Docked at {homeStable.Label}");
                    }
                    else
                    {
                        // Stable is full, wait as idle
                        state = MuleState.Idle;
                        Log.Warning($"[MULE] {Label}: Could not dock at {homeStable.Label} - stable full?");
                    }
                }
            }
            else
            {
                // Not at stable yet - make sure we have a job to get there
                if (jobs?.curJob == null || jobs.curJob.def != JobDefOf.Goto)
                {
                    GoToStable();
                }
            }
        }

        private void TickWorking()
        {
            // Check if we've finished our task job
            // The think tree assigns idle/wait jobs when no work is queued,
            // so we detect task completion by checking for these idle jobs
            if (jobs?.curJob == null)
            {
                OnTaskCompleted();
                return;
            }

            // Check if think tree has assigned an idle job (meaning our task job completed)
            JobDef curJobDef = jobs.curJob.def;
            if (curJobDef == JobDefOf.Wait ||
                curJobDef == JobDefOf.Wait_MaintainPosture ||
                curJobDef == JobDefOf.Wait_Wander ||
                curJobDef == JobDefOf.Wait_Combat ||
                curJobDef == JobDefOf.GotoWander ||
                curJobDef == JobDefOf.Goto && jobs.curJob.targetA.Cell == Position) // Goto self = idle
            {
                OnTaskCompleted();
                return;
            }

            // Validate our task is still valid (target not destroyed, etc.)
            if (currentTask != null && !IsTaskStillValid())
            {
                OnTaskCompleted();
            }
        }

        private bool IsTaskStillValid()
        {
            if (currentTask == null) return false;

            switch (currentTask.taskType)
            {
                case MuleTaskType.Mine:
                    // Check if mineable still exists
                    if (!currentTask.targetCell.IsValid) return false;
                    Building mineable = currentTask.targetCell.GetFirstMineable(Map);
                    return mineable != null && !mineable.Destroyed;

                case MuleTaskType.Haul:
                case MuleTaskType.MoriaFeed:
                    // Check if thing still exists
                    if (currentTask.targetThing == null) return false;
                    if (currentTask.targetThing.Destroyed) return false;

                    // If we're carrying it, task is still valid (hauling to destination)
                    if (carryTracker?.CarriedThing == currentTask.targetThing) return true;

                    // If item is not spawned and we're not carrying it, something went wrong
                    if (!currentTask.targetThing.Spawned) return false;

                    // Check if it's still haulable (not forbidden)
                    return !currentTask.targetThing.IsForbidden(Faction.OfPlayer);

                default:
                    return true;
            }
        }

        private void TickIdle()
        {
            idleTicks++;

            // Check frequently for available work (every 60 ticks = 1 second)
            if (this.IsHashIntervalTick(60))
            {
                // Try to find and start a task
                if (TryFindAndStartTask())
                {
                    idleTicks = 0;
                    return;
                }
            }

            // If idle too long, return to STABLE to conserve battery and free up space
            if (idleTicks >= MAX_IDLE_TICKS)
            {
                if (homeStable != null && !homeStable.Destroyed)
                {
                    ReturnToStable();
                    idleTicks = 0;
                }
            }
        }

        private void TickCharging()
        {
            if (homeStable == null || !homeStable.IsPoweredOn())
            {
                return;
            }

            BatteryComp?.Charge();

            if (IsBatteryFull)
            {
                state = MuleState.Idle;
            }
        }

        private void TickInert()
        {
            // Passive slow recharge
            BatteryComp?.PassiveCharge();

            // Check if we can recover
            if (this.IsHashIntervalTick(120))
            {
                if (homeStable != null && !homeStable.Destroyed && BatteryComp != null)
                {
                    float safetyBuffer = BatteryComp.MaxCharge * 0.10f;
                    if (BatteryComp.CurrentCharge >= safetyBuffer)
                    {
                        state = MuleState.ReturningHome;
                        GoToStable();
                    }
                }
            }
        }

        private void EnterInertState()
        {
            state = MuleState.Inert;

            // Drop carried thing
            if (carryTracker?.CarriedThing != null)
            {
                carryTracker.TryDropCarriedThing(Position, ThingPlaceMode.Near, out _);
            }

            // Cancel any jobs
            jobs?.EndCurrentJob(JobCondition.InterruptForced);
            currentTask = null;

            Messages.Message($"{Label} battery depleted - entering inert mode.", this, MessageTypeDefOf.NegativeEvent);
        }

        #endregion

        #region Task Management

        public void AssignTask(MuleTask task)
        {
            currentTask = task;
            state = MuleState.Deploying;
            idleTicks = 0; // Reset idle counter

            // Start appropriate job based on task type
            StartJobForTask(task);
        }

        private void StartJobForTask(MuleTask task)
        {
            Job job = null;
            LocalTargetInfo targetToReserve = LocalTargetInfo.Invalid;

            switch (task.taskType)
            {
                case MuleTaskType.Mine:
                    Building mineable = task.targetCell.GetFirstMineable(Map);
                    if (mineable != null && Map.reservationManager.CanReserve(this, mineable))
                    {
                        job = JobMaker.MakeJob(JobDefOf.Mine, mineable);
                        targetToReserve = mineable;
                    }
                    break;

                case MuleTaskType.Haul:
                case MuleTaskType.MoriaFeed:
                    if (task.targetThing != null && task.targetThing.Spawned &&
                        Map.reservationManager.CanReserve(this, task.targetThing))
                    {
                        job = HaulAIUtility.HaulToStorageJob(this, task.targetThing, false);
                        if (job == null && task.destinationCell.IsValid)
                        {
                            job = HaulAIUtility.HaulToCellStorageJob(this, task.targetThing, task.destinationCell, false);
                        }
                        if (job != null)
                        {
                            targetToReserve = task.targetThing;
                        }
                    }
                    break;
            }

            if (job != null && targetToReserve.IsValid)
            {
                // Try to reserve before starting job to prevent conflicts
                if (Map.reservationManager.CanReserve(this, targetToReserve))
                {
                    jobs.StartJob(job, JobCondition.InterruptForced);
                    // Update state based on task type
                    if (task.taskType == MuleTaskType.Mine)
                        state = MuleState.Mining;
                    else
                        state = MuleState.Hauling;
                }
                else
                {
                    // Target was reserved between check and start - find another task
                    currentTask = null;
                    state = MuleState.Idle;
                    TryFindAndStartTask();
                }
            }
            else
            {
                // Couldn't create job or target invalid, try to find another task or return home
                currentTask = null;
                state = MuleState.Idle;
                if (!TryFindAndStartTask())
                {
                    ReturnToStable();
                }
            }
        }

        public void OnTaskCompleted()
        {
            currentTask = null;
            state = MuleState.Idle;

            // Check if we need to return for charging
            if (BatteryComp != null && BatteryComp.NeedsRecharge)
            {
                ReturnToStable();
                return;
            }

            // Try to find another task nearby before returning to STABLE
            if (TryFindAndStartTask())
            {
                return; // Found and started a new task
            }

            // No tasks available - return to STABLE
            ReturnToStable();
        }

        public bool CanAcceptTask(MuleTask task)
        {
            if (state != MuleState.Idle) return false;
            if (BatteryComp == null) return false;

            // Docked MULEs must be fully charged before deployment
            if (!Spawned && !BatteryComp.IsFull) return false;

            // Spawned MULEs just need enough battery for the task + return trip
            if (BatteryComp.NeedsRecharge) return false;

            // For docked MULEs, use stable position; for spawned, use current position
            IntVec3 startPos = Spawned ? Position : (homeStable?.Position ?? IntVec3.Zero);
            IntVec3 homePos = homeStable?.Position ?? startPos;

            // Estimate if we have enough battery for round trip
            float distToTask = startPos.DistanceTo(task.targetCell);
            float distToHome = task.targetCell.DistanceTo(homePos);
            float totalDist = distToTask + distToHome;
            float estimatedCost = totalDist * BatteryComp.DrainPerTick * 2f; // Buffer

            return BatteryComp.CurrentCharge >= estimatedCost + (BatteryComp.MaxCharge * 0.10f);
        }

        private bool TryFindAndStartTask()
        {
            if (Map == null) return false;

            // Try local LATTICE first
            Building_Lattice lattice = ArsenalNetworkManager.GetLatticeOnMap(Map);
            if (lattice != null)
            {
                MuleTask task = lattice.RequestNewTaskForMule(this);
                if (task != null)
                {
                    AssignTask(task);
                    return true;
                }
            }

            // Scan for local tasks (mining, hauling)
            MuleTask localTask = ScanForLocalTask();
            if (localTask != null)
            {
                AssignTask(localTask);
                return true;
            }

            return false;
        }

        private MuleTask ScanForLocalTask()
        {
            // Mining tasks - find unreserved mineables
            foreach (var miningDes in Map.designationManager.AllDesignations
                .Where(d => d.def == DesignationDefOf.Mine && !d.target.HasThing))
            {
                IntVec3 cell = miningDes.target.Cell;
                Building mineable = cell.GetFirstMineable(Map);
                if (mineable == null) continue;

                // Check if we can reserve this mineable (not already claimed by another pawn)
                if (!Map.reservationManager.CanReserve(this, mineable)) continue;

                if (CanAcceptTask(new MuleTask { targetCell = cell }))
                {
                    return MuleTask.CreateMiningTask(cell, miningDes);
                }
            }

            // Hauling tasks - find unreserved haulables
            var haulables = Map.listerHaulables.ThingsPotentiallyNeedingHauling();
            foreach (Thing item in haulables)
            {
                if (item == null || item.Destroyed || !item.Spawned) continue;
                if (item.IsForbidden(Faction.OfPlayer)) continue;

                // Check if we can reserve this item
                if (!Map.reservationManager.CanReserve(this, item)) continue;

                Building_Moria moria = ArsenalNetworkManager.GetNearestMoriaForItem(item, Map);
                if (moria != null)
                {
                    return MuleTask.CreateMoriaFeedTask(item, moria);
                }

                IntVec3 stockpile = FindStockpileCell(item);
                if (stockpile.IsValid)
                {
                    return MuleTask.CreateHaulTask(item, null, stockpile);
                }
            }

            return null;
        }

        private IntVec3 FindStockpileCell(Thing item)
        {
            if (StoreUtility.TryFindBestBetterStoreCellFor(item, this, Map, StoragePriority.Unstored, Faction.OfPlayer, out IntVec3 result, true))
            {
                return result;
            }
            return IntVec3.Invalid;
        }

        #endregion

        #region Movement

        public void GoToStable()
        {
            if (homeStable == null || jobs == null) return;

            Job job = JobMaker.MakeJob(JobDefOf.Goto, homeStable.InteractionCell);
            jobs.StartJob(job, JobCondition.InterruptForced);
        }

        public void ReturnToStable()
        {
            state = MuleState.ReturningHome;
            currentTask = null;
            if (Spawned && jobs != null)
            {
                GoToStable();
            }
        }

        public void DockAtStable()
        {
            state = MuleState.Charging;
            jobs?.EndCurrentJob(JobCondition.Succeeded);
        }

        public bool IsAdjacentToStable()
        {
            if (homeStable == null) return false;
            return Position.AdjacentTo8WayOrInside(homeStable.Position);
        }

        #endregion

        #region Initialization

        public void SetHomeStable(Building_Stable stable)
        {
            homeStable = stable;
        }

        /// <summary>
        /// Initializes the MULE for delivery from ARSENAL to target STABLE.
        /// Called when a newly manufactured MULE is spawned.
        /// </summary>
        public void InitializeForDelivery(Building_Stable targetStable)
        {
            homeStable = targetStable;
            state = MuleState.DeliveringToStable;

            // Use job system to go to the stable
            if (Spawned && jobs != null && targetStable != null)
            {
                Job goToStableJob = JobMaker.MakeJob(JobDefOf.Goto, targetStable.InteractionCell);
                jobs.StartJob(goToStableJob, JobCondition.InterruptForced);
                Log.Message($"[MULE] {Label}: Initialized for delivery to {targetStable.Label}");
            }
        }

        #endregion

        #region Naming

        public void SetCustomName(string name)
        {
            customName = name;
        }

        #endregion

        #region Gizmos

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }

            // Battery status
            if (BatteryComp != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = $"Battery: {BatteryComp.ChargePercent:P0}",
                    defaultDesc = $"Current charge: {BatteryComp.CurrentCharge:F1}/{BatteryComp.MaxCharge:F0}",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/PodEject", false),
                    action = delegate { }
                };
            }

            // Return to STABLE
            var returnCmd = new Command_Action
            {
                defaultLabel = "Return to STABLE",
                defaultDesc = "Order this MULE to return to its home STABLE for charging.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/PodEject", false),
                action = delegate { ReturnToStable(); }
            };
            if (homeStable == null)
            {
                returnCmd.Disable("No home STABLE assigned");
            }
            yield return returnCmd;

            // Rename
            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Give this MULE a custom name.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameMule(this));
                }
            };

            // Debug gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Fill Battery",
                    action = delegate { BatteryComp?.FillBattery(); }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Drain Battery",
                    action = delegate { BatteryComp?.EmptyBattery(); }
                };

                yield return new Command_Action
                {
                    defaultLabel = $"DEV: State={state}",
                    action = delegate { Log.Message($"[MULE] {Label}: State={state}, Task={currentTask?.taskType}"); }
                };
            }
        }

        #endregion
    }

    /// <summary>
    /// Dialog for renaming a MULE.
    /// </summary>
    public class Dialog_RenameMule : Window
    {
        private MULE_Pawn mule;
        private string newName;

        public override Vector2 InitialSize => new Vector2(300, 150);

        public Dialog_RenameMule(MULE_Pawn mule)
        {
            this.mule = mule;
            this.newName = mule.CustomName ?? mule.Label;
            doCloseButton = false;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename MULE");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                mule.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
