using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// SKYLINK Orbital Communications Satellite - persistent world object in orbit.
    /// Enables global MITHRIL network operations when connected via Terminal to LATTICE.
    /// Only one satellite can be in orbit at any time.
    /// </summary>
    public class WorldObject_SkyLinkSatellite : WorldObject
    {
        private int launchTick = -1;
        private float health = 100f;
        private const float MAX_HEALTH = 100f;

        public bool IsOperational => health > 0f;
        public float HealthPercent => health / MAX_HEALTH;

        public override void SpawnSetup()
        {
            base.SpawnSetup();

            if (launchTick < 0)
                launchTick = Find.TickManager.TicksGame;

            // Register with network manager
            ArsenalNetworkManager.RegisterSatellite(this);
        }

        public override void PostRemove()
        {
            ArsenalNetworkManager.DeregisterSatellite(this);
            base.PostRemove();
        }

        public override void Destroy()
        {
            ArsenalNetworkManager.DeregisterSatellite(this);
            base.Destroy();
        }

        /// <summary>
        /// Damages the satellite. If health reaches 0, it is destroyed.
        /// </summary>
        public void TakeDamage(float damage)
        {
            health -= damage;
            if (health <= 0f)
            {
                health = 0f;
                Messages.Message("SKYLINK satellite has been destroyed! Global network connectivity lost.",
                    MessageTypeDefOf.ThreatSmall);
                Destroy();
            }
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!str.NullOrEmpty()) str += "\n";
            str += $"Operational: {(IsOperational ? "Yes" : "No")}";
            str += $"\nHealth: {health:F0}/{MAX_HEALTH:F0} ({HealthPercent:P0})";

            int daysSinceLaunch = (Find.TickManager.TicksGame - launchTick) / GenDate.TicksPerDay;
            str += $"\nDays in orbit: {daysSinceLaunch}";

            // Show connection status
            if (ArsenalNetworkManager.IsLatticeConnectedToSkylink())
            {
                str += "\nLATTICE Connection: ONLINE";
            }
            else
            {
                str += "\nLATTICE Connection: OFFLINE (no Terminal link)";
            }

            return str;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref launchTick, "launchTick", -1);
            Scribe_Values.Look(ref health, "health", MAX_HEALTH);
        }
    }
}
