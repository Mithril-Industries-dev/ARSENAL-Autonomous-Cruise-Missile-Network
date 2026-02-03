using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// PERCH Landing Beacon - marks the corner of a landing zone for SLING cargo craft.
    /// Four beacons placed at corners define a landing pad (minimum 9x12 for SLING size 6x10).
    /// Similar to vanilla ship landing beacons but for SLING logistics.
    /// </summary>
    public class Building_PerchBeacon : Building
    {
        // Naming
        private string customName;
        private static int beaconCounter = 1;

        // Logistics role
        public enum PerchRole { Source, Sink }
        private PerchRole role = PerchRole.Source;

        // Source configuration (what to export)
        private List<ThingDef> sourceFilter = new List<ThingDef>();
        private Dictionary<ThingDef, int> thresholdTargets = new Dictionary<ThingDef, int>();

        // Landing zone constants
        public const int MIN_WIDTH = 9;   // Minimum landing zone width (SLING is 6 wide + margin)
        public const int MIN_HEIGHT = 12; // Minimum landing zone height (SLING is 10 tall + margin)
        public const int MAX_BEACON_DISTANCE = 30; // Max distance to search for other beacons

        // Components
        private CompPowerTrader powerComp;

        // Cached landing zone (updated periodically)
        private CellRect? cachedLandingZone;
        private int lastZoneCheck = -999;
        private const int ZONE_CHECK_INTERVAL = 120;

        #region Properties

        public override string Label => !string.IsNullOrEmpty(customName) ? customName : base.Label;
        public PerchRole Role => role;
        public bool IsSource => role == PerchRole.Source;
        public bool IsSink => role == PerchRole.Sink;
        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;
        public List<ThingDef> SourceFilter => sourceFilter;
        public Dictionary<ThingDef, int> ThresholdTargets => thresholdTargets;

        /// <summary>
        /// Returns true if this beacon is part of a valid 4-beacon landing zone.
        /// </summary>
        public bool HasValidLandingZone => GetLandingZone().HasValue;

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();

            ArsenalNetworkManager.RegisterPerchBeacon(this);

            if (!respawningAfterLoad)
            {
                customName = "PERCH-" + beaconCounter.ToString("D2");
                beaconCounter++;
            }

            // Invalidate cache
            cachedLandingZone = null;
            lastZoneCheck = -999;
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ArsenalNetworkManager.DeregisterPerchBeacon(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Values.Look(ref role, "role", PerchRole.Source);
            Scribe_Collections.Look(ref sourceFilter, "sourceFilter", LookMode.Def);
            Scribe_Collections.Look(ref thresholdTargets, "thresholdTargets", LookMode.Def, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (sourceFilter == null) sourceFilter = new List<ThingDef>();
                if (thresholdTargets == null) thresholdTargets = new Dictionary<ThingDef, int>();
            }
        }

        #endregion

        #region Landing Zone Detection

        /// <summary>
        /// Gets the landing zone defined by this beacon and 3 others forming a rectangle.
        /// Returns null if no valid zone can be formed.
        /// </summary>
        public CellRect? GetLandingZone()
        {
            // Use cache if recent
            if (Find.TickManager.TicksGame - lastZoneCheck < ZONE_CHECK_INTERVAL && cachedLandingZone.HasValue)
            {
                return cachedLandingZone;
            }

            cachedLandingZone = CalculateLandingZone();
            lastZoneCheck = Find.TickManager.TicksGame;
            return cachedLandingZone;
        }

        private CellRect? CalculateLandingZone()
        {
            if (Map == null) return null;

            // Find all other powered beacons on this map
            var otherBeacons = ArsenalNetworkManager.GetPerchBeaconsOnMap(Map)
                .Where(b => b != this && b.IsPoweredOn)
                .ToList();

            if (otherBeacons.Count < 3) return null;

            // Try to find 3 other beacons that form a valid rectangle with this one
            foreach (var b1 in otherBeacons)
            {
                foreach (var b2 in otherBeacons.Where(b => b != b1))
                {
                    foreach (var b3 in otherBeacons.Where(b => b != b1 && b != b2))
                    {
                        var zone = TryFormRectangle(this, b1, b2, b3);
                        if (zone.HasValue)
                        {
                            return zone;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Tries to form a valid landing rectangle from 4 beacons.
        /// Returns the CellRect if valid, null otherwise.
        /// </summary>
        private CellRect? TryFormRectangle(Building_PerchBeacon a, Building_PerchBeacon b, Building_PerchBeacon c, Building_PerchBeacon d)
        {
            // Get all 4 positions
            var positions = new List<IntVec3> { a.Position, b.Position, c.Position, d.Position };

            // Find min/max to form rectangle
            int minX = positions.Min(p => p.x);
            int maxX = positions.Max(p => p.x);
            int minZ = positions.Min(p => p.z);
            int maxZ = positions.Max(p => p.z);

            // Check that all 4 beacons are at corners
            var corners = new HashSet<IntVec3>
            {
                new IntVec3(minX, 0, minZ),
                new IntVec3(minX, 0, maxZ),
                new IntVec3(maxX, 0, minZ),
                new IntVec3(maxX, 0, maxZ)
            };

            foreach (var pos in positions)
            {
                if (!corners.Contains(new IntVec3(pos.x, 0, pos.z)))
                {
                    return null; // Not at a corner
                }
            }

            // Check minimum size
            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;

            if (width < MIN_WIDTH || height < MIN_HEIGHT)
            {
                return null; // Too small
            }

            // Create the landing zone (inside the beacons, not including beacon cells)
            CellRect zone = new CellRect(minX + 1, minZ + 1, width - 2, height - 2);
            return zone;
        }

        /// <summary>
        /// Finds a valid landing cell for a SLING within the landing zone.
        /// Returns IntVec3.Invalid if no valid spot found.
        /// </summary>
        public IntVec3 FindLandingSpot(int slingWidth = 6, int slingHeight = 10)
        {
            var zone = GetLandingZone();
            if (!zone.HasValue || Map == null) return IntVec3.Invalid;

            CellRect landingZone = zone.Value;

            // Try to find a clear spot within the zone
            // Start from center and work outward
            IntVec3 center = landingZone.CenterCell;

            // Check center first
            if (IsValidLandingSpot(center, slingWidth, slingHeight, landingZone))
            {
                return center;
            }

            // Spiral outward from center
            for (int radius = 1; radius <= Mathf.Max(landingZone.Width, landingZone.Height); radius++)
            {
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
                {
                    if (landingZone.Contains(cell) && IsValidLandingSpot(cell, slingWidth, slingHeight, landingZone))
                    {
                        return cell;
                    }
                }
            }

            return IntVec3.Invalid;
        }

        /// <summary>
        /// Checks if a cell is valid for landing a SLING of given size.
        /// </summary>
        private bool IsValidLandingSpot(IntVec3 cell, int width, int height, CellRect zone)
        {
            // Check all cells the SLING would occupy
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < height; z++)
                {
                    IntVec3 checkCell = cell + new IntVec3(x, 0, z);

                    if (!checkCell.InBounds(Map)) return false;
                    if (!zone.Contains(checkCell)) return false;
                    if (!checkCell.Standable(Map)) return false;
                    if (checkCell.Roofed(Map)) return false;

                    // Check for blocking things
                    foreach (Thing t in checkCell.GetThingList(Map))
                    {
                        // Allow beacons
                        if (t is Building_PerchBeacon) continue;
                        // Block on other buildings
                        if (t is Building) return false;
                        // Block on existing SLINGs
                        if (t is SLING_Thing) return false;
                    }
                }
            }

            return true;
        }

        #endregion

        #region SLING Tracking

        /// <summary>
        /// Gets all SLINGs currently within this beacon's landing zone.
        /// </summary>
        public List<SLING_Thing> GetDockedSlings()
        {
            var slings = new List<SLING_Thing>();
            var zone = GetLandingZone();
            if (!zone.HasValue || Map == null) return slings;

            foreach (IntVec3 cell in zone.Value)
            {
                if (!cell.InBounds(Map)) continue;
                foreach (Thing t in cell.GetThingList(Map))
                {
                    if (t is SLING_Thing sling && !slings.Contains(sling))
                    {
                        slings.Add(sling);
                    }
                }
            }

            return slings;
        }

        /// <summary>
        /// Gets idle SLINGs (not loading) within the landing zone.
        /// </summary>
        public List<SLING_Thing> GetIdleSlings()
        {
            return GetDockedSlings().Where(s => !s.IsLoading).ToList();
        }

        /// <summary>
        /// Checks if this beacon's zone has any idle SLINGs available.
        /// </summary>
        public bool HasAvailableSling => GetIdleSlings().Any();

        #endregion

        #region Source/Sink Configuration

        public void SetRole(PerchRole newRole)
        {
            role = newRole;
        }

        public void ToggleRole()
        {
            role = role == PerchRole.Source ? PerchRole.Sink : PerchRole.Source;
        }

        public void AddToSourceFilter(ThingDef def)
        {
            if (!sourceFilter.Contains(def))
                sourceFilter.Add(def);
        }

        public void RemoveFromSourceFilter(ThingDef def)
        {
            sourceFilter.Remove(def);
        }

        public void SetThreshold(ThingDef def, int amount)
        {
            if (amount <= 0)
                thresholdTargets.Remove(def);
            else
                thresholdTargets[def] = amount;
        }

        public int GetThreshold(ThingDef def)
        {
            return thresholdTargets.TryGetValue(def, out int val) ? val : 0;
        }

        /// <summary>
        /// Gets resources available for export (above threshold).
        /// </summary>
        public Dictionary<ThingDef, int> GetAvailableForExport()
        {
            var available = new Dictionary<ThingDef, int>();
            if (Map == null || !IsSource) return available;

            foreach (ThingDef def in sourceFilter)
            {
                int onMap = Map.resourceCounter.GetCount(def);
                int threshold = GetThreshold(def);
                int exportable = onMap - threshold;

                if (exportable > 0)
                {
                    available[def] = exportable;
                }
            }

            return available;
        }

        /// <summary>
        /// Gets resources needed (below threshold) for sinks.
        /// </summary>
        public Dictionary<ThingDef, int> GetResourcesNeeded()
        {
            var needed = new Dictionary<ThingDef, int>();
            if (Map == null || !IsSink) return needed;

            foreach (var kvp in thresholdTargets)
            {
                int onMap = Map.resourceCounter.GetCount(kvp.Key);
                int deficit = kvp.Value - onMap;

                if (deficit > 0)
                {
                    needed[kvp.Key] = deficit;
                }
            }

            return needed;
        }

        #endregion

        #region Drawing

        public override void Draw()
        {
            base.Draw();

            // Draw landing zone outline when selected
            if (Find.Selector.IsSelected(this))
            {
                var zone = GetLandingZone();
                if (zone.HasValue)
                {
                    GenDraw.DrawFieldEdges(zone.Value.Cells.ToList(), Color.cyan);
                }
            }
        }

        #endregion

        #region UI

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!str.NullOrEmpty()) str += "\n";

            var zone = GetLandingZone();
            if (zone.HasValue)
            {
                str += $"Landing zone: {zone.Value.Width}x{zone.Value.Height}";
            }
            else
            {
                str += "No valid landing zone (need 4 beacons at corners)";
            }

            str += $"\nRole: {role}";
            str += $"\nPowered: {(IsPoweredOn ? "Yes" : "No")}";

            if (HasValidLandingZone)
            {
                var slings = GetDockedSlings();
                str += $"\nSLINGs in zone: {slings.Count}";
            }

            if (IsSource)
            {
                var available = GetAvailableForExport();
                if (available.Any())
                {
                    str += "\nExportable: " + string.Join(", ", available.Take(3).Select(a => $"{a.Key.label} x{a.Value}"));
                    if (available.Count > 3) str += "...";
                }
            }
            else
            {
                var needed = GetResourcesNeeded();
                if (needed.Any())
                {
                    str += "\nNeeded: " + string.Join(", ", needed.Take(3).Select(n => $"{n.Key.label} x{n.Value}"));
                    if (needed.Count > 3) str += "...";
                }
            }

            return str;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // Rename
            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this beacon.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameBeacon(this));
                }
            };

            // Toggle Role
            yield return new Command_Action
            {
                defaultLabel = $"Role: {role}",
                defaultDesc = "Toggle between SOURCE (exports resources) and SINK (imports resources).",
                icon = ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate", false),
                action = delegate
                {
                    ToggleRole();
                    Messages.Message($"{Label} is now a {role}", this, MessageTypeDefOf.NeutralEvent);
                }
            };

            // Configure filter (for sources)
            if (IsSource)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Configure Export",
                    defaultDesc = "Set which resources to export and threshold amounts.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", false),
                    action = delegate
                    {
                        Find.WindowStack.Add(new Dialog_ConfigureBeaconExport(this));
                    }
                };
            }

            // Configure thresholds (for sinks)
            if (IsSink)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Configure Import",
                    defaultDesc = "Set target resource levels for this location.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", false),
                    action = delegate
                    {
                        Find.WindowStack.Add(new Dialog_ConfigureBeaconImport(this));
                    }
                };
            }

            // Dev gizmos
            if (Prefs.DevMode)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Test Landing",
                    action = delegate
                    {
                        IntVec3 spot = FindLandingSpot();
                        if (spot.IsValid)
                        {
                            Log.Message($"[PERCH BEACON] Found landing spot at {spot}");
                            MoteMaker.ThrowText(spot.ToVector3Shifted(), Map, "LAND", Color.green);
                        }
                        else
                        {
                            Log.Warning($"[PERCH BEACON] No valid landing spot!");
                            Messages.Message("No valid landing spot in zone!", this, MessageTypeDefOf.RejectInput);
                        }
                    }
                };

                yield return new Command_Action
                {
                    defaultLabel = $"DEV: Zone Info",
                    action = delegate
                    {
                        var zone = GetLandingZone();
                        if (zone.HasValue)
                        {
                            Log.Message($"[PERCH BEACON] Zone: {zone.Value}, Size: {zone.Value.Width}x{zone.Value.Height}");
                            Log.Message($"[PERCH BEACON] SLINGs in zone: {GetDockedSlings().Count}");
                        }
                        else
                        {
                            Log.Message($"[PERCH BEACON] No valid zone");
                        }
                    }
                };
            }
        }

        #endregion

        #region Static

        public static void ResetCounter()
        {
            beaconCounter = 1;
        }

        public static void SetCounter(int value)
        {
            beaconCounter = System.Math.Max(1, value);
        }

        #endregion
    }

    #region Dialogs

    public class Dialog_RenameBeacon : Window
    {
        private Building_PerchBeacon beacon;
        private string newName;

        public Dialog_RenameBeacon(Building_PerchBeacon beacon)
        {
            this.beacon = beacon;
            this.newName = beacon.Label;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("Enter new name:");
            newName = listing.TextEntry(newName);
            listing.Gap();

            if (listing.ButtonText("Confirm"))
            {
                typeof(Building_PerchBeacon)
                    .GetField("customName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.SetValue(beacon, newName);
                Close();
            }
            listing.End();
        }
    }

    public class Dialog_ConfigureBeaconExport : Window
    {
        private Building_PerchBeacon beacon;
        private Vector2 scrollPosition;

        public Dialog_ConfigureBeaconExport(Building_PerchBeacon beacon)
        {
            this.beacon = beacon;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 500f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label($"Configure Export for {beacon.Label}");
            listing.GapLine();

            if (beacon.Map != null)
            {
                var resources = beacon.Map.resourceCounter.AllCountedAmounts
                    .Where(kvp => kvp.Value > 0 && kvp.Key.category == ThingCategory.Item)
                    .OrderBy(kvp => kvp.Key.label)
                    .ToList();

                listing.Label($"Resources on map: {resources.Count}");
                listing.Gap();

                Rect scrollRect = listing.GetRect(inRect.height - 120f);
                Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, resources.Count * 60f);

                Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
                float y = 0;
                foreach (var kvp in resources)
                {
                    Rect rowRect = new Rect(0, y, viewRect.width, 55f);
                    bool inFilter = beacon.SourceFilter.Contains(kvp.Key);
                    int threshold = beacon.GetThreshold(kvp.Key);

                    Rect checkRect = new Rect(rowRect.x, rowRect.y, 24f, 24f);
                    bool newInFilter = inFilter;
                    Widgets.Checkbox(checkRect.position, ref newInFilter);
                    if (newInFilter != inFilter)
                    {
                        if (newInFilter) beacon.AddToSourceFilter(kvp.Key);
                        else beacon.RemoveFromSourceFilter(kvp.Key);
                    }

                    Rect labelRect = new Rect(checkRect.xMax + 5f, rowRect.y, 150f, 24f);
                    Widgets.Label(labelRect, $"{kvp.Key.label} ({kvp.Value})");

                    if (newInFilter)
                    {
                        Rect threshRect = new Rect(rowRect.x + 30f, rowRect.y + 26f, rowRect.width - 40f, 24f);
                        int newThreshold = (int)Widgets.HorizontalSlider(threshRect, threshold, 0, kvp.Value, true, $"Keep: {threshold}");
                        if (newThreshold != threshold)
                        {
                            beacon.SetThreshold(kvp.Key, newThreshold);
                        }
                    }

                    y += 60f;
                }
                Widgets.EndScrollView();
            }

            listing.End();
        }
    }

    public class Dialog_ConfigureBeaconImport : Window
    {
        private Building_PerchBeacon beacon;
        private Vector2 scrollPosition;
        private string searchText = "";

        public Dialog_ConfigureBeaconImport(Building_PerchBeacon beacon)
        {
            this.beacon = beacon;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 500f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label($"Configure Import Targets for {beacon.Label}");
            listing.GapLine();

            searchText = listing.TextEntry(searchText);
            listing.Gap();

            var allItems = DefDatabase<ThingDef>.AllDefs
                .Where(d => d.category == ThingCategory.Item && d.stackLimit > 1)
                .Where(d => string.IsNullOrEmpty(searchText) || d.label.ToLower().Contains(searchText.ToLower()))
                .OrderBy(d => d.label)
                .Take(50)
                .ToList();

            Rect scrollRect = listing.GetRect(inRect.height - 150f);
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, allItems.Count * 60f);

            Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
            float y = 0;
            foreach (var def in allItems)
            {
                Rect rowRect = new Rect(0, y, viewRect.width, 55f);
                int currentTarget = beacon.GetThreshold(def);
                bool hasTarget = currentTarget > 0;

                Rect checkRect = new Rect(rowRect.x, rowRect.y, 24f, 24f);
                bool newHasTarget = hasTarget;
                Widgets.Checkbox(checkRect.position, ref newHasTarget);
                if (newHasTarget != hasTarget)
                {
                    beacon.SetThreshold(def, newHasTarget ? 100 : 0);
                }

                Rect labelRect = new Rect(checkRect.xMax + 5f, rowRect.y, 200f, 24f);
                Widgets.Label(labelRect, def.label);

                if (hasTarget || newHasTarget)
                {
                    Rect sliderRect = new Rect(rowRect.x + 30f, rowRect.y + 26f, rowRect.width - 40f, 24f);
                    int newTarget = (int)Widgets.HorizontalSlider(sliderRect, currentTarget, 0, 1000, true, $"Target: {currentTarget}");
                    if (newTarget != currentTarget)
                    {
                        beacon.SetThreshold(def, newTarget);
                    }
                }

                y += 60f;
            }
            Widgets.EndScrollView();

            listing.End();
        }
    }

    #endregion
}
