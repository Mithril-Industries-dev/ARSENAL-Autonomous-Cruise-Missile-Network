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
    /// Shows valid/invalid landing zone status.
    /// </summary>
    public class PlaceWorker_PerchBeacon : PlaceWorker
    {
        // Landing zone size requirements (SLING is 6x10, need margin)
        private const int MIN_WIDTH = 9;
        private const int MIN_HEIGHT = 12;
        private const int MAX_BEACON_SEARCH_DIST = 40;

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            base.DrawGhost(def, center, rot, ghostCol, thing);

            Map map = Find.CurrentMap;
            if (map == null) return;

            // Get all existing beacons on this map
            List<Building_PerchBeacon> existingBeacons = GetExistingBeacons(map, thing);

            // Draw lines to nearby beacons
            foreach (var beacon in existingBeacons)
            {
                float dist = center.DistanceTo(beacon.Position);
                if (dist > MAX_BEACON_SEARCH_DIST) continue;

                // Determine line color based on alignment
                Color lineColor;
                if (IsAligned(center, beacon.Position))
                {
                    lineColor = new Color(0f, 0.8f, 0.8f, 0.5f); // Cyan for aligned
                }
                else
                {
                    lineColor = new Color(0.5f, 0.5f, 0.5f, 0.3f); // Gray for not aligned
                }

                GenDraw.DrawLineBetween(center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays),
                                        beacon.Position.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays),
                                        SimpleColor.White, 0.2f);
            }

            // Try to form a rectangle with existing beacons + placement position
            var potentialZone = TryFormZone(center, existingBeacons);
            if (potentialZone.HasValue)
            {
                // Draw the landing zone preview
                CellRect zone = potentialZone.Value;

                // Check if zone is valid (clear of obstructions)
                bool isValid = IsZoneClear(zone, map);
                Color zoneColor = isValid ? new Color(0f, 1f, 1f, 0.25f) : new Color(1f, 0.3f, 0.3f, 0.25f);

                // Draw filled zone
                GenDraw.DrawFieldEdges(zone.Cells.ToList(), isValid ? Color.cyan : Color.red);

                // Draw size indicator
                Vector3 labelPos = zone.CenterVector3;
                labelPos.y = AltitudeLayer.MetaOverlays.AltitudeFor();
            }
            else if (existingBeacons.Count >= 1 && existingBeacons.Count < 3)
            {
                // Show that more beacons are needed
                // Draw lines showing potential rectangle edges
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

            // Also check for blueprints/frames of beacons
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t == thingToIgnore) continue;

                if (t.def.IsBlueprint && t.def.entityDefToBuild?.defName == "Arsenal_PerchBeacon")
                {
                    // Can't add blueprint as beacon, but note its position
                }
                else if (t.def.IsFrame && t.def.entityDefToBuild?.defName == "Arsenal_PerchBeacon")
                {
                    // Can't add frame as beacon, but note its position
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

                        if (zone.HasValue && zone.Value.Width >= MIN_WIDTH && zone.Value.Height >= MIN_HEIGHT)
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

            // Return the inner zone (excluding beacon cells)
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

            // With 1-2 beacons, draw lines showing the edges we're creating
            foreach (var beacon in existingBeacons)
            {
                if (IsAligned(placementPos, beacon.Position))
                {
                    // This would form an edge of the rectangle
                    GenDraw.DrawLineBetween(
                        placementPos.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays),
                        beacon.Position.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays),
                        SimpleColor.Cyan, 0.3f);
                }
            }
        }
    }
}
