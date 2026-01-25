using RimWorld;
using Verse;

namespace Arsenal
{
    /// <summary>
    /// Custom Thing class for SLING cargo craft.
    /// Supports custom naming (SLING-01, SLING-02, etc.) that persists
    /// through transport and displays correctly when on pad.
    /// </summary>
    public class SLING_Thing : Building
    {
        private string customName;
        private static int slingCounter = 1;

        public string CustomName
        {
            get => customName;
            set => customName = value;
        }

        public override string Label
        {
            get
            {
                if (!string.IsNullOrEmpty(customName))
                    return customName;
                return base.Label;
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // Assign name only if not already named (preserves name through transit)
            if (string.IsNullOrEmpty(customName))
            {
                customName = "SLING-" + slingCounter.ToString("D2");
                slingCounter++;
            }
        }

        /// <summary>
        /// Assigns a name to this SLING. Call before spawning to set a specific name.
        /// </summary>
        public void AssignName(string name)
        {
            if (!string.IsNullOrEmpty(name))
                customName = name;
        }

        /// <summary>
        /// Assigns a new unique name to this SLING.
        /// </summary>
        public void AssignNewName()
        {
            customName = "SLING-" + slingCounter.ToString("D2");
            slingCounter++;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
        }

        public override string GetInspectString()
        {
            string str = base.GetInspectString();

            if (!str.NullOrEmpty())
                str += "\n";

            str += "Status: Ready for dispatch";

            return str;
        }

        /// <summary>
        /// Resets the naming counter (called on game load).
        /// </summary>
        public static void ResetCounter()
        {
            slingCounter = 1;
        }

        /// <summary>
        /// Sets the counter to continue from existing SLINGs.
        /// </summary>
        public static void SetCounterFromExisting(int maxExisting)
        {
            if (maxExisting >= slingCounter)
                slingCounter = maxExisting + 1;
        }

        /// <summary>
        /// Gets the name from a SLING Thing (works with both SLING_Thing and generic Things).
        /// </summary>
        public static string GetSlingName(Thing sling)
        {
            if (sling is SLING_Thing slingThing)
                return slingThing.CustomName ?? sling.Label;
            return sling?.Label ?? "SLING";
        }
    }
}
