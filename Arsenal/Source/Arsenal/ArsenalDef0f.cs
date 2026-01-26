using RimWorld;
using Verse;

namespace Arsenal
{
    [DefOf]
    public static class ArsenalDefOf
    {
        public static ThingDef Arsenal_CruiseMissile;
        public static ThingDef Arsenal_MissileFactory;
        public static ThingDef Arsenal_MissileHub;
        public static ThingDef Arsenal_RefuelStation;

        // Skyfallers
        public static ThingDef Arsenal_MissileLaunching;
        public static ThingDef Arsenal_MissileLanding;
        public static ThingDef Arsenal_MissileStrikeIncoming;

        public static WorldObjectDef Arsenal_TravelingMissile;
        public static WorldObjectDef Arsenal_MissileStrike;

        public static ResearchProjectDef Arsenal_CruiseMissiles;

        // LATTICE system - Drone swarm defense
        public static ThingDef Arsenal_Lattice;
        public static ThingDef Arsenal_Quiver;
        public static ThingDef Arsenal_DART_Flyer;
        public static ThingDef Arsenal_DART_Item;

        // Research for LATTICE system
        public static ResearchProjectDef Arsenal_DroneSwarm;

        // MithrilProducts for manufacturing
        public static MithrilProductDef MITHRIL_Product_DAGGER;
        public static MithrilProductDef MITHRIL_Product_DART;
        public static MithrilProductDef MITHRIL_Product_SLING;
        public static MithrilProductDef MITHRIL_Product_MULE;

        // SLING/PERCH Logistics System
        public static ThingDef Arsenal_PERCH;
        public static ThingDef Arsenal_SLING;
        public static ThingDef Arsenal_SlingLanding;
        public static ThingDef Arsenal_SlingLaunching;
        public static WorldObjectDef Arsenal_TravelingSling;
        public static JobDef Arsenal_HaulToSling;

        // Research for SLING system
        public static ResearchProjectDef Arsenal_SlingLogistics;

        // MULE system - Ground utility drones
        public static ThingDef Arsenal_MULE_Race;
        public static PawnKindDef Arsenal_MULE_Kind;
        public static ThingDef Arsenal_MULE_Item;
        public static ThingDef Arsenal_Stable;
        public static ThingDef Arsenal_Moria;

        // Research for MULE system
        public static ResearchProjectDef Arsenal_MULESystem;

        static ArsenalDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ArsenalDefOf));
        }
    }
}
