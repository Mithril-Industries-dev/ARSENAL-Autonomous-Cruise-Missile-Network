using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// PlaceWorker for PERCH Landing Beacons.
    /// Draws preview lines to other beacons when placing, similar to vanilla ShipLandingBeacon.
    /// Shows valid/invalid landing zone status with color coding:
    /// - White lines: Zone would be valid size (>= 9x12)
    /// - Red lines: Zone would be too small
    /// - Cyan zone overlay: Valid complete zone
    /// </summary>
    public class PlaceWorker_PerchBeacon : PlaceWorker
    {
        // Landing zone size requirements (SLING is 6x10, need margin)
        private const int MIN_WIDTH = Building_PerchBeacon.MIN_WIDTH;
        private const int MIN_HEIGHT = Building_PerchBeacon.MIN_HEIGHT;
        private const int MAX_BEACON_SEARCH_DIST = 40;

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            base.DrawGhost(def, center, rot, ghostCol, thing);

            Map map = Find.CurrentMap;
            if (map == null) return;

            // Get all existing beacons on this map
            List<Building_PerchBeacon> existingBeacons = GetExistingBeacons(map, thing);

            // Draw lines to all nearby beacons
            foreach (var beacon in existingBeacons)
            {
                float dist = center.DistanceTo(beacon.Position);
                if (dist > MAX_BEACON_SEARCH_DIST) continue;

                // Check if this would form a valid edge (aligned on same axis)
                bool isAligned = IsAligned(center, beacon.Position);

                // Determine if the potential zone would be valid size
                bool wouldBeValidSize = WouldFormValidSizeZone(center, beacon, existingBeacons);

                // Draw line only for aligned beacons
                if (isAligned)
                {
                    Vector3 startPos = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                    Vector3 endPos = beacon.Position.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);

                    // Use SimpleColor for valid/invalid indication
                    SimpleColor lineColor = wouldBeValidSize ? SimpleColor.White : SimpleColor.Red;
                    GenDraw.DrawLineBetween(startPos, endPos, lineColor);
                }
            }

            // Try to form a complete zone with existing beacons + placement position
            var potentialZone = TryFormZone(center, existingBeacons);
            if (potentialZone.HasValue)
            {
                CellRect zone = potentialZone.Value;
                int width = zone.Width;
                int height = zone.Height;

                // Check if zone is valid size
                bool validSize = width >= MIN_WIDTH && height >= MIN_HEIGHT;

                // Check if zone is clear of obstructions
                bool isClear = IsZoneClear(zone, map);

                // Draw zone outline
                Color zoneColor = (validSize && isClear) ? Color.cyan : Color.red;
                GenDraw.DrawFieldEdges(zone.Cells.ToList(), zoneColor);

                // Draw size label at center
                Vector3 labelPos = zone.CenterVector3;
                labelPos.y = AltitudeLayer.MetaOverlays.AltitudeFor();

                string sizeText = $"{width}x{height}";
                if (!validSize)
                {
                    sizeText += $" (need {MIN_WIDTH}x{MIN_HEIGHT})";
                }

                // Show zone name if this would complete a valid zone
                if (validSize && isClear)
                {
                    // Check if any beacon in this potential zone already has a name
                    string existingName = null;
                    foreach (var beacon in existingBeacons)
                    {
                        if (zone.Contains(beacon.Position) && !string.IsNullOrEmpty(beacon.ZoneName))
                        {
                            existingName = beacon.ZoneName;
                            break;
                        }
                    }

                    if (existingName != null)
                    {
                        sizeText = $"{existingName} ({width}x{height})";
                    }
                    else
                    {
                        sizeText = $"PERCH-{Building_PerchBeacon.GetZoneCounter():D2} ({width}x{height})";
                    }
                }
            }
            else if (existingBeacons.Count >= 1 && existingBeacons.Count < 3)
            {
                // Show potential edges when we have 1-2 beacons
                DrawPotentialEdges(center, existingBeacons, map);
            }
        }

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // Check for existing beacon at this location
            foreach (Thing t in loc.GetThingList(map))
            {
                if (t is Building_PerchBeacon && t != thingToIgnore)
                {
                    return new AcceptanceReport("Another beacon is already here.");
                }
            }

            return AcceptanceReport.WasAccepted;
        }

        private List<Building_PerchBeacon> GetExistingBeacons(Map map, Thing thingToIgnore)
        {
            var beacons = new List<Building_PerchBeacon>();

            foreach (var beacon in ArsenalNetworkManager.GetPerchBeaconsOnMap(map))
            {
                if (beacon != thingToIgnore && beacon.Spawned && !beacon.Destroyed)
                {
                    beacons.Add(beacon);
                }
            }

            return beacons;
        }

        /// <summary>
        /// Checks if two positions are aligned (same X or same Z).
        /// </summary>
        private bool IsAligned(IntVec3 a, IntVec3 b)
        {
            return a.x == b.x || a.z == b.z;
        }

        /// <summary>
        /// Checks if placing at 'center' with 'beacon' would potentially form a valid-size zone.
        /// </summary>
        private bool WouldFormValidSizeZone(IntVec3 center, Building_PerchBeacon beacon, List<Building_PerchBeacon> allBeacons)
        {
            // Check if center and beacon are aligned
            if (!IsAligned(center, beacon.Position))
                return false;

            // Calculate the edge length this would create
            int edgeLength;
            if (center.x == beacon.Position.x)
            {
                // Vertical edge
                edgeLength = Mathf.Abs(center.z - beacon.Position.z) + 1;
            }
            else
            {
                // Horizontal edge
                edgeLength = Mathf.Abs(center.x - beacon.Position.x) + 1;
            }

            // An edge needs to be at least MIN_WIDTH or MIN_HEIGHT
            return edgeLength >= Mathf.Min(MIN_WIDTH, MIN_HEIGHT);
        }

        /// <summary>
        /// Tries to form a valid landing zone rectangle from the placement position + existing beacons.
        /// </summary>
        private CellRect? TryFormZone(IntVec3 placementPos, List<Building_PerchBeacon> existingBeacons)
        {
            if (existingBeacons.Count < 3) return null;

            // Try all combinations of 3 existing beacons + placement position
            for (int i = 0; i < existingBeacons.Count; i++)
            {
                for (int j = i + 1; j < existingBeacons.Count; j++)
                {
                    for (int k = j + 1; k < existingBeacons.Count; k++)
                    {
                        var zone = TryFormRectangle(
                            placementPos,
                            existingBeacons[i].Position,
                            existingBeacons[j].Position,
                            existingBeacons[k].Position);

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
        /// Checks if 4 positions form a valid rectangle with positions at corners.
        /// Returns the zone CellRect if valid, null otherwise.
        /// </summary>
        private CellRect? TryFormRectangle(IntVec3 a, IntVec3 b, IntVec3 c, IntVec3 d)
        {
            var positions = new List<IntVec3> { a, b, c, d };

            int minX = positions.Min(p => p.x);
            int maxX = positions.Max(p => p.x);
            int minZ = positions.Min(p => p.z);
            int maxZ = positions.Max(p => p.z);

            // All 4 positions must be at corners
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
                    return null;
                }
            }

            // Return the zone (any size - caller checks min size)
            int width = maxX - minX + 1;
            int height = maxZ - minZ + 1;

            if (width < 3 || height < 3) return null;

            return new CellRect(minX, minZ, width, height);
        }

        /// <summary>
        /// Checks if a landing zone is clear of obstructions.
        /// </summary>
        private bool IsZoneClear(CellRect zone, Map map)
        {
            // Check inner zone (not including corners where beacons are)
            CellRect innerZone = zone.ContractedBy(1);

            foreach (IntVec3 cell in innerZone)
            {
                if (!cell.InBounds(map)) return false;
                if (!cell.Standable(map)) return false;
                if (cell.Roofed(map)) return false;

                foreach (Thing t in cell.GetThingList(map))
                {
                    if (t is Building && !(t is Building_PerchBeacon))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Draws potential edges when we have 1-2 beacons, showing where to place more.
        /// </summary>
        private void DrawPotentialEdges(IntVec3 placementPos, List<Building_PerchBeacon> existingBeacons, Map map)
        {
            if (existingBeacons.Count == 0) return;

            foreach (var beacon in existingBeacons)
            {
                if (!IsAligned(placementPos, beacon.Position))
                    continue;

                // Calculate edge length
                int edgeLength;
                if (placementPos.x == beacon.Position.x)
                {
                    edgeLength = Mathf.Abs(placementPos.z - beacon.Position.z) + 1;
                }
                else
                {
                    edgeLength = Mathf.Abs(placementPos.x - beacon.Position.x) + 1;
                }

                // Color based on whether this edge is long enough
                bool validEdge = edgeLength >= Mathf.Min(MIN_WIDTH, MIN_HEIGHT);
                SimpleColor lineColor = validEdge ? SimpleColor.White : SimpleColor.Red;

                Vector3 startPos = placementPos.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                Vector3 endPos = beacon.Position.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);

                GenDraw.DrawLineBetween(startPos, endPos, lineColor);
            }
        }
    }
}
