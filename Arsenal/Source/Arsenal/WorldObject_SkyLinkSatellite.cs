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
    /// Renders in orbit layer (like space stations/asteroids from Odyssey) not on world map tile.
    /// </summary>
    public class WorldObject_SkyLinkSatellite : WorldObject
    {
        private int launchTick = -1;
        private float health = 100f;
        private const float MAX_HEALTH = 100f;

        // Orbit animation - satellite orbits around the planet
        private float orbitAngle = 0f;
        private const float ORBIT_SPEED = 0.3f; // Degrees per game-tick at normal speed

        // Orbit parameters - matches Odyssey-style orbital layer
        // Public so WorldLayer can use consistent values
        public const float ORBIT_RADIUS = 115f; // Distance from world center (world radius ~100)
        public const float ORBIT_HEIGHT = 10f;   // Height above equatorial plane
        private const float ICON_SIZE = 8f;       // Size of the satellite icon

        // Cached material
        private Material cachedMat;

        public bool IsOperational => health > 0f;
        public float HealthPercent => health / MAX_HEALTH;

        public override void SpawnSetup()
        {
            base.SpawnSetup();

            if (launchTick < 0)
            {
                launchTick = Find.TickManager.TicksGame;
                orbitAngle = Rand.Range(0f, 360f); // Random starting position in orbit
            }

            // Register with network manager
            ArsenalNetworkManager.RegisterSatellite(this);

            Log.Message($"[ARSENAL] SKYLINK satellite spawned in orbit at angle {orbitAngle:F1}°");
        }

        protected override void Tick()
        {
            base.Tick();

            // Slowly orbit around the planet
            orbitAngle += ORBIT_SPEED;
            if (orbitAngle >= 360f)
                orbitAngle -= 360f;
        }

        /// <summary>
        /// Calculate the 3D position in orbit around the world globe.
        /// Public so WorldLayer can access for rendering.
        /// </summary>
        public Vector3 GetOrbitalPosition()
        {
            float radians = orbitAngle * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Sin(radians) * ORBIT_RADIUS,
                ORBIT_HEIGHT,
                Mathf.Cos(radians) * ORBIT_RADIUS
            );
        }

        /// <summary>
        /// Draw the satellite in orbit around the planet.
        /// Renders in the orbital layer like Odyssey space stations/asteroids.
        /// </summary>
        public override void Draw()
        {
            // Don't call base.Draw() - we render at orbital position, not at tile

            Vector3 pos = GetOrbitalPosition();

            // Get or create material
            Material mat = this.Material;
            if (mat == null)
            {
                // Fallback: create a simple colored material if texture missing
                if (cachedMat == null)
                {
                    cachedMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.5f, 1f, 1f));
                }
                mat = cachedMat;
            }

            // Draw satellite icon facing camera
            Vector3 scale = new Vector3(ICON_SIZE, 1f, ICON_SIZE);
            Quaternion rotation = Quaternion.LookRotation(pos.normalized, Vector3.up);

            Matrix4x4 matrix = Matrix4x4.TRS(pos, rotation, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, WorldCameraManager.WorldLayer);
        }

        /// <summary>
        /// Override DrawPos for selection and clicking - returns orbital position.
        /// </summary>
        public override Vector3 DrawPos => GetOrbitalPosition();

        /// <summary>
        /// Override to allow selection even though we're not at a standard tile position.
        /// </summary>
        public override bool SelectableNow => true;

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
