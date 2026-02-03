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
    /// PERCH - Dual-slot Landing/Launch Pad for SLING cargo craft.
    /// Slot 1: Primary slot for staging/loading (outbound operations)
    /// Slot 2: Secondary slot for incoming SLINGs
    /// Can be configured as SOURCE (export) or SINK (import) via LATTICE UI.
    /// </summary>
    public class Building_PERCH : Building
    {
        private CompRefuelable refuelableComp;
        private CompPowerTrader powerComp;

        private string customName;
        private static int perchCounter = 1;

        /// <summary>
        /// Sets the perch counter to a specific value.
        /// Called after game load to prevent duplicate names.
        /// </summary>
        public static void SetCounter(int value)
        {
            perchCounter = System.Math.Max(1, value);
        }

        // Role configuration
        public PerchRole role = PerchRole.SOURCE;
        public int priority = 5;  // 1-10, lower = higher priority (for SINKs)

        // Resource thresholds for SINK mode (resource -> target amount)
        public Dictionary<ThingDef, int> thresholdTargets = new Dictionary<ThingDef, int>();

        // Resource filter for SOURCE mode (if empty, export all)
        public List<ThingDef> sourceFilter = new List<ThingDef>();
        public bool filterEnabled = false;

        // Dual SLING slots
        // Slot 1: Primary - for staging, loading, and outbound operations
        // Slot 2: Secondary - for incoming SLINGs
        private Thing slingSlot1;
        private Thing slingSlot2;

        // Slot 1 state (primary - loading/staging)
        private bool slot1IsLoading;
        private Building_PERCH slot1LoadDestination;
        private Dictionary<ThingDef, int> slot1LoadingCargo = new Dictionary<ThingDef, int>();

        // Slot 2 state (secondary - unloading/refueling incoming)
        private bool slot2IsUnloading;
        private int slot2UnloadTicksRemaining;
        private bool slot2IsRefueling;
        private int slot2RefuelTicksRemaining;
        private Building_PERCH slot2PendingReturnOrigin;

        private const int UNLOAD_TICKS = 600; // 10 seconds
        private const int LOAD_TICKS = 600; // 10 seconds (unused now, no timeout)
        private const int REFUEL_TICKS = 1800; // 30 seconds

        // Backwards compatibility helpers - redirect to slot-specific fields
        // These allow gradual migration from single-slot to dual-slot
        private Thing slingOnPad
        {
            get => slingSlot1 ?? slingSlot2;
            set
            {
                // For backward compat writes, assign to slot1 (primary)
                slingSlot1 = value;
            }
        }

        // Slot 1 (primary) properties with setters
        private bool isLoading
        {
            get => slot1IsLoading;
            set => slot1IsLoading = value;
        }
        private Building_PERCH loadDestination
        {
            get => slot1LoadDestination;
            set => slot1LoadDestination = value;
        }
        private Dictionary<ThingDef, int> loadingCargo
        {
            get => slot1LoadingCargo;
            set => slot1LoadingCargo = value;
        }

        // Slot 2 (secondary) properties with setters
        private bool isUnloading
        {
            get => slot2IsUnloading;
            set => slot2IsUnloading = value;
        }
        private int unloadTicksRemaining
        {
            get => slot2UnloadTicksRemaining;
            set => slot2UnloadTicksRemaining = value;
        }
        private bool isRefueling
        {
            get => slot2IsRefueling;
            set => slot2IsRefueling = value;
        }
        private int refuelTicksRemaining
        {
            get => slot2RefuelTicksRemaining;
            set => slot2RefuelTicksRemaining = value;
        }
        private Building_PERCH pendingReturnOrigin
        {
            get => slot2PendingReturnOrigin;
            set => slot2PendingReturnOrigin = value;
        }

        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;
        public bool HasFuel => refuelableComp != null && refuelableComp.Fuel >= 50f;
        public float FuelLevel => refuelableComp?.Fuel ?? 0f;
        public float FuelCapacity => refuelableComp?.Props.fuelCapacity ?? 500f;
        public bool HasSlingOnPad => slingSlot1 != null || slingSlot2 != null;
        public bool HasSlot1Sling => slingSlot1 != null;
        public bool HasSlot2Sling => slingSlot2 != null;
        public bool Slot1Available => slingSlot1 == null;
        public bool Slot2Available => slingSlot2 == null;
        public bool HasAvailableSlot => Slot1Available || Slot2Available;
        public int SlingCount => (slingSlot1 != null ? 1 : 0) + (slingSlot2 != null ? 1 : 0);
        public bool IsBusy => slot1IsLoading || slot2IsUnloading || slot2IsRefueling;
        public bool Slot1Busy => slot1IsLoading;
        public bool Slot2Busy => slot2IsUnloading || slot2IsRefueling;
        public string SlingName => SLING_Thing.GetSlingName(slingSlot1 ?? slingSlot2);
        public string Slot1SlingName => slingSlot1 != null ? SLING_Thing.GetSlingName(slingSlot1) : "Empty";
        public string Slot2SlingName => slingSlot2 != null ? SLING_Thing.GetSlingName(slingSlot2) : "Empty";
        public Thing SlingOnPad => slingSlot1 ?? slingSlot2;
        public Thing Slot1Sling => slingSlot1;
        public Thing Slot2Sling => slingSlot2;

        /// <summary>
        /// Clears slot 1 reference. Used when SLING is dispatched/redistributed.
        /// </summary>
        public void ClearSlot1()
        {
            slingSlot1 = null;
            slot1IsLoading = false;
            slot1LoadDestination = null;
            slot1LoadingCargo.Clear();
        }

        /// <summary>
        /// Clears slot 2 reference. Used when SLING is dispatched/returned.
        /// </summary>
        public void ClearSlot2()
        {
            slingSlot2 = null;
            slot2IsUnloading = false;
            slot2IsRefueling = false;
            slot2PendingReturnOrigin = null;
        }

        /// <summary>
        /// Returns the SLING_Thing that is currently in loading state (slot1 only).
        /// Returns null if no SLING is loading.
        /// </summary>
        public SLING_Thing LoadingSling
        {
            get
            {
                if (!slot1IsLoading || slingSlot1 == null) return null;
                return slingSlot1 as SLING_Thing;
            }
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

            // Always register with network manager (buildings re-register after load)
            ArsenalNetworkManager.RegisterPerch(this);

            // Only assign name on initial spawn, not when loading
            if (!respawningAfterLoad)
            {
                customName = "PERCH-" + perchCounter.ToString("D2");
                perchCounter++;
            }
            else
            {
                // After load, reposition any SLINGs to correct slot positions
                // This fixes positions from old saves before slot position code was correct
                RepositionSlings();
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // If we have SLINGs on pads, they remain on the map
            if (slingSlot1 != null && slingSlot1.Spawned)
            {
                slingSlot1.SetForbidden(false, false);
            }
            if (slingSlot2 != null && slingSlot2.Spawned)
            {
                slingSlot2.SetForbidden(false, false);
            }
            slingSlot1 = null;
            slingSlot2 = null;
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
        /// Checks adjacent storage zones and buildings (for loading purposes).
        /// </summary>
        public int GetCurrentStock(ThingDef resource)
        {
            if (Map == null) return 0;

            int total = 0;

            // Check cells adjacent to PERCH
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map)) continue;

                // Check all items on this cell (includes stockpile zones and storage buildings)
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
        /// Gets the total stock level for a resource across the entire map.
        /// Used for SINK threshold demand calculations.
        /// </summary>
        public int GetMapStock(ThingDef resource)
        {
            if (Map == null) return 0;

            int total = 0;

            // Count all instances of this resource on the map
            foreach (Thing t in Map.listerThings.ThingsOfDef(resource))
            {
                if (t.def.category == ThingCategory.Item && !t.IsForbidden(Faction.OfPlayer))
                    total += t.stackCount;
            }

            return total;
        }

        /// <summary>
        /// Gets all available resources on this map for export.
        /// Checks entire map inventory, not just adjacent cells.
        /// </summary>
        public Dictionary<ThingDef, int> GetAvailableResources()
        {
            var resources = new Dictionary<ThingDef, int>();
            if (Map == null) return resources;

            // Check all haulable items on the map
            foreach (Thing t in Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableAlways))
            {
                if (t.def.category != ThingCategory.Item) continue;
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                if (!t.def.EverHaulable) continue;

                // Apply source filter if enabled
                if (filterEnabled && sourceFilter.Count > 0 && !sourceFilter.Contains(t.def))
                    continue;

                if (resources.ContainsKey(t.def))
                    resources[t.def] += t.stackCount;
                else
                    resources[t.def] = t.stackCount;
            }

            return resources;
        }

        /// <summary>
        /// Gets the demand for resources at this SINK.
        /// If thresholds are configured, returns unfilled threshold amounts.
        /// If NO thresholds configured, returns demand for ALL available resources from connected SOURCEs.
        /// This allows SINKs to work without explicit configuration.
        /// </summary>
        public Dictionary<ThingDef, int> GetDemand()
        {
            var demand = new Dictionary<ThingDef, int>();
            if (role != PerchRole.SINK) return demand;

            // If thresholds are configured, use them
            if (thresholdTargets.Count > 0)
            {
                foreach (var kvp in thresholdTargets)
                {
                    int current = GetMapStock(kvp.Key);
                    int needed = kvp.Value - current;
                    if (needed > 0)
                        demand[kvp.Key] = needed;
                }
                return demand;
            }

            // NO thresholds configured - accept ANY resources from SOURCEs
            // This is the default "accept all" mode for unconfigured SINKs
            foreach (var source in ArsenalNetworkManager.GetSourcePerches())
            {
                if (!source.HasNetworkConnection() || !source.IsPoweredOn) continue;

                var available = source.GetAvailableResources();
                foreach (var kvp in available)
                {
                    if (demand.ContainsKey(kvp.Key))
                        demand[kvp.Key] = Mathf.Max(demand[kvp.Key], kvp.Value);
                    else
                        demand[kvp.Key] = kvp.Value;
                }
            }

            return demand;
        }

        /// <summary>
        /// Checks if this SINK has any unfilled demand.
        /// Returns true if thresholds are unfilled OR if no thresholds and any SOURCE has resources.
        /// </summary>
        public bool HasDemand()
        {
            if (role != PerchRole.SINK) return false;

            // If thresholds configured, check if any are unfilled
            if (thresholdTargets.Count > 0)
            {
                return GetDemand().Any(d => d.Value > 0);
            }

            // No thresholds - check if any SOURCE has exportable resources
            foreach (var source in ArsenalNetworkManager.GetSourcePerches())
            {
                if (!source.HasNetworkConnection() || !source.IsPoweredOn) continue;
                if (source.GetAvailableResources().Count > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Called when a SLING lands at this PERCH.
        /// Handles the landing state (unloading, return tracking).
        /// Note: Slot assignment may already be done by AssignToAvailableSlot - this method
        /// will only assign if the SLING is not yet in a slot.
        /// </summary>
        public void ReceiveSling(Thing sling, Dictionary<ThingDef, int> cargo, Building_PERCH returnOrigin = null, string incomingSlingName = null)
        {
            // Apply incoming name to the SLING_Thing if provided
            if (!string.IsNullOrEmpty(incomingSlingName) && sling is SLING_Thing slingThing)
            {
                slingThing.AssignName(incomingSlingName);
            }

            string displayName = SLING_Thing.GetSlingName(sling);

            // Check if SLING is already assigned to a slot (from AssignToAvailableSlot)
            bool alreadyAssigned = sling == slingSlot1 || sling == slingSlot2;

            if (cargo != null && cargo.Count > 0)
            {
                // Incoming SLING with cargo - needs unloading
                if (!alreadyAssigned)
                {
                    // Assign to Slot 2 for unloading (if not already assigned)
                    if (slingSlot2 == null)
                    {
                        slingSlot2 = sling;
                        Log.Message($"[PERCH] {Label}: ReceiveSling assigned {displayName} to slot 2 for unloading");
                    }
                    else if (slingSlot1 == null)
                    {
                        slingSlot1 = sling;
                        Log.Message($"[PERCH] {Label}: ReceiveSling assigned {displayName} to slot 1 for unloading (slot2 full)");
                    }
                    else
                    {
                        // CRITICAL: Don't overwrite! Both slots occupied is an error state.
                        Log.Error($"[PERCH] {Label}: Cannot receive {displayName} - both slots full! " +
                                 $"Slot1={SLING_Thing.GetSlingName(slingSlot1)}, Slot2={SLING_Thing.GetSlingName(slingSlot2)}");
                        // Don't assign - the SLING is spawned but not tracked
                        // This is better than losing an existing SLING reference
                    }
                }

                // Determine which slot the SLING is in for unloading state
                // Unloading state is tracked on slot 2
                if (sling == slingSlot2)
                {
                    slot2IsUnloading = true;
                    slot2UnloadTicksRemaining = UNLOAD_TICKS;
                    slot2PendingReturnOrigin = returnOrigin;
                }
                else if (sling == slingSlot1)
                {
                    // SLING with cargo landed in slot 1 (unusual but handle it)
                    // For now, we'll still use slot2 state tracking but move to slot2 if available
                    if (slingSlot2 == null)
                    {
                        slingSlot2 = slingSlot1;
                        slingSlot1 = null;
                        if (slingSlot2.Spawned)
                            slingSlot2.Position = GetSlot2Position();
                    }
                    slot2IsUnloading = true;
                    slot2UnloadTicksRemaining = UNLOAD_TICKS;
                    slot2PendingReturnOrigin = returnOrigin;
                }

                // Store cargo manifest for unloading
                slot1LoadingCargo = new Dictionary<ThingDef, int>(cargo);
                Messages.Message($"{Label}: {displayName} landed, unloading cargo...", this, MessageTypeDefOf.NeutralEvent);
            }
            else
            {
                // Empty SLING returning - ready for next mission
                if (!alreadyAssigned)
                {
                    // Assign to Slot 1 (primary, ready for missions)
                    if (slingSlot1 == null)
                    {
                        slingSlot1 = sling;
                        Log.Message($"[PERCH] {Label}: ReceiveSling assigned {displayName} to slot 1 (empty, ready)");
                    }
                    else if (slingSlot2 == null)
                    {
                        slingSlot2 = sling;
                        Log.Message($"[PERCH] {Label}: ReceiveSling assigned {displayName} to slot 2 (empty, slot1 full)");
                    }
                    else
                    {
                        // CRITICAL: Don't overwrite! Both slots occupied is an error state.
                        Log.Error($"[PERCH] {Label}: Cannot receive empty {displayName} - both slots full! " +
                                 $"Slot1={SLING_Thing.GetSlingName(slingSlot1)}, Slot2={SLING_Thing.GetSlingName(slingSlot2)}");
                    }
                }

                slot2PendingReturnOrigin = null;
                Messages.Message($"{Label}: {displayName} returned", this, MessageTypeDefOf.NeutralEvent);
            }
        }

        /// <summary>
        /// Begins loading a SLING for departure to destination.
        /// Loading happens on Slot 1 (primary staging slot).
        /// MULEs and colonists will haul cargo to the SLING.
        /// Loading continues until complete - no timeout.
        /// </summary>
        public bool StartLoading(Building_PERCH destination, Dictionary<ThingDef, int> cargoToLoad)
        {
            string slingName = slingSlot1 != null ? SLING_Thing.GetSlingName(slingSlot1) : "NO SLING";

            // Loading requires a SLING in slot 1, and slot 1 not busy
            if (slingSlot1 == null)
            {
                Log.Warning($"[PERCH] {Label}: StartLoading failed - no SLING in slot 1");
                return false;
            }
            if (slot1IsLoading)
            {
                Log.Warning($"[PERCH] {Label}: StartLoading failed - slot 1 already loading");
                return false;
            }
            if (!HasNetworkConnection())
            {
                Log.Warning($"[PERCH] {Label}: StartLoading failed - no network connection");
                return false;
            }

            // Tell the SLING to start accepting cargo
            var sling = slingSlot1 as SLING_Thing;
            if (sling != null)
            {
                sling.StartLoading(cargoToLoad);
            }
            else
            {
                Log.Warning($"[PERCH] {Label}: Slot 1 has Thing but not SLING_Thing: {slingSlot1.GetType().Name}");
            }

            slot1IsLoading = true;
            slot1LoadDestination = destination;
            slot1LoadingCargo = new Dictionary<ThingDef, int>(cargoToLoad);

            Log.Message($"[PERCH] {Label}: {slingName} START LOADING for {destination.Label}. " +
                       $"Cargo: {string.Join(", ", cargoToLoad.Select(c => $"{c.Key.label}x{c.Value}"))}");
            Messages.Message($"{Label}: {slingName} awaiting cargo for {destination.Label}. Colonists/MULEs will load cargo.", this, MessageTypeDefOf.NeutralEvent);
            return true;
        }

        /// <summary>
        /// Manually dispatches the SLING with whatever cargo is currently loaded.
        /// Dispatching happens from Slot 1.
        /// </summary>
        public void DispatchNow()
        {
            if (!slot1IsLoading || slingSlot1 == null) return;

            var sling = slingSlot1 as SLING_Thing;
            if (sling != null && sling.CurrentCargoCount > 0)
            {
                CompleteLoading();
            }
            else
            {
                Messages.Message($"{Label}: Cannot dispatch - no cargo loaded.", this, MessageTypeDefOf.RejectInput);
            }
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
        /// Called to refuel a SLING on Slot 2 (incoming slot).
        /// </summary>
        public void StartRefuelingSling()
        {
            if (slingSlot2 == null || slot2IsUnloading || slot2IsRefueling) return;
            if (!HasFuel) return;

            slot2IsRefueling = true;
            slot2RefuelTicksRemaining = REFUEL_TICKS;
        }

        protected override void Tick()
        {
            base.Tick();

            // Process Slot 1 operations (loading/staging)
            if (slot1IsLoading && slingSlot1 != null)
            {
                TickLoading();
            }

            // Process Slot 2 operations (unloading/refueling incoming)
            if (slot2IsUnloading && slingSlot2 != null)
            {
                TickUnloading();
            }
            else if (slot2IsRefueling && slingSlot2 != null)
            {
                TickRefueling();
            }

            // Periodically check if idle slot2 SLING should move to slot1
            // This handles edge cases like game load with SLING stuck in slot2
            if (this.IsHashIntervalTick(60))
            {
                TryMoveSlot2ToSlot1();
            }

            // Less frequently, validate and reposition SLINGs to correct positions
            // This catches any drift or position corruption
            if (this.IsHashIntervalTick(300))
            {
                RepositionSlings();
            }
        }

        private void TickUnloading()
        {
            slot2UnloadTicksRemaining--;

            // Visual effects at slot 2 position
            if (slot2UnloadTicksRemaining % 30 == 0 && Map != null)
            {
                Vector3 slot2Pos = GetSlot2Position().ToVector3Shifted();
                FleckMaker.ThrowMicroSparks(slot2Pos, Map);
            }

            if (slot2UnloadTicksRemaining <= 0)
            {
                CompleteUnloading();
            }
        }

        private void CompleteUnloading()
        {
            slot2IsUnloading = false;

            // Unload cargo from SLING's container or from manifest
            // Unloading happens on slot 2
            var sling = slingSlot2 as SLING_Thing;
            string unloadingSlingName = sling != null ? SLING_Thing.GetSlingName(sling) : "SLING";

            if (sling != null && sling.CurrentCargoCount > 0)
            {
                // Drop cargo from SLING's physical container
                IntVec3 dropCell = FindCargoDropCell();
                if (!dropCell.IsValid) dropCell = Position;
                sling.UnloadAllCargo(dropCell, Map);
            }
            else
            {
                // Fallback: spawn from manifest (legacy/transit cargo)
                foreach (var kvp in slot1LoadingCargo)
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
                            GenSpawn.Spawn(cargo, Position, Map);
                        }
                        remaining -= spawnCount;
                    }
                }
            }

            slot1LoadingCargo.Clear();
            Messages.Message($"{Label}: Cargo unloaded", this, MessageTypeDefOf.PositiveEvent);

            // Trigger return flight if this SLING needs to go back
            if (slot2PendingReturnOrigin != null && slingSlot2 != null)
            {
                var returnTo = slot2PendingReturnOrigin;
                slot2PendingReturnOrigin = null;

                // Don't return if already at origin
                if (returnTo != this && returnTo.Map != null && !returnTo.Destroyed)
                {
                    SlingLogisticsManager.InitiateReturnFlight(slingSlot2, unloadingSlingName, this, returnTo);
                    slingSlot2 = null;
                }
                else
                {
                    // Move to slot 1 if available (SLING stays at this PERCH)
                    TryMoveSlot2ToSlot1();
                }
            }
            else if (slingSlot2 != null && slingSlot1 == null)
            {
                // No return needed, move SLING to slot 1 for staging
                TryMoveSlot2ToSlot1();
            }
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

        /// <summary>
        /// Gets the position for Slot 1 (primary/staging slot - TOP area).
        /// PERCH is 8x24 (8 wide, 24 tall at North). SLING is 6x10.
        /// Position returned is the SLING's bottom-left corner for spawning.
        ///
        /// For North rotation (per Excel diagram):
        /// - SLING 1 occupies rows 2-11 (X cols B-G)
        /// - Bottom edge at row 11 = Z offset 13 from PERCH position
        /// - X offset 1 (column B, centered)
        /// </summary>
        public IntVec3 GetSlot1Position()
        {
            IntVec3 offset;
            switch (Rotation.AsInt)
            {
                case 0: // North - 8 wide x 24 tall, slot 1 at TOP
                    offset = new IntVec3(1, 0, 13);
                    break;
                case 1: // East - 24 wide x 8 tall, slot 1 at RIGHT
                    offset = new IntVec3(13, 0, 1);
                    break;
                case 2: // South - 8 wide x 24 tall (flipped), slot 1 at BOTTOM
                    offset = new IntVec3(1, 0, 1);
                    break;
                case 3: // West - 24 wide x 8 tall, slot 1 at LEFT
                    offset = new IntVec3(1, 0, 1);
                    break;
                default:
                    offset = new IntVec3(1, 0, 13);
                    break;
            }
            return Position + offset;
        }

        /// <summary>
        /// Gets the position for Slot 2 (secondary/incoming slot - BOTTOM area).
        ///
        /// For North rotation (per Excel diagram):
        /// - SLING 2 occupies rows 14-23 (X cols B-G)
        /// - Bottom edge at row 23 = Z offset 1 from PERCH position
        /// - X offset 1 (column B, centered)
        /// </summary>
        public IntVec3 GetSlot2Position()
        {
            IntVec3 offset;
            switch (Rotation.AsInt)
            {
                case 0: // North - 8 wide x 24 tall, slot 2 at BOTTOM
                    offset = new IntVec3(1, 0, 1);
                    break;
                case 1: // East - 24 wide x 8 tall, slot 2 at LEFT
                    offset = new IntVec3(1, 0, 1);
                    break;
                case 2: // South - 8 wide x 24 tall (flipped), slot 2 at TOP
                    offset = new IntVec3(1, 0, 13);
                    break;
                case 3: // West - 24 wide x 8 tall, slot 2 at RIGHT
                    offset = new IntVec3(13, 0, 1);
                    break;
                default:
                    offset = new IntVec3(1, 0, 1);
                    break;
            }
            return Position + offset;
        }

        /// <summary>
        /// Gets the position for an available slot (prefers slot 2 for incoming).
        /// </summary>
        public IntVec3 GetAvailableSlotPosition()
        {
            if (slingSlot2 == null) return GetSlot2Position();
            if (slingSlot1 == null) return GetSlot1Position();
            return Position; // Both full, fallback to center
        }

        /// <summary>
        /// Assigns a SLING to an available slot and returns the slot position.
        /// Incoming SLINGs (with cargo) go to slot 2, returning go to slot 1.
        /// IMPORTANT: This method sets the slot reference - do not call if SLING is already assigned.
        /// </summary>
        public IntVec3 AssignToAvailableSlot(Thing sling, bool hasCargoToUnload)
        {
            // Safety check - don't re-assign if already in a slot
            if (sling == slingSlot1) return GetSlot1Position();
            if (sling == slingSlot2) return GetSlot2Position();

            string slingName = SLING_Thing.GetSlingName(sling);

            if (hasCargoToUnload)
            {
                // Incoming with cargo - prefer slot 2
                if (slingSlot2 == null)
                {
                    slingSlot2 = sling;
                    Log.Message($"[PERCH] {Label}: Assigned {slingName} to slot 2 (cargo) at {GetSlot2Position()}");
                    return GetSlot2Position();
                }
                else if (slingSlot1 == null)
                {
                    slingSlot1 = sling;
                    Log.Message($"[PERCH] {Label}: Assigned {slingName} to slot 1 (cargo, slot2 full) at {GetSlot1Position()}");
                    return GetSlot1Position();
                }
            }
            else
            {
                // Returning empty - prefer slot 1 (ready for next mission)
                if (slingSlot1 == null)
                {
                    slingSlot1 = sling;
                    Log.Message($"[PERCH] {Label}: Assigned {slingName} to slot 1 (empty) at {GetSlot1Position()}");
                    return GetSlot1Position();
                }
                else if (slingSlot2 == null)
                {
                    slingSlot2 = sling;
                    Log.Message($"[PERCH] {Label}: Assigned {slingName} to slot 2 (empty, slot1 full) at {GetSlot2Position()}");
                    return GetSlot2Position();
                }
            }

            // Both slots full - emergency fallback, don't overwrite existing slots
            Log.Error($"[PERCH] {Label}: Both slots FULL when trying to assign {slingName}! " +
                     $"Slot1={SLING_Thing.GetSlingName(slingSlot1)}, Slot2={SLING_Thing.GetSlingName(slingSlot2)}");
            // Return slot 1 position for spawn, but DO NOT assign to slot
            // The SLING will need to be handled by ReceiveSling or emergency landing
            return GetSlot1Position();
        }

        /// <summary>
        /// Repositions all SLINGs to their correct slot positions.
        /// Called after load and periodically to fix any position issues.
        /// Also validates slot references and cleans up invalid ones.
        /// </summary>
        public void RepositionSlings()
        {
            // Clean up invalid slot 1 reference
            if (slingSlot1 != null && (slingSlot1.Destroyed || (slingSlot1 is Building b1 && b1.DestroyedOrNull())))
            {
                Log.Warning($"[PERCH] {Label}: Clearing invalid slot 1 reference");
                slingSlot1 = null;
                slot1IsLoading = false;
                slot1LoadDestination = null;
                slot1LoadingCargo.Clear();
            }

            // Clean up invalid slot 2 reference
            if (slingSlot2 != null && (slingSlot2.Destroyed || (slingSlot2 is Building b2 && b2.DestroyedOrNull())))
            {
                Log.Warning($"[PERCH] {Label}: Clearing invalid slot 2 reference");
                slingSlot2 = null;
                slot2IsUnloading = false;
                slot2IsRefueling = false;
                slot2PendingReturnOrigin = null;
            }

            // Reposition slot 1 SLING
            if (slingSlot1 != null && slingSlot1.Spawned)
            {
                IntVec3 correctPos = GetSlot1Position();
                if (slingSlot1.Position != correctPos)
                {
                    Log.Message($"[PERCH] {Label}: Repositioning {SLING_Thing.GetSlingName(slingSlot1)} from {slingSlot1.Position} to slot 1 at {correctPos}");
                    slingSlot1.Position = correctPos;
                }
            }

            // Reposition slot 2 SLING
            if (slingSlot2 != null && slingSlot2.Spawned)
            {
                IntVec3 correctPos = GetSlot2Position();
                if (slingSlot2.Position != correctPos)
                {
                    Log.Message($"[PERCH] {Label}: Repositioning {SLING_Thing.GetSlingName(slingSlot2)} from {slingSlot2.Position} to slot 2 at {correctPos}");
                    slingSlot2.Position = correctPos;
                }
            }
        }

        private void TickLoading()
        {
            // Loading happens on slot 1
            // Visual effects occasionally
            if (this.IsHashIntervalTick(30) && Map != null)
            {
                Vector3 slot1Pos = GetSlot1Position().ToVector3Shifted();
                FleckMaker.ThrowMicroSparks(slot1Pos, Map);
            }

            // Check if SLING has finished loading (all cargo has been hauled in)
            var sling = slingSlot1 as SLING_Thing;
            if (sling != null && sling.IsLoadingComplete())
            {
                CompleteLoading();
                return;
            }

            // Periodically check if cargo is still available on the map
            // If none of the requested cargo exists, cancel loading
            if (this.IsHashIntervalTick(250)) // Check every ~4 seconds
            {
                if (!IsCargoAvailable())
                {
                    if (sling != null && sling.CurrentCargoCount > 0)
                    {
                        // Have some cargo, dispatch with what we have
                        CompleteLoading();
                    }
                    else
                    {
                        // No cargo loaded and none available - cancel
                        CancelLoading();
                    }
                }
            }
        }

        /// <summary>
        /// Checks if any of the requested cargo exists on the map.
        /// Checks against Slot 1's loading manifest.
        /// </summary>
        private bool IsCargoAvailable()
        {
            if (slot1LoadingCargo == null || slot1LoadingCargo.Count == 0) return false;

            var sling = slingSlot1 as SLING_Thing;
            var available = GetAvailableResources();

            foreach (var requested in slot1LoadingCargo)
            {
                // Check if we still need this resource
                int needed = requested.Value;
                if (sling != null)
                {
                    needed = sling.GetRemainingNeeded(requested.Key);
                }

                if (needed <= 0) continue; // Already loaded enough

                // Check if this resource exists on the map
                if (available.TryGetValue(requested.Key, out int mapAmount) && mapAmount > 0)
                {
                    return true; // At least some cargo is available
                }
            }

            return false; // None of the needed cargo is available
        }

        private void CancelLoading()
        {
            slot1IsLoading = false;
            slot1LoadingCargo.Clear();
            slot1LoadDestination = null;

            var sling = slingSlot1 as SLING_Thing;
            string slingName = sling != null ? SLING_Thing.GetSlingName(sling) : "SLING";
            if (sling != null)
            {
                sling.CancelLoading();
            }

            Messages.Message($"{Label}: {slingName} loading cancelled - no cargo available.", this, MessageTypeDefOf.NeutralEvent);
        }

        private void CompleteLoading()
        {
            slot1IsLoading = false;

            if (slingSlot1 == null || slot1LoadDestination == null)
            {
                slot1LoadingCargo.Clear();
                return;
            }

            // Get SLING name before despawning
            string departingSlingName = SLING_Thing.GetSlingName(slingSlot1);

            // Complete the SLING's loading process and get cargo manifest
            var sling = slingSlot1 as SLING_Thing;
            var actualCargo = new Dictionary<ThingDef, int>();
            if (sling != null)
            {
                sling.CompleteLoading();
                actualCargo = sling.GetCargoManifest();
            }

            // Consume fuel for the trip
            float fuelCost = SlingLogisticsManager.CalculateFuelCost(Map.Tile, slot1LoadDestination.Map.Tile);
            if (refuelableComp != null)
            {
                refuelableComp.ConsumeFuel(fuelCost);
            }

            // Create launching skyfaller for takeoff animation
            var launchingSkyfaller = (SlingLaunchingSkyfaller)SkyfallerMaker.MakeSkyfaller(
                ArsenalDefOf.Arsenal_SlingLaunching);
            launchingSkyfaller.sling = slingSlot1;
            launchingSkyfaller.slingName = departingSlingName;
            launchingSkyfaller.cargo = actualCargo;
            launchingSkyfaller.originPerch = this;
            launchingSkyfaller.destinationPerch = slot1LoadDestination;
            launchingSkyfaller.destinationTile = slot1LoadDestination.Map.Tile;
            launchingSkyfaller.isReturnFlight = false;

            // Despawn SLING from slot 1
            if (slingSlot1.Spawned)
            {
                slingSlot1.DeSpawn(DestroyMode.Vanish);
            }

            // Spawn the launching skyfaller at slot 1 position
            GenSpawn.Spawn(launchingSkyfaller, GetSlot1Position(), Map);

            slingSlot1 = null;
            slot1LoadDestination = null;
            slot1LoadingCargo.Clear();

            // Move idle slot2 SLING to slot1 if available
            TryMoveSlot2ToSlot1();

            Messages.Message($"{Label}: {departingSlingName} launching with {actualCargo.Values.Sum()} items", this, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>
        /// Moves an idle SLING from slot2 to slot1 if slot1 is empty.
        /// Called after slot1 becomes available (dispatch, etc.)
        /// </summary>
        private void TryMoveSlot2ToSlot1()
        {
            // Only move if slot1 is empty and slot2 has an idle SLING
            if (slingSlot1 != null) return;
            if (slingSlot2 == null) return;
            if (slot2IsUnloading || slot2IsRefueling) return;

            // Move slot2 SLING to slot1
            slingSlot1 = slingSlot2;
            slingSlot2 = null;

            // Reposition the SLING to slot1 position
            if (slingSlot1.Spawned)
            {
                slingSlot1.Position = GetSlot1Position();
            }

            Log.Message($"[PERCH] {Label}: Moved {SLING_Thing.GetSlingName(slingSlot1)} from slot 2 to slot 1");
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
            slot2RefuelTicksRemaining--;

            if (slot2RefuelTicksRemaining % 60 == 0 && Map != null)
            {
                Vector3 slot2Pos = GetSlot2Position().ToVector3Shifted();
                FleckMaker.ThrowSmoke(slot2Pos + new Vector3(Rand.Range(-0.5f, 0.5f), 0, Rand.Range(-0.5f, 0.5f)), Map, 0.5f);
            }

            if (slot2RefuelTicksRemaining <= 0)
            {
                CompleteRefueling();
            }
        }

        private void CompleteRefueling()
        {
            slot2IsRefueling = false;
            string slingName = slingSlot2 != null ? SLING_Thing.GetSlingName(slingSlot2) : "SLING";

            // After refueling, move SLING to slot 1 if available (ready for dispatch)
            TryMoveSlot2ToSlot1();

            Messages.Message($"{Label}: {slingName} refueled and ready", this, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>
        /// Adds a SLING to this PERCH (for manufacturing/initial placement).
        /// Newly assigned SLINGs go to Slot 1 (primary, ready for missions).
        /// SLINGs are rotated 90 degrees when landing/docking at PERCH.
        /// </summary>
        public void AssignSling(Thing sling)
        {
            // Prefer slot 1 for newly assigned SLINGs
            if (slingSlot1 == null)
            {
                slingSlot1 = sling;
                if (sling != null && !sling.Spawned && Map != null)
                {
                    // Spawn with north orientation to match vertical PERCH
                    GenSpawn.Spawn(sling, GetSlot1Position(), Map, Rot4.North);
                }
            }
            else if (slingSlot2 == null)
            {
                slingSlot2 = sling;
                if (sling != null && !sling.Spawned && Map != null)
                {
                    // Spawn with north orientation to match vertical PERCH
                    GenSpawn.Spawn(sling, GetSlot2Position(), Map, Rot4.North);
                }
            }
            else
            {
                Log.Warning($"[ARSENAL] {Label}: Tried to assign SLING but both slots full!");
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

            // Show Dispatch Now button when SLING in slot 1 is loading
            if (slot1IsLoading && slingSlot1 != null)
            {
                var sling = slingSlot1 as SLING_Thing;
                int cargoCount = sling?.CurrentCargoCount ?? 0;
                string slot1Name = SLING_Thing.GetSlingName(slingSlot1);

                var dispatchCmd = new Command_Action
                {
                    defaultLabel = "Dispatch Now",
                    defaultDesc = $"Launch {slot1Name} immediately with current cargo ({cargoCount} items).\nUse this to dispatch with partial loads.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchShip", false),
                    action = delegate
                    {
                        DispatchNow();
                    }
                };
                if (cargoCount == 0)
                {
                    dispatchCmd.Disable("No cargo loaded yet.");
                }
                yield return dispatchCmd;

                yield return new Command_Action
                {
                    defaultLabel = "Cancel Loading",
                    defaultDesc = "Cancel loading and keep SLING on pad.",
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", false),
                    action = delegate
                    {
                        CancelLoading();
                    }
                };
            }

            if (Prefs.DevMode)
            {
                if (slingSlot1 != null && !slot1IsLoading)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Dispatch Test",
                        action = delegate
                        {
                            SlingLogisticsManager.TryDispatchFromPerch(this);
                        }
                    };

                    // Manual loading start - for testing MULE detection
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Start Loading (All Items)",
                        defaultDesc = "Manually start loading state with all haulable items. Use this to test if MULEs detect the loading SLING.",
                        action = delegate
                        {
                            // Find all haulable items on map and create a cargo manifest
                            var cargoToLoad = new Dictionary<ThingDef, int>();
                            if (Map != null)
                            {
                                foreach (Thing t in Map.listerHaulables.ThingsPotentiallyNeedingHauling().Take(10))
                                {
                                    if (t != null && !t.Destroyed && t.Spawned && !t.IsForbidden(Faction.OfPlayer))
                                    {
                                        if (cargoToLoad.ContainsKey(t.def))
                                            cargoToLoad[t.def] += t.stackCount;
                                        else
                                            cargoToLoad[t.def] = t.stackCount;
                                    }
                                }
                            }

                            if (cargoToLoad.Count == 0)
                            {
                                Messages.Message("No haulable items found on map", this, MessageTypeDefOf.RejectInput);
                                return;
                            }

                            // Create a fake destination (self) just to enable loading state
                            var sling = slingSlot1 as SLING_Thing;
                            if (sling != null)
                            {
                                sling.StartLoading(cargoToLoad);
                                slot1IsLoading = true;
                                slot1LoadDestination = this; // Self as destination for testing
                                slot1LoadingCargo = cargoToLoad;
                                Log.Message($"[PERCH] DEV: Started loading {sling.Label} with {cargoToLoad.Count} item types");
                                Messages.Message($"DEV: {sling.Label} now loading - MULEs should detect it", this, MessageTypeDefOf.NeutralEvent);
                            }
                        }
                    };
                }

                // Diagnostic for dual slots
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Diagnose Slots",
                    defaultDesc = "Show debug info about dual slot status.",
                    action = delegate
                    {
                        string msg = $"=== {Label} Dual Slot Diagnostics ===\n";
                        msg += $"Registered PERCHes: {ArsenalNetworkManager.GetAllPerches().Count}\n";
                        msg += $"HasAvailableSlot: {HasAvailableSlot}\n";
                        msg += $"SlingCount: {SlingCount}\n\n";

                        // Slot 1 info
                        msg += $"--- SLOT 1 (Staging) ---\n";
                        msg += $"Position: {GetSlot1Position()}\n";
                        if (slingSlot1 != null)
                        {
                            msg += $"SLING: {SLING_Thing.GetSlingName(slingSlot1)}\n";
                            msg += $"IsLoading: {slot1IsLoading}\n";
                            var sling1 = slingSlot1 as SLING_Thing;
                            if (sling1 != null)
                            {
                                msg += $"  CurrentCargo: {sling1.CurrentCargoCount}\n";
                                msg += $"  RemainingCapacity: {sling1.RemainingCapacity}\n";
                            }
                        }
                        else
                        {
                            msg += "Empty\n";
                        }

                        // Slot 2 info
                        msg += $"\n--- SLOT 2 (Incoming) ---\n";
                        msg += $"Position: {GetSlot2Position()}\n";
                        if (slingSlot2 != null)
                        {
                            msg += $"SLING: {SLING_Thing.GetSlingName(slingSlot2)}\n";
                            msg += $"IsUnloading: {slot2IsUnloading}\n";
                            msg += $"IsRefueling: {slot2IsRefueling}\n";
                            if (slot2PendingReturnOrigin != null)
                                msg += $"PendingReturn: {slot2PendingReturnOrigin.Label}\n";
                        }
                        else
                        {
                            msg += "Empty\n";
                        }

                        // Available resources check
                        var available = GetAvailableResources();
                        msg += $"\nAvailable resources on map: {available.Count}\n";
                        foreach (var r in available.Take(5))
                        {
                            msg += $"  {r.Key.label}: {r.Value}\n";
                        }

                        Log.Message(msg);
                    }
                };

                // Reposition SLINGs button for fixing position issues
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Reposition SLINGs",
                    defaultDesc = "Force all SLINGs on this PERCH to their correct slot positions.",
                    action = delegate
                    {
                        RepositionSlings();
                        Messages.Message($"{Label}: SLINGs repositioned to correct slots", this, MessageTypeDefOf.NeutralEvent);
                    }
                };

                // Full SLING Loading Diagnostic
                yield return new Command_Action
                {
                    defaultLabel = "DEV: SLING Loading Trace",
                    defaultDesc = "Trace through the entire SLING loading flow to find issues.",
                    action = delegate
                    {
                        Log.Warning($"=== SLING LOADING TRACE for {Label} ===");
                        Log.Warning($"Role: {role}");
                        Log.Warning($"Powered: {IsPoweredOn}, Network: {HasNetworkConnection()}");
                        Log.Warning($"Slot1 SLING: {(slingSlot1 != null ? SLING_Thing.GetSlingName(slingSlot1) : "EMPTY")}");
                        Log.Warning($"Slot1 Busy: {Slot1Busy}, slot1IsLoading: {slot1IsLoading}");

                        if (role == PerchRole.SOURCE)
                        {
                            Log.Warning("--- SOURCE PERCH ---");
                            if (slingSlot1 == null)
                            {
                                Log.Warning("ISSUE: No SLING in slot 1 - cannot dispatch");
                            }
                            else if (slot1IsLoading)
                            {
                                Log.Warning("SLING is already loading - checking SLING state:");
                                var sling = slingSlot1 as SLING_Thing;
                                if (sling != null)
                                {
                                    Log.Warning($"  sling.IsLoading: {sling.IsLoading}");
                                    Log.Warning($"  sling.CurrentCargoCount: {sling.CurrentCargoCount}");
                                    Log.Warning($"  sling.RemainingCapacity: {sling.RemainingCapacity}");

                                    // Show what items SLING wants
                                    var haulables = Map?.listerHaulables?.ThingsPotentiallyNeedingHauling();
                                    if (haulables != null)
                                    {
                                        Log.Warning($"  Checking {haulables.Count} haulables:");
                                        int wantCount = 0;
                                        foreach (var item in haulables.Take(20))
                                        {
                                            if (sling.WantsItem(item.def))
                                            {
                                                Log.Warning($"    WANTS: {item.def.label} (stack={item.stackCount})");
                                                wantCount++;
                                            }
                                        }
                                        if (wantCount == 0)
                                        {
                                            Log.Warning("    ISSUE: SLING doesn't want any haulables!");
                                            Log.Warning("    This means targetCargo doesn't contain any available item types.");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Log.Warning("SLING NOT loading - checking why logistics manager hasn't started loading:");

                                // Check SINKs
                                var sinks = ArsenalNetworkManager.GetSinkPerches().Where(p => p.HasDemand() && p.HasNetworkConnection() && p.IsPoweredOn).ToList();
                                Log.Warning($"  SINKs with demand: {sinks.Count}");
                                foreach (var sink in sinks)
                                {
                                    var demand = sink.GetDemand();
                                    Log.Warning($"    {sink.Label}: {string.Join(", ", demand.Select(d => $"{d.Key.label}x{d.Value}"))}");
                                }

                                if (sinks.Count == 0)
                                {
                                    Log.Warning("  ISSUE: No SINKs with demand! Set threshold targets on a SINK PERCH.");
                                }

                                // Check available resources
                                var available = GetAvailableResources();
                                Log.Warning($"  Available resources at this SOURCE: {available.Count}");
                                foreach (var r in available.Take(10))
                                {
                                    Log.Warning($"    {r.Key.label}: {r.Value}");
                                }
                            }
                        }
                        else // SINK
                        {
                            Log.Warning("--- SINK PERCH ---");
                            Log.Warning($"Priority: {priority}");
                            var demand = GetDemand();
                            Log.Warning($"Demand: {demand.Count} item types");
                            foreach (var d in demand)
                            {
                                int current = GetMapStock(d.Key);
                                int target = thresholdTargets.ContainsKey(d.Key) ? thresholdTargets[d.Key] : 0;
                                Log.Warning($"  {d.Key.label}: {current}/{target} (need {d.Value})");
                            }

                            if (demand.Count == 0 && thresholdTargets.Count == 0)
                            {
                                Log.Warning("ISSUE: No threshold targets set! Use LATTICE UI to set import thresholds.");
                            }
                        }

                        // Check MULE reachability
                        var mules = ArsenalNetworkManager.GetMulesOnMap(Map).ToList();
                        Log.Warning($"MULEs on this map: {mules.Count}");
                        if (slingSlot1 != null && slot1IsLoading)
                        {
                            foreach (var mule in mules)
                            {
                                bool canReach = mule.CanReach(slingSlot1, Verse.AI.PathEndMode.Touch, Danger.Deadly);
                                Log.Warning($"  {mule.Label}: state={mule.state}, canReach SLING={canReach}");
                            }
                        }

                        Log.Warning($"=== END TRACE ===");
                    }
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

            // Role and status
            str += $"\nRole: {role}";
            if (role == PerchRole.SINK)
                str += $" (Priority {priority})";

            // Fuel
            if (refuelableComp != null)
                str += $"\nFuel: {refuelableComp.Fuel:F0} / {FuelCapacity:F0}";

            // Dual slot status
            // Slot 1 (Primary/Staging)
            str += "\nSlot 1 (Staging): ";
            if (slingSlot1 != null)
            {
                string slot1Name = SLING_Thing.GetSlingName(slingSlot1);
                str += slot1Name;
                if (slot1IsLoading)
                {
                    var sling = slingSlot1 as SLING_Thing;
                    if (sling != null)
                    {
                        int loaded = sling.CurrentCargoCount;
                        int target = slot1LoadingCargo?.Values.Sum() ?? 0;
                        str += $" <color=#ffaa00>(LOADING: {loaded}/{target})</color>";

                        // Show detailed breakdown by resource type
                        if (slot1LoadingCargo != null && slot1LoadingCargo.Count > 0)
                        {
                            foreach (var cargo in slot1LoadingCargo)
                            {
                                int loadedAmount = sling.GetLoadedAmount(cargo.Key);
                                string statusColor = loadedAmount >= cargo.Value ? "#00ff00" : "#ffaa00";
                                str += $"\n    <color={statusColor}>{cargo.Key.label}: {loadedAmount}/{cargo.Value}</color>";
                            }
                        }
                    }
                    else
                    {
                        str += " (Loading)";
                    }
                }
                else
                {
                    var sling = slingSlot1 as SLING_Thing;
                    if (sling != null && sling.CurrentCargoCount > 0)
                    {
                        str += $" (Has {sling.CurrentCargoCount} cargo)";
                    }
                    else
                    {
                        str += " (Ready)";
                    }
                }
            }
            else
            {
                str += "Empty";
            }

            // Slot 2 (Secondary/Incoming)
            str += "\nSlot 2 (Incoming): ";
            if (slingSlot2 != null)
            {
                string slot2Name = SLING_Thing.GetSlingName(slingSlot2);
                str += slot2Name;
                if (slot2IsUnloading)
                    str += $" (Unloading: {slot2UnloadTicksRemaining.ToStringTicksToPeriod()})";
                else if (slot2IsRefueling)
                    str += $" (Refueling: {slot2RefuelTicksRemaining.ToStringTicksToPeriod()})";
                else
                    str += " (Idle)";
            }
            else
            {
                str += "Empty";
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

            // Dual slot system - save both slots
            Scribe_References.Look(ref slingSlot1, "slingSlot1");
            Scribe_References.Look(ref slingSlot2, "slingSlot2");

            // Slot 1 state (primary - loading/staging)
            Scribe_Values.Look(ref slot1IsLoading, "slot1IsLoading", false);
            Scribe_References.Look(ref slot1LoadDestination, "slot1LoadDestination");
            Scribe_Collections.Look(ref slot1LoadingCargo, "slot1LoadingCargo", LookMode.Def, LookMode.Value);

            // Slot 2 state (secondary - unloading/refueling)
            Scribe_Values.Look(ref slot2IsUnloading, "slot2IsUnloading", false);
            Scribe_Values.Look(ref slot2UnloadTicksRemaining, "slot2UnloadTicksRemaining", 0);
            Scribe_Values.Look(ref slot2IsRefueling, "slot2IsRefueling", false);
            Scribe_Values.Look(ref slot2RefuelTicksRemaining, "slot2RefuelTicksRemaining", 0);
            Scribe_References.Look(ref slot2PendingReturnOrigin, "slot2PendingReturnOrigin");

            // Backwards compatibility - try loading old single-slot format
            Thing legacySlingOnPad = null;
            bool legacyIsUnloading = false;
            int legacyUnloadTicks = 0;
            bool legacyIsLoading = false;
            bool legacyIsRefueling = false;
            int legacyRefuelTicks = 0;
            Building_PERCH legacyLoadDest = null;
            Building_PERCH legacyReturnOrigin = null;
            Dictionary<ThingDef, int> legacyLoadingCargo = null;

            Scribe_References.Look(ref legacySlingOnPad, "slingOnPad");
            Scribe_Values.Look(ref legacyIsUnloading, "isUnloading", false);
            Scribe_Values.Look(ref legacyUnloadTicks, "unloadTicksRemaining", 0);
            Scribe_Values.Look(ref legacyIsLoading, "isLoading", false);
            Scribe_Values.Look(ref legacyIsRefueling, "isRefueling", false);
            Scribe_Values.Look(ref legacyRefuelTicks, "refuelTicksRemaining", 0);
            Scribe_References.Look(ref legacyLoadDest, "loadDestination");
            Scribe_References.Look(ref legacyReturnOrigin, "pendingReturnOrigin");
            Scribe_Collections.Look(ref legacyLoadingCargo, "loadingCargo", LookMode.Def, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Initialize collections if null
                if (sourceFilter == null) sourceFilter = new List<ThingDef>();
                if (thresholdTargets == null) thresholdTargets = new Dictionary<ThingDef, int>();
                if (slot1LoadingCargo == null) slot1LoadingCargo = new Dictionary<ThingDef, int>();

                // Migrate legacy single-slot data to dual-slot system
                if (legacySlingOnPad != null && slingSlot1 == null && slingSlot2 == null)
                {
                    // Put legacy SLING in slot 1
                    slingSlot1 = legacySlingOnPad;
                }
                if (legacyIsLoading && !slot1IsLoading)
                {
                    slot1IsLoading = true;
                    slot1LoadDestination = legacyLoadDest;
                    if (legacyLoadingCargo != null)
                        slot1LoadingCargo = legacyLoadingCargo;
                }
                if (legacyIsUnloading && !slot2IsUnloading)
                {
                    slot2IsUnloading = true;
                    slot2UnloadTicksRemaining = legacyUnloadTicks;
                    slot2PendingReturnOrigin = legacyReturnOrigin;
                }
                if (legacyIsRefueling && !slot2IsRefueling)
                {
                    slot2IsRefueling = true;
                    slot2RefuelTicksRemaining = legacyRefuelTicks;
                }
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
