using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// MULE (Mobile Utility Logistics Engine) - Autonomous ground drone for mining and hauling.
    /// Integrates with LATTICE for task assignment and STABLE for storage/charging.
    /// </summary>
    public class MULE_Drone : ThingWithComps
    {
        // State
        public MuleState state = MuleState.Idle;
        private MuleTask currentTask;

        // Home STABLE
        public Building_Stable homeStable;

        // Battery system
        public float currentBattery = 100f;
        public const float MAX_BATTERY = 100f;
        public const float SAFETY_BUFFER_PERCENT = 0.10f; // 10% reserve
        public const float ACTIVE_DRAIN_PER_TICK = 0.005f; // Drain while moving/working
        public const float MINING_DRAIN_MULTIPLIER = 1.5f; // Mining drains faster
        public const float PASSIVE_RECHARGE_RATE = 0.001f; // ~1% per in-game minute while inert
        public const float STABLE_RECHARGE_RATE = 0.05f; // Fast recharge when docked

        // Movement
        private const float SPEED = 0.08f; // Cells per tick (slower than DART)
        private List<IntVec3> currentPath;
        private int pathIndex;
        private Vector3 exactPosition;
        private float currentRotation;

        // Pathfinding
        private const int PATH_RECALC_INTERVAL = 120; // Recalculate path every 2 seconds
        private int ticksSincePathCalc;

        // Inert recovery
        private const int INERT_CHECK_INTERVAL = 120; // Check recovery every 2 seconds

        // Carried item
        public Thing carriedThing;
        private const int MAX_CARRY_STACK = 75; // Max stack size MULE can carry

        // Naming
        private string customName;
        private static int muleCounter = 1;

        // Mining
        private int miningProgress;
        private const int MINING_WORK_PER_TICK = 2; // Mining speed

        // Visual
        private const int TRAIL_LENGTH = 5;
        private Queue<Vector3> trailPositions = new Queue<Vector3>();

        #region Properties

        public float BatteryPercent => currentBattery / MAX_BATTERY;
        public bool IsBatteryFull => currentBattery >= MAX_BATTERY;
        public bool IsBatteryDepleted => currentBattery <= 0f;
        public MuleTask CurrentTask => currentTask;
        public override string Label => customName ?? base.Label;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            if (!respawningAfterLoad)
            {
                exactPosition = Position.ToVector3Shifted();
                customName = "MULE-" + muleCounter.ToString("D2");
                muleCounter++;
            }

            ArsenalNetworkManager.RegisterMule(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Drop carried item
            if (carriedThing != null && Map != null)
            {
                GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                carriedThing = null;
            }

            ArsenalNetworkManager.DeregisterMule(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref state, "state", MuleState.Idle);
            Scribe_Deep.Look(ref currentTask, "currentTask");
            Scribe_References.Look(ref homeStable, "homeStable");
            Scribe_Values.Look(ref currentBattery, "currentBattery", MAX_BATTERY);
            Scribe_Values.Look(ref exactPosition, "exactPosition");
            Scribe_Values.Look(ref currentRotation, "currentRotation");
            Scribe_Values.Look(ref pathIndex, "pathIndex");
            Scribe_Collections.Look(ref currentPath, "currentPath", LookMode.Value);
            Scribe_References.Look(ref carriedThing, "carriedThing");
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref miningProgress, "miningProgress");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (trailPositions == null)
                    trailPositions = new Queue<Vector3>();
            }
        }

        #endregion

        #region Initialization

        public void SetHomeStable(Building_Stable stable)
        {
            homeStable = stable;
        }

        public void SetState(MuleState newState)
        {
            state = newState;
        }

        public void SetCustomName(string name)
        {
            customName = name;
        }

        /// <summary>
        /// Initializes the MULE for delivery from ARSENAL to target STABLE.
        /// Called when a newly manufactured MULE is spawned.
        /// </summary>
        public void InitializeForDelivery(Building_Stable targetStable)
        {
            homeStable = targetStable;
            state = MuleState.DeliveringToStable;
            CalculatePathTo(targetStable.Position);
        }

        #endregion

        #region Battery Management

        /// <summary>
        /// Estimates battery cost to complete a task and return home.
        /// </summary>
        public float EstimateBatteryCost(IntVec3 taskLocation, MuleTaskType taskType)
        {
            if (Map == null || homeStable == null) return MAX_BATTERY;

            // Path to task
            float toTask = EstimatePathCost(Position, taskLocation);

            // Task execution cost
            float taskCost = GetTaskDrainCost(taskType);

            // Path home from task
            float toHome = EstimatePathCost(taskLocation, homeStable.Position);

            // Safety buffer
            float buffer = MAX_BATTERY * SAFETY_BUFFER_PERCENT;

            return toTask + taskCost + toHome + buffer;
        }

        private float EstimatePathCost(IntVec3 from, IntVec3 to)
        {
            // Rough estimate: Manhattan distance * drain rate * 1.5 (for pathing overhead)
            float distance = from.DistanceTo(to);
            return distance * ACTIVE_DRAIN_PER_TICK * 1.5f * 60f; // 60 ticks estimate per cell
        }

        private float GetTaskDrainCost(MuleTaskType taskType)
        {
            switch (taskType)
            {
                case MuleTaskType.Mine:
                    return 10f; // Mining is expensive
                case MuleTaskType.Haul:
                    return 2f; // Hauling is cheap
                case MuleTaskType.MoriaFeed:
                    return 2f;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Checks if MULE can accept a task based on battery.
        /// </summary>
        public bool CanAcceptTask(MuleTask task)
        {
            if (state != MuleState.Idle && state != MuleState.Charging)
                return false;

            float cost = EstimateBatteryCost(task.targetCell, task.taskType);
            return currentBattery >= cost;
        }

        /// <summary>
        /// Checks if MULE can reach a location and return home.
        /// </summary>
        public bool CanReachAndReturn(IntVec3 location)
        {
            float toLocation = EstimatePathCost(Position, location);
            float toHome = EstimatePathCost(location, homeStable?.Position ?? Position);
            float buffer = MAX_BATTERY * SAFETY_BUFFER_PERCENT;

            return currentBattery >= toLocation + toHome + buffer;
        }

        private void DrainBattery(float amount)
        {
            currentBattery = Mathf.Max(0f, currentBattery - amount);

            if (currentBattery <= 0f && state != MuleState.Inert)
            {
                EnterInertState();
            }
        }

        private void RechargeBattery(float amount)
        {
            currentBattery = Mathf.Min(MAX_BATTERY, currentBattery + amount);
        }

        /// <summary>
        /// Checks if MULE should return home based on remaining battery.
        /// </summary>
        private void RecalculateReturnViability()
        {
            if (homeStable == null || Map == null) return;

            float returnCost = EstimatePathCost(Position, homeStable.Position);
            float buffer = MAX_BATTERY * SAFETY_BUFFER_PERCENT;

            if (currentBattery < returnCost + buffer)
            {
                AbortCurrentTask();
                SetState(MuleState.ReturningHome);
                CalculatePathTo(homeStable.Position);
            }
        }

        #endregion

        #region Task Management

        public void AssignTask(MuleTask task)
        {
            currentTask = task;
            task.estimatedBatteryCost = EstimateBatteryCost(task.targetCell, task.taskType);
            state = MuleState.Deploying;
            CalculatePathTo(task.targetCell);
        }

        private void AbortCurrentTask()
        {
            // Drop carried item if any
            if (carriedThing != null && Map != null)
            {
                GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                carriedThing = null;
            }

            currentTask = null;
            miningProgress = 0;
        }

        private void CompleteTask()
        {
            if (currentTask != null)
            {
                currentTask.isComplete = true;
            }
            currentTask = null;
            miningProgress = 0;

            // Return home after task
            state = MuleState.ReturningHome;
            if (homeStable != null)
            {
                CalculatePathTo(homeStable.Position);
            }
        }

        #endregion

        #region Tick & State Machine

        protected override void Tick()
        {
            base.Tick();

            switch (state)
            {
                case MuleState.Idle:
                    // Do nothing, wait for task
                    break;

                case MuleState.Charging:
                    TickCharging();
                    break;

                case MuleState.DeliveringToStable:
                    TickDeliveringToStable();
                    break;

                case MuleState.Deploying:
                    TickDeploying();
                    break;

                case MuleState.Mining:
                    TickMining();
                    break;

                case MuleState.Hauling:
                    TickHauling();
                    break;

                case MuleState.ReturningHome:
                    TickReturningHome();
                    break;

                case MuleState.Inert:
                    TickInert();
                    break;
            }

            // Update trail
            if (state == MuleState.DeliveringToStable || state == MuleState.Deploying ||
                state == MuleState.Hauling || state == MuleState.ReturningHome)
            {
                UpdateTrail();
            }
        }

        private void TickCharging()
        {
            if (homeStable == null || !homeStable.IsPoweredOn())
            {
                // Can't charge without powered STABLE
                return;
            }

            RechargeBattery(STABLE_RECHARGE_RATE);

            if (IsBatteryFull)
            {
                state = MuleState.Idle;
            }
        }

        private void TickDeliveringToStable()
        {
            DrainBattery(ACTIVE_DRAIN_PER_TICK);
            MoveAlongPath();

            if (homeStable == null || homeStable.Destroyed)
            {
                // Target STABLE destroyed - find a new one
                homeStable = ArsenalNetworkManager.GetNearestStableWithSpace(Position, Map);
                if (homeStable != null)
                {
                    CalculatePathTo(homeStable.Position);
                }
                else
                {
                    // No STABLE available - go inert
                    EnterInertState();
                }
                return;
            }

            // Check if arrived at STABLE
            if (Position.DistanceTo(homeStable.Position) < 2f)
            {
                // Dock with STABLE
                DockAtStable();
            }
        }

        private void TickDeploying()
        {
            // Check return viability periodically
            if (this.IsHashIntervalTick(60))
            {
                RecalculateReturnViability();
            }

            DrainBattery(ACTIVE_DRAIN_PER_TICK);
            MoveAlongPath();

            // Check if arrived at task location
            if (currentTask != null && Position.DistanceTo(currentTask.targetCell) < 2f)
            {
                // Start the actual task
                switch (currentTask.taskType)
                {
                    case MuleTaskType.Mine:
                        state = MuleState.Mining;
                        miningProgress = 0;
                        break;
                    case MuleTaskType.Haul:
                    case MuleTaskType.MoriaFeed:
                        TryPickupItem();
                        break;
                }
            }
        }

        private void TickMining()
        {
            if (this.IsHashIntervalTick(60))
            {
                RecalculateReturnViability();
            }

            DrainBattery(ACTIVE_DRAIN_PER_TICK * MINING_DRAIN_MULTIPLIER);

            if (currentTask == null)
            {
                state = MuleState.ReturningHome;
                CalculatePathTo(homeStable?.Position ?? Position);
                return;
            }

            // Check if mining target still valid
            IntVec3 mineCell = currentTask.targetCell;
            if (!mineCell.InBounds(Map))
            {
                CompleteTask();
                return;
            }

            Building mineable = mineCell.GetFirstMineable(Map);
            if (mineable == null)
            {
                // Mining complete - collect resources
                CollectMinedResources(mineCell);
                CompleteTask();
                return;
            }

            // Do mining work
            miningProgress += MINING_WORK_PER_TICK;

            // Visual effect
            if (this.IsHashIntervalTick(30))
            {
                FleckMaker.ThrowMicroSparks(mineCell.ToVector3Shifted(), Map);
                FleckMaker.ThrowDustPuff(mineCell, Map, 0.5f);
            }

            // Check if mining complete (simplified - real mining uses work amount)
            float mineWork = mineable.def.building?.mineableScatterCommonality > 0 ? 1000f : 500f;
            if (miningProgress >= mineWork)
            {
                // Destroy the mineable and spawn resources
                var resourceDef = mineable.def.building?.mineableThing;
                int yield = mineable.def.building?.mineableYield ?? 0;

                mineable.Destroy(DestroyMode.KillFinalize);

                // Remove mining designation
                if (currentTask.miningDesignation != null)
                {
                    Map.designationManager.RemoveDesignation(currentTask.miningDesignation);
                }

                // Try to pick up resources
                if (resourceDef != null && yield > 0)
                {
                    Thing resource = ThingMaker.MakeThing(resourceDef);
                    resource.stackCount = Mathf.Min(yield, MAX_CARRY_STACK);
                    carriedThing = resource;
                }

                CompleteTask();
            }
        }

        private void TickHauling()
        {
            if (this.IsHashIntervalTick(60))
            {
                RecalculateReturnViability();
            }

            DrainBattery(ACTIVE_DRAIN_PER_TICK);
            MoveAlongPath();

            if (currentTask == null)
            {
                state = MuleState.ReturningHome;
                CalculatePathTo(homeStable?.Position ?? Position);
                return;
            }

            // Check if arrived at destination
            IntVec3 dest = currentTask.destinationCell;
            if (Position.DistanceTo(dest) < 2f)
            {
                DeliverCarriedItem();
                CompleteTask();
            }
        }

        private void TickReturningHome()
        {
            DrainBattery(ACTIVE_DRAIN_PER_TICK);
            MoveAlongPath();

            if (homeStable == null || homeStable.Destroyed)
            {
                // Find new home
                homeStable = ArsenalNetworkManager.GetNearestStableWithSpace(Position, Map);
                if (homeStable != null)
                {
                    CalculatePathTo(homeStable.Position);
                }
                else
                {
                    // No STABLE available - go inert
                    EnterInertState();
                }
                return;
            }

            // Check if arrived at STABLE
            if (Position.DistanceTo(homeStable.Position) < 2f)
            {
                // Dock with STABLE
                DockAtStable();
            }
        }

        private void TickInert()
        {
            // Passive recharge
            RechargeBattery(PASSIVE_RECHARGE_RATE);

            // Check if can return home
            if (this.IsHashIntervalTick(INERT_CHECK_INTERVAL))
            {
                if (homeStable != null && !homeStable.Destroyed)
                {
                    float returnCost = EstimatePathCost(Position, homeStable.Position);
                    float buffer = MAX_BATTERY * SAFETY_BUFFER_PERCENT;

                    if (currentBattery >= returnCost + buffer)
                    {
                        // Can return home now
                        state = MuleState.ReturningHome;
                        CalculatePathTo(homeStable.Position);
                    }
                }
            }
        }

        private void EnterInertState()
        {
            state = MuleState.Inert;

            // Drop carried item
            if (carriedThing != null && Map != null)
            {
                GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                carriedThing = null;
            }

            // Abort current task
            currentTask = null;
            miningProgress = 0;

            Messages.Message($"{Label} battery depleted - entering inert mode.", this, MessageTypeDefOf.NegativeEvent);
        }

        #endregion

        #region Movement

        private IntVec3 currentDestination = IntVec3.Invalid;

        private void CalculatePathTo(IntVec3 destination)
        {
            if (Map == null) return;

            currentPath = new List<IntVec3>();
            currentDestination = destination;

            if (!destination.InBounds(Map))
            {
                return;
            }

            // If already at destination, no path needed
            if (Position.DistanceTo(destination) < 2f)
            {
                return;
            }

            // BFS pathfinding - guaranteed to find path if one exists
            Queue<IntVec3> frontier = new Queue<IntVec3>();
            Dictionary<IntVec3, IntVec3> cameFrom = new Dictionary<IntVec3, IntVec3>();

            frontier.Enqueue(Position);
            cameFrom[Position] = Position;

            IntVec3 reached = IntVec3.Invalid;
            int maxIterations = 3000;
            int iterations = 0;

            while (frontier.Count > 0 && iterations < maxIterations)
            {
                iterations++;
                IntVec3 current = frontier.Dequeue();

                // Check if close enough to destination
                if (current.DistanceTo(destination) < 2f)
                {
                    reached = current;
                    break;
                }

                // Check all 8 neighbors (cardinal first, then diagonal)
                foreach (IntVec3 dir in GenAdj.AdjacentCells)
                {
                    IntVec3 next = current + dir;

                    if (!next.InBounds(Map)) continue;
                    if (cameFrom.ContainsKey(next)) continue;
                    if (!next.Walkable(Map)) continue;

                    frontier.Enqueue(next);
                    cameFrom[next] = current;
                }
            }

            // Reconstruct path if we found the destination
            if (reached.IsValid)
            {
                List<IntVec3> reversePath = new List<IntVec3>();
                IntVec3 step = reached;

                while (step != Position)
                {
                    reversePath.Add(step);
                    step = cameFrom[step];
                }

                reversePath.Reverse();
                currentPath = reversePath;
            }

            pathIndex = 0;
            ticksSincePathCalc = 0;
        }

        private void MoveAlongPath()
        {
            ticksSincePathCalc++;

            // Get current destination based on state
            IntVec3 dest = IntVec3.Invalid;
            if (state == MuleState.ReturningHome || state == MuleState.DeliveringToStable)
            {
                dest = homeStable?.Position ?? Position;
            }
            else if (state == MuleState.Hauling && currentTask != null)
            {
                dest = currentTask.destinationCell;
            }
            else if (currentTask != null)
            {
                dest = currentTask.targetCell;
            }

            // Recalculate path periodically or if we're stuck
            bool needsRecalc = ticksSincePathCalc >= PATH_RECALC_INTERVAL;
            bool pathEmpty = currentPath == null || currentPath.Count == 0 || pathIndex >= currentPath.Count;

            if ((needsRecalc || pathEmpty) && dest.IsValid)
            {
                CalculatePathTo(dest);
            }

            // If still no path, try direct movement
            if ((currentPath == null || currentPath.Count == 0 || pathIndex >= currentPath.Count) && dest.IsValid)
            {
                // Direct movement fallback - move towards destination if possible
                Vector3 targetDir = (dest.ToVector3Shifted() - exactPosition).normalized;
                Vector3 nextPos = exactPosition + targetDir * SPEED;
                IntVec3 nextCell = nextPos.ToIntVec3();

                if (nextCell.InBounds(Map) && nextCell.Walkable(Map))
                {
                    exactPosition = nextPos;
                    if (nextCell != Position)
                    {
                        Position = nextCell;
                    }
                }
                return;
            }

            if (currentPath == null || currentPath.Count == 0 || pathIndex >= currentPath.Count)
            {
                return;
            }

            IntVec3 targetCell = currentPath[pathIndex];
            Vector3 targetPos = targetCell.ToVector3Shifted();

            Vector3 direction = (targetPos - exactPosition).normalized;
            float distance = Vector3.Distance(exactPosition, targetPos);

            if (distance <= SPEED)
            {
                exactPosition = targetPos;
                pathIndex++;
            }
            else
            {
                exactPosition += direction * SPEED;
            }

            // Update rotation
            if (direction.sqrMagnitude > 0.001f)
            {
                currentRotation = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }

            // Update position
            IntVec3 newCell = exactPosition.ToIntVec3();
            if (newCell != Position && newCell.InBounds(Map))
            {
                Position = newCell;
            }
        }

        private void UpdateTrail()
        {
            trailPositions.Enqueue(exactPosition);
            while (trailPositions.Count > TRAIL_LENGTH)
            {
                trailPositions.Dequeue();
            }
        }

        #endregion

        #region Item Handling

        private void TryPickupItem()
        {
            if (currentTask?.targetThing == null)
            {
                CompleteTask();
                return;
            }

            Thing item = currentTask.targetThing;
            if (item.Destroyed || !item.Spawned)
            {
                CompleteTask();
                return;
            }

            // Pick up the item
            int pickupCount = Mathf.Min(item.stackCount, MAX_CARRY_STACK);
            carriedThing = item.SplitOff(pickupCount);

            // Now haul to destination
            state = MuleState.Hauling;
            CalculatePathTo(currentTask.destinationCell);
        }

        private void DeliverCarriedItem()
        {
            if (carriedThing == null) return;

            // Try to place at destination
            if (currentTask?.destination is Building_Moria moria)
            {
                // Deliver to MORIA - place on a storage cell
                IntVec3 storageCell = moria.GetStorageCell(carriedThing);
                if (storageCell.IsValid)
                {
                    GenPlace.TryPlaceThing(carriedThing, storageCell, Map, ThingPlaceMode.Near);
                }
                else
                {
                    // MORIA full/unpowered - drop near the MULE
                    GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                }
            }
            else
            {
                // Place on ground at destination
                GenPlace.TryPlaceThing(carriedThing, currentTask?.destinationCell ?? Position, Map, ThingPlaceMode.Near);
            }

            carriedThing = null;
        }

        private void CollectMinedResources(IntVec3 mineCell)
        {
            // Look for dropped resources nearby
            foreach (Thing t in mineCell.GetThingList(Map).ToArray())
            {
                if (t.def.category == ThingCategory.Item && carriedThing == null)
                {
                    int pickupCount = Mathf.Min(t.stackCount, MAX_CARRY_STACK);
                    carriedThing = t.SplitOff(pickupCount);
                    break;
                }
            }
        }

        private void DockAtStable()
        {
            if (homeStable == null || homeStable.Destroyed) return;

            // Deliver any carried item first
            if (carriedThing != null)
            {
                GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                carriedThing = null;
            }

            // Dock with STABLE
            homeStable.DockMule(this);

            // Set state based on battery
            state = IsBatteryFull ? MuleState.Idle : MuleState.Charging;
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty()) text += "\n";

            text += $"State: {state}";
            text += $"\nBattery: {BatteryPercent:P0}";

            if (homeStable != null)
            {
                text += $"\nHome: {homeStable.Label}";
            }

            if (currentTask != null)
            {
                text += $"\nTask: {currentTask.taskType}";
            }

            if (carriedThing != null)
            {
                text += $"\nCarrying: {carriedThing.LabelShort} x{carriedThing.stackCount}";
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
                defaultDesc = "Rename this MULE.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameMule(this));
                }
            };

            if (state != MuleState.Idle && state != MuleState.Charging && state != MuleState.Inert)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Return Home",
                    defaultDesc = "Order this MULE to return to its STABLE.",
                    action = delegate
                    {
                        AbortCurrentTask();
                        state = MuleState.ReturningHome;
                        if (homeStable != null)
                        {
                            CalculatePathTo(homeStable.Position);
                        }
                    }
                };
            }

            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Full Battery",
                    action = delegate { currentBattery = MAX_BATTERY; }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Deplete Battery",
                    action = delegate { currentBattery = 0f; EnterInertState(); }
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
        private MULE_Drone mule;
        private string newName;

        public Dialog_RenameMule(MULE_Drone m)
        {
            mule = m;
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
