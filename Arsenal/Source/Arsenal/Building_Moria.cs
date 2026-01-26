using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// MORIA - A simple storage shelf with power connection.
    /// Works exactly like a vanilla shelf.
    /// </summary>
    public class Building_Moria : Building_Storage
    {
        private CompPowerTrader powerComp;
        private string customName;
        private static int moriaCounter = 1;

        /// <summary>
        /// Sets the moria counter to a specific value.
        /// Called after game load to prevent duplicate names.
        /// </summary>
        public static void SetCounter(int value)
        {
            moriaCounter = System.Math.Max(1, value);
        }

        public override string Label => customName ?? base.Label;

        public bool IsPoweredOn => powerComp == null || powerComp.PowerOn;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
            ArsenalNetworkManager.RegisterMoria(this);

            if (!respawningAfterLoad)
            {
                customName = "MORIA-" + moriaCounter.ToString("D2");
                moriaCounter++;
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ArsenalNetworkManager.DeregisterMoria(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref customName, "customName");
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (!IsPoweredOn)
            {
                if (!text.NullOrEmpty()) text += "\n";
                text += "<color=#ff6666>No power</color>";
            }
            return text;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Rename this MORIA.",
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", false),
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_RenameMoria(this));
                }
            };
        }

        public void SetCustomName(string name)
        {
            customName = name;
        }

        // For MULE compatibility
        public bool CanAcceptItem(Thing item)
        {
            return IsPoweredOn && settings.AllowedToAccept(item);
        }

        public bool NeedsResource(ThingDef def)
        {
            return IsPoweredOn && settings.AllowedToAccept(def);
        }
    }

    public class Dialog_RenameMoria : Window
    {
        private Building_Moria moria;
        private string newName;

        public Dialog_RenameMoria(Building_Moria m)
        {
            moria = m;
            newName = m.Label;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(300f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 30), "Rename MORIA");
            Text.Font = GameFont.Small;

            newName = Widgets.TextField(new Rect(0, 40, inRect.width, 30), newName);

            if (Widgets.ButtonText(new Rect(0, 90, 120, 30), "OK"))
            {
                moria.SetCustomName(newName);
                Close();
            }
            if (Widgets.ButtonText(new Rect(140, 90, 120, 30), "Cancel"))
            {
                Close();
            }
        }
    }
}
