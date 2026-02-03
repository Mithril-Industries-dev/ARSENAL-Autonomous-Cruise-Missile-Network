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

        // Default values if Props is null (for deep-saved despawned pawns)
        private const float DEFAULT_MAX_CHARGE = 100f;
        private const float DEFAULT_DRAIN_PER_TICK = 0.005f;
        private const float DEFAULT_CHARGE_PER_TICK = 0.05f;
        private const float DEFAULT_PASSIVE_CHARGE_PER_TICK = 0.001f;
        private const float DEFAULT_RECHARGE_THRESHOLD = 0.25f;

        public CompProperties_MuleBattery Props => (CompProperties_MuleBattery)props;

        // Safe property accessors with fallback defaults
        public float MaxCharge => Props?.maxCharge ?? DEFAULT_MAX_CHARGE;
        public float ChargePerTick => Props?.chargePerTick ?? DEFAULT_CHARGE_PER_TICK;
        public float DrainPerTick => Props?.drainPerTick ?? DEFAULT_DRAIN_PER_TICK;
        public float PassiveChargePerTick => Props?.passiveChargePerTick ?? DEFAULT_PASSIVE_CHARGE_PER_TICK;
        public float RechargeThreshold => Props?.rechargeThreshold ?? DEFAULT_RECHARGE_THRESHOLD;

        public float CurrentCharge => currentCharge;
        public float ChargePercent => MaxCharge > 0 ? currentCharge / MaxCharge : 0f;

        public bool IsFull => currentCharge >= MaxCharge * 0.99f;  // 99% tolerance for floating point
        public bool IsDepleted => currentCharge <= 0f;
        public bool NeedsRecharge => currentCharge <= MaxCharge * RechargeThreshold;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            if (!respawningAfterLoad)
            {
                currentCharge = MaxCharge;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref currentCharge, "currentCharge", DEFAULT_MAX_CHARGE);

            // Validate charge bounds after load
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                float maxCharge = MaxCharge;

                // Fix invalid values: negative, zero, NaN, infinity, or over max
                if (float.IsNaN(currentCharge) || float.IsInfinity(currentCharge))
                {
                    currentCharge = maxCharge;
                    Log.Warning($"[MULE Battery] Charge was NaN/Infinity, reset to max: {currentCharge}");
                }
                else if (currentCharge <= 0f)
                {
                    currentCharge = maxCharge;
                    Log.Warning($"[MULE Battery] Charge was <= 0, reset to max: {currentCharge}");
                }
                else if (currentCharge > maxCharge)
                {
                    currentCharge = maxCharge;
                }
            }
        }

        /// <summary>
        /// Drain battery (called while active/moving).
        /// </summary>
        public void Drain(float multiplier = 1f)
        {
            currentCharge -= DrainPerTick * multiplier;
            if (currentCharge < 0f) currentCharge = 0f;
        }

        /// <summary>
        /// Charge battery (called while docked at STABLE).
        /// </summary>
        public void Charge()
        {
            float before = currentCharge;
            float rate = ChargePerTick;
            currentCharge += rate;
            if (currentCharge > MaxCharge) currentCharge = MaxCharge;

            // Debug: log first charge call per session to verify it works
            if (before == 0f || (before < 1f && currentCharge >= 1f))
            {
                Log.Message($"[BATTERY DEBUG] Charge(): {before:F2} + {rate:F3} = {currentCharge:F2}, Props null={Props == null}, MaxCharge={MaxCharge}");
            }
        }

        /// <summary>
        /// Passive slow charge (called while inert).
        /// </summary>
        public void PassiveCharge()
        {
            currentCharge += PassiveChargePerTick;
            if (currentCharge > MaxCharge) currentCharge = MaxCharge;
        }

        /// <summary>
        /// Fill battery to max (debug).
        /// </summary>
        public void FillBattery()
        {
            currentCharge = MaxCharge;
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
            return $"Battery: {ChargePercent:P0} ({currentCharge:F1}/{MaxCharge:F0})";
        }
    }
}
