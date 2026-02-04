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
    ///
    /// Design: Beacons are simple markers. When 4 beacons form a valid rectangle,
    /// the beacon at the min corner (lowest X, then lowest Z) becomes the "primary"
    /// beacon which holds the zone configuration (role, filters, thresholds, zone name).
    /// </summary>
    public class Building_PerchBeacon : Building
    {
        // Zone name (stored on primary beacon)
        private string zoneName;
        private static int zoneCounter = 1;

        // Logistics role (only used if this is the primary beacon)
        public enum PerchRole { Source, Sink }
        private PerchRole role = PerchRole.Source;

        // Source configuration - what to export (only used if primary)
        private List<ThingDef> sourceFilter = new List<ThingDef>();
        private Dictionary<ThingDef, int> thresholdTargets = new Dictionary<ThingDef, int>();

        // Landing zone constants
        public const int MIN_WIDTH = 9;   // Minimum landing zone width (SLING is 6 wide + margin)
        public const int MIN_HEIGHT = 12; // Minimum landing zone height (SLING is 10 tall + margin)
        public const int MAX_BEACON_DISTANCE = 40; // Max distance to search for other beacons

        // Components
        private CompPowerTrader powerComp;

        // Cached landing zone and zone beacons (updated periodically)
        private CellRect? cachedLandingZone;
        private List<Building_PerchBeacon> cachedZoneBeacons;
        private int lastZoneCheck = -999;
        private const int ZONE_CHECK_INTERVAL = 120;

        // Track if we've already assigned a zone name
        private bool hasAssignedZoneName = false;

        #region Properties

        /// <summary>
        /// The zone name (e.g., "PERCH-01"). Returns from primary beacon.
        /// Lazily assigns a name if the zone is valid but unnamed.
        /// </summary>
        public string ZoneName
        {
            get
            {
                var primary = GetPrimaryBeacon();
                if (primary == null) return null;

                // Lazily assign name if zone is valid but unnamed
                if (string.IsNullOrEmpty(primary.zoneName))
                {
                    primary.zoneName = "PERCH-" + zoneCounter.ToString("D2");
                    primary.hasAssignedZoneName = true;
                    zoneCounter++;
                }

                return primary.zoneName;
            }
        }

        public override string Label
        {
            get
            {
                // If part of a valid zone, show zone name
                if (HasValidLandingZone && !string.IsNullOrEmpty(ZoneName))
                {
                    return ZoneName;
                }
                return base.Label;
            }
        }

        public PerchRole Role => GetPrimaryBeacon()?.role ?? role;
        public bool IsSource => Role == PerchRole.Source;
        public bool IsSink => Role == PerchRole.Sink;
        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;

        /// <summary>
        /// Returns true if this beacon is the primary (configuration holder) for its zone.
        /// Primary is determined by position: lowest X, then lowest Z among zone beacons.
        /// </summary>
        public bool IsPrimary
        {
            get
            {
                var zoneBeacons = GetZoneBeacons();
                if (zoneBeacons == null || zoneBeacons.Count < 4) return false;
                var primary = zoneBeacons.OrderBy(b => b.Position.x).ThenBy(b => b.Position.z).First();
                return primary == this;
            }
        }

        /// <summary>
        /// Gets the primary beacon for this zone (may be this beacon or another).
        /// Returns null if not part of a valid zone.
        /// </summary>
        public Building_PerchBeacon GetPrimaryBeacon()
        {
            var zoneBeacons = GetZoneBeacons();
            if (zoneBeacons == null || zoneBeacons.Count < 4) return null;
            return zoneBeacons.OrderBy(b => b.Position.x).ThenBy(b => b.Position.z).First();
        }

        /// <summary>
        /// Returns true if this beacon is part of a valid 4-beacon landing zone.
        /// </summary>
        public bool HasValidLandingZone => GetLandingZone().HasValue;

        // Config accessors - delegate to primary beacon
        public List<ThingDef> SourceFilter => GetPrimaryBeacon()?.sourceFilter ?? sourceFilter;
        public Dictionary<ThingDef, int> ThresholdTargets => GetPrimaryBeacon()?.thresholdTargets ?? thresholdTargets;

        /// <summary>
        /// Network connectivity check - required for SLING logistics.
        /// </summary>
        public bool HasNetworkConnection()
        {
            if (Map == null) return false;
            return ArsenalNetworkManager.IsTileConnected(Map.Tile);
        }

        #endregion

        #region Static Methods

        public static void ResetZoneCounter()
        {
            zoneCounter = 1;
        }

        public static void SetZoneCounter(int value)
        {
            zoneCounter = System.Math.Max(1, value);
        }

        public static int GetZoneCounter()
        {
            return zoneCounter;
        }

        #endregion

        #region Lifecycle

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();

            ArsenalNetworkManager.RegisterPerchBeacon(this);

            // Invalidate cache
            cachedLandingZone = null;
            cachedZoneBeacons = null;
            lastZoneCheck = -999;

            // Invalidate caches of nearby beacons too (a new beacon may complete their zone)
            InvalidateNearbyBeaconCaches();

            // Check if this beacon completes a zone and needs a name
            if (!respawningAfterLoad)
            {
                TryAssignZoneName();
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Invalidate caches of zone beacons before deregistering
            InvalidateNearbyBeaconCaches();

            ArsenalNetworkManager.DeregisterPerchBeacon(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref zoneName, "zoneName");
            Scribe_Values.Look(ref role, "role", PerchRole.Source);
            Scribe_Values.Look(ref hasAssignedZoneName, "hasAssignedZoneName", false);
            Scribe_Collections.Look(ref sourceFilter, "sourceFilter", LookMode.Def);
            Scribe_Collections.Look(ref thresholdTargets, "thresholdTargets", LookMode.Def, LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (sourceFilter == null) sourceFilter = new List<ThingDef>();
                if (thresholdTargets == null) thresholdTargets = new Dictionary<ThingDef, int>();

                // Try to assign zone name if this is part of a valid zone that's unnamed
                // (handles saves from before zone naming was implemented)
                TryAssignZoneNameOnLoad();
            }
        }

        /// <summary>
        /// Called on game load to assign zone names to zones that don't have them.
        /// This handles saves from before zone naming was implemented.
        /// </summary>
        private void TryAssignZoneNameOnLoad()
        {
            // Skip if Map is null (can happen during certain load states)
            if (Map == null) return;

            // Find all beacons on this map directly from the map's building list
            // This is more reliable than using ArsenalNetworkManager during load
            var allBeaconsOnMap = Map.listerBuildings.AllBuildingsColonistOfClass<Building_PerchBeacon>().ToList();

            // Find beacons that could form a zone with this one
            var otherBeacons = allBeaconsOnMap
                .Where(b => b != this && b.Position.DistanceTo(Position) <= MAX_BEACON_DISTANCE)
                .ToList();

            if (otherBeacons.Count < 3) return;

            // Try to find 3 other beacons that form a valid rectangle
            List<Building_PerchBeacon> zoneBeacons = null;
            foreach (var b1 in otherBeacons)
            {
                foreach (var b2 in otherBeacons.Where(b => b != b1))
                {
                    foreach (var b3 in otherBeacons.Where(b => b != b1 && b != b2))
                    {
                        var beacons = new List<Building_PerchBeacon> { this, b1, b2, b3 };
                        if (IsValidRectangle(beacons))
                        {
                            zoneBeacons = beacons;
                            break;
                        }
                    }
                    if (zoneBeacons != null) break;
                }
                if (zoneBeacons != null) break;
            }

            if (zoneBeacons == null || zoneBeacons.Count < 4)
                return;

            // Update cache
            cachedZoneBeacons = zoneBeacons;
            cachedLandingZone = CalculateLandingZoneFromBeacons(zoneBeacons);
            lastZoneCheck = Find.TickManager.TicksGame;

            // Find the primary beacon (lowest X, then lowest Z)
            var primary = zoneBeacons.OrderBy(b => b.Position.x).ThenBy(b => b.Position.z).First();

            // Only assign name if primary doesn't have one
            if (primary != null && string.IsNullOrEmpty(primary.zoneName))
            {
                primary.zoneName = "PERCH-" + zoneCounter.ToString("D2");
                primary.hasAssignedZoneName = true;
                zoneCounter++;
            }
        }

        private void InvalidateNearbyBeaconCaches()
        {
            if (Map == null) return;
            foreach (var beacon in ArsenalNetworkManager.GetPerchBeaconsOnMap(Map))
            {
                if (beacon != this)
                {
                    beacon.cachedLandingZone = null;
                    beacon.cachedZoneBeacons = null;
                    beacon.lastZoneCheck = -999;
                }
            }
        }

        /// <summary>
        /// Tries to assign a zone name when this beacon completes a valid zone.
        /// Only the primary beacon gets the name.
        /// </summary>
        private void TryAssignZoneName()
        {
            // Force a zone check
            cachedZoneBeacons = CalculateZoneBeacons();
            cachedLandingZone = cachedZoneBeacons != null ? CalculateLandingZoneFromBeacons(cachedZoneBeacons) : null;
            lastZoneCheck = Find.TickManager.TicksGame;

            if (cachedZoneBeacons == null || cachedZoneBeacons.Count < 4)
                return;

            // Check if we're the primary beacon
            var primary = cachedZoneBeacons.OrderBy(b => b.Position.x).ThenBy(b => b.Position.z).First();

            // If no primary has assigned a name yet, assign one now
            if (primary != null && string.IsNullOrEmpty(primary.zoneName) && !primary.hasAssignedZoneName)
            {
                primary.zoneName = "PERCH-" + zoneCounter.ToString("D2");
                primary.hasAssignedZoneName = true;
                zoneCounter++;

                Messages.Message($"Landing zone {primary.zoneName} established.", primary, MessageTypeDefOf.PositiveEvent);
            }
        }

        #endregion

        #region Landing Zone Detection

        /// <summary>
        /// Gets all beacons that form a valid zone with this beacon.
        /// Returns null if no valid zone can be formed.
        /// </summary>
        public List<Building_PerchBeacon> GetZoneBeacons()
        {
            // Use cache if recent
            if (Find.TickManager.TicksGame - lastZoneCheck < ZONE_CHECK_INTERVAL)
            {
                return cachedZoneBeacons;
            }

            cachedZoneBeacons = CalculateZoneBeacons();
            cachedLandingZone = cachedZoneBeacons != null ? CalculateLandingZoneFromBeacons(cachedZoneBeacons) : null;
            lastZoneCheck = Find.TickManager.TicksGame;
            return cachedZoneBeacons;
        }

        /// <summary>
        /// Forces recalculation of zone beacons, ignoring cache.
        /// Use this when accurate detection is needed (e.g., during network scans).
        /// </summary>
        public List<Building_PerchBeacon> GetZoneBeaconsForced()
        {
            cachedZoneBeacons = CalculateZoneBeacons();
            cachedLandingZone = cachedZoneBeacons != null ? CalculateLandingZoneFromBeacons(cachedZoneBeacons) : null;
            lastZoneCheck = Find.TickManager.TicksGame;
            return cachedZoneBeacons;
        }

        /// <summary>
        /// Gets the landing zone defined by this beacon and 3 others forming a rectangle.
        /// Returns null if no valid zone can be formed.
        /// </summary>
        public CellRect? GetLandingZone()
        {
            // Ensure cache is fresh
            GetZoneBeacons();
            return cachedLandingZone;
        }

        private List<Building_PerchBeacon> CalculateZoneBeacons()
        {
            if (Map == null) return null;

            // Find all other powered beacons on this map
            var otherBeacons = ArsenalNetworkManager.GetPerchBeaconsOnMap(Map)
                .Where(b => b != this && b.IsPoweredOn && b.Position.DistanceTo(Position) <= MAX_BEACON_DISTANCE)
                .ToList();

            if (otherBeacons.Count < 3) return null;

            // Try to find 3 other beacons that form a valid rectangle with this one
            foreach (var b1 in otherBeacons)
            {
                foreach (var b2 in otherBeacons.Where(b => b != b1))
                {
                    foreach (var b3 in otherBeacons.Where(b => b != b1 && b != b2))
                    {
                        var beacons = new List<Building_PerchBeacon> { this, b1, b2, b3 };
                        if (IsValidRectangle(beacons))
                        {
                            return beacons;
                        }
                    }
                }
            }

            return null;
        }

        private bool IsValidRectangle(List<Building_PerchBeacon> beacons)
        {
            if (beacons.Count != 4) return false;

            var positions = beacons.Select(b => b.Position).ToList();

            int minX = positions.Min(p => p.x);
            int maxX = positions.Max(p => p.x);
            int minZ = positions.Min(p => p.z);
            int maxZ = positions.Max(p => p.z);

            // All 4 beacons must be at corners
            var corners = new HashSet<(int x, int z)>
            {
                (minX, minZ),
                (minX, maxZ),
                (maxX, minZ),
                (maxX, maxZ)
            };

            foreach (var pos in positions)
            {
                if (!corners.Contains((pos.x, pos.z)))
                {
                    return false;
                }
            }

            // Check minimum size
            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;

            return width >= MIN_WIDTH && height >= MIN_HEIGHT;
        }

        private CellRect? CalculateLandingZoneFromBeacons(List<Building_PerchBeacon> beacons)
        {
            if (beacons == null || beacons.Count != 4) return null;

            var positions = beacons.Select(b => b.Position).ToList();

            int minX = positions.Min(p => p.x);
            int maxX = positions.Max(p => p.x);
            int minZ = positions.Min(p => p.z);
            int maxZ = positions.Max(p => p.z);

            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;

            // Return the full zone including beacon positions
            return new CellRect(minX, minZ, width, height);
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

            // Inner zone (excluding beacon corners)
            CellRect innerZone = landingZone.ContractedBy(1);

            // Calculate the ideal landing position to center the SLING in the zone
            // SLING spawns from SW corner, so offset by half the SLING dimensions
            IntVec3 zoneCenter = innerZone.CenterCell;
            IntVec3 idealLandingPos = new IntVec3(
                zoneCenter.x - slingWidth / 2,
                0,
                zoneCenter.z - slingHeight / 2
            );

            // Check ideal centered position first
            if (IsValidLandingSpot(idealLandingPos, slingWidth, slingHeight, innerZone))
            {
                return idealLandingPos;
            }

            // Spiral outward from ideal position
            for (int radius = 1; radius <= Mathf.Max(innerZone.Width, innerZone.Height); radius++)
            {
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(idealLandingPos, radius, true))
                {
                    if (IsValidLandingSpot(cell, slingWidth, slingHeight, innerZone))
                    {
                        return cell;
                    }
                }
            }

            return IntVec3.Invalid;
        }

        private bool IsValidLandingSpot(IntVec3 cell, int width, int height, CellRect zone)
        {
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < height; z++)
                {
                    IntVec3 checkCell = cell + new IntVec3(x, 0, z);

                    if (!checkCell.InBounds(Map)) return false;
                    if (!zone.Contains(checkCell)) return false;
                    if (!checkCell.Standable(Map)) return false;
                    if (checkCell.Roofed(Map)) return false;

                    foreach (Thing t in checkCell.GetThingList(Map))
                    {
                        if (t is Building_PerchBeacon) continue;
                        if (t is Building) return false;
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

        public bool HasAvailableSling => GetIdleSlings().Any();

        /// <summary>
        /// Returns true if this zone has room for another SLING.
        /// </summary>
        public bool HasSpaceForSling => FindLandingSpot() != IntVec3.Invalid;

        #endregion

        #region Source/Sink Configuration (Primary beacon only)

        public void SetRole(PerchRole newRole)
        {
            var primary = GetPrimaryBeacon();
            if (primary != null && primary != this)
            {
                primary.role = newRole;
            }
            else
            {
                role = newRole;
            }
        }

        public void ToggleRole()
        {
            var primary = GetPrimaryBeacon();
            if (primary != null)
            {
                primary.role = primary.role == PerchRole.Source ? PerchRole.Sink : PerchRole.Source;
            }
            else
            {
                role = role == PerchRole.Source ? PerchRole.Sink : PerchRole.Source;
            }
        }

        public void AddToSourceFilter(ThingDef def)
        {
            var primary = GetPrimaryBeacon() ?? this;
            if (!primary.sourceFilter.Contains(def))
                primary.sourceFilter.Add(def);
        }

        public void RemoveFromSourceFilter(ThingDef def)
        {
            var primary = GetPrimaryBeacon() ?? this;
            primary.sourceFilter.Remove(def);
        }

        public void SetThreshold(ThingDef def, int amount)
        {
            var primary = GetPrimaryBeacon() ?? this;
            if (amount <= 0)
                primary.thresholdTargets.Remove(def);
            else
                primary.thresholdTargets[def] = amount;
        }

        public int GetThreshold(ThingDef def)
        {
            var primary = GetPrimaryBeacon() ?? this;
            return primary.thresholdTargets.TryGetValue(def, out int val) ? val : 0;
        }

        /// <summary>
        /// Gets resources available for export (above threshold).
        /// </summary>
        public Dictionary<ThingDef, int> GetAvailableForExport()
        {
            var available = new Dictionary<ThingDef, int>();
            if (Map == null || !IsSource) return available;

            var filter = SourceFilter;
            foreach (ThingDef def in filter)
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

            var targets = ThresholdTargets;
            foreach (var kvp in targets)
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

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();

            var zone = GetLandingZone();
            if (zone.HasValue)
            {
                GenDraw.DrawFieldEdges(zone.Value.Cells.ToList(), Color.cyan);
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
                string zName = ZoneName ?? "Unnamed";
                str += $"Zone: {zName} ({zone.Value.Width}x{zone.Value.Height})";

                if (IsPrimary)
                {
                    str += " [Primary]";
                }
            }
            else
            {
                str += "No valid landing zone (need 4 beacons at corners, min 9x12)";
            }

            str += $"\nPowered: {(IsPoweredOn ? "Yes" : "No")}";

            // Only show role/config info if we have a valid zone
            if (HasValidLandingZone)
            {
                str += $"\nRole: {Role}";

                if (HasNetworkConnection())
                {
                    str += " | Network: Connected";
                }
                else
                {
                    str += " | <color=yellow>Network: Disconnected</color>";
                }

                var slings = GetDockedSlings();
                str += $"\nSLINGs in zone: {slings.Count}";

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
            }

            return str;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            // Only show configuration gizmos if we have a valid zone
            if (HasValidLandingZone)
            {
                // Toggle Role
                yield return new Command_Action
                {
                    defaultLabel = $"Role: {Role}",
                    defaultDesc = "Toggle between SOURCE (exports resources) and SINK (imports resources).\nConfiguration is shared by all beacons in this landing zone.",
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate", false),
                    action = delegate
                    {
                        ToggleRole();
                        Messages.Message($"{ZoneName} is now a {Role}", this, MessageTypeDefOf.NeutralEvent);
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
                    defaultLabel = "DEV: Zone Info",
                    action = delegate
                    {
                        var zone = GetLandingZone();
                        var zoneBeacons = GetZoneBeacons();
                        if (zone.HasValue && zoneBeacons != null)
                        {
                            Log.Message($"[PERCH BEACON] Zone: {ZoneName}, Rect: {zone.Value}, Size: {zone.Value.Width}x{zone.Value.Height}");
                            Log.Message($"[PERCH BEACON] Beacons in zone: {zoneBeacons.Count}");
                            Log.Message($"[PERCH BEACON] Primary: {GetPrimaryBeacon()?.Position}");
                            Log.Message($"[PERCH BEACON] This is primary: {IsPrimary}");
                            Log.Message($"[PERCH BEACON] SLINGs in zone: {GetDockedSlings().Count}");
                            Log.Message($"[PERCH BEACON] Has network: {HasNetworkConnection()}");
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
    }

    #region Dialogs

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
            listing.Label($"Configure Export for {beacon.ZoneName ?? "Landing Zone"}");
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
            listing.Label($"Configure Import Targets for {beacon.ZoneName ?? "Landing Zone"}");
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
