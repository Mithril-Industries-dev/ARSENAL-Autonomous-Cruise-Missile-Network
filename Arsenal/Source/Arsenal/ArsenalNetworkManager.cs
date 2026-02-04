using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using Verse;

namespace Arsenal
{
    public static class ArsenalNetworkManager
    {
        private static List<Building_Arsenal> arsenals = new List<Building_Arsenal>();
        private static List<Building_Hub> hubs = new List<Building_Hub>();
        private static List<Building_Hop> hops = new List<Building_Hop>();

        // LATTICE system components
        private static List<Building_Lattice> lattices = new List<Building_Lattice>();
        private static List<Building_Quiver> quivers = new List<Building_Quiver>();

        // ARGUS sensors
        private static List<Building_ARGUS> argusUnits = new List<Building_ARGUS>();

        // HERALD comm relays - keyed by world tile
        private static Dictionary<int, Building_HERALD> heraldsPerTile = new Dictionary<int, Building_HERALD>();

        // SKYLINK system components
        private static WorldObject_SkyLinkSatellite orbitalSatellite;
        private static List<Building_SkyLinkTerminal> terminals = new List<Building_SkyLinkTerminal>();

        // HAWKEYE mobile sensors (pawn-mounted)
        private static List<Pawn> hawkeyePawns = new List<Pawn>();

        // SLING/PERCH logistics system
        private static List<Building_PERCH> perches = new List<Building_PERCH>();
        private static List<Building_PerchBeacon> perchBeacons = new List<Building_PerchBeacon>();

        // MULE system components
        private static List<MULE_Pawn> mules = new List<MULE_Pawn>();
        private static List<Building_Stable> stables = new List<Building_Stable>();
        private static List<Building_Moria> morias = new List<Building_Moria>();

        #region Global LATTICE Access

        /// <summary>
        /// Returns the primary LATTICE in the network (first powered one found).
        /// Used for network connectivity checks.
        /// </summary>
        public static Building_Lattice GlobalLattice
        {
            get
            {
                lattices.RemoveAll(l => l == null || l.Destroyed || !l.Spawned);
                return lattices.FirstOrDefault(l => l.IsPoweredOn()) ?? lattices.FirstOrDefault();
            }
        }

        #endregion

        #region SKYLINK Satellite Operations

        /// <summary>
        /// Checks if a SKYLINK satellite is currently in orbit.
        /// </summary>
        public static bool IsSatelliteInOrbit()
        {
            return orbitalSatellite != null && orbitalSatellite.IsOperational;
        }

        /// <summary>
        /// Gets the orbital satellite if it exists.
        /// </summary>
        public static WorldObject_SkyLinkSatellite GetOrbitalSatellite()
        {
            return orbitalSatellite;
        }

        /// <summary>
        /// Checks if LATTICE is connected to SKYLINK via a Terminal.
        /// Requires: satellite in orbit + powered Terminal within 15 tiles of powered LATTICE.
        /// </summary>
        public static bool IsLatticeConnectedToSkylink()
        {
            if (!IsSatelliteInOrbit())
                return false;

            var lattice = GlobalLattice;
            if (lattice == null || !lattice.IsPoweredOn())
                return false;

            // Check for a powered Terminal within range of LATTICE
            terminals.RemoveAll(t => t == null || t.Destroyed || !t.Spawned);
            return terminals.Any(t => t.IsOnline && t.LinkedLattice == lattice);
        }

        /// <summary>
        /// Gets the overall SKYLINK network status message.
        /// </summary>
        public static string GetSkylinkStatus()
        {
            if (!IsSatelliteInOrbit())
                return "OFFLINE — No satellite in orbit";

            if (GlobalLattice == null)
                return "OFFLINE — No LATTICE";

            if (!GlobalLattice.IsPoweredOn())
                return "OFFLINE — LATTICE unpowered";

            if (!IsLatticeConnectedToSkylink())
                return "OFFLINE — No Terminal link to LATTICE";

            return "ONLINE — Global operations enabled";
        }

        public static void RegisterSatellite(WorldObject_SkyLinkSatellite satellite)
        {
            orbitalSatellite = satellite;
        }

        public static void DeregisterSatellite(WorldObject_SkyLinkSatellite satellite)
        {
            // If null passed, force clear (for debugging stale state)
            if (satellite == null || orbitalSatellite == satellite)
                orbitalSatellite = null;
        }

        public static void RegisterTerminal(Building_SkyLinkTerminal terminal)
        {
            if (!terminals.Contains(terminal))
                terminals.Add(terminal);
        }

        public static void DeregisterTerminal(Building_SkyLinkTerminal terminal)
        {
            terminals.Remove(terminal);
        }

        public static List<Building_SkyLinkTerminal> GetAllTerminals()
        {
            terminals.RemoveAll(t => t == null || t.Destroyed || !t.Spawned);
            return terminals.ToList();
        }

        #endregion

        #region HAWKEYE Registration

        public static void RegisterHawkeyePawn(Pawn pawn)
        {
            if (!hawkeyePawns.Contains(pawn))
                hawkeyePawns.Add(pawn);
        }

        public static void DeregisterHawkeyePawn(Pawn pawn)
        {
            hawkeyePawns.Remove(pawn);
        }

        public static List<Pawn> GetAllHawkeyePawns()
        {
            hawkeyePawns.RemoveAll(p => p == null || p.Dead || p.Destroyed);
            return hawkeyePawns.ToList();
        }

        /// <summary>
        /// Gets all threats detected by the network (ARGUS units + HAWKEYE pawns).
        /// Returns hostile pawns within detection range of any networked sensor.
        /// </summary>
        public static List<Pawn> GetAllNetworkDetectedThreats()
        {
            HashSet<Pawn> threats = new HashSet<Pawn>();

            // Only gather threats if LATTICE is online
            var lattice = GlobalLattice;
            if (lattice == null || !lattice.IsPoweredOn())
                return new List<Pawn>();

            // Gather from ARGUS units on same map as LATTICE
            foreach (var argus in GetArgusOnMap(lattice.Map))
            {
                if (!argus.IsPoweredOn) continue;
                foreach (var threat in argus.GetDetectedThreats())
                {
                    threats.Add(threat);
                }
            }

            // Gather from HAWKEYE pawns (requires SKYLINK connection)
            if (IsLatticeConnectedToSkylink())
            {
                foreach (var pawn in GetAllHawkeyePawns())
                {
                    // CompHawkeyeSensor is on the Apparel, not the Pawn - get it from worn apparel
                    var hawkeye = pawn.apparel?.WornApparel?.FirstOrDefault(a => a is Apparel_HawkEye) as Apparel_HawkEye;
                    var comp = hawkeye?.SensorComp;
                    if (comp != null && comp.IsOperational)
                    {
                        foreach (var threat in comp.GetDetectedThreats())
                        {
                            threats.Add(threat);
                        }
                    }
                }
            }

            return threats.ToList();
        }

        /// <summary>
        /// Checks if a specific target can be detected by any HAWKEYE sensor.
        /// Used by DART/QUIVER to enable engagement of targets outside ARGUS range
        /// but within HAWKEYE range. HAWKEYE acts as a mobile ARGUS node.
        /// </summary>
        public static bool CanHawkeyeDetectTarget(Pawn target)
        {
            if (target == null)
                return false;

            // HAWKEYE requires SKYLINK connection
            if (!IsLatticeConnectedToSkylink())
                return false;

            foreach (var pawn in GetAllHawkeyePawns())
            {
                // CompHawkeyeSensor is on the Apparel, not the Pawn
                var hawkeye = pawn.apparel?.WornApparel?.FirstOrDefault(a => a is Apparel_HawkEye) as Apparel_HawkEye;
                var comp = hawkeye?.SensorComp;
                if (comp != null && comp.CanDetectTarget(target))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a target is valid for DART engagement.
        /// A target is valid if detected by ARGUS OR by HAWKEYE (mobile ARGUS node).
        /// </summary>
        public static bool IsTargetValidForDartEngagement(Pawn target, Map map)
        {
            if (target == null || map == null)
                return false;

            var lattice = GlobalLattice;
            if (lattice == null || !lattice.IsPoweredOn())
                return false;

            // Check ARGUS detection (on same map)
            foreach (var argus in GetArgusOnMap(map))
            {
                if (argus.IsPoweredOn && argus.GetDetectedThreats().Contains(target))
                    return true;
            }

            // Check HAWKEYE detection (mobile ARGUS node) - requires SKYLINK
            if (CanHawkeyeDetectTarget(target))
                return true;

            return false;
        }

        #endregion

        #region MULE System Registration

        public static void RegisterMule(MULE_Pawn mule)
        {
            if (!mules.Contains(mule))
                mules.Add(mule);
        }

        public static void DeregisterMule(MULE_Pawn mule)
        {
            mules.Remove(mule);
        }

        public static void RegisterStable(Building_Stable stable)
        {
            if (!stables.Contains(stable))
                stables.Add(stable);
        }

        public static void DeregisterStable(Building_Stable stable)
        {
            stables.Remove(stable);
        }

        public static void RegisterMoria(Building_Moria moria)
        {
            if (!morias.Contains(moria))
                morias.Add(moria);
        }

        public static void DeregisterMoria(Building_Moria moria)
        {
            morias.Remove(moria);
        }

        public static List<MULE_Pawn> GetAllMules()
        {
            mules.RemoveAll(m => m == null || m.Destroyed);

            // Include spawned MULEs
            var result = mules.ToList();

            // Also include docked MULEs from all STABLEs (they are despawned, not in mules list)
            foreach (var stable in stables)
            {
                if (stable == null || stable.Destroyed || !stable.Spawned) continue;
                foreach (var dockedMule in stable.DockedMules)
                {
                    if (dockedMule != null && !dockedMule.Destroyed && !result.Contains(dockedMule))
                    {
                        result.Add(dockedMule);
                    }
                }
            }

            return result;
        }

        public static IEnumerable<MULE_Pawn> GetMulesOnMap(Map map)
        {
            if (map == null) return Enumerable.Empty<MULE_Pawn>();
            mules.RemoveAll(m => m == null || m.Destroyed);
            return mules.Where(m => m.Spawned && m.Map == map);
        }

        public static List<Building_Stable> GetAllStables()
        {
            stables.RemoveAll(s => s == null || s.Destroyed || !s.Spawned);
            return stables.ToList();
        }

        public static List<Building_Moria> GetAllMorias()
        {
            morias.RemoveAll(m => m == null || m.Destroyed || !m.Spawned);
            return morias.ToList();
        }

        public static List<Building_Stable> GetStablesOnMap(Map map)
        {
            if (map == null) return new List<Building_Stable>();
            return GetAllStables().Where(s => s.Map == map).ToList();
        }

        public static List<Building_Moria> GetMoriasOnMap(Map map)
        {
            if (map == null) return new List<Building_Moria>();
            return GetAllMorias().Where(m => m.Map == map).ToList();
        }

        /// <summary>
        /// Gets the nearest STABLE that has space for a MULE.
        /// </summary>
        public static Building_Stable GetNearestStableWithSpace(IntVec3 position, Map map)
        {
            if (map == null) return null;

            Building_Stable nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var stable in GetStablesOnMap(map))
            {
                if (!stable.HasSpace || !stable.IsPoweredOn()) continue;

                float dist = position.DistanceTo(stable.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = stable;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Gets the nearest MORIA that can accept a specific item.
        /// </summary>
        public static Building_Moria GetNearestMoriaForItem(Thing item, Map map)
        {
            if (map == null || item == null) return null;

            Building_Moria nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var moria in GetMoriasOnMap(map))
            {
                if (!moria.CanAcceptItem(item)) continue;

                float dist = item.Position.DistanceTo(moria.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = moria;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Gets an available MULE from any STABLE that can handle the given task.
        /// Returns the MULE and its STABLE.
        /// </summary>
        public static (MULE_Pawn mule, Building_Stable stable) GetAvailableMuleForTask(MuleTask task, Map map)
        {
            if (map == null || task == null) return (null, null);

            // Find nearest STABLE with an available MULE
            float nearestDist = float.MaxValue;
            MULE_Pawn bestMule = null;
            Building_Stable bestStable = null;

            foreach (var stable in GetStablesOnMap(map))
            {
                if (!stable.IsPoweredOn()) continue;

                var mule = stable.GetAvailableMule(task);
                if (mule != null)
                {
                    float dist = task.targetCell.DistanceTo(stable.Position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        bestMule = mule;
                        bestStable = stable;
                    }
                }
            }

            return (bestMule, bestStable);
        }

        #endregion

        #region Network Connectivity

        /// <summary>
        /// Checks if a world tile has network connectivity to LATTICE.
        /// Home tile (where LATTICE is) always connected.
        /// Remote tiles need SKYLINK satellite operational + powered HERALD.
        /// </summary>
        public static bool IsTileConnected(int worldTile)
        {
            // No LATTICE = no network
            var lattice = GlobalLattice;
            if (lattice == null)
                return false;

            // LATTICE not powered = no network
            if (!lattice.IsPoweredOn())
                return false;

            // Home tile (where LATTICE is) = always connected
            if (lattice.Map != null && lattice.Map.Tile == worldTile)
                return true;

            // Remote tiles need SKYLINK operational
            if (!IsLatticeConnectedToSkylink())
                return false;

            // Remote tiles need a powered HERALD
            if (heraldsPerTile.TryGetValue(worldTile, out var herald))
                return herald != null && !herald.Destroyed && herald.IsOnline;

            return false;
        }

        /// <summary>
        /// Gets a user-friendly network status message for a given tile.
        /// </summary>
        public static string GetNetworkStatus(int worldTile)
        {
            var lattice = GlobalLattice;
            if (lattice == null)
                return "OFFLINE — No LATTICE";

            if (!lattice.IsPoweredOn())
                return "OFFLINE — LATTICE unpowered";

            if (lattice.Map != null && lattice.Map.Tile == worldTile)
                return "ONLINE (direct)";

            // Remote tile checks
            if (!IsSatelliteInOrbit())
                return "OFFLINE — No SKYLINK satellite";

            if (!IsLatticeConnectedToSkylink())
                return "OFFLINE — No Terminal link to LATTICE";

            if (heraldsPerTile.TryGetValue(worldTile, out var herald))
            {
                if (herald != null && !herald.Destroyed && herald.IsOnline)
                    return "ONLINE (via SKYLINK → HERALD)";
                else
                    return "OFFLINE — HERALD unpowered";
            }

            return "OFFLINE — No HERALD on this tile";
        }

        #endregion

        #region HERALD Registration

        public static void RegisterHerald(Building_HERALD herald)
        {
            if (herald?.Map == null) return;
            int tile = herald.Map.Tile;
            heraldsPerTile[tile] = herald;
        }

        public static void DeregisterHerald(Building_HERALD herald)
        {
            if (herald?.Map == null) return;
            int tile = herald.Map.Tile;
            if (heraldsPerTile.TryGetValue(tile, out var existing) && existing == herald)
            {
                heraldsPerTile.Remove(tile);
            }
        }

        public static Building_HERALD GetHeraldAtTile(int tile)
        {
            if (heraldsPerTile.TryGetValue(tile, out var herald))
            {
                if (herald != null && !herald.Destroyed)
                    return herald;
                heraldsPerTile.Remove(tile);
            }
            return null;
        }

        #endregion

        #region ARGUS Registration

        public static void RegisterArgus(Building_ARGUS argus)
        {
            if (!argusUnits.Contains(argus))
                argusUnits.Add(argus);
        }

        public static void DeregisterArgus(Building_ARGUS argus)
        {
            argusUnits.Remove(argus);
        }

        public static List<Building_ARGUS> GetAllArgus()
        {
            argusUnits.RemoveAll(a => a == null || a.Destroyed);
            return argusUnits.ToList();
        }

        public static List<Building_ARGUS> GetArgusOnMap(Map map)
        {
            if (map == null) return new List<Building_ARGUS>();
            return GetAllArgus().Where(a => a.Map == map).ToList();
        }

        #endregion

        #region PERCH Registration (SLING Logistics)

        public static void RegisterPerch(Building_PERCH perch)
        {
            if (!perches.Contains(perch))
                perches.Add(perch);
        }

        public static void DeregisterPerch(Building_PERCH perch)
        {
            perches.Remove(perch);
        }

        public static List<Building_PERCH> GetAllPerches()
        {
            perches.RemoveAll(p => p == null || p.Destroyed || !p.Spawned);
            return perches.ToList();
        }

        public static Building_PERCH GetPerchAtTile(int tile)
        {
            return GetAllPerches().FirstOrDefault(p => p.Map != null && p.Map.Tile == tile);
        }

        public static List<Building_PERCH> GetAllPerchesAtTile(int tile)
        {
            return GetAllPerches().Where(p => p.Map != null && p.Map.Tile == tile).ToList();
        }

        public static List<Building_PERCH> GetPerchesOnMap(Map map)
        {
            if (map == null) return new List<Building_PERCH>();
            return GetAllPerches().Where(p => p.Map == map).ToList();
        }

        public static List<Building_PERCH> GetSourcePerches()
        {
            return GetAllPerches().Where(p => p.role == PerchRole.SOURCE).ToList();
        }

        public static List<Building_PERCH> GetSinkPerches()
        {
            return GetAllPerches().Where(p => p.role == PerchRole.SINK).ToList();
        }

        #endregion

        #region PERCH Beacon Registration (New Landing Beacons)

        public static void RegisterPerchBeacon(Building_PerchBeacon beacon)
        {
            if (!perchBeacons.Contains(beacon))
                perchBeacons.Add(beacon);
        }

        public static void DeregisterPerchBeacon(Building_PerchBeacon beacon)
        {
            perchBeacons.Remove(beacon);
        }

        public static List<Building_PerchBeacon> GetAllPerchBeacons()
        {
            perchBeacons.RemoveAll(b => b == null || b.Destroyed || !b.Spawned);
            return perchBeacons.ToList();
        }

        public static List<Building_PerchBeacon> GetPerchBeaconsOnMap(Map map)
        {
            if (map == null) return new List<Building_PerchBeacon>();
            return GetAllPerchBeacons().Where(b => b.Map == map).ToList();
        }

        public static List<Building_PerchBeacon> GetPerchBeaconsAtTile(int tile)
        {
            return GetAllPerchBeacons().Where(b => b.Map != null && b.Map.Tile == tile).ToList();
        }

        public static Building_PerchBeacon GetBeaconWithLandingZone(int tile)
        {
            // Returns a beacon that has a valid landing zone at this tile
            return GetPerchBeaconsAtTile(tile).FirstOrDefault(b => b.HasValidLandingZone);
        }

        public static List<Building_PerchBeacon> GetSourceBeacons()
        {
            return GetAllPerchBeacons().Where(b => b.IsSource && b.HasValidLandingZone).ToList();
        }

        public static List<Building_PerchBeacon> GetSinkBeacons()
        {
            return GetAllPerchBeacons().Where(b => b.IsSink && b.HasValidLandingZone).ToList();
        }

        #endregion

        public static void RegisterArsenal(Building_Arsenal arsenal)
        {
            if (!arsenals.Contains(arsenal))
                arsenals.Add(arsenal);
        }

        public static void DeregisterArsenal(Building_Arsenal arsenal)
        {
            arsenals.Remove(arsenal);
        }

        public static void RegisterHub(Building_Hub hub)
        {
            if (!hubs.Contains(hub))
                hubs.Add(hub);
        }

        public static void DeregisterHub(Building_Hub hub)
        {
            hubs.Remove(hub);
        }

        public static void RegisterHop(Building_Hop hop)
        {
            if (!hops.Contains(hop))
                hops.Add(hop);
        }

        public static void DeregisterHop(Building_Hop hop)
        {
            hops.Remove(hop);
        }

        // LATTICE registration
        public static void RegisterLattice(Building_Lattice lattice)
        {
            if (!lattices.Contains(lattice))
                lattices.Add(lattice);
        }

        public static void DeregisterLattice(Building_Lattice lattice)
        {
            lattices.Remove(lattice);
        }

        // QUIVER registration
        public static void RegisterQuiver(Building_Quiver quiver)
        {
            if (!quivers.Contains(quiver))
                quivers.Add(quiver);
        }

        public static void DeregisterQuiver(Building_Quiver quiver)
        {
            quivers.Remove(quiver);
        }

        public static List<Building_Arsenal> GetAllArsenals()
        {
            arsenals.RemoveAll(a => a == null || a.Destroyed || !a.Spawned);
            return arsenals.ToList();
        }

        public static List<Building_Hub> GetAllHubs()
        {
            hubs.RemoveAll(h => h == null || h.Destroyed || !h.Spawned);
            return hubs.ToList();
        }

        public static List<Building_Hop> GetAllHops()
        {
            hops.RemoveAll(h => h == null || h.Destroyed || !h.Spawned);
            return hops.ToList();
        }

        public static List<Building_Lattice> GetAllLattices()
        {
            lattices.RemoveAll(l => l == null || l.Destroyed || !l.Spawned);
            return lattices.ToList();
        }

        public static List<Building_Quiver> GetAllQuivers()
        {
            quivers.RemoveAll(q => q == null || q.Destroyed || !q.Spawned);
            return quivers.ToList();
        }

        public static Building_Hub GetHubAtTile(int tile)
        {
            return GetAllHubs().FirstOrDefault(h => h.Map != null && h.Map.Tile == tile);
        }

        public static Building_Hop GetHopAtTile(int tile)
        {
            return GetAllHops().FirstOrDefault(h => h.Map != null && h.Map.Tile == tile);
        }

        // NEW: Get an available (not refueling) HOP at a specific tile
        public static Building_Hop GetAvailableHopAtTile(int tile)
        {
            return GetAllHops().FirstOrDefault(h =>
                h.Map != null &&
                h.Map.Tile == tile &&
                h.CanAcceptMissile());
        }

        // NEW: Get all HOPs at a specific tile
        public static List<Building_Hop> GetAllHopsAtTile(int tile)
        {
            return GetAllHops().Where(h => h.Map != null && h.Map.Tile == tile).ToList();
        }

        // Get LATTICE on a specific map (only one allowed per map)
        public static Building_Lattice GetLatticeOnMap(Map map)
        {
            if (map == null) return null;
            return GetAllLattices().FirstOrDefault(l => l.Map == map);
        }

        /// <summary>
        /// Gets the network LATTICE for a map if that map has network connectivity.
        /// Returns local LATTICE if present, otherwise GlobalLattice if tile is connected via SKYLINK/HERALD.
        /// </summary>
        public static Building_Lattice GetConnectedLattice(Map map)
        {
            if (map == null) return null;

            // First check for local LATTICE
            var localLattice = GetLatticeOnMap(map);
            if (localLattice != null && localLattice.IsPoweredOn())
                return localLattice;

            // Check if tile has network connectivity to GlobalLattice
            if (IsTileConnected(map.Tile))
                return GlobalLattice;

            return null;
        }

        // Get all QUIVERs on a specific map
        public static List<Building_Quiver> GetQuiversOnMap(Map map)
        {
            if (map == null) return new List<Building_Quiver>();
            return GetAllQuivers().Where(q => q.Map == map).ToList();
        }

        // Get ARSENAL on a specific map (for DART manufacturing)
        public static Building_Arsenal GetArsenalOnMap(Map map)
        {
            if (map == null) return null;
            return GetAllArsenals().FirstOrDefault(a => a.Map == map);
        }

        public static void Reset()
        {
            arsenals.Clear();
            hubs.Clear();
            hops.Clear();
            lattices.Clear();
            quivers.Clear();
            argusUnits.Clear();
            heraldsPerTile.Clear();
            orbitalSatellite = null;
            terminals.Clear();
            hawkeyePawns.Clear();
            perches.Clear();
            perchBeacons.Clear();
            mules.Clear();
            stables.Clear();
            morias.Clear();
        }

        /// <summary>
        /// Rescans all maps to find and register all ARSENAL network buildings.
        /// Called after Reset() during game load to restore registrations that were
        /// cleared after buildings already registered in SpawnSetup().
        /// </summary>
        public static void RescanAllBuildings()
        {
            if (Current.Game?.Maps == null) return;

            int foundCount = 0;

            foreach (Map map in Current.Game.Maps)
            {
                if (map == null) continue;

                foreach (Building building in map.listerBuildings.allBuildingsColonist)
                {
                    if (building == null || building.Destroyed) continue;

                    // Register each building type
                    if (building is Building_Arsenal arsenal)
                    {
                        if (!arsenals.Contains(arsenal))
                        {
                            arsenals.Add(arsenal);
                            foundCount++;
                        }
                    }
                    else if (building is Building_Hub hub)
                    {
                        if (!hubs.Contains(hub))
                        {
                            hubs.Add(hub);
                            foundCount++;
                        }
                    }
                    else if (building is Building_Hop hop)
                    {
                        if (!hops.Contains(hop))
                        {
                            hops.Add(hop);
                            foundCount++;
                        }
                    }
                    else if (building is Building_Lattice lattice)
                    {
                        if (!lattices.Contains(lattice))
                        {
                            lattices.Add(lattice);
                            foundCount++;
                        }
                    }
                    else if (building is Building_Quiver quiver)
                    {
                        if (!quivers.Contains(quiver))
                        {
                            quivers.Add(quiver);
                            foundCount++;
                        }
                    }
                    else if (building is Building_ARGUS argus)
                    {
                        if (!argusUnits.Contains(argus))
                        {
                            argusUnits.Add(argus);
                            foundCount++;
                        }
                    }
                    else if (building is Building_HERALD herald)
                    {
                        int tile = map.Tile;
                        heraldsPerTile[tile] = herald;
                        foundCount++;
                    }
                    else if (building is Building_SkyLinkTerminal terminal)
                    {
                        if (!terminals.Contains(terminal))
                        {
                            terminals.Add(terminal);
                            foundCount++;
                        }
                    }
                    else if (building is Building_PERCH perch)
                    {
                        if (!perches.Contains(perch))
                        {
                            perches.Add(perch);
                            foundCount++;
                        }
                    }
                    else if (building is Building_Stable stable)
                    {
                        if (!stables.Contains(stable))
                        {
                            stables.Add(stable);
                            foundCount++;
                        }
                    }
                    else if (building is Building_Moria moria)
                    {
                        if (!morias.Contains(moria))
                        {
                            morias.Add(moria);
                            foundCount++;
                        }
                    }
                }

                // Also check for spawned MULEs on map
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn is MULE_Pawn mule && !mules.Contains(mule))
                    {
                        mules.Add(mule);
                        foundCount++;
                    }
                }

                // Check for HAWKEYE-wearing pawns
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.apparel?.WornApparel?.Any(a => a is Apparel_HawkEye) == true)
                    {
                        if (!hawkeyePawns.Contains(pawn))
                        {
                            hawkeyePawns.Add(pawn);
                            foundCount++;
                        }
                    }
                }
            }

            if (foundCount > 0)
            {
                Log.Message($"[ARSENAL] RescanAllBuildings: Registered {foundCount} network components after game load.");
            }

            // Recalculate static counters based on existing building names
            RecalculateAllCounters();
        }

        /// <summary>
        /// Parses a building name to extract the numeric ID.
        /// Expected format: "PREFIX-XX" where XX is the ID.
        /// Returns 0 if parsing fails.
        /// </summary>
        private static int ExtractIdFromName(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix + "-"))
                return 0;

            string numPart = name.Substring(prefix.Length + 1);
            if (int.TryParse(numPart, out int id))
                return id;
            return 0;
        }

        /// <summary>
        /// Recalculates all building counters based on existing building names.
        /// This prevents duplicate names when creating new buildings after loading a game.
        /// </summary>
        public static void RecalculateAllCounters()
        {
            // Calculate max IDs for each building type
            int maxHub = 0, maxHop = 0, maxArsenal = 0;
            int maxQuiver = 0, maxArgus = 0, maxHerald = 0;
            int maxPerch = 0, maxStable = 0, maxMoria = 0, maxMule = 0;
            int maxSling = 0;

            foreach (var hub in hubs)
                maxHub = System.Math.Max(maxHub, ExtractIdFromName(hub.Label, "HUB"));
            foreach (var hop in hops)
                maxHop = System.Math.Max(maxHop, ExtractIdFromName(hop.Label, "HOP"));
            foreach (var arsenal in arsenals)
                maxArsenal = System.Math.Max(maxArsenal, ExtractIdFromName(arsenal.Label, "ARSENAL"));
            foreach (var quiver in quivers)
                maxQuiver = System.Math.Max(maxQuiver, ExtractIdFromName(quiver.Label, "QUIVER"));
            foreach (var argus in argusUnits)
                maxArgus = System.Math.Max(maxArgus, ExtractIdFromName(argus.Label, "ARGUS"));
            foreach (var kvp in heraldsPerTile)
                maxHerald = System.Math.Max(maxHerald, ExtractIdFromName(kvp.Value.Label, "HERALD"));
            foreach (var perch in perches)
                maxPerch = System.Math.Max(maxPerch, ExtractIdFromName(perch.Label, "PERCH"));
            foreach (var stable in stables)
                maxStable = System.Math.Max(maxStable, ExtractIdFromName(stable.Label, "STABLE"));
            foreach (var moria in morias)
                maxMoria = System.Math.Max(maxMoria, ExtractIdFromName(moria.Label, "MORIA"));
            foreach (var mule in mules)
                maxMule = System.Math.Max(maxMule, ExtractIdFromName(mule.Label, "MULE"));

            // Also check docked mules in stables
            foreach (var stable in stables)
            {
                foreach (var mule in stable.DockedMules)
                    maxMule = System.Math.Max(maxMule, ExtractIdFromName(mule.Label, "MULE"));
            }

            // Check SLINGs on perches and in flight
            foreach (var perch in perches)
            {
                var slot1Sling = perch.Slot1Sling;
                var slot2Sling = perch.Slot2Sling;
                if (slot1Sling != null)
                    maxSling = System.Math.Max(maxSling, ExtractIdFromName(slot1Sling.Label, "SLING"));
                if (slot2Sling != null)
                    maxSling = System.Math.Max(maxSling, ExtractIdFromName(slot2Sling.Label, "SLING"));
            }

            // Set counters to max + 1 (minimum 1)
            Building_Hub.SetCounter(maxHub + 1);
            Building_Hop.SetCounter(maxHop + 1);
            Building_Arsenal.SetCounter(maxArsenal + 1);
            Building_Quiver.SetCounter(maxQuiver + 1);
            Building_ARGUS.SetCounter(maxArgus + 1);
            Building_HERALD.SetCounter(maxHerald + 1);
            Building_PERCH.SetCounter(maxPerch + 1);
            Building_Stable.SetCounter(maxStable + 1);
            Building_Moria.SetCounter(maxMoria + 1);
            MULE_Pawn.SetCounter(maxMule + 1);
            SLING_Thing.SetCounter(maxSling + 1);
        }

        /// <summary>
        /// Scans all world objects for SKYLINK satellites and registers them.
        /// Called after game load to ensure satellites are properly tracked.
        /// Always re-registers to handle stale references.
        /// </summary>
        public static void ScanForOrbitingSatellites()
        {
            if (Find.WorldObjects == null) return;

            // Clear any stale reference first
            if (orbitalSatellite != null && (orbitalSatellite.Destroyed || !Find.WorldObjects.Contains(orbitalSatellite)))
            {
                Log.Message("[ARSENAL] Clearing stale satellite reference.");
                orbitalSatellite = null;
            }

            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is WorldObject_SkyLinkSatellite satellite && !satellite.Destroyed)
                {
                    // Always register valid satellite (handles both new detection and re-registration)
                    if (orbitalSatellite != satellite)
                    {
                        orbitalSatellite = satellite;
                        Log.Message($"[ARSENAL] SKYLINK satellite registered. IsOperational: {satellite.IsOperational}");
                    }
                    break; // Only one satellite allowed
                }
            }

            if (orbitalSatellite == null)
            {
                Log.Message("[ARSENAL] No SKYLINK satellite found in orbit.");
            }
        }
    }

    public class GameComponent_ArsenalNetwork : GameComponent
    {
        public GameComponent_ArsenalNetwork(Game game) { }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            ArsenalNetworkManager.Reset();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            // CRITICAL: Reset static state when loading a game to prevent
            // state from previous sessions carrying over.
            ArsenalNetworkManager.Reset();

            // IMPORTANT: Buildings already called SpawnSetup() BEFORE LoadedGame(),
            // so their registrations were just cleared by Reset().
            // We must rescan to re-register all buildings on all maps.
            ArsenalNetworkManager.RescanAllBuildings();

            // CRITICAL: Scan for satellites IMMEDIATELY after buildings rescan.
            // This ensures satellite is registered before any other components
            // check for network connectivity (which depends on satellite).
            ArsenalNetworkManager.ScanForOrbitingSatellites();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // After game fully loads, scan for any satellites that may have been
            // loaded from save but not yet registered (edge case handling)
            ArsenalNetworkManager.ScanForOrbitingSatellites();

            // Safety net: rescan all buildings in case any were missed
            // This handles edge cases like late-loading maps
            ArsenalNetworkManager.RescanAllBuildings();
        }
    }
}