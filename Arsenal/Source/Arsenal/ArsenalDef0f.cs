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
        
        // Jobs
        public static JobDef Arsenal_HaulToArsenal;

        static ArsenalDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ArsenalDefOf));
        }
    }
}