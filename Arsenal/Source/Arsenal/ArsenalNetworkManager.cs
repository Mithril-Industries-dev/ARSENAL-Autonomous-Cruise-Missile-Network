using System.Collections.Generic;
using System.Linq;
using Verse;

namespace Arsenal
{
    public static class ArsenalNetworkManager
    {
        private static List<Building_Arsenal> arsenals = new List<Building_Arsenal>();
        private static List<Building_Hub> hubs = new List<Building_Hub>();
        private static List<Building_Hop> hops = new List<Building_Hop>();

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

        public static List<Building_Arsenal> GetAllArsenals()
        {
            arsenals.RemoveAll(a => a == null || a.Destroyed);
            return arsenals.ToList();
        }

        public static List<Building_Hub> GetAllHubs()
        {
            hubs.RemoveAll(h => h == null || h.Destroyed);
            return hubs.ToList();
        }

        public static List<Building_Hop> GetAllHops()
        {
            hops.RemoveAll(h => h == null || h.Destroyed);
            return hops.ToList();
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

        public static void Reset()
        {
            arsenals.Clear();
            hubs.Clear();
            hops.Clear();
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
        }
    }
}