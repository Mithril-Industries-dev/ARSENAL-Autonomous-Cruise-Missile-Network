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
            return mules.ToList();
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
            mules.Clear();
            stables.Clear();
            morias.Clear();
        }

        /// <summary>
        /// Scans all world objects for SKYLINK satellites and registers them.
        /// Called after game load to ensure satellites are properly tracked.
        /// </summary>
        public static void ScanForOrbitingSatellites()
        {
            if (Find.WorldObjects == null) return;

            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is WorldObject_SkyLinkSatellite satellite)
                {
                    if (orbitalSatellite == null)
                    {
                        orbitalSatellite = satellite;
                        Log.Message($"[ARSENAL] Found orbiting SKYLINK satellite, registered.");
                    }
                }
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
            // Buildings and WorldObjects will re-register themselves in SpawnSetup().
            ArsenalNetworkManager.Reset();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // After game fully loads, scan for any satellites that may have been
            // loaded from save but not yet registered (edge case handling)
            ArsenalNetworkManager.ScanForOrbitingSatellites();
        }
    }
}