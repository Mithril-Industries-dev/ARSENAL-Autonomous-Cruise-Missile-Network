using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace Arsenal
{
    public class WorkGiver_HaulToArsenal : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableEver);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t.IsForbidden(pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced))
                return false;

            // Find an arsenal that needs this resource
            Building_Arsenal arsenal = FindArsenalNeedingResource(t, pawn);
            if (arsenal == null)
                return false;

            // Can pawn reach it?
            if (!pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, pawn.NormalMaxDanger(), 1, -1, null, forced))
                return false;

            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_Arsenal arsenal = FindArsenalNeedingResource(t, pawn);
            if (arsenal == null)
                return null;

            int needed = arsenal.GetNeededCount(t.def);
            int toHaul = UnityEngine.Mathf.Min(t.stackCount, needed);

            Job job = JobMaker.MakeJob(ArsenalDefOf.Arsenal_HaulToArsenal, t, arsenal);
            job.count = toHaul;
            return job;
        }

        private Building_Arsenal FindArsenalNeedingResource(Thing t, Pawn pawn)
        {
            // Check if this is a resource type we care about
            if (t.def != ThingDefOf.Plasteel && 
                t.def != ThingDefOf.Gold && 
                t.def != ThingDefOf.ComponentSpacer && 
                t.def != ThingDefOf.Chemfuel)
                return null;

            // Find closest arsenal that needs this resource
            Building_Arsenal closest = null;
            float closestDist = float.MaxValue;

            foreach (Building_Arsenal arsenal in ArsenalNetworkManager.GetAllArsenals())
            {
                if (arsenal.Map != pawn.Map)
                    continue;

                if (!arsenal.AcceptsResource(t.def))
                    continue;

                if (!pawn.CanReserveAndReach(arsenal, PathEndMode.Touch, pawn.NormalMaxDanger()))
                    continue;

                float dist = t.Position.DistanceTo(arsenal.Position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = arsenal;
                }
            }

            return closest;
        }
    }
}
