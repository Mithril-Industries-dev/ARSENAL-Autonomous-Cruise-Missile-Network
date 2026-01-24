using System.Collections.Generic;
using System.Linq;
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
        private const int PATH_RECALC_INTERVAL = 60; // Recalculate path every 1 second
        private const int STUCK_THRESHOLD_TICKS = 90; // Consider stuck after 1.5 seconds no movement
        private const int MAX_PATH_ITERATIONS = 10000; // A* iteration limit
        private int ticksSincePathCalc;
        private IntVec3 currentDestination;

        // Stuck detection
        private IntVec3 lastProgressPosition;
        private int ticksSinceProgress;

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
            Scribe_Values.Look(ref currentDestination, "currentDestination");
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
                TryGetNewTaskOrGoHome();
                return;
            }

            IntVec3 mineCell = currentTask.targetCell;
            if (!mineCell.InBounds(Map))
            {
                TryGetNewTaskOrGoHome();
                return;
            }

            Building mineable = mineCell.GetFirstMineable(Map);
            if (mineable == null)
            {
                // Rock is gone - collect any dropped resources and move on
                CollectMinedResources(mineCell);

                // Remove mining designation if it exists
                if (currentTask.miningDesignation != null && Map.designationManager != null)
                {
                    Map.designationManager.RemoveDesignation(currentTask.miningDesignation);
                }

                TransitionToHaulingOrComplete();
                return;
            }

            // Track mining progress
            miningProgress += MINING_WORK_PER_TICK;

            // Visual effects
            if (this.IsHashIntervalTick(15))
            {
                FleckMaker.ThrowMicroSparks(mineCell.ToVector3Shifted(), Map);
                FleckMaker.ThrowDustPuff(mineCell, Map, 0.8f);
            }

            // Mining complete when we've done enough work (based on HP)
            if (miningProgress >= mineable.MaxHitPoints)
            {
                // Get yield info before destroying
                ThingDef resourceDef = mineable.def.building?.mineableThing;
                int yieldAmount = mineable.def.building?.mineableYield ?? 0;

                // Apply yield multiplier from difficulty/settings if available
                float yieldMultiplier = Find.Storyteller?.difficulty?.mineYieldFactor ?? 1f;
                yieldAmount = Mathf.RoundToInt(yieldAmount * yieldMultiplier);

                // Destroy the mineable (use Vanish to prevent RimWorld from also spawning resources)
                mineable.Destroy(DestroyMode.Vanish);

                // Clean up any resources that RimWorld might have spawned anyway
                // (some mineables have special destroy handlers that ignore DestroyMode)
                if (resourceDef != null)
                {
                    foreach (Thing t in mineCell.GetThingList(Map).ToArray())
                    {
                        if (t.def == resourceDef && t.def.category == ThingCategory.Item)
                        {
                            t.Destroy(DestroyMode.Vanish);
                        }
                    }
                }

                // Remove mining designation
                if (currentTask.miningDesignation != null && Map.designationManager != null)
                {
                    Map.designationManager.RemoveDesignation(currentTask.miningDesignation);
                }

                // Spawn and pick up resources manually (DestroyMined with null pawn doesn't work well)
                if (resourceDef != null && yieldAmount > 0)
                {
                    int pickupAmount = Mathf.Min(yieldAmount, MAX_CARRY_STACK);
                    Thing resource = ThingMaker.MakeThing(resourceDef);
                    resource.stackCount = pickupAmount;
                    carriedThing = resource;
                    Log.Message($"[MULE] {Label}: Mined {resourceDef.defName} x{pickupAmount}");

                    // If there's leftover, spawn it on the ground
                    if (yieldAmount > pickupAmount)
                    {
                        Thing leftover = ThingMaker.MakeThing(resourceDef);
                        leftover.stackCount = yieldAmount - pickupAmount;
                        GenPlace.TryPlaceThing(leftover, mineCell, Map, ThingPlaceMode.Near);
                    }
                }

                miningProgress = 0;
                TransitionToHaulingOrComplete();
            }
        }

        /// <summary>
        /// After mining, either haul resources or complete task.
        /// </summary>
        private void TransitionToHaulingOrComplete()
        {
            miningProgress = 0;

            // If carrying something, try to haul it
            if (carriedThing != null)
            {
                IntVec3 haulDest = FindHaulDestination(carriedThing);
                if (haulDest.IsValid)
                {
                    Log.Message($"[MULE] {Label}: Hauling {carriedThing.LabelShort} to {haulDest}");

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
                    return;
                }
                else
                {
                    // No destination - drop the item here
                    Log.Message($"[MULE] {Label}: No haul destination, dropping {carriedThing.LabelShort} here");
                    GenPlace.TryPlaceThing(carriedThing, Position, Map, ThingPlaceMode.Near);
                    carriedThing = null;
                }
            }

            // Go home
            CompleteTask();
        }

        /// <summary>
        /// Finds a suitable destination for hauling an item.
        /// Uses RimWorld's built-in system when possible, falls back to manual search.
        /// </summary>
        private IntVec3 FindHaulDestination(Thing item)
        {
            if (Map == null || item == null) return IntVec3.Invalid;

            // Try RimWorld's built-in storage finding first (works for spawned items)
            if (item.Spawned)
            {
                IntVec3 result;
                if (StoreUtility.TryFindBestBetterStoreCellFor(item, null, Map, StoragePriority.Unstored, Faction.OfPlayer, out result, true))
                {
                    return result;
                }
            }

            // Manual search for unspawned items (like resources we just created from mining)
            foreach (var slotGroup in Map.haulDestinationManager.AllGroupsListForReading)
            {
                if (slotGroup?.Settings == null) continue;
                if (!slotGroup.Settings.AllowedToAccept(item)) continue;

                foreach (IntVec3 cell in slotGroup.CellsList)
                {
                    if (!cell.InBounds(Map) || !cell.Walkable(Map)) continue;

                    // Check if cell can accept the item (empty or stackable)
                    Thing existing = cell.GetFirstItem(Map);
                    if (existing == null)
                    {
                        return cell; // Empty cell
                    }
                    if (existing.def == item.def && existing.stackCount < existing.def.stackLimit)
                    {
                        return cell; // Can stack
                    }
                }
            }

            // Fallback: drop near home STABLE
            if (homeStable != null)
            {
                return homeStable.InteractionCell;
            }

            return IntVec3.Invalid;
        }

        private void TickHauling()
        {
            // Don't check return viability while hauling - we're committed to delivering
            // (checking every 60 ticks was causing MULEs to abort mid-haul)

            DrainBattery(ACTIVE_DRAIN_PER_TICK);
            MoveAlongPath();

            if (currentTask == null)
            {
                Log.Message($"[MULE] {Label}: TickHauling - no current task, completing");
                // Drop item if we have one
                if (carriedThing != null)
                {
                    DeliverCarriedItem();
                }
                CompleteTask();
                return;
            }

            // Check if arrived at destination
            IntVec3 dest = currentTask.destinationCell;
            Thing destThing = currentTask.destination;
            bool arrived = Position.DistanceTo(dest) < 2f;
            if (!arrived && destThing != null)
            {
                arrived = IsAdjacentToBuilding(destThing);
            }

            if (arrived)
            {
                Log.Message($"[MULE] {Label}: Arrived at haul destination, delivering {carriedThing?.LabelShort ?? "nothing"}");
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

        /// <summary>
        /// A* pathfinding node for priority queue
        /// </summary>
        private struct PathNode
        {
            public IntVec3 cell;
            public float fCost; // g + h
            public float gCost; // distance from start

            public PathNode(IntVec3 cell, float gCost, float hCost)
            {
                this.cell = cell;
                this.gCost = gCost;
                this.fCost = gCost + hCost;
            }
        }

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

            // A* pathfinding - efficient directed search
            // Open set as a list we keep sorted by fCost
            List<PathNode> openSet = new List<PathNode>();
            Dictionary<IntVec3, IntVec3> cameFrom = new Dictionary<IntVec3, IntVec3>();
            Dictionary<IntVec3, float> gScore = new Dictionary<IntVec3, float>();
            HashSet<IntVec3> closedSet = new HashSet<IntVec3>();

            float startH = HeuristicDistance(Position, destination);
            openSet.Add(new PathNode(Position, 0, startH));
            gScore[Position] = 0;
            cameFrom[Position] = Position;

            IntVec3 reached = IntVec3.Invalid;
            int iterations = 0;

            while (openSet.Count > 0 && iterations < MAX_PATH_ITERATIONS)
            {
                iterations++;

                // Find node with lowest fCost
                int bestIndex = 0;
                for (int i = 1; i < openSet.Count; i++)
                {
                    if (openSet[i].fCost < openSet[bestIndex].fCost)
                        bestIndex = i;
                }

                PathNode current = openSet[bestIndex];
                openSet.RemoveAt(bestIndex);

                // Check if we've reached the destination (or close enough)
                if (current.cell.DistanceTo(destination) < 1.5f)
                {
                    reached = current.cell;
                    break;
                }

                closedSet.Add(current.cell);

                // Check all 8 neighbors
                foreach (IntVec3 dir in GenAdj.AdjacentCells)
                {
                    IntVec3 neighbor = current.cell + dir;

                    if (!neighbor.InBounds(Map)) continue;
                    if (closedSet.Contains(neighbor)) continue;
                    if (!neighbor.Walkable(Map)) continue;

                    // Calculate movement cost (diagonal costs more)
                    bool isDiagonal = dir.x != 0 && dir.z != 0;
                    float moveCost = isDiagonal ? 1.41f : 1f;

                    // Add terrain cost from RimWorld's pathgrid
                    int terrainCost = Map.pathGrid.PerceivedPathCostAt(neighbor);
                    if (terrainCost >= 10000) continue; // Impassable

                    moveCost += terrainCost * 0.01f; // Small terrain weight

                    float tentativeG = current.gCost + moveCost;

                    if (!gScore.ContainsKey(neighbor) || tentativeG < gScore[neighbor])
                    {
                        gScore[neighbor] = tentativeG;
                        cameFrom[neighbor] = current.cell;

                        float h = HeuristicDistance(neighbor, destination);
                        PathNode newNode = new PathNode(neighbor, tentativeG, h);

                        // Add to open set if not already there
                        bool found = false;
                        for (int i = 0; i < openSet.Count; i++)
                        {
                            if (openSet[i].cell == neighbor)
                            {
                                openSet[i] = newNode;
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            openSet.Add(newNode);
                        }
                    }
                }
            }

            // Reconstruct path if we found the destination
            if (reached.IsValid)
            {
                List<IntVec3> reversePath = new List<IntVec3>();
                IntVec3 step = reached;

                while (step != Position && cameFrom.ContainsKey(step))
                {
                    reversePath.Add(step);
                    step = cameFrom[step];
                }

                reversePath.Reverse();
                currentPath = reversePath;

                // Path smoothing - remove unnecessary waypoints
                SmoothPath();
            }

            pathIndex = 0;
            ticksSincePathCalc = 0;
            lastProgressPosition = Position;
            ticksSinceProgress = 0;
        }

        /// <summary>
        /// Heuristic distance for A* - uses Euclidean distance
        /// </summary>
        private float HeuristicDistance(IntVec3 a, IntVec3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Simplifies path by removing intermediate waypoints when direct line is clear
        /// </summary>
        private void SmoothPath()
        {
            if (currentPath == null || currentPath.Count < 3) return;

            List<IntVec3> smoothed = new List<IntVec3>();
            smoothed.Add(currentPath[0]);

            int i = 0;
            while (i < currentPath.Count - 1)
            {
                // Try to skip ahead as far as possible with clear line of sight
                int furthest = i + 1;
                for (int j = i + 2; j < currentPath.Count; j++)
                {
                    if (HasClearPath(currentPath[i], currentPath[j]))
                    {
                        furthest = j;
                    }
                    else
                    {
                        break; // Can't skip further
                    }
                }

                smoothed.Add(currentPath[furthest]);
                i = furthest;
            }

            currentPath = smoothed;
        }

        /// <summary>
        /// Checks if there's a clear walkable line between two points
        /// </summary>
        private bool HasClearPath(IntVec3 from, IntVec3 to)
        {
            // Bresenham's line algorithm to check all cells
            int dx = Mathf.Abs(to.x - from.x);
            int dz = Mathf.Abs(to.z - from.z);
            int sx = from.x < to.x ? 1 : -1;
            int sz = from.z < to.z ? 1 : -1;
            int err = dx - dz;

            int x = from.x;
            int z = from.z;

            while (x != to.x || z != to.z)
            {
                IntVec3 cell = new IntVec3(x, 0, z);
                if (!cell.InBounds(Map) || !cell.Walkable(Map))
                    return false;

                int e2 = 2 * err;
                if (e2 > -dz)
                {
                    err -= dz;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    z += sz;
                }
            }

            return true;
        }

        private void MoveAlongPath()
        {
            ticksSincePathCalc++;
            ticksSinceProgress++;

            // Stuck detection - if we haven't moved in a while, force recalc
            if (Position != lastProgressPosition)
            {
                lastProgressPosition = Position;
                ticksSinceProgress = 0;
            }

            bool isStuck = ticksSinceProgress >= STUCK_THRESHOLD_TICKS;

            // Get current destination based on state
            IntVec3 dest = IntVec3.Invalid;
            if (state == MuleState.ReturningHome || state == MuleState.DeliveringToStable)
            {
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

            // Recalculate path if: periodic interval, empty path, or stuck
            bool needsRecalc = ticksSincePathCalc >= PATH_RECALC_INTERVAL;
            bool pathEmpty = currentPath == null || currentPath.Count == 0 || pathIndex >= currentPath.Count;
            bool nextCellBlocked = false;

            // Check if next cell in path is now blocked
            if (!pathEmpty && pathIndex < currentPath.Count)
            {
                IntVec3 nextCell = currentPath[pathIndex];
                if (!nextCell.Walkable(Map))
                {
                    nextCellBlocked = true;
                }
            }

            if ((needsRecalc || pathEmpty || isStuck || nextCellBlocked) && dest.IsValid)
            {
                if (isStuck)
                {
                    // When stuck, try to find alternate path by clearing old path first
                    currentPath = null;
                    pathIndex = 0;
                }
                CalculatePathTo(dest);
            }

            // If still no path, try direct movement as fallback
            if ((currentPath == null || currentPath.Count == 0 || pathIndex >= currentPath.Count) && dest.IsValid)
            {
                TryDirectMovement(dest);
                return;
            }

            if (currentPath == null || currentPath.Count == 0 || pathIndex >= currentPath.Count)
            {
                return;
            }

            // Move along path
            IntVec3 targetCell = currentPath[pathIndex];

            // Skip waypoint if it became unwalkable
            if (!targetCell.Walkable(Map))
            {
                pathIndex++;
                if (pathIndex >= currentPath.Count)
                {
                    CalculatePathTo(dest);
                }
                return;
            }

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
                // Check if next position is walkable before moving
                Vector3 nextPos = exactPosition + direction * SPEED;
                IntVec3 nextIntCell = nextPos.ToIntVec3();

                if (nextIntCell.InBounds(Map) && nextIntCell.Walkable(Map))
                {
                    exactPosition = nextPos;
                }
                else
                {
                    // Can't move forward, recalculate path
                    CalculatePathTo(dest);
                    return;
                }
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

        /// <summary>
        /// Direct movement fallback when A* can't find a path
        /// </summary>
        private void TryDirectMovement(IntVec3 dest)
        {
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

                // Update rotation
                if (targetDir.sqrMagnitude > 0.001f)
                {
                    currentRotation = Mathf.Atan2(targetDir.x, targetDir.z) * Mathf.Rad2Deg;
                }
            }
            else
            {
                // Try moving around obstacle
                TryMoveAroundObstacle(dest);
            }
        }

        /// <summary>
        /// Attempts to move around an obstacle when direct path is blocked
        /// </summary>
        private void TryMoveAroundObstacle(IntVec3 dest)
        {
            // Try perpendicular directions
            Vector3 toDest = (dest.ToVector3Shifted() - exactPosition);
            Vector3 perp1 = new Vector3(-toDest.z, 0, toDest.x).normalized;
            Vector3 perp2 = new Vector3(toDest.z, 0, -toDest.x).normalized;

            Vector3[] attempts = { perp1, perp2 };

            foreach (Vector3 dir in attempts)
            {
                Vector3 nextPos = exactPosition + dir * SPEED;
                IntVec3 nextCell = nextPos.ToIntVec3();

                if (nextCell.InBounds(Map) && nextCell.Walkable(Map))
                {
                    exactPosition = nextPos;
                    if (nextCell != Position)
                    {
                        Position = nextCell;
                    }
                    break;
                }
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
            if (carriedThing != null) return; // Already carrying something

            // Look for dropped resources at and around the mine cell
            for (int i = 0; i < 9; i++)
            {
                IntVec3 checkCell = (i == 0) ? mineCell : mineCell + GenAdj.AdjacentCells[i - 1];
                if (!checkCell.InBounds(Map)) continue;

                foreach (Thing t in checkCell.GetThingList(Map).ToArray())
                {
                    if (t.def.category == ThingCategory.Item)
                    {
                        int pickupCount = Mathf.Min(t.stackCount, MAX_CARRY_STACK);
                        carriedThing = t.SplitOff(pickupCount);
                        Log.Message($"[MULE] {Label}: Collected {carriedThing.LabelShort} x{carriedThing.stackCount}");
                        return;
                    }
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
