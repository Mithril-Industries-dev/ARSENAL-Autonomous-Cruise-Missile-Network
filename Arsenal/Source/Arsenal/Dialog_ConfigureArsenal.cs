using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Arsenal
{
    public class Dialog_ConfigureArsenal : Window
    {
        private Building_Arsenal arsenal;
        private Vector2 scroll;
        private const float ROW_HEIGHT = 80f;

        public Dialog_ConfigureArsenal(Building_Arsenal a)
        {
            arsenal = a;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(650f, 550f);

        public override void DoWindowContents(Rect inRect)
        {
            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35), "Configure " + arsenal.Label);
            Text.Font = GameFont.Small;

            // Production toggle
            Rect toggleRect = new Rect(0, 40, 200, 24);
            bool prodEnabled = arsenal.productionEnabled;
            Widgets.CheckboxLabeled(toggleRect, "Production Enabled", ref prodEnabled);
            arsenal.productionEnabled = prodEnabled;

            // Status
            string status = prodEnabled ? "ACTIVE" : "STOPPED";
            Widgets.Label(new Rect(220, 40, 200, 24), "Status: " + status);

            // Header
            Rect headerRect = new Rect(0, 75, inRect.width, 24);
            GUI.color = Color.gray;
            Widgets.Label(new Rect(10, 75, 150, 24), "HUB Name");
            Widgets.Label(new Rect(165, 75, 60, 24), "Stored");
            Widgets.Label(new Rect(230, 75, 60, 24), "Limit");
            Widgets.Label(new Rect(295, 75, 50, 24), "Priority");
            Widgets.Label(new Rect(350, 75, 80, 24), "Distance");
            Widgets.Label(new Rect(435, 75, 150, 24), "Route via HOPs");
            GUI.color = Color.white;

            // Divider
            Widgets.DrawLineHorizontal(0, 100, inRect.width);

            // Hub list
            Rect outRect = new Rect(0, 105, inRect.width, inRect.height - 160);
            List<Building_Hub> hubs = ArsenalNetworkManager.GetAllHubs();
            float viewHeight = Mathf.Max(hubs.Count * ROW_HEIGHT + 20, outRect.height);
            Rect viewRect = new Rect(0, 0, inRect.width - 20, viewHeight);

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0;

            if (hubs.Count == 0)
            {
                Widgets.Label(new Rect(10, y, viewRect.width - 20, 30), "No HUBs built yet. Build a HUB staging platform first.");
            }
            else
            {
                foreach (var hub in hubs)
                {
                    DrawHubRow(new Rect(0, y, viewRect.width, ROW_HEIGHT), hub);
                    y += ROW_HEIGHT;
                }
            }
            Widgets.EndScrollView();

            // Bottom buttons
            float buttonY = inRect.height - 40;
            if (Widgets.ButtonText(new Rect(inRect.width / 2 - 60, buttonY, 120, 35), "Close"))
                Close();
        }

        private void DrawHubRow(Rect rect, Building_Hub hub)
        {
            int tile = hub.Map?.Tile ?? -1;
            if (tile < 0) return;

            // Ensure config exists
            if (!arsenal.hubConfigs.ContainsKey(tile))
                arsenal.hubConfigs[tile] = new Building_Arsenal.HubConfig();

            var config = arsenal.hubConfigs[tile];

            // Background on hover
            if (Mouse.IsOver(rect))
                Widgets.DrawHighlight(rect);

            // Divider line
            Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1, rect.width);

            float rowY = rect.y + 5;

            // HUB Name
            Widgets.Label(new Rect(10, rowY, 150, 24), hub.Label);

            // Stored count
            Widgets.Label(new Rect(165, rowY, 60, 24), hub.GetStoredMissileCount().ToString() + " / 10");

            // Stock limit input
            string limitBuf = config.stockLimit.ToString();
            limitBuf = Widgets.TextField(new Rect(230, rowY, 50, 24), limitBuf);
            if (int.TryParse(limitBuf, out int newLimit))
                config.stockLimit = Mathf.Clamp(newLimit, 0, 10);

            // Priority input
            string prioBuf = config.priority.ToString();
            prioBuf = Widgets.TextField(new Rect(295, rowY, 40, 24), prioBuf);
            if (int.TryParse(prioBuf, out int newPrio))
                config.priority = Mathf.Clamp(newPrio, 1, 99);

            // Distance
            int arsenalTile = arsenal.Map?.Tile ?? -1;
            int distance = 0;
            if (arsenalTile >= 0 && tile >= 0)
                distance = Find.WorldGrid.TraversalDistanceBetween(arsenalTile, tile);
            
            float km = distance * 25f; // ~25km per tile
            Widgets.Label(new Rect(350, rowY, 80, 24), distance + " tiles");

            // Route via HOPs
            List<int> route = arsenal.GetRouteToHub(tile);
            string routeStr = "Direct";
            if (route.Count > 1)
            {
                List<string> hopNames = new List<string>();
                for (int i = 0; i < route.Count - 1; i++)
                {
                    Building_Hop hop = ArsenalNetworkManager.GetHopAtTile(route[i]);
                    if (hop != null)
                        hopNames.Add(hop.Label);
                }
                if (hopNames.Count > 0)
                    routeStr = string.Join(" → ", hopNames);
            }
            Widgets.Label(new Rect(435, rowY, 180, 24), routeStr);

            // Second row: buttons
            float row2Y = rowY + 30;

            if (Widgets.ButtonText(new Rect(10, row2Y, 70, 24), "View"))
            {
                CameraJumper.TryJumpAndSelect(hub);
                Close();
            }

            if (Widgets.ButtonText(new Rect(90, row2Y, 70, 24), "Clear"))
            {
                config.stockLimit = 0;
                config.priority = 1;
            }

            // Distance in KM
            Widgets.Label(new Rect(350, row2Y, 100, 24), "(" + km.ToString("F0") + " km)");
        }
    }
}