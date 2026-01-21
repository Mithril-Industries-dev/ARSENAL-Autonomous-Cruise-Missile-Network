using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// WorldComponent that renders the SKYLINK satellite in orbit.
    /// Uses WorldComponentUpdate to draw every frame when on the world map.
    /// </summary>
    [StaticConstructorOnStartup]
    public class WorldComponent_SkyLinkRenderer : WorldComponent
    {
        // Satellite icon material
        private static Material satelliteMat;
        private static Material ringMat;
        private static readonly float ICON_SIZE = 6f;

        static WorldComponent_SkyLinkRenderer()
        {
            // Load satellite texture or use fallback
            Texture2D tex = ContentFinder<Texture2D>.Get("World/WorldObjects/Arsenal/SkyLinkSatellite", false);
            if (tex != null)
            {
                satelliteMat = MaterialPool.MatFrom(tex, ShaderDatabase.WorldOverlayTransparentLit, Color.white, WorldMaterials.WorldObjectRenderQueue);
            }
            else
            {
                // Fallback: bright blue for visibility
                satelliteMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.6f, 1f, 1f));
            }

            // Orbit ring material
            ringMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.5f, 0.8f, 0.15f));
        }

        public WorldComponent_SkyLinkRenderer(World world) : base(world)
        {
        }

        /// <summary>
        /// Called every frame when on world map. Draws the orbiting satellite.
        /// </summary>
        public override void WorldComponentUpdate()
        {
            base.WorldComponentUpdate();

            // Only render when on world map
            if (!WorldRendererUtility.WorldRenderedNow)
                return;

            // Get satellite from network manager
            var satellite = ArsenalNetworkManager.GetOrbitalSatellite();
            if (satellite == null || !satellite.IsOperational)
                return;

            // Get orbital position from satellite
            Vector3 pos = satellite.GetOrbitalPosition();

            // Draw satellite facing camera
            Vector3 cameraPos = Find.WorldCamera.transform.position;
            Quaternion rotation = Quaternion.LookRotation(cameraPos - pos);
            Vector3 scale = new Vector3(ICON_SIZE, 1f, ICON_SIZE);

            Matrix4x4 matrix = Matrix4x4.TRS(pos, rotation, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, satelliteMat, WorldCameraManager.WorldLayer);

            // Draw orbit ring
            DrawOrbitRing();
        }

        /// <summary>
        /// Draws a faint ring showing the satellite's orbital path.
        /// </summary>
        private void DrawOrbitRing()
        {
            const int segments = 72;
            float radius = WorldObject_SkyLinkSatellite.ORBIT_RADIUS;
            float height = WorldObject_SkyLinkSatellite.ORBIT_HEIGHT;

            for (int i = 0; i < segments; i++)
            {
                float angle1 = (i / (float)segments) * 360f * Mathf.Deg2Rad;
                float angle2 = ((i + 1) / (float)segments) * 360f * Mathf.Deg2Rad;

                Vector3 p1 = new Vector3(
                    Mathf.Sin(angle1) * radius,
                    height,
                    Mathf.Cos(angle1) * radius
                );

                Vector3 p2 = new Vector3(
                    Mathf.Sin(angle2) * radius,
                    height,
                    Mathf.Cos(angle2) * radius
                );

                // Draw line segment
                GenDraw.DrawWorldLineBetween(p1, p2, ringMat);
            }
        }
    }
}
