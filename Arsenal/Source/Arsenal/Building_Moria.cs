using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// MORIA (Materials Orchestration and Resource Integration Archive) -
    /// Climate-controlled storage vault for raw materials and components.
    /// Items are stored internally (not visible) with large capacity.
    /// MULEs deliver resources here, colonists can retrieve via interface.
    /// </summary>
    public class Building_Moria : Building, IStoreSettingsParent
    {
        // Internal storage - items stored inside, not on map
        private Dictionary<ThingDef, int> storedItems = new Dictionary<ThingDef, int>();

        // Capacity settings
        public const int MAX_STACK_PER_TYPE = 10000;  // Max per item type
        public const int MAX_UNIQUE_TYPES = 100;      // Max different item types
        public const int MAX_TOTAL_ITEMS = 100000;    // Max total items across all types

        // Power
        private CompPowerTrader powerComp;

        // Storage settings (for filter)
        private StorageSettings storageSettings;

        // Climate control
        private float targetTemperature = -10f;  // Default freezing temp
        private bool climateControlEnabled = true;

        // Naming
        private string customName;
        private static int moriaCounter = 1;

        #region Properties

        public bool IsPoweredOn()
        {
            return powerComp == null || powerComp.PowerOn;
        }

        public bool HasNetworkConnection()
        {
            if (Map == null) return false;
            return ArsenalNetworkManager.IsTileConnected(Map.Tile);
        }

        public override string Label => customName ?? base.Label;

        public int TotalStoredCount => storedItems.Values.Sum();
        public int UniqueTypesCount => storedItems.Count;
        public bool StorageTabVisible => true;

        /// <summary>
        /// Gets the effective temperature for stored items.
        /// When powered with climate control, maintains target temp.
        /// </summary>
        public float EffectiveTemperature
        {
            get
            {
                if (IsPoweredOn() && climateControlEnabled)
                {
                    return targetTemperature;
                }
                // When unpowered, use ambient temperature
                return Map?.mapTemperature?.OutdoorTemp ?? 21f;
            }
        }

        public bool IsFreezing => EffectiveTemperature < 0f;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            powerComp = GetComp<CompPowerTrader>();

            // Initialize storage settings
            if (storageSettings == null)
            {
                storageSettings = new StorageSettings(this);
                if (def.building.defaultStorageSettings != null)
                {
                    storageSettings.CopyFrom(def.building.defaultStorageSettings);
                }
            }

            ArsenalNetworkManager.RegisterMoria(this);

            if (!respawningAfterLoad)
            {
                customName = "MORIA-" + moriaCounter.ToString("D2");
                moriaCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Drop all stored items when destroyed
            if (mode == DestroyMode.Deconstruct || mode == DestroyMode.KillFinalize)
            {
                EjectAllItems();
            }

            ArsenalNetworkManager.DeregisterMoria(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref targetTemperature, "targetTemperature", -10f);
            Scribe_Values.Look(ref climateControlEnabled, "climateControlEnabled", true);
            Scribe_Deep.Look(ref storageSettings, "storageSettings", this);
            Scribe_Collections.Look(ref storedItems, "storedItems", LookMode.Def, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                storedItems = storedItems ?? new Dictionary<ThingDef, int>();
            }
        }

        #endregion

        #region IStoreSettingsParent

        public StorageSettings GetStoreSettings()
        {
            return storageSettings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            return def.building.fixedStorageSettings;
        }

        public void Notify_SettingsChanged()
        {
            // Storage settings changed
        }

        #endregion

        #region Storage Operations

        /// <summary>
        /// Checks if this MORIA can accept the given item.
        /// </summary>
        public bool CanAcceptItem(Thing item)
        {
            if (item == null || item.Destroyed) return false;
            if (!IsPoweredOn()) return false;

            // Check storage filter
            if (!storageSettings.AllowedToAccept(item)) return false;

            // Check per-type capacity
            int currentCount = GetStoredCount(item.def);
            if (currentCount >= MAX_STACK_PER_TYPE) return false;

            // Check unique types limit
            if (!storedItems.ContainsKey(item.def) && UniqueTypesCount >= MAX_UNIQUE_TYPES) return false;

            // Check total capacity
            if (TotalStoredCount + item.stackCount > MAX_TOTAL_ITEMS) return false;

            return true;
        }

        /// <summary>
        /// Tries to accept an item into storage.
        /// </summary>
        public bool TryAcceptItem(Thing item)
        {
            if (!CanAcceptItem(item)) return false;

            int spaceAvailable = Mathf.Min(
                MAX_STACK_PER_TYPE - GetStoredCount(item.def),
                MAX_TOTAL_ITEMS - TotalStoredCount
            );
            int toStore = Mathf.Min(item.stackCount, spaceAvailable);

            if (toStore <= 0) return false;

            if (storedItems.ContainsKey(item.def))
            {
                storedItems[item.def] += toStore;
            }
            else
            {
                storedItems[item.def] = toStore;
            }

            // Consume the item
            if (toStore >= item.stackCount)
            {
                item.Destroy();
            }
            else
            {
                item.stackCount -= toStore;
            }

            return true;
        }

        /// <summary>
        /// Gets the count of a specific item type in storage.
        /// </summary>
        public int GetStoredCount(ThingDef def)
        {
            return storedItems.TryGetValue(def, out int count) ? count : 0;
        }

        /// <summary>
        /// Retrieves items from storage (returns a Thing, removes from storage).
        /// </summary>
        public Thing RetrieveItem(ThingDef def, int desiredCount)
        {
            if (!storedItems.ContainsKey(def) || storedItems[def] <= 0)
                return null;

            int actualCount = Mathf.Min(desiredCount, storedItems[def]);
            actualCount = Mathf.Min(actualCount, def.stackLimit); // Cap at stack limit

            storedItems[def] -= actualCount;
            if (storedItems[def] <= 0)
            {
                storedItems.Remove(def);
            }

            Thing item = ThingMaker.MakeThing(def);
            item.stackCount = actualCount;

            return item;
        }

        /// <summary>
        /// Spawns a retrieved item near the MORIA.
        /// </summary>
        public Thing SpawnRetrievedItem(ThingDef def, int desiredCount)
        {
            Thing item = RetrieveItem(def, desiredCount);
            if (item == null) return null;

            IntVec3 spawnCell = GetOutputCell();
            if (spawnCell.IsValid)
            {
                GenSpawn.Spawn(item, spawnCell, Map);
                return item;
            }
            else
            {
                // Can't spawn - put it back
                if (storedItems.ContainsKey(def))
                    storedItems[def] += item.stackCount;
                else
                    storedItems[def] = item.stackCount;

                return null;
            }
        }

        /// <summary>
        /// Ejects all stored items when destroyed.
        /// </summary>
        private void EjectAllItems()
        {
            if (Map == null) return;

            foreach (var kvp in storedItems.ToList())
            {
                int remaining = kvp.Value;
                while (remaining > 0)
                {
                    int stackSize = Mathf.Min(kvp.Key.stackLimit, remaining);
                    remaining -= stackSize;

                    Thing item = ThingMaker.MakeThing(kvp.Key);
                    item.stackCount = stackSize;

                    IntVec3 cell = GetOutputCell();
                    if (cell.IsValid)
                    {
                        GenPlace.TryPlaceThing(item, cell, Map, ThingPlaceMode.Near);
                    }
                    else
                    {
                        GenPlace.TryPlaceThing(item, Position, Map, ThingPlaceMode.Near);
                    }
                }
            }

            storedItems.Clear();
        }

        private IntVec3 GetOutputCell()
        {
            // Find a valid cell adjacent to the MORIA
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (cell.InBounds(Map) && cell.Walkable(Map) && !cell.Fogged(Map))
                {
                    return cell;
                }
            }

            return IntVec3.Invalid;
        }

        /// <summary>
        /// Gets a cell for MULE to target when delivering (interaction spot).
        /// </summary>
        public IntVec3 GetStorageCell(Thing item)
        {
            if (!CanAcceptItem(item)) return IntVec3.Invalid;
            return GetOutputCell(); // MULEs drop items at output cell, then we absorb them
        }

        /// <summary>
        /// Gets all stored item types for display.
        /// </summary>
        public IEnumerable<KeyValuePair<ThingDef, int>> GetStoredItemsSorted()
        {
            return storedItems.OrderByDescending(kvp => kvp.Value);
        }

        /// <summary>
        /// Checks if this MORIA needs a specific resource.
        /// Used by LATTICE for task prioritization.
        /// </summary>
        public bool NeedsResource(ThingDef def)
        {
            if (!IsPoweredOn()) return false;
            if (!storageSettings.AllowedToAccept(def)) return false;
            if (GetStoredCount(def) >= MAX_STACK_PER_TYPE) return false;
            if (TotalStoredCount >= MAX_TOTAL_ITEMS) return false;
            return true;
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty()) text += "\n";

            text += $"Stored: {TotalStoredCount:N0} / {MAX_TOTAL_ITEMS:N0} items ({UniqueTypesCount} types)";

            if (climateControlEnabled && IsPoweredOn())
            {
                text += $"\nClimate: {EffectiveTemperature:F0}°C";
                if (IsFreezing)
                {
                    text += " (Freezing)";
                }
            }

            if (!IsPoweredOn())
            {
                text += "\n<color=#ff6666>No power - cannot accept items, climate control offline</color>";
            }

            if (!HasNetworkConnection())
            {
                text += "\n<color=#ffaa00>No network connection</color>";
            }

            // Show top 5 stored items
            var topItems = GetStoredItemsSorted().Take(5).ToList();
            if (topItems.Count > 0)
            {
                text += "\nContents:";
                foreach (var item in topItems)
                {
                    text += $"\n  • {item.Key.label}: {item.Value:N0}";
                }
                if (UniqueTypesCount > 5)
                {
                    text += $"\n  ... and {UniqueTypesCount - 5} more types";
                }
            }

            return text;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // Storage filter configuration
            yield return new Command_Action
            {
                defaultLabel = "Storage Filter",
                defaultDesc = "Configure which items this MORIA will accept.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_MoriaStorageFilter(this, storageSettings));
                }
            };

            // Retrieve items
            yield return new Command_Action
            {
                defaultLabel = "Retrieve Items",
                defaultDesc = "Retrieve items from storage.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/ResourceReadout", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_MoriaRetrieve(this));
                }
            };

            // Climate control toggle
            yield return new Command_Toggle
            {
                defaultLabel = "Climate Control",
                defaultDesc = $"Toggle climate control. Currently set to {targetTemperature:F0}°C.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/DesirePower", false),
                isActive = () => climateControlEnabled,
                toggleAction = delegate { climateControlEnabled = !climateControlEnabled; }
            };

            // Temperature setting
            yield return new Command_Action
            {
                defaultLabel = $"Temp: {targetTemperature:F0}°C",
                defaultDesc = "Set target temperature for climate control.",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/TempLower", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_MoriaTemperature(this));
                }
            };

            // Rename
            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this MORIA.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameMoria(this));
                }
            };

            // Copy/paste storage settings
            foreach (Gizmo g in StorageSettingsClipboard.CopyPasteGizmosFor(storageSettings))
                yield return g;

            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Add Steel x1000",
                    action = delegate
                    {
                        Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                        steel.stackCount = 1000;
                        TryAcceptItem(steel);
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = "DEV: Eject All",
                    action = delegate
                    {
                        EjectAllItems();
                    }
                };
            }
        }

        #endregion

        #region Naming & Temperature

        public void SetCustomName(string name)
        {
            customName = name;
        }

        public void SetTargetTemperature(float temp)
        {
            targetTemperature = Mathf.Clamp(temp, -40f, 40f);
        }

        public float GetTargetTemperature()
        {
            return targetTemperature;
        }

        #endregion
    }

    #region Dialogs

    /// <summary>
    /// Dialog for renaming a MORIA.
    /// </summary>
    public class Dialog_RenameMoria : Window
    {
        private Building_Moria moria;
        private string newName;

        public Dialog_RenameMoria(Building_Moria m)
        {
            moria = m;
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
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename MORIA");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                moria.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Dialog for configuring MORIA storage filter.
    /// </summary>
    public class Dialog_MoriaStorageFilter : Window
    {
        private Building_Moria moria;
        private StorageSettings settings;
        private ThingFilterUI.UIState filterState = new ThingFilterUI.UIState();

        public Dialog_MoriaStorageFilter(Building_Moria m, StorageSettings s)
        {
            moria = m;
            settings = s;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 600f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), $"{moria.Label} - Storage Filter");
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0, 35, inRect.width, 20),
                "Select which items this MORIA will accept:");

            Rect filterRect = new Rect(0, 60, inRect.width, inRect.height - 110);

            ThingFilter parentFilter = moria.def.building?.fixedStorageSettings?.filter;
            ThingFilterUI.DoThingFilterConfigWindow(filterRect, filterState, settings.filter, parentFilter);

            if (Widgets.ButtonText(new Rect(inRect.width / 2 - 60, inRect.height - 40, 120, 35), "Close"))
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Dialog for retrieving items from MORIA.
    /// </summary>
    public class Dialog_MoriaRetrieve : Window
    {
        private Building_Moria moria;
        private Vector2 scrollPosition;
        private Dictionary<ThingDef, int> retrieveAmounts = new Dictionary<ThingDef, int>();

        public Dialog_MoriaRetrieve(Building_Moria m)
        {
            moria = m;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(450f, 550f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), $"{moria.Label} - Retrieve Items");
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0, 35, inRect.width, 20),
                $"Total stored: {moria.TotalStoredCount:N0} items");

            Rect scrollRect = new Rect(0, 60, inRect.width, inRect.height - 120);
            var items = moria.GetStoredItemsSorted().ToList();

            float viewHeight = items.Count * 35f;
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20, viewHeight);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);

            float y = 0;
            foreach (var kvp in items)
            {
                Rect row = new Rect(0, y, viewRect.width, 32);

                // Icon
                Widgets.ThingIcon(new Rect(row.x, row.y + 2, 28, 28), kvp.Key);

                // Label
                Widgets.Label(new Rect(row.x + 32, row.y + 6, 150, 24), kvp.Key.label.Truncate(140));

                // Count
                Widgets.Label(new Rect(row.x + 185, row.y + 6, 60, 24), $"({kvp.Value:N0})");

                // Amount to retrieve
                if (!retrieveAmounts.ContainsKey(kvp.Key))
                {
                    retrieveAmounts[kvp.Key] = 0;
                }

                string amountStr = retrieveAmounts[kvp.Key].ToString();
                amountStr = Widgets.TextField(new Rect(row.x + 250, row.y + 2, 60, 28), amountStr);
                if (int.TryParse(amountStr, out int amount))
                {
                    retrieveAmounts[kvp.Key] = Mathf.Clamp(amount, 0, kvp.Value);
                }

                // Quick buttons
                if (Widgets.ButtonText(new Rect(row.x + 315, row.y + 2, 40, 28), "All"))
                {
                    retrieveAmounts[kvp.Key] = kvp.Value;
                }
                if (Widgets.ButtonText(new Rect(row.x + 360, row.y + 2, 50, 28), "Stack"))
                {
                    retrieveAmounts[kvp.Key] = Mathf.Min(kvp.Key.stackLimit, kvp.Value);
                }

                y += 35;
            }

            Widgets.EndScrollView();

            // Retrieve button
            if (Widgets.ButtonText(new Rect(inRect.width / 2 - 100, inRect.height - 45, 90, 35), "Retrieve"))
            {
                foreach (var kvp in retrieveAmounts.ToList())
                {
                    if (kvp.Value > 0)
                    {
                        int remaining = kvp.Value;
                        while (remaining > 0)
                        {
                            int toSpawn = Mathf.Min(remaining, kvp.Key.stackLimit);
                            Thing spawned = moria.SpawnRetrievedItem(kvp.Key, toSpawn);
                            if (spawned == null) break;
                            remaining -= spawned.stackCount;
                        }
                        retrieveAmounts[kvp.Key] = 0;
                    }
                }
            }

            if (Widgets.ButtonText(new Rect(inRect.width / 2 + 10, inRect.height - 45, 90, 35), "Close"))
            {
                Close();
            }
        }
    }

    /// <summary>
    /// Dialog for setting MORIA temperature.
    /// </summary>
    public class Dialog_MoriaTemperature : Window
    {
        private Building_Moria moria;
        private float tempValue;

        public Dialog_MoriaTemperature(Building_Moria m)
        {
            moria = m;
            tempValue = m.GetTargetTemperature();
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(350f, 180f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Set Target Temperature");
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0, 40, inRect.width, 25), $"Temperature: {tempValue:F0}°C");

            tempValue = Widgets.HorizontalSlider(
                new Rect(0, 70, inRect.width, 30),
                tempValue,
                -40f,
                40f,
                true,
                $"{tempValue:F0}°C",
                "-40°C",
                "40°C",
                1f
            );

            // Quick presets
            if (Widgets.ButtonText(new Rect(0, 110, 80, 28), "Freeze"))
            {
                tempValue = -18f;
            }
            if (Widgets.ButtonText(new Rect(90, 110, 80, 28), "Cool"))
            {
                tempValue = 5f;
            }
            if (Widgets.ButtonText(new Rect(180, 110, 80, 28), "Room"))
            {
                tempValue = 21f;
            }

            if (Widgets.ButtonText(new Rect(inRect.width / 2 - 50, inRect.height - 35, 100, 30), "Apply"))
            {
                moria.SetTargetTemperature(tempValue);
                Close();
            }
        }
    }

    #endregion
}
