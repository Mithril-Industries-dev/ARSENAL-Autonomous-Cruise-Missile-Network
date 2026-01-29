using Verse;

namespace Arsenal
{
    /// <summary>
    /// Properties for the MULE battery component.
    /// </summary>
    public class CompProperties_MuleBattery : CompProperties
    {
        public float maxCharge = 100f;
        public float drainPerTick = 0.005f;
        public float chargePerTick = 0.05f;
        public float passiveChargePerTick = 0.001f;
        public float rechargeThreshold = 0.25f; // Return to charge at 25%

        public CompProperties_MuleBattery()
        {
            compClass = typeof(Comp_MuleBattery);
        }
    }

    /// <summary>
    /// Battery component for MULE pawns.
    /// Handles charge, drain, and recharge logic.
    /// </summary>
    public class Comp_MuleBattery : ThingComp
    {
        private float currentCharge;

        public CompProperties_MuleBattery Props => (CompProperties_MuleBattery)props;

        public float CurrentCharge => currentCharge;
        public float MaxCharge => Props.maxCharge;
        public float ChargePercent => currentCharge / Props.maxCharge;
        public float DrainPerTick => Props.drainPerTick;

        public bool IsFull => currentCharge >= Props.maxCharge * 0.99f;  // 99% tolerance for floating point
        public bool IsDepleted => currentCharge <= 0f;
        public bool NeedsRecharge => currentCharge <= Props.maxCharge * Props.rechargeThreshold;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            if (!respawningAfterLoad)
            {
                currentCharge = Props.maxCharge;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref currentCharge, "currentCharge", Props?.maxCharge ?? 100f);

            // Ensure charge is never stuck at 0 after load (defensive)
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (currentCharge <= 0f)
                {
                    currentCharge = Props?.maxCharge ?? 100f;
                    Log.Warning($"[MULE Battery] Charge was 0, reset to max: {currentCharge}");
                }
            }
        }

        /// <summary>
        /// Drain battery (called while active/moving).
        /// </summary>
        public void Drain(float multiplier = 1f)
        {
            currentCharge -= Props.drainPerTick * multiplier;
            if (currentCharge < 0f) currentCharge = 0f;
        }

        /// <summary>
        /// Charge battery (called while docked at STABLE).
        /// </summary>
        public void Charge()
        {
            currentCharge += Props.chargePerTick;
            if (currentCharge > Props.maxCharge) currentCharge = Props.maxCharge;
        }

        /// <summary>
        /// Passive slow charge (called while inert).
        /// </summary>
        public void PassiveCharge()
        {
            currentCharge += Props.passiveChargePerTick;
            if (currentCharge > Props.maxCharge) currentCharge = Props.maxCharge;
        }

        /// <summary>
        /// Fill battery to max (debug).
        /// </summary>
        public void FillBattery()
        {
            currentCharge = Props.maxCharge;
        }

        /// <summary>
        /// Empty battery (debug).
        /// </summary>
        public void EmptyBattery()
        {
            currentCharge = 0f;
        }

        public override string CompInspectStringExtra()
        {
            return $"Battery: {ChargePercent:P0} ({currentCharge:F1}/{Props.maxCharge:F0})";
        }
    }
}
