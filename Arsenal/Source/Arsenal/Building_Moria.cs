using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// MORIA (Materials Orchestration and Resource Integration Archive) -
    /// Climate-controlled storage for raw ores, refined materials, and components.
    /// MULEs deliver resources here from mining and hauling tasks.
    /// </summary>
    public class Building_Moria : Building, IStoreSettingsParent
    {
        // Storage
        private Dictionary<ThingDef, int> storedItems = new Dictionary<ThingDef, int>();
        public const int MAX_STACK_PER_TYPE = 10000; // Max per item type
        public const int MAX_UNIQUE_TYPES = 50; // Max different item types

        // Power
        private CompPowerTrader powerComp;

        // Storage settings
        private StorageSettings storageSettings;

        // Naming
        private string customName;
        private static int moriaCounter = 1;

        // Visual
        private int lastVisualUpdateTick;
        private const int VISUAL_UPDATE_INTERVAL = 60;

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

            // Check capacity
            int currentCount = GetStoredCount(item.def);
            if (currentCount + item.stackCount > MAX_STACK_PER_TYPE) return false;

            // Check unique types limit
            if (!storedItems.ContainsKey(item.def) && UniqueTypesCount >= MAX_UNIQUE_TYPES) return false;

            return true;
        }

        /// <summary>
        /// Tries to accept an item into storage.
        /// </summary>
        public bool TryAcceptItem(Thing item)
        {
            if (!CanAcceptItem(item)) return false;

            int toStore = Mathf.Min(item.stackCount, MAX_STACK_PER_TYPE - GetStoredCount(item.def));

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
        /// Retrieves items from storage.
        /// </summary>
        public Thing RetrieveItem(ThingDef def, int desiredCount)
        {
            if (!storedItems.ContainsKey(def) || storedItems[def] <= 0)
                return null;

            int actualCount = Mathf.Min(desiredCount, storedItems[def]);

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
                // Put it back
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
                while (kvp.Value > 0)
                {
                    int stackSize = Mathf.Min(kvp.Key.stackLimit, kvp.Value);
                    storedItems[kvp.Key] -= stackSize;

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

                    if (storedItems[kvp.Key] <= 0)
                        break;
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
            if (!storageSettings.AllowedToAccept(def)) return false;
            return GetStoredCount(def) < MAX_STACK_PER_TYPE;
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string text = base.GetInspectString();

            if (!text.NullOrEmpty()) text += "\n";

            text += $"Stored: {TotalStoredCount:N0} items ({UniqueTypesCount} types)";

            if (!IsPoweredOn())
            {
                text += "\n<color=#ff6666>No power - cannot accept items</color>";
            }

            if (!HasNetworkConnection())
            {
                text += "\n<color=#ffaa00>No network connection</color>";
            }

            // Show top 3 stored items
            var topItems = GetStoredItemsSorted().Take(3).ToList();
            if (topItems.Count > 0)
            {
                text += "\nContains:";
                foreach (var item in topItems)
                {
                    text += $"\n  - {item.Key.label}: {item.Value:N0}";
                }
                if (UniqueTypesCount > 3)
                {
                    text += $"\n  ... and {UniqueTypesCount - 3} more types";
                }
            }

            return text;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // Storage settings
            foreach (Gizmo g in StorageSettingsClipboard.CopyPasteGizmosFor(storageSettings))
                yield return g;

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

            yield return new Command_Action
            {
                defaultLabel = "Inventory",
                defaultDesc = "View stored items.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/ResourceReadout", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_MoriaInventory(this));
                }
            };

            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Add Steel x100",
                    action = delegate
                    {
                        Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                        steel.stackCount = 100;
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

        #region Naming

        public void SetCustomName(string name)
        {
            customName = name;
        }

        #endregion
    }

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
    /// Dialog for viewing MORIA inventory.
    /// </summary>
    public class Dialog_MoriaInventory : Window
    {
        private Building_Moria moria;
        private Vector2 scrollPosition;

        public Dialog_MoriaInventory(Building_Moria m)
        {
            moria = m;
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 500f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), $"{moria.Label} Inventory");
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0, 35, inRect.width, 20),
                $"Total: {moria.TotalStoredCount:N0} items | Types: {moria.UniqueTypesCount}");

            Rect scrollRect = new Rect(0, 60, inRect.width, inRect.height - 70);
            var items = moria.GetStoredItemsSorted().ToList();

            float viewHeight = items.Count * 30f;
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20, viewHeight);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);

            float y = 0;
            foreach (var kvp in items)
            {
                Rect row = new Rect(0, y, viewRect.width, 28);

                // Icon
                Widgets.ThingIcon(new Rect(row.x, row.y, 28, 28), kvp.Key);

                // Label
                Widgets.Label(new Rect(row.x + 32, row.y + 4, row.width - 150, 24), kvp.Key.label);

                // Count
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(row.width - 100, row.y + 4, 95, 24), kvp.Value.ToString("N0"));
                Text.Anchor = TextAnchor.UpperLeft;

                y += 30;
            }

            Widgets.EndScrollView();

            if (Widgets.ButtonText(new Rect(inRect.width - 80, inRect.height - 35, 75, 30), "Close"))
            {
                Close();
            }
        }
    }
}
