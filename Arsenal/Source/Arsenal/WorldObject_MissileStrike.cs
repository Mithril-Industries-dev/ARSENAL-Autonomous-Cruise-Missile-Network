using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace Arsenal
{
    public class WorldObject_MissileStrike : WorldObject
    {
        public int destinationTile = -1;
        public int arrivalTick = -1;
        public IntVec3 targetCell = IntVec3.Invalid;
        public string sourceHubLabel = "";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref destinationTile, "destinationTile", -1);
            Scribe_Values.Look(ref arrivalTick, "arrivalTick", -1);
            Scribe_Values.Look(ref targetCell, "targetCell", IntVec3.Invalid);
            Scribe_Values.Look(ref sourceHubLabel, "sourceHubLabel", "");
        }

        protected override void Tick()
        {
            base.Tick();
            if (Find.TickManager.TicksGame >= arrivalTick)
                ExecuteStrike();
        }

        private void ExecuteStrike()
        {
            MapParent mp = Find.WorldObjects.MapParentAt(destinationTile);
            if (mp?.Map != null)
            {
                Map map = mp.Map;
                
                IntVec3 cell = targetCell.IsValid ? targetCell : DropCellFinder.RandomDropSpot(map);
                
                // Spawn incoming missile skyfaller - this creates the visual!
                MissileStrikeSkyfaller skyfaller = (MissileStrikeSkyfaller)SkyfallerMaker.MakeSkyfaller(
                    ArsenalDefOf.Arsenal_MissileStrikeIncoming);
                skyfaller.sourceHubLabel = sourceHubLabel;
                skyfaller.explosionDamage = 250;
                skyfaller.explosionRadius = 12f;
                
                GenSpawn.Spawn(skyfaller, cell, map);
                
                // Jump camera to see the incoming strike
                CameraJumper.TryJump(new TargetInfo(cell, map));
            }
            else
            {
                Messages.Message("Cruise missile strike completed at target location.", 
                    new GlobalTargetInfo(destinationTile), MessageTypeDefOf.NeutralEvent);
            }
            Destroy();
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();
            str += "\nSource: " + sourceHubLabel;
            int remaining = arrivalTick - Find.TickManager.TicksGame;
            if (remaining > 0)
                str += "\nTime to impact: " + remaining.ToStringTicksToPeriod();
            if (targetCell.IsValid)
                str += "\nPrecision strike";
            return str;
        }
    }
}