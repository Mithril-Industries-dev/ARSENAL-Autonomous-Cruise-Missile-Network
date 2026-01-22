using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Represents a threat reported by an ARGUS sensor.
    /// </summary>
    public class ThreatEntry
    {
        public Pawn Pawn;
        public int LastSeenTick;
        public Building_ARGUS ReportingArgus;

        public ThreatEntry(Pawn pawn, int tick, Building_ARGUS argus)
        {
            Pawn = pawn;
            LastSeenTick = tick;
            ReportingArgus = argus;
        }

        public bool IsStale(int currentTick, int staleThreshold)
        {
            return currentTick - LastSeenTick > staleThreshold;
        }
    }

    /// <summary>
    /// LATTICE - Command & Control node for the drone swarm defense system.
    /// Only ONE LATTICE allowed per map.
    /// Aggregates threat reports from ARGUS sensors and coordinates DART response.
    /// </summary>
    public class Building_Lattice : Building
    {
        // Registered QUIVERs
        private List<Building_Quiver> registeredQuivers = new List<Building_Quiver>();

        // Registered ARGUS sensors
        private List<Building_ARGUS> registeredArgus = new List<Building_ARGUS>();

        // Threat aggregation from ARGUS reports
        private Dictionary<Pawn, ThreatEntry> aggregatedThreats = new Dictionary<Pawn, ThreatEntry>();
        private const int THREAT_STALE_TICKS = 180; // 3 seconds without sighting = stale

        // Tracking in-flight DARTs per target
        private Dictionary<Pawn, int> assignedDartsPerTarget = new Dictionary<Pawn, int>();

        // DARTs awaiting reassignment
        private List<DART_Flyer> awaitingReassignment = new List<DART_Flyer>();

        // Flight pathfinding grid
        private FlightPathGrid flightGrid;

        // Processing interval (no longer "scanning" - just processing ARGUS reports)
        private const int PROCESS_INTERVAL = 60; // Ticks between processing (~1 second)
        private int ticksSinceLastProcess;

        // Threat evaluation parameters
        private const float DART_LETHALITY = 20f; // How much threat a single DART can handle
        private const float BASE_THREAT_TRIBAL = 35f;
        private const float BASE_THREAT_PIRATE = 50f;
        private const float BASE_THREAT_MECHANOID = 150f;

        // Custom name
        private string customName;

        // Sound and effects
        private Sustainer scanningSustainer;
        private bool hadThreatsLastScan = false;

        // Max DARTs launched per processing cycle (prevents overwhelming all at once)
        private const int MAX_DARTS_PER_CYCLE = 8;

        // Properties
        public FlightPathGrid FlightGrid
        {
            get
            {
                if (flightGrid == null && Map != null)
                {
                    flightGrid = new FlightPathGrid(Map);
                }
                return flightGrid;
            }
        }

        public List<Building_Quiver> RegisteredQuivers => registeredQuivers;
        public int TotalAvailableDarts => registeredQuivers.Where(q => !q.IsInert).Sum(q => q.DartCount);

        // Properties for UI
        private CompPowerTrader powerComp;

        public bool IsPoweredOn()
        {
            return powerComp == null || powerComp.PowerOn;
        }

        public int ActiveThreatCount
        {
            get
            {
                CleanStaleThreats();
                return aggregatedThreats.Count;
            }
        }

        public int RegisteredArgusCount => registeredArgus.Count;

        public int DartsInFlight
        {
            get
            {
                if (Map == null) return 0;
                return Map.listerThings.ThingsOfDef(ArsenalDefOf.Arsenal_DART_Flyer)
                    .Cast<DART_Flyer>()
                    .Count(d => d.state == DartState.Engaging);
            }
        }

        public int DartsReturning
        {
            get
            {
                if (Map == null) return 0;
                return Map.listerThings.ThingsOfDef(ArsenalDefOf.Arsenal_DART_Flyer)
                    .Cast<DART_Flyer>()
                    .Count(d => d.state == DartState.Returning);
            }
        }

        public int DartsAwaiting => awaitingReassignment.Count;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();

            // Check for existing LATTICE on this map
            Building_Lattice existingLattice = ArsenalNetworkManager.GetLatticeOnMap(map);
            if (existingLattice != null && existingLattice != this)
            {
                // Cannot have two LATTICE nodes
                Messages.Message("Cannot place more than one LATTICE per map. Existing LATTICE will be used.",
                    MessageTypeDefOf.RejectInput, false);
                Destroy(DestroyMode.Vanish);
                return;
            }

            // Initialize flight grid
            flightGrid = new FlightPathGrid(map);

            // Register with network manager
            ArsenalNetworkManager.RegisterLattice(this);

            if (!respawningAfterLoad)
            {
                // Find and register all existing QUIVERs
                RegisterExistingQuivers();
            }
            else
            {
                // Re-register QUIVERs after load
                RegisterExistingQuivers();
            }

            // Notify all QUIVERs that LATTICE is available
            NotifyQuiversOfAvailability();

            // Notify all ARGUS sensors that LATTICE is available
            NotifyArgusOfAvailability();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Stop scanning sound
            StopScanningSound();

            // All QUIVERs go inert
            foreach (var quiver in registeredQuivers.ToList())
            {
                quiver.OnLatticeDestroyed();
            }
            registeredQuivers.Clear();

            // Notify all ARGUS sensors
            foreach (var argus in registeredArgus.ToList())
            {
                argus.OnLatticeDestroyed();
            }
            registeredArgus.Clear();

            // Clear threat aggregation
            aggregatedThreats.Clear();

            // Unregister from network
            ArsenalNetworkManager.DeregisterLattice(this);

            base.DeSpawn(mode);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (mode != DestroyMode.Vanish && Map != null)
            {
                Messages.Message("LATTICE destroyed! All QUIVERs are now inert. In-flight DARTs will continue to last target then crash.",
                    new TargetInfo(Position, Map), MessageTypeDefOf.NegativeEvent, false);
            }

            base.Destroy(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Collections.Look(ref registeredQuivers, "registeredQuivers", LookMode.Reference);
            Scribe_Collections.Look(ref registeredArgus, "registeredArgus", LookMode.Reference);
            Scribe_Collections.Look(ref awaitingReassignment, "awaitingReassignment", LookMode.Reference);
            Scribe_Values.Look(ref ticksSinceLastProcess, "ticksSinceLastProcess", 0);
            Scribe_Values.Look(ref customName, "customName");

            // Dictionary requires special handling
            Scribe_Collections.Look(ref assignedDartsPerTarget, "assignedDartsPerTarget",
                LookMode.Reference, LookMode.Value, ref tempPawnKeys, ref tempIntValues);

            // MULE system
            Scribe_Values.Look(ref ticksSinceMuleProcess, "ticksSinceMuleProcess", 0);

            // Clean up null references after load
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                registeredQuivers.RemoveAll(q => q == null);
                registeredArgus.RemoveAll(a => a == null);
                awaitingReassignment.RemoveAll(d => d == null);
                aggregatedThreats.Clear(); // Clear on load - ARGUS will repopulate
                CleanupDeadTargets();
                pendingMuleTasks = pendingMuleTasks ?? new Queue<MuleTask>();
            }
        }

        // Temp lists for dictionary serialization
        private List<Pawn> tempPawnKeys;
        private List<int> tempIntValues;

        protected override void Tick()
        {
            base.Tick();

            // Only operate when powered
            CompPowerTrader power = GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                StopScanningSound();
                return;
            }

            // Maintain scanning sound
            MaintainScanningSound();

            // Visual effect (command processing pulse)
            if (this.IsHashIntervalTick(30))
            {
                SpawnScanningEffects();
            }

            ticksSinceLastProcess++;
            if (ticksSinceLastProcess >= PROCESS_INTERVAL)
            {
                ticksSinceLastProcess = 0;
                ProcessThreatsAndAssign();
            }

            // Process MULE tasks
            ProcessMuleTasks();
        }

        private void MaintainScanningSound()
        {
            // Sound sustainer removed - MechSerumUsed is not a sustainer-compatible sound
            // and was causing 99+ errors per tick. LATTICE operates silently now.
            // Visual effects in SpawnScanningEffects() provide feedback instead.
        }

        private void StopScanningSound()
        {
            if (scanningSustainer != null && !scanningSustainer.Ended)
            {
                scanningSustainer.End();
            }
            scanningSustainer = null;
        }

        private void SpawnScanningEffects()
        {
            if (Map == null) return;

            // Subtle glow pulse effect at the LATTICE position
            FleckMaker.ThrowLightningGlow(Position.ToVector3Shifted(), Map, 0.5f);
        }

        /// <summary>
        /// Finds and registers existing QUIVERs on the map.
        /// </summary>
        private void RegisterExistingQuivers()
        {
            var quivers = ArsenalNetworkManager.GetQuiversOnMap(Map);
            foreach (var quiver in quivers)
            {
                if (!registeredQuivers.Contains(quiver))
                {
                    RegisterQuiver(quiver);
                }
            }
        }

        /// <summary>
        /// Notifies all QUIVERs on the map that LATTICE is available.
        /// </summary>
        private void NotifyQuiversOfAvailability()
        {
            var quivers = ArsenalNetworkManager.GetQuiversOnMap(Map);
            foreach (var quiver in quivers)
            {
                quiver.OnLatticeAvailable(this);
            }
        }

        /// <summary>
        /// Notifies all ARGUS sensors on the map that LATTICE is available.
        /// </summary>
        private void NotifyArgusOfAvailability()
        {
            var argusUnits = ArsenalNetworkManager.GetArgusOnMap(Map);
            foreach (var argus in argusUnits)
            {
                argus.OnLatticeAvailable(this);
                if (!registeredArgus.Contains(argus))
                {
                    registeredArgus.Add(argus);
                }
            }
        }

        /// <summary>
        /// Registers a QUIVER with this LATTICE.
        /// </summary>
        public void RegisterQuiver(Building_Quiver quiver)
        {
            if (quiver != null && !registeredQuivers.Contains(quiver))
            {
                registeredQuivers.Add(quiver);
            }
        }

        /// <summary>
        /// Unregisters a QUIVER from this LATTICE.
        /// </summary>
        public void UnregisterQuiver(Building_Quiver quiver)
        {
            registeredQuivers.Remove(quiver);
        }

        #region ARGUS Integration

        /// <summary>
        /// Registers an ARGUS sensor with this LATTICE.
        /// </summary>
        public void RegisterArgus(Building_ARGUS argus)
        {
            if (argus != null && !registeredArgus.Contains(argus))
            {
                registeredArgus.Add(argus);
            }
        }

        /// <summary>
        /// Unregisters an ARGUS sensor from this LATTICE.
        /// </summary>
        public void UnregisterArgus(Building_ARGUS argus)
        {
            registeredArgus.Remove(argus);
        }

        /// <summary>
        /// Sets a priority target designated by HAWKEYE.
        /// DARTs will converge on this target until dead or duration expires.
        /// </summary>
        public void SetPriorityTarget(Pawn target)
        {
            if (target == null || target.Dead || !target.Spawned)
                return;

            // Force assignment of multiple DARTs to priority target
            int dartsNeeded = CalculateDartsNeeded(target) + 3; // Extra DARTs for priority
            int dartsAlreadyAssigned = GetAssignedDarts(target);
            int additionalDartsNeeded = dartsNeeded - dartsAlreadyAssigned;

            if (additionalDartsNeeded > 0)
            {
                // Rapid launch for priority targets - bypass rate limiting
                var sortedQuivers = registeredQuivers
                    .Where(q => !q.IsInert && q.DartCount > 0)
                    .OrderBy(q => q.Position.DistanceTo(target.Position))
                    .ToList();

                int launched = 0;
                foreach (var quiver in sortedQuivers)
                {
                    while (quiver.DartCount > 0 && launched < additionalDartsNeeded)
                    {
                        DART_Flyer dart = quiver.LaunchDart(target);
                        if (dart != null)
                        {
                            if (!assignedDartsPerTarget.ContainsKey(target))
                            {
                                assignedDartsPerTarget[target] = 0;
                            }
                            assignedDartsPerTarget[target]++;
                            launched++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (launched >= additionalDartsNeeded)
                        break;
                }
            }
        }

        /// <summary>
        /// Called by ARGUS sensors to report a detected threat.
        /// Aggregates and deduplicates threat reports.
        /// </summary>
        public void ReportThreat(Pawn threat, Building_ARGUS reportingArgus)
        {
            if (threat == null || threat.Dead || threat.Destroyed || !threat.Spawned)
                return;

            int currentTick = Find.TickManager.TicksGame;

            if (aggregatedThreats.TryGetValue(threat, out ThreatEntry existingEntry))
            {
                // Update existing entry with fresh sighting
                existingEntry.LastSeenTick = currentTick;
                existingEntry.ReportingArgus = reportingArgus;
            }
            else
            {
                // New threat detected
                aggregatedThreats[threat] = new ThreatEntry(threat, currentTick, reportingArgus);
            }
        }

        /// <summary>
        /// Removes stale threat entries that haven't been seen recently.
        /// </summary>
        private void CleanStaleThreats()
        {
            int currentTick = Find.TickManager.TicksGame;
            var staleKeys = aggregatedThreats
                .Where(kvp => kvp.Value.IsStale(currentTick, THREAT_STALE_TICKS) ||
                              kvp.Key == null || kvp.Key.Dead || kvp.Key.Destroyed || !kvp.Key.Spawned)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                aggregatedThreats.Remove(key);
            }
        }

        #endregion

        #region MULE Task Processing

        // MULE processing
        private const int MULE_PROCESS_INTERVAL = 120; // Process MULE tasks every 2 seconds
        private int ticksSinceMuleProcess;
        private Queue<MuleTask> pendingMuleTasks = new Queue<MuleTask>();

        /// <summary>
        /// Processes MULE tasks - mining designations and hauling to MORIA.
        /// </summary>
        private void ProcessMuleTasks()
        {
            if (Map == null) return;

            ticksSinceMuleProcess++;
            if (ticksSinceMuleProcess < MULE_PROCESS_INTERVAL)
                return;

            ticksSinceMuleProcess = 0;

            // Scan for mining designations
            ScanMiningDesignations();

            // Scan for items to haul to MORIA
            ScanMoriaHaulTasks();

            // Assign queued tasks to available MULEs
            AssignPendingMuleTasks();
        }

        /// <summary>
        /// Scans for mining designations and creates MULE tasks.
        /// Note: We don't check colonist reservations here - MULEs will compete with colonists.
        /// If a colonist mines the rock first, the MULE will detect it and get a new task.
        /// </summary>
        private void ScanMiningDesignations()
        {
            var designations = Map.designationManager.AllDesignations
                .Where(d => d.def == DesignationDefOf.Mine && d.target.HasThing == false)
                .ToList();

            foreach (var des in designations)
            {
                IntVec3 cell = des.target.Cell;

                // Check if mineable exists
                Building mineable = cell.GetFirstMineable(Map);
                if (mineable == null) continue;

                // Check if we already have a task for this cell
                bool alreadyQueued = pendingMuleTasks.Any(t =>
                    t.taskType == MuleTaskType.Mine && t.targetCell == cell);
                if (alreadyQueued) continue;

                // Check if a MULE is already working on this
                bool muleAssigned = ArsenalNetworkManager.GetAllMules()
                    .Any(m => m.CurrentTask?.taskType == MuleTaskType.Mine &&
                              m.CurrentTask?.targetCell == cell);
                if (muleAssigned) continue;

                // Create mining task
                MuleTask task = MuleTask.CreateMiningTask(cell, des);
                pendingMuleTasks.Enqueue(task);
            }
        }

        /// <summary>
        /// Scans for items that should be hauled to MORIA or stockpiles.
        /// Note: We don't check colonist reservations here - MULEs will compete with colonists.
        /// If a colonist picks up the item first, the MULE will detect it and get a new task.
        /// </summary>
        private void ScanMoriaHaulTasks()
        {
            // Find items on the ground that need hauling
            var haulableItems = Map.listerHaulables.ThingsPotentiallyNeedingHauling();

            foreach (Thing item in haulableItems)
            {
                if (item == null || item.Destroyed || !item.Spawned) continue;

                // Skip forbidden items
                if (item.IsForbidden(Faction.OfPlayer)) continue;

                // Skip items currently being carried
                if (item.ParentHolder != null && !(item.ParentHolder is Map)) continue;

                // Check if we already have a task for this item
                bool alreadyQueued = pendingMuleTasks.Any(t =>
                    (t.taskType == MuleTaskType.MoriaFeed || t.taskType == MuleTaskType.Haul) &&
                    t.targetThing == item);
                if (alreadyQueued) continue;

                // Check if a MULE is already working on this
                bool muleAssigned = ArsenalNetworkManager.GetAllMules()
                    .Any(m => m.CurrentTask?.targetThing == item);
                if (muleAssigned) continue;

                // First priority: MORIA storage
                Building_Moria targetMoria = ArsenalNetworkManager.GetNearestMoriaForItem(item, Map);
                if (targetMoria != null)
                {
                    MuleTask task = MuleTask.CreateMoriaFeedTask(item, targetMoria);
                    pendingMuleTasks.Enqueue(task);
                    continue;
                }

                // Second priority: Regular stockpile
                IntVec3 stockpileCell = FindStockpileCellForItem(item);
                if (stockpileCell.IsValid)
                {
                    MuleTask task = MuleTask.CreateHaulTask(item, null, stockpileCell);
                    pendingMuleTasks.Enqueue(task);
                }
            }
        }

        /// <summary>
        /// Finds a stockpile cell that can accept the given item.
        /// </summary>
        private IntVec3 FindStockpileCellForItem(Thing item)
        {
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

        /// <summary>
        /// Assigns pending MULE tasks to available MULEs.
        /// </summary>
        private void AssignPendingMuleTasks()
        {
            if (pendingMuleTasks.Count == 0) return;

            int maxAssignPerCycle = 5; // Limit assignments per cycle
            int maxAttemptsPerCycle = 20; // Prevent infinite loops
            int assigned = 0;
            int attempts = 0;

            // Temporary list for tasks we couldn't assign this cycle
            List<MuleTask> deferredTasks = new List<MuleTask>();

            while (pendingMuleTasks.Count > 0 && assigned < maxAssignPerCycle && attempts < maxAttemptsPerCycle)
            {
                attempts++;
                MuleTask task = pendingMuleTasks.Dequeue();

                // Validate task is still valid
                if (!IsTaskValid(task))
                {
                    // Invalid task - discard it entirely
                    continue;
                }

                // Find an available MULE for this task
                var (mule, stable) = ArsenalNetworkManager.GetAvailableMuleForTask(task, Map);

                if (mule != null && stable != null)
                {
                    // Deploy the MULE
                    if (stable.DeployMule(mule, task))
                    {
                        assigned++;
                        // Task successfully assigned - don't re-queue
                    }
                    else
                    {
                        // Deployment failed (e.g., spawn cell blocked) - defer for later
                        deferredTasks.Add(task);
                    }
                }
                else
                {
                    // No MULE available for this task right now - defer for later
                    deferredTasks.Add(task);
                }
            }

            // Re-add deferred tasks to the back of the queue
            foreach (var task in deferredTasks)
            {
                pendingMuleTasks.Enqueue(task);
            }
        }

        /// <summary>
        /// Validates that a task is still valid.
        /// Note: We don't check colonist reservations - MULEs compete with colonists.
        /// The MULE will detect if work is done while traveling and get a new task.
        /// </summary>
        private bool IsTaskValid(MuleTask task)
        {
            if (task == null) return false;

            switch (task.taskType)
            {
                case MuleTaskType.Mine:
                    // Check if mining designation still exists
                    if (Map.designationManager.DesignationAt(task.targetCell, DesignationDefOf.Mine) == null)
                        return false;

                    // Check if mineable still exists
                    Building mineable = task.targetCell.GetFirstMineable(Map);
                    if (mineable == null)
                        return false;

                    return true;

                case MuleTaskType.Haul:
                    // Check if item still exists
                    if (task.targetThing == null || task.targetThing.Destroyed || !task.targetThing.Spawned)
                        return false;

                    // Check if item is being carried (colonist picked it up)
                    if (task.targetThing.ParentHolder != null && !(task.targetThing.ParentHolder is Map))
                        return false;

                    // Haul tasks to stockpile don't need destination building check
                    return task.destinationCell.IsValid;

                case MuleTaskType.MoriaFeed:
                    // Check if item still exists
                    if (task.targetThing == null || task.targetThing.Destroyed || !task.targetThing.Spawned)
                        return false;

                    // Check if destination MORIA still valid
                    if (task.destination == null || task.destination.Destroyed)
                        return false;

                    // Check if item is being carried (colonist picked it up)
                    if (task.targetThing.ParentHolder != null && !(task.targetThing.ParentHolder is Map))
                        return false;

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Gets the count of pending MULE tasks.
        /// </summary>
        public int PendingMuleTaskCount => pendingMuleTasks.Count;

        /// <summary>
        /// Allows a MULE to request a new task directly.
        /// Returns null if no suitable task is available.
        /// </summary>
        public MuleTask RequestNewTaskForMule(MULE_Drone mule)
        {
            if (pendingMuleTasks.Count == 0) return null;

            // Try to find a valid task from the queue
            int attempts = Mathf.Min(pendingMuleTasks.Count, 10);
            List<MuleTask> checkedTasks = new List<MuleTask>();

            for (int i = 0; i < attempts; i++)
            {
                if (pendingMuleTasks.Count == 0) break;

                MuleTask task = pendingMuleTasks.Dequeue();

                // Validate task
                if (!IsTaskValid(task))
                {
                    // Invalid task - discard
                    continue;
                }

                // Check if MULE can handle this task
                if (mule.CanAcceptTask(task))
                {
                    // Re-queue any tasks we checked but didn't take
                    foreach (var t in checkedTasks)
                    {
                        pendingMuleTasks.Enqueue(t);
                    }
                    return task;
                }
                else
                {
                    // Can't take this task (battery too low?), keep it for later
                    checkedTasks.Add(task);
                }
            }

            // Re-queue tasks we couldn't assign
            foreach (var t in checkedTasks)
            {
                pendingMuleTasks.Enqueue(t);
            }

            return null;
        }

        /// <summary>
        /// Gets the count of active MULEs on this map.
        /// </summary>
        public int ActiveMuleCount
        {
            get
            {
                return ArsenalNetworkManager.GetAllMules()
                    .Count(m => m.Map == Map &&
                               m.state != MuleState.Idle &&
                               m.state != MuleState.Charging);
            }
        }

        #endregion

        /// <summary>
        /// Processes aggregated threats and assigns DARTs.
        /// No longer scans directly - relies on ARGUS reports.
        /// Also considers HAWKEYE-marked priority targets.
        /// </summary>
        private void ProcessThreatsAndAssign()
        {
            // Clean up dead targets from assignment tracking
            CleanupDeadTargets();

            // Clean up stale threats from aggregation
            CleanStaleThreats();

            // Process any DARTs awaiting reassignment
            ProcessReassignmentQueue();

            // Get threats from ARGUS aggregation (not direct scanning)
            List<Pawn> threats = GetAggregatedThreats();

            // Also check for HAWKEYE priority target - it's valid even outside ARGUS range
            Pawn hawkeyeTarget = CompHawkeyeSensor.GlobalPriorityTarget;
            if (hawkeyeTarget != null && !hawkeyeTarget.Dead && hawkeyeTarget.Spawned &&
                hawkeyeTarget.Map == Map && !threats.Contains(hawkeyeTarget))
            {
                threats.Add(hawkeyeTarget);
            }

            // Also include any threats detected by HAWKEYE sensors via SKYLINK
            if (ArsenalNetworkManager.IsLatticeConnectedToSkylink())
            {
                foreach (var pawn in ArsenalNetworkManager.GetAllHawkeyePawns())
                {
                    if (pawn?.Map != Map) continue;
                    var hawkeye = pawn.apparel?.WornApparel?.FirstOrDefault(a => a is Apparel_HawkEye) as Apparel_HawkEye;
                    var comp = hawkeye?.SensorComp;
                    if (comp != null && comp.IsOperational)
                    {
                        foreach (var threat in comp.GetDetectedThreats())
                        {
                            if (!threats.Contains(threat))
                            {
                                threats.Add(threat);
                            }
                        }
                    }
                }
            }

            if (threats.Count == 0)
            {
                // No threats - recall any in-flight DARTs
                RecallAllDarts();

                // Play all-clear sound if we just cleared threats
                if (hadThreatsLastScan && Map != null)
                {
                    SoundDefOf.ClickReject.PlayOneShotOnCamera(Map);
                }
                hadThreatsLastScan = false;
                return;
            }

            // Alert sound when threats first detected
            if (!hadThreatsLastScan && Map != null)
            {
                // Alert! Threats detected
                SoundDefOf.TinyBell.PlayOneShotOnCamera(Map);

                // Visual alert effect
                FleckMaker.ThrowLightningGlow(Position.ToVector3Shifted(), Map, 2f);
            }
            hadThreatsLastScan = true;

            // Sort threats by distance to nearest QUIVER (prioritize closer threats)
            threats = threats.OrderBy(t => GetDistanceToNearestQuiver(t.Position)).ToList();

            // Calculate launch budget for this cycle
            // Allow multiple DARTs per processing cycle, distributed across threats
            int launchBudget = Mathf.Min(MAX_DARTS_PER_CYCLE, Mathf.Max(4, threats.Count));
            int dartsLaunched = 0;

            // Multiple passes to ensure each threat gets assigned DARTs
            bool launchedThisPass = true;
            while (launchedThisPass && dartsLaunched < launchBudget)
            {
                launchedThisPass = false;

                foreach (Pawn threat in threats)
                {
                    if (dartsLaunched >= launchBudget)
                        break;

                    int dartsNeeded = CalculateDartsNeeded(threat);
                    int dartsAlreadyAssigned = GetAssignedDarts(threat);
                    int additionalDartsNeeded = dartsNeeded - dartsAlreadyAssigned;

                    if (additionalDartsNeeded > 0)
                    {
                        if (AssignDarts(threat, 1))
                        {
                            dartsLaunched++;
                            launchedThisPass = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets all threats from ARGUS aggregation.
        /// Only threats visible to ARGUS sensors are targetable.
        /// </summary>
        private List<Pawn> GetAggregatedThreats()
        {
            // Return pawns from aggregated threats (reported by ARGUS sensors)
            return aggregatedThreats.Keys
                .Where(p => p != null && !p.Dead && !p.Downed && p.Spawned)
                .ToList();
        }

        /// <summary>
        /// Legacy method for compatibility - gets threats from aggregation.
        /// </summary>
        private List<Pawn> GetHostileThreats()
        {
            return GetAggregatedThreats();
        }

        /// <summary>
        /// Calculates how many DARTs are needed to eliminate a threat.
        /// </summary>
        private int CalculateDartsNeeded(Pawn threat)
        {
            float threatValue = EvaluateThreat(threat);
            int dartsNeeded = Mathf.CeilToInt(threatValue / DART_LETHALITY);
            return Mathf.Max(1, dartsNeeded); // At least 1 DART per threat
        }

        /// <summary>
        /// Evaluates the threat level of a pawn.
        /// Considers: race type, tech level, health, equipment, armor, and shields.
        /// </summary>
        private float EvaluateThreat(Pawn pawn)
        {
            float baseThreat;

            // Mechanoids are high priority
            if (pawn.RaceProps.IsMechanoid)
            {
                baseThreat = BASE_THREAT_MECHANOID;

                // Centipedes and other large mechs
                if (pawn.def.race.baseBodySize > 2f)
                {
                    baseThreat *= 1.5f;
                }
            }
            // Animals (manhunters, predators, etc.)
            else if (pawn.RaceProps.Animal)
            {
                // Base threat on body size - larger animals are more dangerous
                float bodySize = pawn.def.race.baseBodySize;

                if (pawn.RaceProps.predator)
                {
                    // Predators: scale from 30 (small) to 120+ (thrumbo-sized)
                    baseThreat = 30f + (bodySize * 30f);
                }
                else
                {
                    // Non-predators (manhunters, etc): lower base threat
                    baseThreat = 20f + (bodySize * 15f);
                }

                // Manhunting animals are more aggressive - allocate more DARTs
                if (pawn.MentalState?.def == MentalStateDefOf.Manhunter ||
                    pawn.MentalState?.def == MentalStateDefOf.ManhunterPermanent)
                {
                    baseThreat *= 1.3f;
                }
            }
            else if (pawn.Faction?.def?.techLevel >= TechLevel.Industrial)
            {
                baseThreat = BASE_THREAT_PIRATE;
            }
            else
            {
                baseThreat = BASE_THREAT_TRIBAL;
            }

            // Modify by current health
            float healthPct = pawn.health.summaryHealth.SummaryHealthPercent;
            baseThreat *= healthPct;

            // Modify by equipment (weapons)
            if (pawn.equipment?.Primary != null)
            {
                var weapon = pawn.equipment.Primary;
                if (weapon.def.IsRangedWeapon)
                {
                    baseThreat *= 1.2f;
                }
                if (weapon.def.techLevel >= TechLevel.Spacer)
                {
                    baseThreat *= 1.3f;
                }
            }

            // Modify by armor - more armor = need more DARTs to kill
            float armorFactor = CalculateArmorFactor(pawn);
            baseThreat *= armorFactor;

            // Check for shield belt - need to overwhelm the shield
            if (HasShieldBelt(pawn))
            {
                baseThreat *= 2.0f; // Double the DARTs needed for shielded targets
            }

            return baseThreat;
        }

        /// <summary>
        /// Calculates armor factor based on pawn's equipped armor.
        /// Returns multiplier: 1.0 (no armor) to ~2.0 (heavy armor)
        /// </summary>
        private float CalculateArmorFactor(Pawn pawn)
        {
            if (pawn.apparel == null || pawn.apparel.WornApparelCount == 0)
                return 1.0f;

            float totalArmorSharp = 0f;
            float totalArmorBlunt = 0f;
            int armorPieces = 0;

            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                float sharp = apparel.GetStatValue(StatDefOf.ArmorRating_Sharp);
                float blunt = apparel.GetStatValue(StatDefOf.ArmorRating_Blunt);

                if (sharp > 0 || blunt > 0)
                {
                    totalArmorSharp += sharp;
                    totalArmorBlunt += blunt;
                    armorPieces++;
                }
            }

            if (armorPieces == 0)
                return 1.0f;

            // Average armor rating (sharp matters more for DART explosives)
            float avgArmor = (totalArmorSharp * 0.7f + totalArmorBlunt * 0.3f) / armorPieces;

            // Convert to multiplier: 0% armor = 1.0x, 100% armor = 2.0x
            // Heavily armored targets get more DARTs assigned
            return 1.0f + Mathf.Clamp(avgArmor, 0f, 1f);
        }

        /// <summary>
        /// Checks if pawn has an active shield belt equipped.
        /// </summary>
        private bool HasShieldBelt(Pawn pawn)
        {
            if (pawn.apparel == null)
                return false;

            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                // Check for shield belt comp (vanilla and most mods)
                var shieldComp = apparel.TryGetComp<CompShield>();
                if (shieldComp != null)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the number of DARTs already assigned to a target.
        /// </summary>
        private int GetAssignedDarts(Pawn target)
        {
            if (assignedDartsPerTarget.TryGetValue(target, out int count))
            {
                return count;
            }
            return 0;
        }

        /// <summary>
        /// Assigns DARTs from QUIVERs to a threat.
        /// Pulls from nearest QUIVER first.
        /// Returns true if a DART was launched, false otherwise.
        /// </summary>
        public bool AssignDarts(Pawn target, int count)
        {
            if (count <= 0 || target == null)
                return false;

            // Sort QUIVERs by distance to target
            var sortedQuivers = registeredQuivers
                .Where(q => !q.IsInert && q.DartCount > 0)
                .OrderBy(q => q.Position.DistanceTo(target.Position))
                .ToList();

            // Launch one DART for this target
            foreach (var quiver in sortedQuivers)
            {
                if (quiver.DartCount > 0)
                {
                    DART_Flyer dart = quiver.LaunchDart(target);
                    if (dart != null)
                    {
                        // Track assignment
                        if (!assignedDartsPerTarget.ContainsKey(target))
                        {
                            assignedDartsPerTarget[target] = 0;
                        }
                        assignedDartsPerTarget[target]++;

                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Gets the best QUIVER for DART delivery from ARSENAL.
        /// </summary>
        public Building_Quiver GetQuiverForDelivery()
        {
            return registeredQuivers
                .Where(q => !q.IsFull && !q.IsInert)
                .OrderBy(q => q.Priority)
                .ThenByDescending(q => q.EmptySlots)
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets a QUIVER for a returning DART.
        /// </summary>
        public Building_Quiver GetQuiverForReturn(DART_Flyer dart)
        {
            // Prefer the DART's home QUIVER
            if (dart.homeQuiver != null && !dart.homeQuiver.Destroyed &&
                !dart.homeQuiver.IsInert && !dart.homeQuiver.IsFull)
            {
                return dart.homeQuiver;
            }

            // Find nearest non-full QUIVER
            return registeredQuivers
                .Where(q => !q.IsFull && !q.IsInert)
                .OrderBy(q => q.Position.DistanceTo(dart.Position))
                .FirstOrDefault();
        }

        /// <summary>
        /// Called when a DART requests reassignment (target died mid-flight).
        /// Attempts immediate reassignment before queuing.
        /// </summary>
        public void RequestReassignment(DART_Flyer dart, Pawn oldTarget = null)
        {
            if (dart == null || dart.Destroyed || !dart.Spawned)
                return;

            // Decrement assignment count from old target
            if (oldTarget != null && assignedDartsPerTarget.ContainsKey(oldTarget))
            {
                assignedDartsPerTarget[oldTarget]--;
                if (assignedDartsPerTarget[oldTarget] <= 0)
                {
                    assignedDartsPerTarget.Remove(oldTarget);
                }
            }

            // Try immediate reassignment first
            List<Pawn> threats = GetAllValidThreats();
            Pawn newTarget = FindBestTargetForReassignment(dart, threats);

            if (newTarget != null)
            {
                // Immediate reassignment - no queue needed
                dart.AssignNewTarget(newTarget);

                // Track the assignment
                if (!assignedDartsPerTarget.ContainsKey(newTarget))
                {
                    assignedDartsPerTarget[newTarget] = 0;
                }
                assignedDartsPerTarget[newTarget]++;
                return;
            }

            // No immediate target available - queue for later
            if (!awaitingReassignment.Contains(dart))
            {
                awaitingReassignment.Add(dart);
            }
        }

        /// <summary>
        /// Gets all valid threats including ARGUS and HAWKEYE-detected targets.
        /// </summary>
        private List<Pawn> GetAllValidThreats()
        {
            List<Pawn> threats = GetAggregatedThreats();

            // Include HAWKEYE priority target
            Pawn hawkeyeTarget = CompHawkeyeSensor.GlobalPriorityTarget;
            if (hawkeyeTarget != null && !hawkeyeTarget.Dead && hawkeyeTarget.Spawned &&
                hawkeyeTarget.Map == Map && !threats.Contains(hawkeyeTarget))
            {
                threats.Add(hawkeyeTarget);
            }

            // Include HAWKEYE-detected threats via SKYLINK
            if (ArsenalNetworkManager.IsLatticeConnectedToSkylink())
            {
                foreach (var pawn in ArsenalNetworkManager.GetAllHawkeyePawns())
                {
                    if (pawn?.Map != Map) continue;
                    var hawkeye = pawn.apparel?.WornApparel?.FirstOrDefault(a => a is Apparel_HawkEye) as Apparel_HawkEye;
                    var comp = hawkeye?.SensorComp;
                    if (comp != null && comp.IsOperational)
                    {
                        foreach (var threat in comp.GetDetectedThreats())
                        {
                            if (!threats.Contains(threat))
                            {
                                threats.Add(threat);
                            }
                        }
                    }
                }
            }

            return threats;
        }

        /// <summary>
        /// Processes the reassignment queue.
        /// </summary>
        private void ProcessReassignmentQueue()
        {
            if (awaitingReassignment.Count == 0)
                return;

            List<Pawn> threats = GetAllValidThreats();

            for (int i = awaitingReassignment.Count - 1; i >= 0; i--)
            {
                DART_Flyer dart = awaitingReassignment[i];

                if (dart == null || dart.Destroyed || !dart.Spawned)
                {
                    awaitingReassignment.RemoveAt(i);
                    continue;
                }

                // Find a new target
                Pawn newTarget = FindBestTargetForReassignment(dart, threats);

                if (newTarget != null)
                {
                    dart.AssignNewTarget(newTarget);

                    // Track the assignment
                    if (!assignedDartsPerTarget.ContainsKey(newTarget))
                    {
                        assignedDartsPerTarget[newTarget] = 0;
                    }
                    assignedDartsPerTarget[newTarget]++;

                    awaitingReassignment.RemoveAt(i);
                }
                else if (threats.Count == 0)
                {
                    // No threats - return home
                    dart.ReturnHome();
                    awaitingReassignment.RemoveAt(i);
                }
                // If there are threats but no suitable one found, DART will timeout and return home
            }
        }

        /// <summary>
        /// Finds the best target for a DART being reassigned.
        /// </summary>
        private Pawn FindBestTargetForReassignment(DART_Flyer dart, List<Pawn> threats)
        {
            if (threats.Count == 0)
                return null;

            // Prefer nearby targets that need more DARTs
            return threats
                .Where(t => CalculateDartsNeeded(t) > GetAssignedDarts(t))
                .OrderBy(t => dart.Position.DistanceTo(t.Position))
                .FirstOrDefault();
        }

        /// <summary>
        /// Called when a DART impacts a target.
        /// </summary>
        public void OnDartImpact(DART_Flyer dart, Pawn target)
        {
            if (target != null && assignedDartsPerTarget.ContainsKey(target))
            {
                assignedDartsPerTarget[target]--;
                if (assignedDartsPerTarget[target] <= 0)
                {
                    assignedDartsPerTarget.Remove(target);
                }
            }
        }

        /// <summary>
        /// Recalls all in-flight DARTs to return home.
        /// </summary>
        private void RecallAllDarts()
        {
            // Find all DART flyers on the map
            var darts = Map.listerThings.ThingsOfDef(ArsenalDefOf.Arsenal_DART_Flyer)
                .Cast<DART_Flyer>()
                .Where(d => d.state == DartState.Engaging || d.state == DartState.Reassigning)
                .ToList();

            foreach (var dart in darts)
            {
                dart.ReturnHome();
            }

            // Clear assignments
            assignedDartsPerTarget.Clear();
        }

        /// <summary>
        /// Cleans up tracking for dead targets.
        /// </summary>
        private void CleanupDeadTargets()
        {
            var deadTargets = assignedDartsPerTarget.Keys
                .Where(p => p == null || p.Dead || p.Destroyed || !p.Spawned)
                .ToList();

            foreach (var target in deadTargets)
            {
                assignedDartsPerTarget.Remove(target);
            }
        }

        /// <summary>
        /// Gets the distance from a position to the nearest QUIVER.
        /// </summary>
        private float GetDistanceToNearestQuiver(IntVec3 pos)
        {
            if (registeredQuivers.Count == 0)
                return float.MaxValue;

            return registeredQuivers.Min(q => q.Position.DistanceTo(pos));
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            // Rename gizmo
            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Give this LATTICE a custom name.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", true),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameLattice(this));
                }
            };

            // Status overview gizmo
            yield return new Command_Action
            {
                defaultLabel = "Status",
                defaultDesc = "View LATTICE network status.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", true),
                action = delegate
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"LATTICE Network Status");
                    sb.AppendLine($"----------------------");
                    sb.AppendLine($"ARGUS sensors: {registeredArgus.Count}");
                    sb.AppendLine($"Registered QUIVERs: {registeredQuivers.Count}");
                    sb.AppendLine($"Total DARTs available: {TotalAvailableDarts}");
                    sb.AppendLine($"Active threats (via ARGUS): {GetAggregatedThreats().Count}");
                    sb.AppendLine($"DARTs in flight: {assignedDartsPerTarget.Values.Sum()}");
                    sb.AppendLine($"DARTs awaiting reassignment: {awaitingReassignment.Count}");
                    sb.AppendLine();

                    // MULE system status
                    var stablesOnMap = ArsenalNetworkManager.GetStablesOnMap(Map);
                    var moriasOnMap = ArsenalNetworkManager.GetMoriasOnMap(Map);
                    sb.AppendLine($"MULE System:");
                    sb.AppendLine($"  STABLEs: {stablesOnMap.Count}");
                    sb.AppendLine($"  MORIAs: {moriasOnMap.Count}");
                    if (stablesOnMap.Count > 0)
                    {
                        int totalMules = stablesOnMap.Sum(s => s.DockedMuleCount);
                        int availableMules = stablesOnMap.Sum(s => s.AvailableMuleCount);
                        sb.AppendLine($"  MULEs: {availableMules}/{totalMules} ready");
                        sb.AppendLine($"  Active MULEs: {ActiveMuleCount}");
                        sb.AppendLine($"  Pending tasks: {PendingMuleTaskCount}");
                    }

                    Messages.Message(sb.ToString(), MessageTypeDefOf.NeutralEvent, false);
                }
            };

            // Debug gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Force Process",
                    action = delegate
                    {
                        ProcessThreatsAndAssign();
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Recall All",
                    action = delegate
                    {
                        RecallAllDarts();
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Rebuild Grid",
                    action = delegate
                    {
                        flightGrid?.RebuildGrid();
                    }
                };
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty())
                text += "\n";

            CompPowerTrader power = GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                text += "<color=red>No power - system offline</color>\n";
            }

            text += $"ARGUS sensors: {registeredArgus.Count}";
            text += $"\nQUIVERs: {registeredQuivers.Count}";
            text += $"\nDARTs available: {TotalAvailableDarts}";

            int threatCount = GetAggregatedThreats().Count;
            if (threatCount > 0)
            {
                text += $"\n<color=orange>Active threats (ARGUS): {threatCount}</color>";
            }
            else if (registeredArgus.Count == 0)
            {
                text += $"\n<color=yellow>No ARGUS sensors - threat detection offline</color>";
            }

            int inFlightCount = assignedDartsPerTarget.Values.Sum();
            if (inFlightCount > 0)
            {
                text += $"\nDARTs in flight: {inFlightCount}";
            }

            // MULE system status
            var stablesOnMap = ArsenalNetworkManager.GetStablesOnMap(Map);
            if (stablesOnMap.Count > 0)
            {
                int totalMules = stablesOnMap.Sum(s => s.DockedMuleCount);
                int availableMules = stablesOnMap.Sum(s => s.AvailableMuleCount);
                text += $"\nSTABLEs: {stablesOnMap.Count} | MULEs: {availableMules}/{totalMules} ready";

                if (PendingMuleTaskCount > 0)
                {
                    text += $"\nPending MULE tasks: {PendingMuleTaskCount}";
                }
                if (ActiveMuleCount > 0)
                {
                    text += $"\nActive MULEs: {ActiveMuleCount}";
                }
            }

            return text;
        }

        public override string Label
        {
            get
            {
                if (!customName.NullOrEmpty())
                    return customName;
                return base.Label;
            }
        }

        public void SetCustomName(string name)
        {
            customName = name;
        }
    }

    /// <summary>
    /// Dialog for renaming a LATTICE.
    /// </summary>
    public class Dialog_RenameLattice : Window
    {
        private Building_Lattice lattice;
        private string curName;

        public Dialog_RenameLattice(Building_Lattice lattice)
        {
            this.lattice = lattice;
            this.curName = lattice.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename LATTICE");
            Text.Font = GameFont.Small;

            curName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), curName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                lattice.SetCustomName(curName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
