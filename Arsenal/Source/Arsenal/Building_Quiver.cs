using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// QUIVER - Drone hub that stores DARTs and launches them on LATTICE command.
    /// </summary>
    public class Building_Quiver : Building
    {
        // Storage
        private int dartCount = 0;
        public const int MAX_CAPACITY = 10;

        // Priority for DART delivery (1-10, lower = filled first)
        private int priority = 5;

        // Reference to controlling LATTICE
        private Building_Lattice linkedLattice;

        // Inert state (no LATTICE present)
        private bool isInert = false;

        // Custom name
        private string customName;

        // Visual - stored DART positions for rendering
        private List<Vector3> storedDartPositions;

        // Properties
        public int DartCount => dartCount;
        public int EmptySlots => MAX_CAPACITY - dartCount;
        public bool IsFull => dartCount >= MAX_CAPACITY;
        public bool IsInert => isInert;
        public int Priority
        {
            get => priority;
            set => priority = Mathf.Clamp(value, 1, 10);
        }
        public Building_Lattice LinkedLattice => linkedLattice;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // Initialize visual positions
            CalculateStoredDartPositions();

            if (!respawningAfterLoad)
            {
                // Find and register with LATTICE
                RegisterWithLattice();
            }
            else
            {
                // Re-register after load
                RegisterWithLattice();
            }

            // Register with network manager
            ArsenalNetworkManager.RegisterQuiver(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Unregister from LATTICE
            linkedLattice?.UnregisterQuiver(this);
            linkedLattice = null;

            // Unregister from network
            ArsenalNetworkManager.DeregisterQuiver(this);

            base.DeSpawn(mode);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // Explosion when destroyed - loses all stored DARTs
            if (mode != DestroyMode.Vanish && dartCount > 0 && Map != null)
            {
                // Chain explosion based on stored DARTs
                float explosionRadius = 2f + (dartCount * 0.3f);
                int explosionDamage = 15 + (dartCount * 5);

                GenExplosion.DoExplosion(
                    center: Position,
                    map: Map,
                    radius: explosionRadius,
                    damType: DamageDefOf.Bomb,
                    instigator: null,
                    damAmount: explosionDamage,
                    armorPenetration: 0.5f,
                    explosionSound: SoundDefOf.Explosion_Bomb,
                    chanceToStartFire: 0.5f,
                    damageFalloff: true
                );

                Messages.Message($"QUIVER destroyed! {dartCount} DARTs lost in explosion.",
                    new TargetInfo(Position, Map), MessageTypeDefOf.NegativeEvent, false);
            }

            base.Destroy(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref dartCount, "dartCount", 0);
            Scribe_Values.Look(ref priority, "priority", 5);
            Scribe_Values.Look(ref isInert, "isInert", false);
            Scribe_Values.Look(ref customName, "customName");
            Scribe_References.Look(ref linkedLattice, "linkedLattice");
        }

        /// <summary>
        /// Finds and registers with the map's LATTICE.
        /// </summary>
        private void RegisterWithLattice()
        {
            linkedLattice = ArsenalNetworkManager.GetLatticeOnMap(Map);

            if (linkedLattice != null)
            {
                linkedLattice.RegisterQuiver(this);
                isInert = false;
            }
            else
            {
                isInert = true;
            }
        }

        /// <summary>
        /// Called by LATTICE when it spawns to register QUIVERs.
        /// </summary>
        public void OnLatticeAvailable(Building_Lattice lattice)
        {
            if (lattice != null && lattice.Map == Map)
            {
                linkedLattice = lattice;
                isInert = false;
            }
        }

        /// <summary>
        /// Called when LATTICE is destroyed.
        /// </summary>
        public void OnLatticeDestroyed()
        {
            linkedLattice = null;
            isInert = true;
        }

        /// <summary>
        /// Receives a DART (from delivery or return).
        /// </summary>
        public void ReceiveDart(DART_Flyer dart)
        {
            if (IsFull)
            {
                Log.Warning($"QUIVER {LabelShort} attempted to receive DART but is full!");
                return;
            }

            dartCount++;

            // Destroy the flyer - it's now stored
            if (dart != null && dart.Spawned)
            {
                dart.Destroy(DestroyMode.Vanish);
            }

            // Sound and visual feedback
            if (Map != null)
            {
                // Docking/storage sound
                SoundDefOf.Building_Complete.PlayOneShot(new TargetInfo(Position, Map));

                // Visual effects - landing dust and smoke
                FleckMaker.ThrowSmoke(Position.ToVector3Shifted(), Map, 0.7f);
                FleckMaker.ThrowDustPuff(Position, Map, 0.5f);
                FleckMaker.ThrowMicroSparks(Position.ToVector3Shifted(), Map);
            }
        }

        /// <summary>
        /// Launches a DART at the specified target.
        /// </summary>
        public DART_Flyer LaunchDart(Pawn target)
        {
            if (dartCount <= 0)
            {
                Log.Warning($"QUIVER {LabelShort} attempted to launch DART but is empty!");
                return null;
            }

            if (isInert)
            {
                Log.Warning($"QUIVER {LabelShort} is inert - cannot launch DARTs without LATTICE!");
                return null;
            }

            dartCount--;

            // Spawn the DART flyer
            DART_Flyer dart = (DART_Flyer)ThingMaker.MakeThing(ArsenalDefOf.Arsenal_DART_Flyer);
            dart.LaunchAtTarget(target, this, linkedLattice);

            // Spawn at launch position (slightly above QUIVER)
            IntVec3 launchPos = Position;
            GenSpawn.Spawn(dart, launchPos, Map);

            // Launch effects - sounds and visuals
            if (Map != null)
            {
                // Target acquisition beep
                SoundDefOf.TurretAcquireTarget.PlayOneShot(new TargetInfo(Position, Map));

                // Launch smoke and fire effects
                FleckMaker.ThrowSmoke(Position.ToVector3Shifted(), Map, 1.2f);
                FleckMaker.ThrowMicroSparks(Position.ToVector3Shifted(), Map);
                FleckMaker.ThrowFireGlow(Position, Map, 0.5f);
                FleckMaker.ThrowLightningGlow(Position.ToVector3Shifted(), Map, 0.8f);

                // Dust kick-up from launch
                for (int i = 0; i < 3; i++)
                {
                    IntVec3 dustPos = Position + GenRadial.RadialPattern[Rand.Range(1, 9)];
                    if (dustPos.InBounds(Map))
                    {
                        FleckMaker.ThrowDustPuff(dustPos, Map, 0.8f);
                    }
                }
            }

            return dart;
        }

        /// <summary>
        /// Calculates visual positions for stored DARTs.
        /// </summary>
        private void CalculateStoredDartPositions()
        {
            storedDartPositions = new List<Vector3>();
            Vector3 center = Position.ToVector3Shifted();

            // Arrange DARTs in a grid pattern within the building footprint
            for (int i = 0; i < MAX_CAPACITY; i++)
            {
                int row = i / 5;
                int col = i % 5;
                float offsetX = (col - 2) * 0.15f;
                float offsetZ = (row - 0.5f) * 0.15f;
                storedDartPositions.Add(center + new Vector3(offsetX, 0.1f, offsetZ));
            }
        }

        public override void Draw()
        {
            base.Draw();

            // Draw stored DARTs
            if (storedDartPositions != null && dartCount > 0)
            {
                Material dartMat = ArsenalDefOf.Arsenal_DART_Flyer?.graphic?.MatSingle;
                if (dartMat != null)
                {
                    for (int i = 0; i < dartCount && i < storedDartPositions.Count; i++)
                    {
                        Vector3 pos = storedDartPositions[i];
                        pos.y = AltitudeLayer.Item.AltitudeFor();

                        Matrix4x4 matrix = Matrix4x4.TRS(
                            pos,
                            Quaternion.identity,
                            new Vector3(0.3f, 1f, 0.3f) // Smaller scale for stored DARTs
                        );

                        Graphics.DrawMesh(MeshPool.plane10, matrix, dartMat, 0);
                    }
                }
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            // Priority setting gizmo
            yield return new Command_Action
            {
                defaultLabel = $"Priority: {priority}",
                defaultDesc = "Set delivery priority. Lower numbers are filled first (1 = highest priority, 10 = lowest).",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", true),
                action = delegate
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    for (int i = 1; i <= 10; i++)
                    {
                        int p = i;
                        string label = $"Priority {p}";
                        if (p == 1) label += " (Highest)";
                        if (p == 10) label += " (Lowest)";

                        options.Add(new FloatMenuOption(label, delegate
                        {
                            priority = p;
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

            // Rename gizmo
            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Give this QUIVER a custom name.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", true),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameQuiver(this));
                }
            };

            // Debug gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Add DART",
                    action = delegate
                    {
                        if (!IsFull)
                            dartCount++;
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Remove DART",
                    action = delegate
                    {
                        if (dartCount > 0)
                            dartCount--;
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Toggle Inert",
                    action = delegate
                    {
                        isInert = !isInert;
                    }
                };
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty())
                text += "\n";

            text += $"DARTs: {dartCount}/{MAX_CAPACITY}";
            text += $"\nPriority: {priority}";

            if (isInert)
            {
                text += "\n<color=red>INERT - No LATTICE detected</color>";
            }
            else if (linkedLattice != null)
            {
                text += $"\nLinked to: {linkedLattice.LabelShort}";
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
    /// Dialog for renaming a QUIVER.
    /// </summary>
    public class Dialog_RenameQuiver : Dialog_Rename
    {
        private Building_Quiver quiver;

        public Dialog_RenameQuiver(Building_Quiver quiver)
        {
            this.quiver = quiver;
            this.curName = quiver.Label;
        }

        protected override void SetName(string name)
        {
            quiver.SetCustomName(name);
        }
    }
}
