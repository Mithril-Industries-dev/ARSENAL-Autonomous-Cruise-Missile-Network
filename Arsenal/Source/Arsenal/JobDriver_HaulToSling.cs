using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace Arsenal
{
    /// <summary>
    /// Job for hauling items to a loading SLING cargo craft.
    /// </summary>
    public class JobDriver_HaulToSling : JobDriver
    {
        private const TargetIndex ItemInd = TargetIndex.A;
        private const TargetIndex SlingInd = TargetIndex.B;

        private Thing Item => job.GetTarget(ItemInd).Thing;
        private SLING_Thing Sling => job.GetTarget(SlingInd).Thing as SLING_Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Item, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Fail conditions
            this.FailOnDestroyedOrNull(ItemInd);
            this.FailOnDestroyedOrNull(SlingInd);
            this.FailOn(() => Sling == null || !Sling.IsLoading);
            this.FailOn(() => !Sling.WantsItem(Item?.def));

            // Go to item
            yield return Toils_Goto.GotoThing(ItemInd, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(ItemInd);

            // Pick up item
            yield return Toils_Haul.StartCarryThing(ItemInd, putRemainderInQueue: false, subtractNumTakenFromJobCount: true);

            // Carry to SLING
            yield return Toils_Goto.GotoThing(SlingInd, PathEndMode.Touch);

            // Deliver to SLING
            Toil deliverToil = new Toil();
            deliverToil.initAction = delegate
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                if (Sling == null || !Sling.IsLoading)
                {
                    // SLING no longer loading - drop the item
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                // Try to add cargo to SLING
                int added = Sling.TryAddCargo(carried);
                if (added > 0)
                {
                    // If we only added part of the stack, the rest is still in carryTracker
                    if (pawn.carryTracker.CarriedThing != null)
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                }
                else
                {
                    // Couldn't add - drop it
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                }
            };
            deliverToil.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return deliverToil;
        }
    }

    /// <summary>
    /// WorkGiver for hauling items to loading SLING cargo craft.
    /// </summary>
    public class WorkGiver_HaulToSling : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableAlways);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t.IsForbidden(pawn)) return false;
            if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced)) return false;

            // Find a loading SLING that wants this item
            SLING_Thing sling = FindLoadingSlingForItem(pawn, t);
            return sling != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            SLING_Thing sling = FindLoadingSlingForItem(pawn, t);
            if (sling == null) return null;

            Job job = JobMaker.MakeJob(ArsenalDefOf.Arsenal_HaulToSling, t, sling);
            job.count = sling.GetRemainingNeeded(t.def);
            return job;
        }

        private SLING_Thing FindLoadingSlingForItem(Pawn pawn, Thing item)
        {
            // Find all PERCHes with loading SLINGs
            foreach (var perch in ArsenalNetworkManager.GetAllPerches())
            {
                if (perch.Map != pawn.Map) continue;

                // Use LoadingSling property which directly returns the slot1 SLING if loading
                var sling = perch.LoadingSling;
                if (sling == null) continue;
                if (!sling.WantsItem(item.def)) continue;

                // Check if pawn can reach the SLING
                if (!pawn.CanReach(sling, PathEndMode.Touch, Danger.Deadly)) continue;

                return sling;
            }

            return null;
        }
    }
}
