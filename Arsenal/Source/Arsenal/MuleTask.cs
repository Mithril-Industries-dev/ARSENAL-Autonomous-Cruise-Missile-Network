using Verse;

namespace Arsenal
{
    /// <summary>
    /// Represents a task assigned to a MULE drone.
    /// </summary>
    public class MuleTask : IExposable
    {
        /// <summary>Type of task to perform.</summary>
        public MuleTaskType taskType = MuleTaskType.None;

        /// <summary>Target location for the task (mining cell, pickup location, etc.).</summary>
        public IntVec3 targetCell;

        /// <summary>Target thing (for hauling tasks).</summary>
        public Thing targetThing;

        /// <summary>Destination for delivery (MORIA, stockpile, ARSENAL).</summary>
        public Thing destination;

        /// <summary>Destination cell if destination thing is null.</summary>
        public IntVec3 destinationCell;

        /// <summary>Resource type being transported (for mining/hauling).</summary>
        public ThingDef resourceDef;

        /// <summary>Amount being transported.</summary>
        public int resourceCount;

        /// <summary>Mining designation reference (to mark complete when done).</summary>
        public Designation miningDesignation;

        /// <summary>Whether the task has been completed.</summary>
        public bool isComplete;

        /// <summary>Estimated battery cost for this task.</summary>
        public float estimatedBatteryCost;

        public MuleTask()
        {
        }

        public MuleTask(MuleTaskType type, IntVec3 target)
        {
            taskType = type;
            targetCell = target;
        }

        public static MuleTask CreateMiningTask(IntVec3 mineCell, Designation designation)
        {
            return new MuleTask
            {
                taskType = MuleTaskType.Mine,
                targetCell = mineCell,
                miningDesignation = designation
            };
        }

        public static MuleTask CreateHaulTask(Thing item, Thing dest, IntVec3 destCell)
        {
            return new MuleTask
            {
                taskType = MuleTaskType.Haul,
                targetCell = item.Position,
                targetThing = item,
                destination = dest,
                destinationCell = destCell,
                resourceDef = item.def,
                resourceCount = item.stackCount
            };
        }

        public static MuleTask CreateMoriaFeedTask(Thing item, Building_Moria moria)
        {
            return new MuleTask
            {
                taskType = MuleTaskType.MoriaFeed,
                targetCell = item.Position,
                targetThing = item,
                destination = moria,
                destinationCell = moria.Position,
                resourceDef = item.def,
                resourceCount = item.stackCount
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref taskType, "taskType", MuleTaskType.None);
            Scribe_Values.Look(ref targetCell, "targetCell");
            Scribe_References.Look(ref targetThing, "targetThing");
            Scribe_References.Look(ref destination, "destination");
            Scribe_Values.Look(ref destinationCell, "destinationCell");
            Scribe_Defs.Look(ref resourceDef, "resourceDef");
            Scribe_Values.Look(ref resourceCount, "resourceCount");
            // Note: miningDesignation is not saved - look it up via targetCell after load
            Scribe_Values.Look(ref isComplete, "isComplete");
            Scribe_Values.Look(ref estimatedBatteryCost, "estimatedBatteryCost");
        }

        public override string ToString()
        {
            return $"MuleTask({taskType}, target={targetCell}, dest={destinationCell})";
        }
    }
}
