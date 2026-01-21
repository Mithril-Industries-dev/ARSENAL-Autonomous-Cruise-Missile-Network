using RimWorld;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// PlaceWorker that ensures only one LATTICE can exist per map.
    /// </summary>
    public class PlaceWorker_OnlyOneLattice : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // Check if there's already a LATTICE on this map
            Building_Lattice existingLattice = ArsenalNetworkManager.GetLatticeOnMap(map);

            if (existingLattice != null && existingLattice != thingToIgnore)
            {
                return new AcceptanceReport("Only one LATTICE C2 node is allowed per map.");
            }

            // Also check for blueprints/frames of LATTICE
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t == thingToIgnore)
                    continue;

                // Check if this is a blueprint for LATTICE
                if (t.def.IsBlueprint && t.def.entityDefToBuild == ArsenalDefOf.Arsenal_Lattice)
                {
                    return new AcceptanceReport("A LATTICE C2 node is already being constructed.");
                }

                // Check if this is a frame for LATTICE
                if (t.def.IsFrame && t.def.entityDefToBuild == ArsenalDefOf.Arsenal_Lattice)
                {
                    return new AcceptanceReport("A LATTICE C2 node is already being constructed.");
                }
            }

            return AcceptanceReport.WasAccepted;
        }
    }
}
