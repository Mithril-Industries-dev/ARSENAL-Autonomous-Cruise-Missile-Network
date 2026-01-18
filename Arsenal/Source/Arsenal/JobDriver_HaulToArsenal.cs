using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace Arsenal
{
    public class JobDriver_HaulToArsenal : JobDriver
    {
        private const TargetIndex ResourceInd = TargetIndex.A;
        private const TargetIndex ArsenalInd = TargetIndex.B;

        private Thing Resource => job.GetTarget(ResourceInd).Thing;
        private Building_Arsenal Arsenal => job.GetTarget(ArsenalInd).Thing as Building_Arsenal;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Resource, job, 1, -1, null, errorOnFailed) &&
                   pawn.Reserve(Arsenal, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedNullOrForbidden(ResourceInd);
            this.FailOnDestroyedNullOrForbidden(ArsenalInd);
            this.FailOn(() => Arsenal == null || !Arsenal.acceptDeliveries);

            // Go to resource
            yield return Toils_Goto.GotoThing(ResourceInd, PathEndMode.ClosestTouch)
                .FailOnSomeonePhysicallyInteracting(ResourceInd);

            // Pick up resource
            yield return Toils_Haul.StartCarryThing(ResourceInd, false, true, false);

            // Go to arsenal
            yield return Toils_Goto.GotoThing(ArsenalInd, PathEndMode.Touch);

            // Deposit resource
            Toil deposit = new Toil();
            deposit.initAction = delegate
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null)
                    return;

                int deposited = Arsenal.DepositResource(carried);
                if (deposited > 0)
                {
                    if (deposited >= carried.stackCount)
                    {
                        // Deposited everything - destroy the carried thing
                        carried.Destroy();
                        // Don't call TryDropCarriedThing - nothing left to drop
                    }
                    else
                    {
                        // Deposited partial - reduce stack and drop remainder
                        carried.stackCount -= deposited;
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                }
            };
            deposit.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return deposit;
        }
    }
}