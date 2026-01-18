using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// LATTICE - Command & Control node for the drone swarm defense system.
    /// Only ONE LATTICE allowed per map.
    /// </summary>
    public class Building_Lattice : Building
    {
        // Registered QUIVERs
        private List<Building_Quiver> registeredQuivers = new List<Building_Quiver>();

        // Tracking in-flight DARTs per target
        private Dictionary<Pawn, int> assignedDartsPerTarget = new Dictionary<Pawn, int>();

        // DARTs awaiting reassignment
        private List<DART_Flyer> awaitingReassignment = new List<DART_Flyer>();

        // Flight pathfinding grid
        private FlightPathGrid flightGrid;

        // Scanning
        private const int SCAN_INTERVAL = 60; // Ticks between scans (~1 second)
        private int ticksSinceLastScan;

        // Threat evaluation parameters
        private const float DART_LETHALITY = 20f; // How much threat a single DART can handle
        private const float BASE_THREAT_TRIBAL = 35f;
        private const float BASE_THREAT_PIRATE = 50f;
        private const float BASE_THREAT_MECHANOID = 150f;

        // Custom name
        private string customName;

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

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

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
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // All QUIVERs go inert
            foreach (var quiver in registeredQuivers.ToList())
            {
                quiver.OnLatticeDestroyed();
            }
            registeredQuivers.Clear();

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
            Scribe_Collections.Look(ref awaitingReassignment, "awaitingReassignment", LookMode.Reference);
            Scribe_Values.Look(ref ticksSinceLastScan, "ticksSinceLastScan", 0);
            Scribe_Values.Look(ref customName, "customName");

            // Dictionary requires special handling
            Scribe_Collections.Look(ref assignedDartsPerTarget, "assignedDartsPerTarget",
                LookMode.Reference, LookMode.Value, ref tempPawnKeys, ref tempIntValues);

            // Clean up null references after load
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                registeredQuivers.RemoveAll(q => q == null);
                awaitingReassignment.RemoveAll(d => d == null);
                CleanupDeadTargets();
            }
        }

        // Temp lists for dictionary serialization
        private List<Pawn> tempPawnKeys;
        private List<int> tempIntValues;

        public override void Tick()
        {
            base.Tick();

            // Only operate when powered
            CompPowerTrader power = GetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
                return;

            ticksSinceLastScan++;
            if (ticksSinceLastScan >= SCAN_INTERVAL)
            {
                ticksSinceLastScan = 0;
                ScanAndAssign();
            }
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

        /// <summary>
        /// Main threat scanning and DART assignment logic.
        /// </summary>
        private void ScanAndAssign()
        {
            // Clean up dead targets
            CleanupDeadTargets();

            // Process any DARTs awaiting reassignment
            ProcessReassignmentQueue();

            // Find all hostile pawns on the map
            List<Pawn> threats = GetHostileThreats();

            if (threats.Count == 0)
            {
                // No threats - recall any in-flight DARTs
                RecallAllDarts();
                return;
            }

            // Sort threats by distance to nearest QUIVER (prioritize closer threats)
            threats = threats.OrderBy(t => GetDistanceToNearestQuiver(t.Position)).ToList();

            // Evaluate and assign DARTs to threats
            foreach (Pawn threat in threats)
            {
                int dartsNeeded = CalculateDartsNeeded(threat);
                int dartsAlreadyAssigned = GetAssignedDarts(threat);
                int additionalDartsNeeded = dartsNeeded - dartsAlreadyAssigned;

                if (additionalDartsNeeded > 0)
                {
                    AssignDarts(threat, additionalDartsNeeded);
                }
            }
        }

        /// <summary>
        /// Gets all hostile pawns on the map that should be targeted.
        /// </summary>
        private List<Pawn> GetHostileThreats()
        {
            List<Pawn> threats = new List<Pawn>();

            foreach (Pawn pawn in Map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.HostileTo(Faction.OfPlayer) && !pawn.Dead && !pawn.Downed)
                {
                    // Skip animals unless manhunting
                    if (pawn.RaceProps.Animal && !pawn.InAggroMentalState)
                        continue;

                    threats.Add(pawn);
                }
            }

            return threats;
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

            // Modify by equipment
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

            return baseThreat;
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
        /// </summary>
        public void AssignDarts(Pawn target, int count)
        {
            if (count <= 0 || target == null)
                return;

            // Sort QUIVERs by distance to target
            var sortedQuivers = registeredQuivers
                .Where(q => !q.IsInert && q.DartCount > 0)
                .OrderBy(q => q.Position.DistanceTo(target.Position))
                .ToList();

            int remaining = count;

            foreach (var quiver in sortedQuivers)
            {
                int toTake = Mathf.Min(remaining, quiver.DartCount);

                for (int i = 0; i < toTake; i++)
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
                    }
                }

                remaining -= toTake;
                if (remaining <= 0)
                    break;
            }

            if (remaining > 0)
            {
                // Not enough DARTs available
                // The system will continue to assign as DARTs become available
            }
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
        /// </summary>
        public void RequestReassignment(DART_Flyer dart)
        {
            if (dart != null && !awaitingReassignment.Contains(dart))
            {
                awaitingReassignment.Add(dart);
            }
        }

        /// <summary>
        /// Processes the reassignment queue.
        /// </summary>
        private void ProcessReassignmentQueue()
        {
            if (awaitingReassignment.Count == 0)
                return;

            List<Pawn> threats = GetHostileThreats();

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
                    sb.AppendLine($"Registered QUIVERs: {registeredQuivers.Count}");
                    sb.AppendLine($"Total DARTs available: {TotalAvailableDarts}");
                    sb.AppendLine($"Active threats: {GetHostileThreats().Count}");
                    sb.AppendLine($"DARTs in flight: {assignedDartsPerTarget.Values.Sum()}");
                    sb.AppendLine($"DARTs awaiting reassignment: {awaitingReassignment.Count}");

                    Messages.Message(sb.ToString(), MessageTypeDefOf.NeutralEvent, false);
                }
            };

            // Debug gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Force Scan",
                    action = delegate
                    {
                        ScanAndAssign();
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

            text += $"QUIVERs: {registeredQuivers.Count}";
            text += $"\nDARTs available: {TotalAvailableDarts}";

            int threatCount = GetHostileThreats().Count;
            if (threatCount > 0)
            {
                text += $"\n<color=orange>Active threats: {threatCount}</color>";
            }

            int inFlightCount = assignedDartsPerTarget.Values.Sum();
            if (inFlightCount > 0)
            {
                text += $"\nDARTs in flight: {inFlightCount}";
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
    public class Dialog_RenameLattice : Dialog_Rename
    {
        private Building_Lattice lattice;

        public Dialog_RenameLattice(Building_Lattice lattice)
        {
            this.lattice = lattice;
            this.curName = lattice.Label;
        }

        protected override void SetName(string name)
        {
            lattice.SetCustomName(name);
        }
    }
}
