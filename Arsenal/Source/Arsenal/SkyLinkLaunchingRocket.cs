using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Arsenal
{
    /// <summary>
    /// Visual effect for SKYLINK satellite launch - rocket ascends upward and despawns at map edge.
    /// </summary>
    public class SkyLinkLaunchingRocket : Thing
    {
        public Building_SkyLinkLaunchPad launchPad;

        private int ticksAlive;
        private float currentAltitude;
        private float currentSpeed;

        // Launch parameters
        private const float INITIAL_SPEED = 0.5f;
        private const float ACCELERATION = 0.02f;
        private const float MAX_SPEED = 8f;
        private const float ORBIT_ALTITUDE = 200f; // Despawn at this altitude

        // Visual parameters
        private Vector3 startPosition;
        private Sustainer rocketSustainer;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            startPosition = DrawPos;
            currentSpeed = INITIAL_SPEED;
            currentAltitude = 0f;

            // Start launch sound
            SoundInfo info = SoundInfo.InMap(this, MaintenanceType.None);
            rocketSustainer = SoundDefOf.Interact_Sow.TrySpawnSustainer(info);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            StopSound();
            base.DeSpawn(mode);
        }

        public override void Tick()
        {
            ticksAlive++;

            // Accelerate
            currentSpeed = Mathf.Min(currentSpeed + ACCELERATION, MAX_SPEED);

            // Ascend
            currentAltitude += currentSpeed;

            // Spawn exhaust effect periodically
            if (ticksAlive % 5 == 0)
            {
                SpawnExhaust();
            }

            // Check if reached orbit
            if (currentAltitude >= ORBIT_ALTITUDE)
            {
                ReachOrbit();
            }
        }

        private void SpawnExhaust()
        {
            // Spawn smoke/fire effect at current position
            Vector3 exhaustPos = DrawPos;
            exhaustPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();

            FleckMaker.ThrowSmoke(exhaustPos, Map, 2f);
            FleckMaker.ThrowFireGlow(Position.ToVector3Shifted(), Map, 1.5f);
        }

        private void ReachOrbit()
        {
            StopSound();

            // Notify launch pad
            if (launchPad != null && !launchPad.Destroyed)
            {
                launchPad.OnLaunchComplete();
            }

            // Despawn the rocket visual
            Destroy();
        }

        private void StopSound()
        {
            if (rocketSustainer != null && !rocketSustainer.Ended)
            {
                rocketSustainer.End();
            }
            rocketSustainer = null;
        }

        public override void Draw()
        {
            // Calculate draw position with altitude offset
            Vector3 drawPos = startPosition;
            drawPos.y = AltitudeLayer.Skyfaller.AltitudeFor();
            drawPos.z += currentAltitude * 0.1f; // Visual upward movement

            // Scale down as it "goes higher"
            float scale = Mathf.Lerp(1f, 0.2f, currentAltitude / ORBIT_ALTITUDE);

            // Draw the rocket
            Matrix4x4 matrix = Matrix4x4.TRS(drawPos, Quaternion.identity, new Vector3(scale, 1f, scale));
            Graphics.DrawMesh(MeshPool.plane10, matrix, def.graphic.MatSingle, 0);

            // Draw exhaust trail
            DrawExhaustTrail(drawPos, scale);
        }

        private void DrawExhaustTrail(Vector3 rocketPos, float scale)
        {
            // Draw a simple exhaust trail below the rocket
            Vector3 trailPos = rocketPos;
            trailPos.z -= 1f * scale;
            trailPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();

            // Use a simple mesh for the trail
            float trailScale = scale * 0.8f;
            Matrix4x4 trailMatrix = Matrix4x4.TRS(trailPos, Quaternion.identity, new Vector3(trailScale, 1f, trailScale * 2f));

            // Draw with fire-like color
            Material trailMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(1f, 0.5f, 0.1f, 0.7f));
            Graphics.DrawMesh(MeshPool.plane10, trailMatrix, trailMat, 0);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref launchPad, "launchPad");
            Scribe_Values.Look(ref ticksAlive, "ticksAlive", 0);
            Scribe_Values.Look(ref currentAltitude, "currentAltitude", 0f);
            Scribe_Values.Look(ref currentSpeed, "currentSpeed", INITIAL_SPEED);
            Scribe_Values.Look(ref startPosition, "startPosition");
        }
    }
}
