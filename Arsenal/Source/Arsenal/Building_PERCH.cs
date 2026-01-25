using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    public enum PerchRole
    {
        SOURCE,   // Exports resources, never requests inbound
        SINK      // Imports resources up to threshold targets
    }

    /// <summary>
    /// PERCH - Landing/Launch Pad for SLING cargo craft.
    /// Can be configured as SOURCE (export) or SINK (import) via LATTICE UI.
    /// </summary>
    public class Building_PERCH : Building
    {
        private CompRefuelable refuelableComp;
        private CompPowerTrader powerComp;

        private string customName;
        private static int perchCounter = 1;

        // Role configuration
        public PerchRole role = PerchRole.SOURCE;
        public int priority = 5;  // 1-10, lower = higher priority (for SINKs)

        // Resource thresholds for SINK mode (resource -> target amount)
        public Dictionary<ThingDef, int> thresholdTargets = new Dictionary<ThingDef, int>();

        // Resource filter for SOURCE mode (if empty, export all)
        public List<ThingDef> sourceFilter = new List<ThingDef>();
        public bool filterEnabled = false;

        // SLING currently on pad
        private Thing slingOnPad;
        private bool isUnloading;
        private int unloadTicksRemaining;
        private const int UNLOAD_TICKS = 600; // 10 seconds

        // Loading state
        private bool isLoading;
        private int loadTicksRemaining;
        private const int LOAD_TICKS = 600; // 10 seconds
        private Building_PERCH loadDestination;
        private Dictionary<ThingDef, int> loadingCargo = new Dictionary<ThingDef, int>();

        // Refueling state for SLINGs
        private bool isRefueling;
        private int refuelTicksRemaining;
        private const int REFUEL_TICKS = 1800; // 30 seconds

        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;
        public bool HasFuel => refuelableComp != null && refuelableComp.Fuel >= 50f;
        public float FuelLevel => refuelableComp?.Fuel ?? 0f;
        public float FuelCapacity => refuelableComp?.Props.fuelCapacity ?? 500f;
        public bool HasSlingOnPad => slingOnPad != null;
        public bool IsBusy => isUnloading || isLoading || isRefueling;

        public float FuelPercent
        {
            get
            {
                if (refuelableComp == null) return 0f;
                float maxFuel = refuelableComp.Props.fuelCapacity;
                return maxFuel > 0 ? refuelableComp.Fuel / maxFuel : 0f;
            }
        }

        /// <summary>
        /// Checks if PERCH has network connectivity to LATTICE.
        /// </summary>
        public bool HasNetworkConnection()
        {
            if (Map == null) return false;
            return ArsenalNetworkManager.IsTileConnected(Map.Tile);
        }

        public string GetNetworkStatusMessage()
        {
            if (Map == null) return "OFFLINE - No map";
            return ArsenalNetworkManager.GetNetworkStatus(Map.Tile);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            refuelableComp = GetComp<CompRefuelable>();
            powerComp = GetComp<CompPowerTrader>();
            if (!respawningAfterLoad)
            {
                ArsenalNetworkManager.RegisterPerch(this);
                customName = "PERCH-" + perchCounter.ToString("D2");
                perchCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // If we have a SLING on pad, it remains on the map
            if (slingOnPad != null && slingOnPad.Spawned)
            {
                slingOnPad.SetForbidden(false, false);
            }
            slingOnPad = null;
            ArsenalNetworkManager.DeregisterPerch(this);
            base.DeSpawn(mode);
        }

        public override string Label => customName ?? base.Label;

        public void SetCustomName(string name) => customName = name;

        public void SetRole(PerchRole newRole)
        {
            role = newRole;
            if (role == PerchRole.SOURCE)
            {
                thresholdTargets.Clear();
            }
        }

        public void SetPriority(int newPriority)
        {
            priority = Mathf.Clamp(newPriority, 1, 10);
        }

        public void SetThreshold(ThingDef resource, int target)
        {
            if (target <= 0)
                thresholdTargets.Remove(resource);
            else
                thresholdTargets[resource] = target;
        }

        /// <summary>
        /// Gets the current stock level for a resource near this PERCH.
        /// Checks adjacent storage zones and buildings.
        /// </summary>
        public int GetCurrentStock(ThingDef resource)
        {
            if (Map == null) return 0;

            int total = 0;

            // Check cells adjacent to PERCH
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;

                // Check for storage buildings
                var building = cell.GetFirstBuilding(Map);
                if (building != null)
                {
                    var storage = building.TryGetComp<CompStorage>();
                    if (storage != null)
                    {
                        foreach (Thing t in storage.StoredThings)
                        {
                            if (t.def == resource)
                                total += t.stackCount;
                        }
                    }
                }

                // Check zone stockpiles
                var things = cell.GetThingList(Map);
                foreach (Thing t in things)
                {
                    if (t.def == resource && t.def.category == ThingCategory.Item)
                        total += t.stackCount;
                }
            }

            return total;
        }

        /// <summary>
        /// Gets all available resources near this PERCH for export.
        /// </summary>
        public Dictionary<ThingDef, int> GetAvailableResources()
        {
            var resources = new Dictionary<ThingDef, int>();
            if (Map == null) return resources;

            // Check cells adjacent to PERCH
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;

                var things = cell.GetThingList(Map);
                foreach (Thing t in things)
                {
                    if (t.def.category == ThingCategory.Item && t.def.EverHaulable)
                    {
                        // Apply source filter if enabled
                        if (filterEnabled && sourceFilter.Count > 0 && !sourceFilter.Contains(t.def))
                            continue;

                        if (resources.ContainsKey(t.def))
                            resources[t.def] += t.stackCount;
                        else
                            resources[t.def] = t.stackCount;
                    }
                }
            }

            return resources;
        }

        /// <summary>
        /// Gets the demand for resources at this SINK.
        /// Returns resource types and amounts needed to reach threshold.
        /// </summary>
        public Dictionary<ThingDef, int> GetDemand()
        {
            var demand = new Dictionary<ThingDef, int>();
            if (role != PerchRole.SINK) return demand;

            foreach (var kvp in thresholdTargets)
            {
                int current = GetCurrentStock(kvp.Key);
                int needed = kvp.Value - current;
                if (needed > 0)
                    demand[kvp.Key] = needed;
            }

            return demand;
        }

        /// <summary>
        /// Checks if this SINK has any unfilled demand.
        /// </summary>
        public bool HasDemand()
        {
            if (role != PerchRole.SINK) return false;
            return GetDemand().Any(d => d.Value > 0);
        }

        /// <summary>
        /// Called when a SLING lands at this PERCH.
        /// </summary>
        public void ReceiveSling(Thing sling, Dictionary<ThingDef, int> cargo)
        {
            slingOnPad = sling;

            if (cargo != null && cargo.Count > 0)
            {
                // Start unloading
                isUnloading = true;
                unloadTicksRemaining = UNLOAD_TICKS;
                loadingCargo = new Dictionary<ThingDef, int>(cargo);
                Messages.Message($"{Label}: SLING landed, unloading cargo...", this, MessageTypeDefOf.NeutralEvent);
            }
            else
            {
                // Empty SLING arrived (return flight)
                Messages.Message($"{Label}: SLING returned", this, MessageTypeDefOf.NeutralEvent);
            }
        }

        /// <summary>
        /// Begins loading a SLING for departure to destination.
        /// </summary>
        public bool StartLoading(Building_PERCH destination, Dictionary<ThingDef, int> cargoToLoad)
        {
            if (slingOnPad == null || IsBusy) return false;
            if (!HasNetworkConnection()) return false;

            isLoading = true;
            loadTicksRemaining = LOAD_TICKS;
            loadDestination = destination;
            loadingCargo = new Dictionary<ThingDef, int>(cargoToLoad);

            Messages.Message($"{Label}: Loading SLING for {destination.Label}...", this, MessageTypeDefOf.NeutralEvent);
            return true;
        }

        /// <summary>
        /// Checks if SLING can be dispatched (has fuel for the journey).
        /// </summary>
        public bool CanDispatch(int destinationTile)
        {
            if (slingOnPad == null || IsBusy) return false;
            if (!IsPoweredOn || !HasNetworkConnection()) return false;

            // Check fuel
            float fuelNeeded = SlingLogisticsManager.CalculateFuelCost(Map.Tile, destinationTile);
            return FuelLevel >= fuelNeeded;
        }

        /// <summary>
        /// Called to refuel a SLING on the pad.
        /// </summary>
        public void StartRefuelingSling()
        {
            if (slingOnPad == null || IsBusy) return;
            if (!HasFuel) return;

            isRefueling = true;
            refuelTicksRemaining = REFUEL_TICKS;
        }

        protected override void Tick()
        {
            base.Tick();

            if (isUnloading)
            {
                TickUnloading();
            }
            else if (isLoading)
            {
                TickLoading();
            }
            else if (isRefueling)
            {
                TickRefueling();
            }
        }

        private void TickUnloading()
        {
            unloadTicksRemaining--;

            // Visual effects
            if (unloadTicksRemaining % 30 == 0 && Map != null)
            {
                FleckMaker.ThrowMicroSparks(Position.ToVector3Shifted(), Map);
            }

            if (unloadTicksRemaining <= 0)
            {
                CompleteUnloading();
            }
        }

        private void CompleteUnloading()
        {
            isUnloading = false;

            // Spawn cargo on pad or adjacent cells
            foreach (var kvp in loadingCargo)
            {
                int remaining = kvp.Value;
                while (remaining > 0)
                {
                    int spawnCount = Mathf.Min(remaining, kvp.Key.stackLimit);
                    Thing cargo = ThingMaker.MakeThing(kvp.Key);
                    cargo.stackCount = spawnCount;

                    IntVec3 spawnCell = FindCargoDropCell();
                    if (spawnCell.IsValid)
                    {
                        GenSpawn.Spawn(cargo, spawnCell, Map);
                    }
                    else
                    {
                        // Fallback: spawn on pad position
                        GenSpawn.Spawn(cargo, Position, Map);
                    }
                    remaining -= spawnCount;
                }
            }

            loadingCargo.Clear();
            Messages.Message($"{Label}: Cargo unloaded", this, MessageTypeDefOf.PositiveEvent);

            // SLING now needs to return or wait for new orders
        }

        private IntVec3 FindCargoDropCell()
        {
            // Find an adjacent cell that can hold items
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (cell.InBounds(Map) && cell.Standable(Map) && cell.GetFirstItem(Map) == null)
                    return cell;
            }
            return IntVec3.Invalid;
        }

        private void TickLoading()
        {
            loadTicksRemaining--;

            if (loadTicksRemaining % 30 == 0 && Map != null)
            {
                FleckMaker.ThrowMicroSparks(Position.ToVector3Shifted(), Map);
            }

            if (loadTicksRemaining <= 0)
            {
                CompleteLoading();
            }
        }

        private void CompleteLoading()
        {
            isLoading = false;

            if (slingOnPad == null || loadDestination == null)
            {
                loadingCargo.Clear();
                return;
            }

            // Consume resources from adjacent storage
            var actualCargo = new Dictionary<ThingDef, int>();
            foreach (var kvp in loadingCargo)
            {
                int toLoad = ConsumeResource(kvp.Key, kvp.Value);
                if (toLoad > 0)
                    actualCargo[kvp.Key] = toLoad;
            }

            // Consume fuel for the trip
            float fuelCost = SlingLogisticsManager.CalculateFuelCost(Map.Tile, loadDestination.Map.Tile);
            if (refuelableComp != null)
            {
                refuelableComp.ConsumeFuel(fuelCost);
            }

            // Despawn SLING and launch
            if (slingOnPad.Spawned)
            {
                slingOnPad.DeSpawn(DestroyMode.Vanish);
            }

            // Create traveling world object
            var traveling = (WorldObject_TravelingSling)WorldObjectMaker.MakeWorldObject(ArsenalDefOf.Arsenal_TravelingSling);
            traveling.Tile = Map.Tile;
            traveling.destinationTile = loadDestination.Map.Tile;
            traveling.sling = slingOnPad;
            traveling.cargo = actualCargo;
            traveling.originPerch = this;
            traveling.destinationPerch = loadDestination;
            Find.WorldObjects.Add(traveling);

            slingOnPad = null;
            loadDestination = null;
            loadingCargo.Clear();

            Messages.Message($"{Label}: SLING dispatched", this, MessageTypeDefOf.PositiveEvent);
        }

        private int ConsumeResource(ThingDef resource, int amount)
        {
            int consumed = 0;

            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;
                if (consumed >= amount) break;

                var things = cell.GetThingList(Map).ToList();
                foreach (Thing t in things)
                {
                    if (t.def == resource)
                    {
                        int take = Mathf.Min(amount - consumed, t.stackCount);
                        if (take >= t.stackCount)
                        {
                            consumed += t.stackCount;
                            t.Destroy();
                        }
                        else
                        {
                            t.stackCount -= take;
                            consumed += take;
                        }
                    }
                    if (consumed >= amount) break;
                }
            }

            return consumed;
        }

        private void TickRefueling()
        {
            refuelTicksRemaining--;

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
            isRefueling = false;
            // SLING is now refueled and ready for dispatch
            Messages.Message($"{Label}: SLING refueled", this, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// Adds a SLING to this PERCH (for fleet count purposes).
        /// </summary>
        public void AssignSling(Thing sling)
        {
            slingOnPad = sling;
            if (sling != null && !sling.Spawned && Map != null)
            {
                GenSpawn.Spawn(sling, Position, Map);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this PERCH.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenamePerch(this));
                }
            };

            // Role toggle
            yield return new Command_Action
            {
                defaultLabel = $"Role: {role}",
                defaultDesc = role == PerchRole.SOURCE ?
                    "SOURCE: Exports available resources." :
                    "SINK: Imports resources to threshold targets.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", false),
                action = delegate
                {
                    SetRole(role == PerchRole.SOURCE ? PerchRole.SINK : PerchRole.SOURCE);
                }
            };

            if (role == PerchRole.SINK)
            {
                yield return new Command_Action
                {
                    defaultLabel = $"Priority: {priority}",
                    defaultDesc = "Lower number = higher priority. SINKs with lower priority numbers are filled first.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", false),
                    action = delegate
                    {
                        List<FloatMenuOption> options = new List<FloatMenuOption>();
                        for (int p = 1; p <= 10; p++)
                        {
                            int pVal = p;
                            options.Add(new FloatMenuOption(p.ToString(), () => SetPriority(pVal)));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                };
            }

            if (Prefs.DevMode)
            {
                if (slingOnPad != null && !IsBusy)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Dispatch Test",
                        action = delegate
                        {
                            SlingLogisticsManager.TryDispatchFromPerch(this);
                        }
                    };
                }
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

            // Role and status
            str += $"\nRole: {role}";
            if (role == PerchRole.SINK)
                str += $" (Priority {priority})";

            // Fuel
            if (refuelableComp != null)
                str += $"\nFuel: {refuelableComp.Fuel:F0} / {FuelCapacity:F0}";

            // SLING status
            if (slingOnPad != null)
            {
                str += "\nSLING: On pad";
                if (isUnloading)
                    str += $" (Unloading: {unloadTicksRemaining.ToStringTicksToPeriod()})";
                else if (isLoading)
                    str += $" (Loading: {loadTicksRemaining.ToStringTicksToPeriod()})";
                else if (isRefueling)
                    str += $" (Refueling: {refuelTicksRemaining.ToStringTicksToPeriod()})";
                else
                    str += " (Ready)";
            }
            else
            {
                str += "\nSLING: None";
            }

            // Demand/Supply info
            if (role == PerchRole.SINK && thresholdTargets.Count > 0)
            {
                var demand = GetDemand();
                if (demand.Count > 0)
                {
                    str += "\nDemand: ";
                    str += string.Join(", ", demand.Select(d => $"{d.Key.label} ({d.Value})"));
                }
                else
                {
                    str += "\nDemand: Satisfied";
                }
            }

            return str;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref role, "role", PerchRole.SOURCE);
            Scribe_Values.Look(ref priority, "priority", 5);
            Scribe_Values.Look(ref filterEnabled, "filterEnabled", false);
            Scribe_Collections.Look(ref sourceFilter, "sourceFilter", LookMode.Def);
            Scribe_Collections.Look(ref thresholdTargets, "thresholdTargets", LookMode.Def, LookMode.Value);

            Scribe_References.Look(ref slingOnPad, "slingOnPad");
            Scribe_Values.Look(ref isUnloading, "isUnloading", false);
            Scribe_Values.Look(ref unloadTicksRemaining, "unloadTicksRemaining", 0);
            Scribe_Values.Look(ref isLoading, "isLoading", false);
            Scribe_Values.Look(ref loadTicksRemaining, "loadTicksRemaining", 0);
            Scribe_References.Look(ref loadDestination, "loadDestination");
            Scribe_Values.Look(ref isRefueling, "isRefueling", false);
            Scribe_Values.Look(ref refuelTicksRemaining, "refuelTicksRemaining", 0);
            Scribe_Collections.Look(ref loadingCargo, "loadingCargo", LookMode.Def, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (sourceFilter == null) sourceFilter = new List<ThingDef>();
                if (thresholdTargets == null) thresholdTargets = new Dictionary<ThingDef, int>();
                if (loadingCargo == null) loadingCargo = new Dictionary<ThingDef, int>();
            }
        }
    }

    public class Dialog_RenamePerch : Window
    {
        private Building_PERCH perch;
        private string newName;

        public Dialog_RenamePerch(Building_PERCH p)
        {
            perch = p;
            newName = p.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename PERCH");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                perch.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
