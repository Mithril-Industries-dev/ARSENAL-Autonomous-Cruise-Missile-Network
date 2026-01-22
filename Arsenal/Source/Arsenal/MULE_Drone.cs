using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;
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
        private const int MINING_WORK_PER_TICK = 18; // Fast mining - equivalent to level 19 miner

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
            // Use interaction cell for pathing to buildings
            CalculatePathTo(targetStable.InteractionCell);
            Log.Message($"[MULE] {Label}: Initialized for delivery to {targetStable.Label} at {targetStable.InteractionCell}");
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

            float returnCost = EstimatePathCost(Position, homeStable.InteractionCell);
            float buffer = MAX_BATTERY * SAFETY_BUFFER_PERCENT;

            if (currentBattery < returnCost + buffer)
            {
                AbortCurrentTask();
                SetState(MuleState.ReturningHome);
                CalculatePathTo(homeStable.InteractionCell);
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
            Log.Message($"[MULE] {Label}: Assigned task {task.taskType} at {task.targetCell}, battery={BatteryPercent:P0}");
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
                CalculatePathTo(homeStable.InteractionCell);
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
                    CalculatePathTo(homeStable.InteractionCell);
                }
                else
                {
                    // No STABLE available - go inert
                    EnterInertState();
                }
                return;
            }

            // Check if arrived at STABLE (use interaction cell or adjacent check)
            if (IsAdjacentToBuilding(homeStable))
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

            // Validate task is still valid while traveling
            if (currentTask == null)
            {
                Log.Message($"[MULE] {Label}: TickDeploying - task became null, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            // For haul tasks, check if item still exists mid-travel
            if (currentTask.taskType == MuleTaskType.Haul || currentTask.taskType == MuleTaskType.MoriaFeed)
            {
                if (currentTask.targetThing == null || currentTask.targetThing.Destroyed || !currentTask.targetThing.Spawned)
                {
                    Log.Message($"[MULE] {Label}: TickDeploying - haul target gone mid-travel, looking for new task");
                    TryGetNewTaskOrGoHome();
                    return;
                }
                // Check if item is being carried by someone
                if (currentTask.targetThing.ParentHolder != null && !(currentTask.targetThing.ParentHolder is Map))
                {
                    Log.Message($"[MULE] {Label}: TickDeploying - haul target being carried by someone else");
                    TryGetNewTaskOrGoHome();
                    return;
                }
            }

            // For mining tasks, check if mineable still exists
            if (currentTask.taskType == MuleTaskType.Mine)
            {
                Building mineable = currentTask.targetCell.GetFirstMineable(Map);
                if (mineable == null)
                {
                    Log.Message($"[MULE] {Label}: TickDeploying - mine target already gone, looking for new task");
                    TryGetNewTaskOrGoHome();
                    return;
                }
            }

            // Check if arrived at task location
            if (Position.DistanceTo(currentTask.targetCell) < 2f)
            {
                Log.Message($"[MULE] {Label}: Arrived at task location {currentTask.targetCell} for {currentTask.taskType}");
                // Start the actual task
                switch (currentTask.taskType)
                {
                    case MuleTaskType.Mine:
                        state = MuleState.Mining;
                        miningProgress = 0;
                        Log.Message($"[MULE] {Label}: Starting mining");
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
                Log.Message($"[MULE] {Label}: TickMining - no current task, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            // Check if mining target still valid
            IntVec3 mineCell = currentTask.targetCell;
            if (!mineCell.InBounds(Map))
            {
                Log.Message($"[MULE] {Label}: TickMining - mine cell out of bounds, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            Building mineable = mineCell.GetFirstMineable(Map);
            if (mineable == null)
            {
                // Rock is gone - either we finished it or a colonist did
                // Try to collect any dropped resources
                CollectMinedResources(mineCell);

                // Remove mining designation if it exists
                if (currentTask.miningDesignation != null && Map.designationManager != null)
                {
                    Map.designationManager.RemoveDesignation(currentTask.miningDesignation);
                }

                Log.Message($"[MULE] {Label}: TickMining - mineable gone, collected={carriedThing?.LabelShort ?? "nothing"}");
                TransitionToHaulingOrComplete();
                return;
            }

            // Do mining work
            miningProgress += MINING_WORK_PER_TICK;

            // Visual effects for mining - dust, sparks, debris
            if (this.IsHashIntervalTick(15))
            {
                FleckMaker.ThrowMicroSparks(mineCell.ToVector3Shifted(), Map);
                FleckMaker.ThrowDustPuff(mineCell, Map, 0.8f);

                if (Rand.Chance(0.3f))
                {
                    FleckMaker.ThrowDustPuffThick(mineCell.ToVector3Shifted() + new Vector3(Rand.Range(-0.3f, 0.3f), 0, Rand.Range(-0.3f, 0.3f)), Map, 0.5f, new Color(0.5f, 0.5f, 0.5f));
                }
            }

            // Mining work threshold - based on rock's hitpoints
            // Standard rocks have ~1500 HP, we want to mine quickly
            // Level 19 miner with 400% mining speed would take ~2-3 seconds per rock
            // Using HP * 0.5 as work needed, with 18 work/tick = ~40-50 ticks (~1 second) per rock
            float mineWork = mineable.MaxHitPoints * 0.5f;

            // Log progress periodically
            if (this.IsHashIntervalTick(30))
            {
                Log.Message($"[MULE] {Label}: Mining progress {miningProgress}/{mineWork}");
            }

            if (miningProgress >= mineWork)
            {
                Log.Message($"[MULE] {Label}: Mining complete! Destroying rock and collecting resources");

                // Destroy the mineable and spawn resources
                var resourceDef = mineable.def.building?.mineableThing;
                int yield = mineable.def.building?.mineableYield ?? 0;

                mineable.Destroy(DestroyMode.KillFinalize);

                // Remove mining designation
                if (currentTask.miningDesignation != null && Map.designationManager != null)
                {
                    Map.designationManager.RemoveDesignation(currentTask.miningDesignation);
                }

                // Try to pick up resources
                if (resourceDef != null && yield > 0)
                {
                    Thing resource = ThingMaker.MakeThing(resourceDef);
                    resource.stackCount = Mathf.Min(yield, MAX_CARRY_STACK);
                    carriedThing = resource;
                    Log.Message($"[MULE] {Label}: Collected {carriedThing.LabelShort} x{carriedThing.stackCount}");
                }

                TransitionToHaulingOrComplete();
            }
        }

        /// <summary>
        /// After mining, either haul resources or complete task.
        /// </summary>
        private void TransitionToHaulingOrComplete()
        {
            miningProgress = 0;
            Log.Message($"[MULE] {Label}: TransitionToHaulingOrComplete, carrying={carriedThing?.LabelShort ?? "nothing"}");

            // If carrying something, try to haul it
            if (carriedThing != null)
            {
                IntVec3 haulDest = FindHaulDestination(carriedThing);
                Log.Message($"[MULE] {Label}: Found haul destination: {(haulDest.IsValid ? haulDest.ToString() : "NONE")}");
                if (haulDest.IsValid)
                {
                    // Create haul task and transition to hauling
                    currentTask = new MuleTask
                    {
                        taskType = MuleTaskType.Haul,
                        targetThing = carriedThing,
                        destinationCell = haulDest,
                        resourceDef = carriedThing.def,
                        resourceCount = carriedThing.stackCount
                    };
                    state = MuleState.Hauling;
                    CalculatePathTo(haulDest);
                    Log.Message($"[MULE] {Label}: Transitioning to Hauling state, dest={haulDest}");
                    return;
                }
            }

            // No hauling needed or no destination found - complete and go home
            Log.Message($"[MULE] {Label}: No haul destination, completing task and going home");
            CompleteTask();
        }

        /// <summary>
        /// Finds a suitable destination for hauling an item.
        /// Checks MORIA first, then stockpiles.
        /// </summary>
        private IntVec3 FindHaulDestination(Thing item)
        {
            if (Map == null || item == null) return IntVec3.Invalid;

            // First, check for MORIA that wants this item
            Building_Moria targetMoria = ArsenalNetworkManager.GetNearestMoriaForItem(item, Map);
            if (targetMoria != null)
            {
                return targetMoria.InteractionCell;
            }

            // Try to find any valid storage cell for this item type
            foreach (var zone in Map.zoneManager.AllZones)
            {
                if (zone is Zone_Stockpile stockpile)
                {
                    if (stockpile.GetStoreSettings().AllowedToAccept(item))
                    {
                        foreach (IntVec3 cell in stockpile.Cells)
                        {
                            if (StoreUtility.IsGoodStoreCell(cell, Map, item, null, null))
                            {
                                return cell;
                            }
                        }
                    }
                }
            }

            return IntVec3.Invalid;
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
                Log.Message($"[MULE] {Label}: TickHauling - no current task, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            // Check if arrived at destination
            IntVec3 dest = currentTask.destinationCell;
            // Also check if destination is a building (MORIA, etc.) and we're adjacent
            Thing destThing = currentTask.destination;
            bool arrived = Position.DistanceTo(dest) < 2f;
            if (!arrived && destThing != null)
            {
                arrived = IsAdjacentToBuilding(destThing);
            }

            if (arrived)
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
                    CalculatePathTo(homeStable.InteractionCell);
                }
                else
                {
                    // No STABLE available - go inert
                    EnterInertState();
                }
                return;
            }

            // Check if arrived at STABLE (use adjacency check for multi-cell buildings)
            if (IsAdjacentToBuilding(homeStable))
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
                    float returnCost = EstimatePathCost(Position, homeStable.InteractionCell);
                    float buffer = MAX_BATTERY * SAFETY_BUFFER_PERCENT;

                    if (currentBattery >= returnCost + buffer)
                    {
                        // Can return home now
                        state = MuleState.ReturningHome;
                        CalculatePathTo(homeStable.InteractionCell);
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
                // Use interaction cell for buildings (walkable cell adjacent to building)
                dest = homeStable?.InteractionCell ?? Position;
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
                Log.Message($"[MULE] {Label}: TryPickupItem - targetThing is null, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            Thing item = currentTask.targetThing;
            if (item.Destroyed || !item.Spawned)
            {
                Log.Message($"[MULE] {Label}: TryPickupItem - item destroyed/not spawned, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            // Check if item is being held/carried by someone
            if (item.ParentHolder != null && !(item.ParentHolder is Map))
            {
                Log.Message($"[MULE] {Label}: TryPickupItem - item is held by something else, looking for new task");
                TryGetNewTaskOrGoHome();
                return;
            }

            // Pick up the item
            int pickupCount = Mathf.Min(item.stackCount, MAX_CARRY_STACK);
            carriedThing = item.SplitOff(pickupCount);
            Log.Message($"[MULE] {Label}: Picked up {carriedThing.LabelShort} x{carriedThing.stackCount}");

            // Now haul to destination
            state = MuleState.Hauling;
            CalculatePathTo(currentTask.destinationCell);
        }

        /// <summary>
        /// Tries to get a new task from LATTICE, or returns home if none available.
        /// </summary>
        private void TryGetNewTaskOrGoHome()
        {
            currentTask = null;
            miningProgress = 0;

            // Try to get a new task from LATTICE
            Building_Lattice lattice = ArsenalNetworkManager.GetLatticeOnMap(Map);
            if (lattice != null)
            {
                MuleTask newTask = lattice.RequestNewTaskForMule(this);
                if (newTask != null)
                {
                    Log.Message($"[MULE] {Label}: Got new task {newTask.taskType} at {newTask.targetCell}");
                    AssignTask(newTask);
                    return;
                }
            }

            // No task available - return home
            Log.Message($"[MULE] {Label}: No new task available, returning home");
            state = MuleState.ReturningHome;
            if (homeStable != null)
            {
                CalculatePathTo(homeStable.InteractionCell);
            }
        }

        private void DeliverCarriedItem()
        {
            if (carriedThing == null) return;

            // Place at destination cell
            IntVec3 dest = currentTask?.destinationCell ?? Position;
            GenPlace.TryPlaceThing(carriedThing, dest, Map, ThingPlaceMode.Near);
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
            if (homeStable == null || homeStable.Destroyed)
            {
                Log.Message($"[MULE] {Label}: DockAtStable - homeStable null/destroyed, finding new one");
                // Find a new home
                homeStable = ArsenalNetworkManager.GetNearestStableWithSpace(Position, Map);
                if (homeStable == null)
                {
                    Log.Warning($"[MULE] {Label}: No STABLE found, going inert");
                    EnterInertState();
                }
                return;
            }

            // Deliver any carried item first
            if (carriedThing != null)
            {
                Log.Message($"[MULE] {Label}: Dropping carried item {carriedThing.LabelShort} before docking");
                GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                carriedThing = null;
            }

            // Try to dock with STABLE
            Log.Message($"[MULE] {Label}: Attempting to dock at {homeStable.Label}");
            if (homeStable.DockMule(this))
            {
                // Successfully docked - set state based on battery
                // Note: MULE is now despawned, but state assignment still works
                state = IsBatteryFull ? MuleState.Idle : MuleState.Charging;
                Log.Message($"[MULE] {Label}: Successfully docked, state={state}");
            }
            else
            {
                Log.Warning($"[MULE] {Label}: Docking failed at {homeStable.Label}, finding another STABLE");
                // Docking failed (STABLE full?) - find another STABLE
                Building_Stable newStable = ArsenalNetworkManager.GetNearestStableWithSpace(Position, Map);
                if (newStable != null && newStable != homeStable)
                {
                    homeStable = newStable;
                    state = MuleState.ReturningHome;
                    CalculatePathTo(homeStable.InteractionCell);
                    Log.Message($"[MULE] {Label}: Redirecting to {newStable.Label}");
                }
                else
                {
                    // No available STABLE - just wait here as Idle
                    state = MuleState.Idle;
                    Log.Warning($"[MULE] {Label}: No STABLE available, waiting as Idle");
                }
            }
        }

        /// <summary>
        /// Checks if MULE is adjacent to a building (within 1 cell of any occupied cell).
        /// </summary>
        private bool IsAdjacentToBuilding(Thing building)
        {
            if (building == null || Map == null) return false;

            // Get the building's occupied rectangle
            CellRect rect = building.OccupiedRect();

            // Check if our position is adjacent to any cell in the rectangle
            // ExpandedBy(1) includes the building cells AND one cell around them
            foreach (IntVec3 cell in rect.ExpandedBy(1))
            {
                if (cell == Position)
                    return true;
            }

            return false;
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
                            CalculatePathTo(homeStable.InteractionCell);
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

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Log State",
                    action = delegate
                    {
                        Log.Warning($"=== MULE DEBUG: {Label} ===");
                        Log.Warning($"State: {state}");
                        Log.Warning($"Position: {Position}");
                        Log.Warning($"ExactPosition: {exactPosition}");
                        Log.Warning($"Battery: {currentBattery}/{MAX_BATTERY} ({BatteryPercent:P0})");
                        Log.Warning($"HomeStable: {homeStable?.Label ?? "NULL"}");
                        Log.Warning($"CurrentTask: {currentTask?.ToString() ?? "NULL"}");
                        if (currentTask != null)
                        {
                            Log.Warning($"  Task Type: {currentTask.taskType}");
                            Log.Warning($"  Target Cell: {currentTask.targetCell}");
                            Log.Warning($"  Distance to target: {Position.DistanceTo(currentTask.targetCell)}");
                            if (currentTask.taskType == MuleTaskType.Mine)
                            {
                                Building mineable = currentTask.targetCell.GetFirstMineable(Map);
                                Log.Warning($"  Mineable at target: {mineable?.Label ?? "NULL"}");
                            }
                        }
                        Log.Warning($"MiningProgress: {miningProgress}");
                        Log.Warning($"CarriedThing: {carriedThing?.LabelShort ?? "NULL"}");
                        Log.Warning($"Path: {currentPath?.Count ?? 0} waypoints, index={pathIndex}");
                        if (currentPath != null && currentPath.Count > 0)
                        {
                            Log.Warning($"  Path start: {currentPath[0]}");
                            Log.Warning($"  Path end: {currentPath[currentPath.Count - 1]}");
                        }
                        Log.Warning($"=== END DEBUG ===");
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Force Mining State",
                    action = delegate
                    {
                        // Find nearest mining designation
                        var des = Map.designationManager.AllDesignations
                            .Where(d => d.def == DesignationDefOf.Mine && !d.target.HasThing)
                            .OrderBy(d => d.target.Cell.DistanceTo(Position))
                            .FirstOrDefault();

                        if (des != null)
                        {
                            IntVec3 cell = des.target.Cell;
                            Building mineable = cell.GetFirstMineable(Map);
                            if (mineable != null)
                            {
                                currentTask = MuleTask.CreateMiningTask(cell, des);
                                state = MuleState.Mining;
                                miningProgress = 0;
                                Log.Warning($"[MULE] {Label}: Forced into Mining state for {cell}, mineable={mineable.Label}");
                            }
                            else
                            {
                                Log.Warning($"[MULE] {Label}: No mineable at {cell}");
                            }
                        }
                        else
                        {
                            Log.Warning($"[MULE] {Label}: No mining designations found");
                        }
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Step Mining",
                    action = delegate
                    {
                        if (state == MuleState.Mining && currentTask != null)
                        {
                            IntVec3 mineCell = currentTask.targetCell;
                            Building mineable = mineCell.GetFirstMineable(Map);
                            float mineWork = mineable?.MaxHitPoints * 0.5f ?? 750f;

                            Log.Warning($"[MULE] {Label}: Manual mining step");
                            Log.Warning($"  Before: progress={miningProgress}, threshold={mineWork}");

                            // Do 100 ticks worth of mining
                            miningProgress += MINING_WORK_PER_TICK * 100;

                            Log.Warning($"  After: progress={miningProgress}");

                            if (miningProgress >= mineWork && mineable != null)
                            {
                                Log.Warning($"  Mining complete! Destroying rock...");
                                var resourceDef = mineable.def.building?.mineableThing;
                                int yield = mineable.def.building?.mineableYield ?? 0;
                                mineable.Destroy(DestroyMode.KillFinalize);

                                if (resourceDef != null && yield > 0)
                                {
                                    Thing resource = ThingMaker.MakeThing(resourceDef);
                                    resource.stackCount = Mathf.Min(yield, MAX_CARRY_STACK);
                                    carriedThing = resource;
                                    Log.Warning($"  Collected: {carriedThing.LabelShort} x{carriedThing.stackCount}");
                                }
                            }
                        }
                        else
                        {
                            Log.Warning($"[MULE] {Label}: Not in Mining state (state={state})");
                        }
                    }
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
