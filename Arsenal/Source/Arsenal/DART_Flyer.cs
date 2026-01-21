using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// DART (One-Way-Attack) drone - kamikaze munition for the LATTICE system.
    /// Custom flyer that navigates on-map using A* pathfinding.
    /// </summary>
    public class DART_Flyer : ThingWithComps
    {
        // State machine
        public DartState state = DartState.Delivery;

        // Targeting
        private LocalTargetInfo target;
        private IntVec3 lastKnownTargetPos;

        // Home QUIVER (where this DART was launched from or will return to)
        public Building_Quiver homeQuiver;

        // Reference to LATTICE for coordination
        public Building_Lattice lattice;

        // Flight path
        private List<IntVec3> flightPath;
        private int pathIndex;
        private Vector3 exactPosition;
        private float currentRotation;

        // Flight parameters
        private const float SPEED = 0.18f; // cells per tick (catches sprinting pawns)
        private const float EXPLOSION_RADIUS = 2.5f;
        private const int EXPLOSION_DAMAGE = 65;
        private const int PATH_UPDATE_INTERVAL = 30; // Ticks between path updates when chasing
        private const int REASSIGN_TIMEOUT = 180; // 3 seconds to get reassigned before returning

        // Timing
        private int ticksSincePathUpdate;
        private int ticksInReassigning;
        private int ticksAlive;

        // Flag to calculate path after spawn (when Map becomes available)
        private bool needsPathCalculation;

        // Visual trail
        private const int TRAIL_LENGTH = 8;
        private Queue<Vector3> trailPositions = new Queue<Vector3>();

        // Sound
        private Sustainer flightSustainer;

        public LocalTargetInfo Target => target;
        public Vector3 ExactPosition => exactPosition;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            if (!respawningAfterLoad)
            {
                exactPosition = Position.ToVector3Shifted();
                currentRotation = 0f;

                // Play launch sound
                SoundDefOf.Building_Complete.PlayOneShot(new TargetInfo(Position, map));
            }

            // Calculate path now that we have a valid Map reference
            if (needsPathCalculation)
            {
                needsPathCalculation = false;
                CalculatePathToTarget();
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            StopFlightSound();
            base.DeSpawn(mode);
        }

        private void StartFlightSound()
        {
            if (flightSustainer == null || flightSustainer.Ended)
            {
                SoundInfo info = SoundInfo.InMap(this, MaintenanceType.PerTick);
                flightSustainer = SoundDefOf.Interact_Sow.TrySpawnSustainer(info);
            }
        }

        private void StopFlightSound()
        {
            if (flightSustainer != null && !flightSustainer.Ended)
            {
                flightSustainer.End();
            }
            flightSustainer = null;
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref state, "state", DartState.Delivery);
            Scribe_TargetInfo.Look(ref target, "target");
            Scribe_Values.Look(ref lastKnownTargetPos, "lastKnownTargetPos");
            Scribe_References.Look(ref homeQuiver, "homeQuiver");
            Scribe_References.Look(ref lattice, "lattice");
            Scribe_Values.Look(ref pathIndex, "pathIndex", 0);
            Scribe_Values.Look(ref exactPosition, "exactPosition");
            Scribe_Values.Look(ref currentRotation, "currentRotation");
            Scribe_Values.Look(ref ticksSincePathUpdate, "ticksSincePathUpdate");
            Scribe_Values.Look(ref ticksInReassigning, "ticksInReassigning");
            Scribe_Values.Look(ref ticksAlive, "ticksAlive");
            Scribe_Values.Look(ref needsPathCalculation, "needsPathCalculation");

            Scribe_Collections.Look(ref flightPath, "flightPath", LookMode.Value);

            // Rebuild trail after load
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (trailPositions == null)
                    trailPositions = new Queue<Vector3>();
            }
        }

        /// <summary>
        /// Initializes the DART for delivery from ARSENAL to QUIVER.
        /// </summary>
        public void InitializeForDelivery(Building_Quiver targetQuiver, Building_Lattice latticeRef)
        {
            state = DartState.Delivery;
            homeQuiver = targetQuiver;
            lattice = latticeRef;
            target = new LocalTargetInfo(targetQuiver);

            // Flag for path calculation - will be done in SpawnSetup when Map is available
            if (Map != null)
            {
                CalculatePathToTarget();
            }
            else
            {
                needsPathCalculation = true;
            }
        }

        /// <summary>
        /// Launches the DART at a hostile target.
        /// </summary>
        public void LaunchAtTarget(Pawn hostileTarget, Building_Quiver fromQuiver, Building_Lattice latticeRef)
        {
            state = DartState.Engaging;
            homeQuiver = fromQuiver;
            lattice = latticeRef;
            target = new LocalTargetInfo(hostileTarget);
            lastKnownTargetPos = hostileTarget.Position;

            // Flag for path calculation - will be done in SpawnSetup when Map is available
            // If already spawned (Map != null), calculate immediately
            if (Map != null)
            {
                CalculatePathToTarget();
            }
            else
            {
                needsPathCalculation = true;
            }
        }

        /// <summary>
        /// Assigns a new target (called by LATTICE during reassignment).
        /// </summary>
        public void AssignNewTarget(Pawn newTarget)
        {
            if (newTarget == null || newTarget.Dead || newTarget.Destroyed)
            {
                ReturnHome();
                return;
            }

            state = DartState.Engaging;
            target = new LocalTargetInfo(newTarget);
            lastKnownTargetPos = newTarget.Position;
            ticksInReassigning = 0;
            CalculatePathToTarget();
        }

        /// <summary>
        /// Orders the DART to return to its home QUIVER.
        /// </summary>
        public void ReturnHome()
        {
            if (homeQuiver == null || homeQuiver.Destroyed || homeQuiver.IsInert)
            {
                // Find any available QUIVER
                homeQuiver = lattice?.GetQuiverForReturn(this);
            }

            if (homeQuiver != null && !homeQuiver.Destroyed)
            {
                state = DartState.Returning;
                target = new LocalTargetInfo(homeQuiver);
                CalculatePathToTarget();
            }
            else
            {
                // No QUIVER available - crash
                Crash();
            }
        }

        /// <summary>
        /// Requests reassignment from LATTICE (target died mid-flight).
        /// </summary>
        public void RequestReassignment()
        {
            // Capture old target before changing state
            Pawn oldTarget = target.Pawn;

            state = DartState.Reassigning;
            ticksInReassigning = 0;

            if (lattice != null && !lattice.Destroyed)
            {
                lattice.RequestReassignment(this, oldTarget);
            }
            else
            {
                // No LATTICE - return home or crash
                ReturnHome();
            }
        }

        protected override void Tick()
        {
            base.Tick();
            ticksAlive++;

            // Skip processing if idle (stored in QUIVER)
            if (state == DartState.Idle)
            {
                StopFlightSound();
                return;
            }

            // Maintain flight sound
            StartFlightSound();
            flightSustainer?.Maintain();

            // Update trail
            UpdateTrail();

            // State machine
            switch (state)
            {
                case DartState.Delivery:
                    TickDelivery();
                    break;
                case DartState.Engaging:
                    TickEngaging();
                    break;
                case DartState.Returning:
                    TickReturning();
                    break;
                case DartState.Reassigning:
                    TickReassigning();
                    break;
            }

            // Visual effects
            if (Map != null)
            {
                SpawnFlightEffects();
            }
        }

        private void TickDelivery()
        {
            if (homeQuiver == null || homeQuiver.Destroyed)
            {
                // Target QUIVER destroyed - find another
                Building_Quiver newQuiver = lattice?.GetQuiverForDelivery();
                if (newQuiver != null)
                {
                    homeQuiver = newQuiver;
                    target = new LocalTargetInfo(newQuiver);
                    CalculatePathToTarget();
                }
                else
                {
                    Crash();
                    return;
                }
            }

            FlyAlongPath();

            // Check if arrived at QUIVER
            if (Position.DistanceTo(homeQuiver.Position) < 2f)
            {
                CompleteDelivery();
            }
        }

        private void TickEngaging()
        {
            // Check if target is still valid
            Pawn targetPawn = target.Pawn;
            if (targetPawn == null || targetPawn.Dead || targetPawn.Destroyed ||
                !targetPawn.Spawned || targetPawn.Map != Map)
            {
                RequestReassignment();
                return;
            }

            // Update path periodically if target moved
            ticksSincePathUpdate++;
            if (ticksSincePathUpdate >= PATH_UPDATE_INTERVAL)
            {
                if (targetPawn.Position != lastKnownTargetPos)
                {
                    lastKnownTargetPos = targetPawn.Position;
                    CalculatePathToTarget();
                }
                ticksSincePathUpdate = 0;
            }

            FlyAlongPath();

            // Check for impact
            float distToTarget = exactPosition.ToIntVec3().DistanceTo(targetPawn.Position);
            if (distToTarget < 1.5f)
            {
                Impact(targetPawn);
            }
        }

        private void TickReturning()
        {
            if (homeQuiver == null || homeQuiver.Destroyed || homeQuiver.IsInert)
            {
                // Home QUIVER gone - find another
                Building_Quiver newQuiver = lattice?.GetQuiverForReturn(this);
                if (newQuiver != null)
                {
                    homeQuiver = newQuiver;
                    target = new LocalTargetInfo(newQuiver);
                    CalculatePathToTarget();
                }
                else
                {
                    Crash();
                    return;
                }
            }

            FlyAlongPath();

            // Check if arrived at QUIVER
            if (Position.DistanceTo(homeQuiver.Position) < 2f)
            {
                CompleteReturn();
            }
        }

        private void TickReassigning()
        {
            ticksInReassigning++;

            // Circle/loiter while waiting for reassignment
            PerformLoiterPattern();

            if (ticksInReassigning >= REASSIGN_TIMEOUT)
            {
                // Timeout - return home
                ReturnHome();
            }
        }

        /// <summary>
        /// Performs a circling/loiter pattern while awaiting reassignment.
        /// </summary>
        private void PerformLoiterPattern()
        {
            // Circle around current position
            float loiterRadius = 2f;
            float angularSpeed = 0.05f; // radians per tick

            // Calculate circle position based on time
            float angle = ticksInReassigning * angularSpeed;
            Vector3 loiterCenter = lastKnownTargetPos.IsValid ? lastKnownTargetPos.ToVector3Shifted() : exactPosition;

            Vector3 targetPos = loiterCenter + new Vector3(
                Mathf.Cos(angle) * loiterRadius,
                0f,
                Mathf.Sin(angle) * loiterRadius
            );

            // Move toward the loiter position
            Vector3 direction = (targetPos - exactPosition).normalized;
            float distToTarget = Vector3.Distance(exactPosition, targetPos);

            if (distToTarget > SPEED)
            {
                exactPosition += direction * SPEED * 0.5f; // Half speed while loitering
            }

            // Update rotation to face movement direction
            if (direction.sqrMagnitude > 0.001f)
            {
                currentRotation = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }

            // Update grid position
            IntVec3 newCell = exactPosition.ToIntVec3();
            if (newCell != Position && newCell.InBounds(Map))
            {
                Position = newCell;
            }
        }

        private void FlyAlongPath()
        {
            if (flightPath == null || flightPath.Count == 0 || pathIndex >= flightPath.Count)
            {
                // No valid path - try to recalculate
                CalculatePathToTarget();
                if (flightPath == null || flightPath.Count == 0)
                {
                    // Still no path - handle based on state
                    HandleNoPath();
                    return;
                }
            }

            // Get current target waypoint
            IntVec3 targetWaypoint = flightPath[pathIndex];
            Vector3 targetPos = targetWaypoint.ToVector3Shifted();

            // Calculate direction and move
            Vector3 direction = (targetPos - exactPosition).normalized;
            float distanceToWaypoint = Vector3.Distance(exactPosition, targetPos);

            if (distanceToWaypoint <= SPEED)
            {
                // Reached waypoint
                exactPosition = targetPos;
                pathIndex++;

                // Check if reached end of path
                if (pathIndex >= flightPath.Count)
                {
                    // Path complete - should be handled by state-specific tick
                    return;
                }
            }
            else
            {
                // Move toward waypoint
                exactPosition += direction * SPEED;
            }

            // Update rotation to face movement direction
            if (direction.sqrMagnitude > 0.001f)
            {
                currentRotation = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }

            // Update position for collision detection etc
            IntVec3 newCell = exactPosition.ToIntVec3();
            if (newCell != Position && newCell.InBounds(Map))
            {
                Position = newCell;
            }
        }

        private void HandleNoPath()
        {
            switch (state)
            {
                case DartState.Delivery:
                case DartState.Returning:
                    // Can't reach QUIVER - crash
                    Crash();
                    break;
                case DartState.Engaging:
                    // Can't reach target - request reassignment
                    RequestReassignment();
                    break;
                case DartState.Reassigning:
                    // Already reassigning - continue waiting
                    break;
            }
        }

        private void CalculatePathToTarget()
        {
            if (Map == null)
                return;

            FlightPathGrid grid = GetFlightPathGrid();
            if (grid == null)
                return;

            IntVec3 destination;
            if (target.HasThing && target.Thing != null && target.Thing.Spawned)
            {
                destination = target.Thing.Position;
            }
            else if (target.Cell.IsValid)
            {
                destination = target.Cell;
            }
            else
            {
                flightPath = null;
                return;
            }

            flightPath = grid.FindPath(Position, destination);
            pathIndex = 0;
            ticksSincePathUpdate = 0;
        }

        private FlightPathGrid GetFlightPathGrid()
        {
            // Get grid from LATTICE or create temporary one
            if (lattice != null && !lattice.Destroyed)
            {
                return lattice.FlightGrid;
            }

            // Fallback - create temporary grid (expensive)
            return new FlightPathGrid(Map);
        }

        private void UpdateTrail()
        {
            trailPositions.Enqueue(exactPosition);
            while (trailPositions.Count > TRAIL_LENGTH)
            {
                trailPositions.Dequeue();
            }
        }

        private void SpawnFlightEffects()
        {
            // Engine glow/exhaust - position behind the drone based on rotation
            Vector3 exhaustPos = exactPosition - Quaternion.Euler(0, currentRotation, 0) * Vector3.forward * 0.3f;

            // Smoke trail every few ticks
            if (ticksAlive % 2 == 0)
            {
                FleckMaker.ThrowSmoke(exhaustPos, Map, 0.4f);
            }

            // Micro sparks for engine effect
            if (ticksAlive % 4 == 0)
            {
                FleckMaker.ThrowMicroSparks(exhaustPos, Map);
            }

            // More intense effects when engaging target
            if (state == DartState.Engaging)
            {
                if (ticksAlive % 3 == 0)
                {
                    FleckMaker.ThrowLightningGlow(exactPosition, Map, 0.3f);
                }

                // Fire trail when attacking
                if (ticksAlive % 5 == 0)
                {
                    FleckMaker.ThrowFireGlow(exhaustPos, Map, 0.3f);
                }
            }

            // Dust kick-up when flying low
            if (ticksAlive % 8 == 0)
            {
                FleckMaker.ThrowDustPuffThick(exactPosition, Map, 0.5f, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            }
        }

        private void Impact(Pawn targetPawn)
        {
            // Stop flight sound
            StopFlightSound();

            // Notify LATTICE before destruction
            lattice?.OnDartImpact(this, targetPawn);

            // Play impact warning sound just before explosion
            SoundDefOf.Designate_DragStandard.PlayOneShotOnCamera(Map);

            // Create explosion
            GenExplosion.DoExplosion(
                center: Position,
                map: Map,
                radius: EXPLOSION_RADIUS,
                damType: DamageDefOf.Bomb,
                instigator: this,
                damAmount: EXPLOSION_DAMAGE,
                armorPenetration: 0.5f,
                explosionSound: null,
                weapon: null,
                projectile: null,
                intendedTarget: targetPawn,
                postExplosionSpawnThingDef: null,
                postExplosionSpawnChance: 0f,
                postExplosionSpawnThingCount: 0,
                postExplosionGasType: null,
                applyDamageToExplosionCellsNeighbors: false,
                preExplosionSpawnThingDef: null,
                preExplosionSpawnChance: 0f,
                preExplosionSpawnThingCount: 0,
                chanceToStartFire: 0.3f,
                damageFalloff: true
            );

            // Visual effects - explosion flash and debris
            FleckMaker.ThrowExplosionCell(Position, Map, FleckDefOf.ExplosionFlash, Color.white);
            FleckMaker.ThrowFireGlow(Position.ToVector3Shifted(), Map, 1.5f);
            FleckMaker.ThrowMicroSparks(Position.ToVector3Shifted(), Map);
            FleckMaker.ThrowSmoke(Position.ToVector3Shifted(), Map, 1.5f);

            // Additional debris effects
            for (int i = 0; i < 5; i++)
            {
                IntVec3 debrisPos = Position + GenRadial.RadialPattern[Rand.Range(0, 9)];
                if (debrisPos.InBounds(Map))
                {
                    FleckMaker.ThrowDustPuff(debrisPos, Map, 1f);
                }
            }

            // Destroy self
            Destroy(DestroyMode.Vanish);
        }

        private void CompleteDelivery()
        {
            if (homeQuiver != null && !homeQuiver.Destroyed && !homeQuiver.IsFull)
            {
                homeQuiver.ReceiveDart(this);
            }
            else
            {
                // QUIVER full or gone - crash
                Crash();
            }
        }

        private void CompleteReturn()
        {
            if (homeQuiver != null && !homeQuiver.Destroyed && !homeQuiver.IsFull)
            {
                homeQuiver.ReceiveDart(this);
            }
            else
            {
                // Try another QUIVER
                Building_Quiver altQuiver = lattice?.GetQuiverForReturn(this);
                if (altQuiver != null && !altQuiver.IsFull)
                {
                    altQuiver.ReceiveDart(this);
                }
                else
                {
                    // No room anywhere - crash
                    Crash();
                }
            }
        }

        private void Crash()
        {
            // Stop flight sound
            StopFlightSound();

            // Small explosion when crashing
            if (Map != null)
            {
                // Crash sound
                SoundDefOf.Building_Deconstructed.PlayOneShot(new TargetInfo(Position, Map));

                GenExplosion.DoExplosion(
                    center: Position,
                    map: Map,
                    radius: 0.5f,
                    damType: DamageDefOf.Bomb,
                    instigator: null,
                    damAmount: 5,
                    explosionSound: null
                );

                // Visual effects
                FleckMaker.ThrowSmoke(exactPosition, Map, 1.5f);
                FleckMaker.ThrowMicroSparks(exactPosition, Map);
                FleckMaker.ThrowDustPuff(Position, Map, 1f);

                Messages.Message("DART drone crashed - no valid destination.", MessageTypeDefOf.NegativeEvent, false);
            }

            Destroy(DestroyMode.Vanish);
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty())
                text += "\n";

            text += "State: " + state.ToString();

            if (state == DartState.Engaging && target.Pawn != null)
            {
                text += "\nTarget: " + target.Pawn.LabelShort;
            }
            else if (state == DartState.Returning || state == DartState.Delivery)
            {
                text += "\nDestination: " + (homeQuiver?.LabelShort ?? "Unknown");
            }

            return text;
        }
    }
}
