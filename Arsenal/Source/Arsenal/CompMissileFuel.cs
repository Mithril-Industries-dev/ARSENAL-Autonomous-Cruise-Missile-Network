using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    public class CompProperties_MissileFuel : CompProperties
    {
        public float fuelCapacity = 300f; // 300 fuel = 100 tiles at 3 fuel/tile... 
                                           // Actually let's keep 1:1 ratio, 300 = 300 tiles max range

        public CompProperties_MissileFuel()
        {
            compClass = typeof(CompMissileFuel);
        }
    }

    public class CompMissileFuel : ThingComp
    {
        private float fuel;

        public CompProperties_MissileFuel Props => (CompProperties_MissileFuel)props;

        public float Fuel
        {
            get => fuel;
            set => fuel = Mathf.Clamp(value, 0f, Props.fuelCapacity);
        }

        public float FuelCapacity => Props.fuelCapacity;

        public float FuelPercentOfMax => fuel / Props.fuelCapacity;

        public bool HasFuel => fuel > 0f;

        public bool IsFull => fuel >= Props.fuelCapacity;

        public override void PostPostMake()
        {
            base.PostPostMake();
            fuel = Props.fuelCapacity;
        }

        public void ConsumeFuel(float amount)
        {
            fuel = Mathf.Max(0f, fuel - amount);
        }

        public void Refuel(float amount)
        {
            fuel = Mathf.Min(Props.fuelCapacity, fuel + amount);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref fuel, "fuel", 0f);
        }

        public override string CompInspectStringExtra()
        {
            return "Fuel: " + fuel.ToString("F0") + " / " + Props.fuelCapacity.ToString("F0") + " (Range: " + fuel.ToString("F0") + " tiles)";
        }
    }
}