using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Custom Def type for MITHRIL products. Allows easy addition of future products.
    /// </summary>
    public class MithrilProductDef : Def
    {
        // Basic info
        public string productLabel;                    // Display name: "DAGGER", "DART"
        public string productDescription;

        // Manufacturing
        public int workAmount = 2500;                  // Ticks to manufacture
        public List<ThingDefCountClass> costList;      // Resources required

        // Output
        public ThingDef outputFlyer;                   // ThingDef of the flyer to spawn
        public Type destinationType;                   // typeof(Building_Hub), typeof(Building_Quiver)

        // UI Configuration
        public bool showRouteInfo = false;             // Show HOP chain and range (DAGGER only)
        public string networkTabLabel;                 // "DAGGER Network", "DART Network"

        // Fuel consumption per unit
        public float fuelCost = 50f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors())
                yield return error;

            if (productLabel.NullOrEmpty())
                yield return "MithrilProductDef requires productLabel";

            if (outputFlyer == null)
                yield return "MithrilProductDef requires outputFlyer";

            if (destinationType == null)
                yield return "MithrilProductDef requires destinationType";
        }
    }
}
