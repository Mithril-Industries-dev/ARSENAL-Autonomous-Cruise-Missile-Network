using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    public class Building_Hop : Building
    {
        private CompRefuelable refuelableComp;
        private CompPowerTrader powerComp;

        private string customName;
        private static int hopCounter = 1;

        /// <summary>
        /// Sets the hop counter to a specific value.
        /// Called after game load to prevent duplicate names.
        /// </summary>
        public static void SetCounter(int value)
        {
            hopCounter = System.Math.Max(1, value);
        }

        // Refueling system
        private Thing missileBeingRefueled;
        private int destinationTile = -1;
        private Building_Hub destinationHub;
        private int refuelTicksRemaining = 0;
        private const int REFUEL_TICKS = 3600; // 1 minute real time

        // Range extension for DAGGER network
        public int RangeExtension = 12;

        public bool IsRefueling => missileBeingRefueled != null;

        // Properties for UI
        public bool IsPoweredOn()
        {
            return powerComp == null || powerComp.PowerOn;
        }

        public bool HasFuel => refuelableComp != null && refuelableComp.Fuel >= 50f;

        /// <summary>
        /// Checks if HOP has network connectivity to LATTICE.
        /// Required for remote coordination.
        /// </summary>
        public bool HasNetworkConnection()
        {
            if (Map == null) return false;
            return ArsenalNetworkManager.IsTileConnected(Map.Tile);
        }

        /// <summary>
        /// Gets network status message for UI.
        /// </summary>
        public string GetNetworkStatusMessage()
        {
            if (Map == null) return "OFFLINE — No map";
            return ArsenalNetworkManager.GetNetworkStatus(Map.Tile);
        }

        public float FuelPercent
        {
            get
            {
                if (refuelableComp == null) return 0f;
                float maxFuel = refuelableComp.Props.fuelCapacity;
                return maxFuel > 0 ? refuelableComp.Fuel / maxFuel : 0f;
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            refuelableComp = GetComp<CompRefuelable>();
            powerComp = GetComp<CompPowerTrader>();

            // ALWAYS register with network manager
            ArsenalNetworkManager.RegisterHop(this);

            // Only assign name if new building
            if (!respawningAfterLoad)
            {
                customName = "HOP-" + hopCounter.ToString("D2");
                hopCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // If we have a missile being refueled, it's already spawned on the pad
            // Just clear our reference - the missile will remain on the map
            if (missileBeingRefueled != null)
            {
                // Unforbid it so colonists can interact with it
                if (missileBeingRefueled.Spawned)
                {
                    missileBeingRefueled.SetForbidden(false, false);
                }
                missileBeingRefueled = null;
            }
            ArsenalNetworkManager.DeregisterHop(this);
            base.DeSpawn(mode);
        }

        public override string Label => customName ?? base.Label;

        public void SetCustomName(string name) => customName = name;

        public float GetAvailableFuel() => refuelableComp?.Fuel ?? 0f;
        
        public bool CanAcceptMissile()
        {
            return !IsRefueling && refuelableComp != null && refuelableComp.Fuel >= 50f;
        }

        public void StartRefueling(Thing missile, int destTile, Building_Hub destHub)
        {
            if (missileBeingRefueled != null)
            {
                Log.Warning("[ARSENAL] HOP already refueling a missile!");
                return;
            }
            
            missileBeingRefueled = missile;
            destinationTile = destTile;
            destinationHub = destHub;
            refuelTicksRemaining = REFUEL_TICKS;
            
            // Spawn the actual missile on the pad for display
            if (missile != null && !missile.Spawned && Map != null)
            {
                GenSpawn.Spawn(missile, Position, Map);
                missile.SetForbidden(true, false);
            }
            
            Messages.Message(Label + ": Missile refueling started...", this, MessageTypeDefOf.NeutralEvent);
        }

        protected override void Tick()
        {
            base.Tick();
            
            if (missileBeingRefueled == null)
                return;
            
            refuelTicksRemaining--;
            
            // Visual effects during refueling (every 30 ticks)
            if (refuelTicksRemaining % 30 == 0 && Map != null)
            {
                FleckMaker.ThrowMicroSparks(Position.ToVector3Shifted(), Map);
            }
            
            // Occasional smoke during refueling (every 60 ticks)
            if (refuelTicksRemaining % 60 == 0 && Map != null)
            {
                FleckMaker.ThrowSmoke(Position.ToVector3Shifted() + new Vector3(Rand.Range(-0.5f, 0.5f), 0, Rand.Range(-0.5f, 0.5f)), Map, 0.5f);
            }
            
            if (refuelTicksRemaining <= 0)
            {
                CompleteRefueling();
            }
        }

        private void CompleteRefueling()
        {
            if (missileBeingRefueled == null)
                return;
            
            // Despawn the missile from the pad before launching
            if (missileBeingRefueled.Spawned)
            {
                missileBeingRefueled.DeSpawn(DestroyMode.Vanish);
            }
            
            // Actually refuel the missile
            DoRefuel(missileBeingRefueled);
            
            Messages.Message(Label + ": Missile refueled, launching to " + (destinationHub?.Label ?? "destination"), this, MessageTypeDefOf.PositiveEvent);
            
            // Create new traveling missile
            WorldObject_TravelingMissile newTraveling = 
                (WorldObject_TravelingMissile)WorldObjectMaker.MakeWorldObject(ArsenalDefOf.Arsenal_TravelingMissile);
            
            newTraveling.Tile = Map.Tile;
            newTraveling.destinationTile = destinationTile;
            newTraveling.missile = missileBeingRefueled;
            newTraveling.destinationHub = destinationHub;
            newTraveling.CalculateRoute();

            // Spawn takeoff skyfaller
            MissileLaunchingSkyfaller skyfaller = (MissileLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_MissileLaunching);
            skyfaller.travelingMissile = newTraveling;
            
            GenSpawn.Spawn(skyfaller, Position, Map);
            
            // Clear state
            missileBeingRefueled = null;
            destinationTile = -1;
            destinationHub = null;
            refuelTicksRemaining = 0;
        }

        private void DoRefuel(Thing missile)
        {
            if (refuelableComp == null || !refuelableComp.HasFuel) return;
            
            CompMissileFuel fc = missile.TryGetComp<CompMissileFuel>();
            if (fc == null) return;
            
            float needed = fc.FuelCapacity - fc.Fuel;
            float transfer = Mathf.Min(needed, refuelableComp.Fuel);
            
            if (transfer > 0)
            {
                refuelableComp.ConsumeFuel(transfer);
                fc.Refuel(transfer);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this HOP.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate 
                { 
                    Find.WindowStack.Add(new Dialog_RenameHop(this)); 
                }
            };
            
            if (Prefs.DevMode && IsRefueling)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Complete Refuel",
                    action = delegate { CompleteRefueling(); }
                };
            }
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            // Network status
            if (!str.NullOrEmpty()) str += "\n";
            if (HasNetworkConnection())
            {
                str += $"Network: {GetNetworkStatusMessage()}";
            }
            else
            {
                str += $"<color=yellow>Network: {GetNetworkStatusMessage()}</color>";
            }

            if (refuelableComp != null)
                str += "\nFuel: " + refuelableComp.Fuel.ToString("F0") + " / 5000";

            if (IsRefueling)
            {
                str += "\nStatus: REFUELING";
                str += "\nTime remaining: " + refuelTicksRemaining.ToStringTicksToPeriod();
                if (destinationHub != null)
                    str += "\nDestination: " + destinationHub.Label;
            }
            else
            {
                str += "\nStatus: Ready";
            }

            return str;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref refuelTicksRemaining, "refuelTicksRemaining", 0);
            Scribe_Values.Look(ref destinationTile, "destinationTile", -1);
            Scribe_References.Look(ref missileBeingRefueled, "missileBeingRefueled");
            Scribe_References.Look(ref destinationHub, "destinationHub");
        }
    }

    public class Dialog_RenameHop : Window
    {
        private Building_Hop hop;
        private string newName;

        public Dialog_RenameHop(Building_Hop h)
        {
            hop = h;
            newName = h.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename HOP");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                hop.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}