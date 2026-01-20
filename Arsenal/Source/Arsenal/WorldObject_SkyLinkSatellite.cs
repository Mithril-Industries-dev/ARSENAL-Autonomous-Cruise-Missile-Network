using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// SKYLINK Orbital Communications Satellite - persistent world object in orbit.
    /// Enables global MITHRIL network operations when connected via Terminal to LATTICE.
    /// Only one satellite can be in orbit at any time.
    /// Renders in orbit layer (like space stations/asteroids) not on world map.
    /// </summary>
    public class WorldObject_SkyLinkSatellite : WorldObject
    {
        private int launchTick = -1;
        private float health = 100f;
        private const float MAX_HEALTH = 100f;

        // Orbit animation
        private float orbitAngle = 0f;
        private const float ORBIT_SPEED = 0.5f; // Degrees per tick at normal speed

        public bool IsOperational => health > 0f;
        public float HealthPercent => health / MAX_HEALTH;

        // Override to indicate this is an orbital object
        public override bool ShowsWorldIcon => true;

        public override void SpawnSetup()
        {
            base.SpawnSetup();

            if (launchTick < 0)
            {
                launchTick = Find.TickManager.TicksGame;
                orbitAngle = Rand.Range(0f, 360f); // Random starting position
            }

            // Register with network manager
            ArsenalNetworkManager.RegisterSatellite(this);
        }

        public override void Tick()
        {
            base.Tick();

            // Slowly orbit
            orbitAngle += ORBIT_SPEED * Find.TickManager.TickRateMultiplier / 60f;
            if (orbitAngle >= 360f)
                orbitAngle -= 360f;
        }

        /// <summary>
        /// Draw the satellite in orbit around the planet.
        /// Uses the orbit layer rendering similar to asteroids/space stations.
        /// </summary>
        public override void Draw()
        {
            // Don't call base.Draw() - we handle our own rendering
            // The satellite orbits visually around the globe

            float orbitRadius = 1.15f; // Slightly outside the globe
            Vector3 center = Vector3.zero;

            // Calculate position in orbit
            float radians = orbitAngle * Mathf.Deg2Rad;
            Vector3 pos = center + new Vector3(
                Mathf.Sin(radians) * orbitRadius,
                0.3f, // Slight elevation
                Mathf.Cos(radians) * orbitRadius
            );

            // Draw the satellite icon
            float size = 0.7f;
            Material mat = def.DrawMatSingle;
            if (mat != null)
            {
                Vector3 s = new Vector3(size, 1f, size);
                Matrix4x4 matrix = default;
                matrix.SetTRS(pos, Quaternion.identity, s);
                Graphics.DrawMesh(MeshPool.plane10, matrix, mat, WorldCameraManager.WorldLayer);
            }
        }

        public override Vector3 DrawPos
        {
            get
            {
                // Return orbital position for selection/clicking
                float orbitRadius = 1.15f;
                float radians = orbitAngle * Mathf.Deg2Rad;
                return new Vector3(
                    Mathf.Sin(radians) * orbitRadius,
                    0.3f,
                    Mathf.Cos(radians) * orbitRadius
                );
            }
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
            Scribe_Values.Look(ref orbitAngle, "orbitAngle", 0f);
        }
    }
}
