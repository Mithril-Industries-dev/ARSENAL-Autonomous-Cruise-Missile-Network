using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Tabbed manager dialog for ARSENAL.
    /// Tabs: Production, DAGGER Network, DART Network
    /// </summary>
    public class Dialog_ArsenalManager : Window
    {
        private Building_Arsenal arsenal;

        private enum Tab { Production, DaggerNetwork, DartNetwork }
        private Tab currentTab = Tab.Production;

        // Scroll positions
        private Vector2 productionScrollPos;
        private Vector2 daggerScrollPos;
        private Vector2 dartScrollPos;

        // Window settings
        public override Vector2 InitialSize => new Vector2(750f, 600f);

        public Dialog_ArsenalManager(Building_Arsenal arsenal)
        {
            this.arsenal = arsenal;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = false;
            this.forcePause = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Title
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), $"ARSENAL MANAGER - {arsenal.Label}");
            Text.Font = GameFont.Small;

            // Tab buttons
            Rect tabRect = new Rect(0, 40f, inRect.width, 30f);
            DrawTabs(tabRect);

            // Content area
            Rect contentRect = new Rect(0, 80f, inRect.width, inRect.height - 90f);

            switch (currentTab)
            {
                case Tab.Production:
                    DrawProductionTab(contentRect);
                    break;
                case Tab.DaggerNetwork:
                    DrawDaggerNetworkTab(contentRect);
                    break;
                case Tab.DartNetwork:
                    DrawDartNetworkTab(contentRect);
                    break;
            }
        }

        private void DrawTabs(Rect rect)
        {
            float tabWidth = rect.width / 3f;

            // Production tab
            Color prodColor = currentTab == Tab.Production ? Color.white : Color.gray;
            GUI.color = prodColor;
            if (Widgets.ButtonText(new Rect(0, rect.y, tabWidth - 2f, rect.height), "Production"))
                currentTab = Tab.Production;

            // DAGGER Network tab
            Color dagColor = currentTab == Tab.DaggerNetwork ? Color.white : Color.gray;
            GUI.color = dagColor;
            if (Widgets.ButtonText(new Rect(tabWidth, rect.y, tabWidth - 2f, rect.height), "DAGGER Network"))
                currentTab = Tab.DaggerNetwork;

            // DART Network tab
            Color dartColor = currentTab == Tab.DartNetwork ? Color.white : Color.gray;
            GUI.color = dartColor;
            if (Widgets.ButtonText(new Rect(tabWidth * 2, rect.y, tabWidth, rect.height), "DART Network"))
                currentTab = Tab.DartNetwork;

            GUI.color = Color.white;
        }

        #region Production Tab

        private void DrawProductionTab(Rect rect)
        {
            float y = rect.y;
            float lineHeight = 95f;
            float padding = 8f;

            // Scrollable area for lines
            Rect scrollRect = new Rect(rect.x, y, rect.width, rect.height - 80f);
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, (lineHeight + padding) * 3 + 20f);

            Widgets.BeginScrollView(scrollRect, ref productionScrollPos, viewRect);

            float scrollY = 10f;

            // Draw each manufacturing line
            for (int i = 0; i < arsenal.lines.Count; i++)
            {
                Rect lineRect = new Rect(5f, scrollY, viewRect.width - 10f, lineHeight);
                DrawManufacturingLine(lineRect, arsenal.lines[i]);
                scrollY += lineHeight + padding;
            }

            Widgets.EndScrollView();

            // Separator
            y = rect.y + rect.height - 75f;
            Widgets.DrawLineHorizontal(rect.x, y, rect.width);
            y += 10f;

            // Adjacent storage display
            Rect storageRect = new Rect(rect.x, y, rect.width, 60f);
            DrawAdjacentStorage(storageRect);
        }

        private void DrawManufacturingLine(Rect rect, ManufacturingLine line)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.15f, 0.15f, 0.15f));
            Widgets.DrawBox(rect);

            float x = rect.x + 10f;
            float y = rect.y + 5f;
            float rowHeight = 22f;

            // Row 1: LINE # | ON/OFF | Priority | Product dropdown
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(x, y, 55f, rowHeight), $"LINE {line.index + 1}");
            x += 60f;

            // ON/OFF toggle
            bool enabled = line.enabled;
            Widgets.Checkbox(new Vector2(x, y), ref enabled);
            if (enabled != line.enabled)
            {
                line.enabled = enabled;
                line.UpdateStatus();
            }
            Widgets.Label(new Rect(x + 28f, y, 35f, rowHeight), enabled ? "ON" : "OFF");
            x += 70f;

            // Priority dropdown
            Widgets.Label(new Rect(x, y, 50f, rowHeight), "Priority:");
            x += 52f;
            if (Widgets.ButtonText(new Rect(x, y, 28f, rowHeight), line.priority.ToString()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                for (int p = 1; p <= 3; p++)
                {
                    int priority = p;
                    string desc = p == 1 ? " (Highest)" : (p == 3 ? " (Lowest)" : "");
                    options.Add(new FloatMenuOption(p.ToString() + desc, () =>
                    {
                        line.priority = priority;
                        line.UpdateStatus();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            x += 38f;

            // Product dropdown
            Widgets.Label(new Rect(x, y, 52f, rowHeight), "Product:");
            x += 55f;
            string productLabel = line.product?.productLabel ?? "None";
            if (Widgets.ButtonText(new Rect(x, y, 90f, rowHeight), productLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("None", () =>
                {
                    line.product = null;
                    line.Reset();
                }));
                foreach (MithrilProductDef def in DefDatabase<MithrilProductDef>.AllDefs)
                {
                    MithrilProductDef d = def;
                    options.Add(new FloatMenuOption(def.productLabel, () =>
                    {
                        line.product = d;
                        line.Reset();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Row 2: Status and progress bar
            y += rowHeight + 3f;
            x = rect.x + 10f;

            Widgets.Label(new Rect(x, y, 50f, rowHeight), "Status:");
            x += 52f;

            string statusText = GetStatusText(line);
            Color statusColor = GetStatusColor(line);
            GUI.color = statusColor;
            Widgets.Label(new Rect(x, y, 140f, rowHeight), statusText);
            GUI.color = Color.white;

            // Progress bar (if manufacturing)
            if (line.status == LineStatus.Manufacturing && line.product != null)
            {
                Rect barRect = new Rect(x + 145f, y + 2f, 150f, rowHeight - 4f);
                Widgets.FillableBar(barRect, line.ProgressPercent);

                string pctText = $"{(line.ProgressPercent * 100f):F0}%";
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(barRect, pctText);
                Text.Anchor = TextAnchor.UpperLeft;
            }

            // Row 3: Destination
            y += rowHeight + 3f;
            x = rect.x + 10f;

            Widgets.Label(new Rect(x, y, 70f, rowHeight), "Destination:");
            x += 75f;

            // Auto/Locked dropdown
            string modeLabel = line.destMode == DestinationMode.Auto ? "Auto" : "Locked";
            if (Widgets.ButtonText(new Rect(x, y, 55f, rowHeight), modeLabel))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Auto (least full)", () =>
                    {
                        line.destMode = DestinationMode.Auto;
                        line.lockedDestination = null;
                        line.UpdateStatus();
                    }),
                    new FloatMenuOption("Locked (specific)", () =>
                    {
                        line.destMode = DestinationMode.Locked;
                        line.UpdateStatus();
                    })
                };
                Find.WindowStack.Add(new FloatMenu(options));
            }
            x += 60f;

            // If locked, show destination picker
            if (line.destMode == DestinationMode.Locked && line.product != null)
            {
                string destLabel = line.lockedDestination?.Label ?? "Select...";
                if (Widgets.ButtonText(new Rect(x, y, 110f, rowHeight), destLabel))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    foreach (Building dest in arsenal.GetAllDestinationsFor(line.product))
                    {
                        Building d = dest;
                        string capacity = GetCapacityShort(d);
                        options.Add(new FloatMenuOption($"{d.Label} {capacity}", () =>
                        {
                            line.lockedDestination = d;
                            line.UpdateStatus();
                        }));
                    }
                    if (options.Count == 0)
                        options.Add(new FloatMenuOption("No valid destinations", null));
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                x += 115f;
            }

            // Show current target and capacity
            Building target = line.currentDestination ?? line.lockedDestination;
            if (target != null)
            {
                string capacityInfo = GetCapacityInfo(target);
                Widgets.Label(new Rect(x, y, 200f, rowHeight), $"-> {capacityInfo}");
            }
            else if (line.enabled && line.product != null && line.destMode == DestinationMode.Auto)
            {
                Widgets.Label(new Rect(x, y, 200f, rowHeight), "-> (auto-selecting)");
            }
        }

        private string GetStatusText(ManufacturingLine line)
        {
            switch (line.status)
            {
                case LineStatus.Paused: return "Paused";
                case LineStatus.Idle: return "Idle (dests full)";
                case LineStatus.WaitingResources: return "Waiting resources";
                case LineStatus.Manufacturing: return "Manufacturing...";
                default: return "Unknown";
            }
        }

        private Color GetStatusColor(ManufacturingLine line)
        {
            switch (line.status)
            {
                case LineStatus.Paused: return Color.gray;
                case LineStatus.Idle: return Color.yellow;
                case LineStatus.WaitingResources: return new Color(1f, 0.5f, 0f); // Orange
                case LineStatus.Manufacturing: return Color.green;
                default: return Color.white;
            }
        }

        private string GetCapacityShort(Building target)
        {
            if (target is Building_Hub hub)
                return $"({hub.CurrentCount}/{hub.MaxCapacity})";
            if (target is Building_Quiver quiver)
                return $"({quiver.DartCount}/{quiver.MaxCapacity})";
            return "";
        }

        private string GetCapacityInfo(Building target)
        {
            if (target is Building_Hub hub)
                return $"{hub.Label} ({hub.CurrentCount}/{hub.MaxCapacity})";
            if (target is Building_Quiver quiver)
                return $"{quiver.Label} ({quiver.DartCount}/{quiver.MaxCapacity})";
            return target.Label;
        }

        private void DrawAdjacentStorage(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, 180f, 22f), "ADJACENT STORAGE:");

            var resources = arsenal.GetAllAvailableResources();

            if (!arsenal.HasAdjacentStorage())
            {
                GUI.color = Color.red;
                Widgets.Label(new Rect(rect.x + 190f, rect.y, 300f, 22f), "NO STORAGE ADJACENT!");
                GUI.color = Color.white;
                return;
            }

            float x = rect.x;
            float y = rect.y + 25f;

            // Show key resources
            ThingDef[] keyResources = new[]
            {
                ThingDefOf.Steel,
                ThingDefOf.Plasteel,
                ThingDefOf.ComponentIndustrial,
                ThingDefOf.Chemfuel
            };

            foreach (ThingDef def in keyResources)
            {
                int count = resources.TryGetValue(def, out int c) ? c : 0;
                Color color = count == 0 ? Color.red : Color.white;
                GUI.color = color;

                string label = $"{def.label}: {count}";
                Widgets.Label(new Rect(x, y, 140f, 22f), label);
                x += 145f;

                GUI.color = Color.white;
            }
        }

        #endregion

        #region DAGGER Network Tab

        private void DrawDaggerNetworkTab(Rect rect)
        {
            arsenal.RefreshNetworkCache();

            Rect scrollRect = new Rect(rect.x, rect.y, rect.width, rect.height - 40f);
            float contentHeight = (arsenal.CachedHubs.Count * 90f) + (arsenal.CachedHops.Count * 50f) + 100f;
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, contentHeight);

            Widgets.BeginScrollView(scrollRect, ref daggerScrollPos, viewRect);

            float y = 10f;

            // HUBs section
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(10f, y, 200f, 25f), "HUBS");
            Text.Font = GameFont.Small;
            y += 30f;

            if (arsenal.CachedHubs.Count == 0)
            {
                Widgets.Label(new Rect(15f, y, 300f, 22f), "No HUBs found on this map.");
                y += 30f;
            }
            else
            {
                foreach (Building_Hub hub in arsenal.CachedHubs)
                {
                    Rect hubRect = new Rect(10f, y, viewRect.width - 20f, 80f);
                    DrawHubCard(hubRect, hub);
                    y += 90f;
                }
            }

            // HOPs section
            y += 10f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(10f, y, 200f, 25f), "HOPs");
            Text.Font = GameFont.Small;
            y += 30f;

            if (arsenal.CachedHops.Count == 0)
            {
                Widgets.Label(new Rect(15f, y, 300f, 22f), "No HOPs found on this map.");
                y += 30f;
            }
            else
            {
                foreach (Building_Hop hop in arsenal.CachedHops)
                {
                    Rect hopRect = new Rect(10f, y, viewRect.width - 20f, 40f);
                    DrawHopCard(hopRect, hop);
                    y += 50f;
                }
            }

            Widgets.EndScrollView();

            // Summary at bottom
            float summaryY = rect.y + rect.height - 35f;
            int totalDaggers = arsenal.CachedHubs.Sum(h => h.CurrentCount);
            Widgets.Label(new Rect(rect.x, summaryY, rect.width, 25f),
                $"Network Total: {totalDaggers} DAGGERs across {arsenal.CachedHubs.Count} HUBs");
        }

        private void DrawHubCard(Rect rect, Building_Hub hub)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.12f, 0.12f, 0.12f));
            Widgets.DrawBox(rect);

            float x = rect.x + 10f;
            float y = rect.y + 5f;

            // Row 1: Name and capacity bar
            Widgets.Label(new Rect(x, y, 140f, 22f), hub.Label);

            Rect barRect = new Rect(x + 150f, y + 2f, 130f, 18f);
            Widgets.FillableBar(barRect, (float)hub.CurrentCount / hub.MaxCapacity);
            Widgets.Label(new Rect(x + 290f, y, 70f, 22f), $"{hub.CurrentCount}/{hub.MaxCapacity}");

            // Priority dropdown
            Widgets.Label(new Rect(x + 370f, y, 50f, 22f), "Priority:");
            if (Widgets.ButtonText(new Rect(x + 422f, y, 28f, 22f), hub.priority.ToString()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                for (int p = 1; p <= 10; p++)
                {
                    int priority = p;
                    options.Add(new FloatMenuOption(p.ToString(), () => hub.priority = priority));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Status
            string status = hub.IsFull ? "FULL" : (hub.IsPoweredOn() ? "OK" : "NO POWER");
            Color statusColor = hub.IsFull ? Color.yellow : (hub.IsPoweredOn() ? Color.green : Color.red);
            GUI.color = statusColor;
            Widgets.Label(new Rect(x + 460f, y, 60f, 22f), status);
            GUI.color = Color.white;

            // Row 2: Base range
            y += 25f;
            Widgets.Label(new Rect(x, y, 180f, 22f), $"Base Range: {hub.BaseRange} tiles");

            // Row 3: HOP chain and extended range
            y += 22f;
            var hops = arsenal.GetHopChainForHub(hub);
            if (hops.Count > 0)
            {
                string hopChain = string.Join(" -> ", hops.Select(h => h.Label));
                Widgets.Label(new Rect(x, y, 280f, 22f), $"HOP Chain: {hopChain}");

                int extendedRange = hub.GetExtendedRange(hops);
                Widgets.Label(new Rect(x + 290f, y, 180f, 22f), $"Extended Range: {extendedRange} tiles");
            }
            else
            {
                Widgets.Label(new Rect(x, y, 280f, 22f), "HOP Chain: (none)");
            }
        }

        private void DrawHopCard(Rect rect, Building_Hop hop)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f));
            Widgets.DrawBox(rect);

            float x = rect.x + 10f;
            float y = rect.y + 8f;

            Widgets.Label(new Rect(x, y, 100f, 22f), hop.Label);

            // Fuel bar
            Widgets.Label(new Rect(x + 110f, y, 35f, 22f), "Fuel:");
            Rect barRect = new Rect(x + 145f, y + 2f, 90f, 18f);
            Widgets.FillableBar(barRect, hop.FuelPercent);
            Widgets.Label(new Rect(x + 240f, y, 45f, 22f), $"{(hop.FuelPercent * 100f):F0}%");

            // Status
            string status = hop.IsPoweredOn() ? "Online" : "Offline";
            Color statusColor = hop.IsPoweredOn() ? Color.green : Color.red;
            GUI.color = statusColor;
            Widgets.Label(new Rect(x + 300f, y, 70f, 22f), status);
            GUI.color = Color.white;

            // Range extension
            Widgets.Label(new Rect(x + 380f, y, 150f, 22f), $"Range: +{hop.RangeExtension} tiles");
        }

        #endregion

        #region DART Network Tab

        private void DrawDartNetworkTab(Rect rect)
        {
            arsenal.RefreshNetworkCache();

            float y = rect.y;

            // LATTICE status (prominent at top)
            bool latticeOnline = arsenal.CachedLattice != null && arsenal.CachedLattice.IsPoweredOn();
            string latticeStatus = latticeOnline ? "ONLINE" : "OFFLINE";
            Color statusColor = latticeOnline ? Color.green : Color.red;

            GUI.color = statusColor;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.xMax - 180f, y, 180f, 30f), $"LATTICE: {latticeStatus}");
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            y += 35f;

            // Scrollable content
            Rect scrollRect = new Rect(rect.x, y, rect.width, rect.height - 80f);
            float contentHeight = (arsenal.CachedQuivers.Count * 65f) + 150f;
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, contentHeight);

            Widgets.BeginScrollView(scrollRect, ref dartScrollPos, viewRect);

            float scrollY = 10f;

            // QUIVERs section
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(10f, scrollY, 200f, 25f), "QUIVERS");
            Text.Font = GameFont.Small;
            scrollY += 30f;

            if (arsenal.CachedQuivers.Count == 0)
            {
                Widgets.Label(new Rect(15f, scrollY, 300f, 22f), "No QUIVERs found on this map.");
                scrollY += 30f;
            }
            else
            {
                foreach (Building_Quiver quiver in arsenal.CachedQuivers)
                {
                    Rect quiverRect = new Rect(10f, scrollY, viewRect.width - 20f, 55f);
                    DrawQuiverCard(quiverRect, quiver);
                    scrollY += 65f;
                }
            }

            // LATTICE details section
            scrollY += 15f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(10f, scrollY, 200f, 25f), "LATTICE STATUS");
            Text.Font = GameFont.Small;
            scrollY += 30f;

            if (arsenal.CachedLattice != null)
            {
                Rect latticeRect = new Rect(10f, scrollY, viewRect.width - 20f, 70f);
                DrawLatticeCard(latticeRect, arsenal.CachedLattice);
                scrollY += 80f;
            }
            else
            {
                GUI.color = Color.red;
                Widgets.Label(new Rect(15f, scrollY, 350f, 22f), "No LATTICE installed. DART system offline.");
                GUI.color = Color.white;
                scrollY += 30f;
            }

            Widgets.EndScrollView();

            // Summary at bottom
            float summaryY = rect.y + rect.height - 35f;
            int totalDarts = arsenal.CachedQuivers.Sum(q => q.DartCount);
            int quiversOnline = arsenal.CachedQuivers.Count(q => !q.IsInert);
            Widgets.Label(new Rect(rect.x, summaryY, rect.width, 25f),
                $"Total DARTs: {totalDarts}    QUIVERs Online: {quiversOnline}/{arsenal.CachedQuivers.Count}");
        }

        private void DrawQuiverCard(Rect rect, Building_Quiver quiver)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.12f, 0.12f, 0.12f));
            Widgets.DrawBox(rect);

            float x = rect.x + 10f;
            float y = rect.y + 5f;

            // Row 1: Name and capacity
            Widgets.Label(new Rect(x, y, 110f, 22f), quiver.Label);

            Rect barRect = new Rect(x + 120f, y + 2f, 90f, 18f);
            Widgets.FillableBar(barRect, (float)quiver.DartCount / quiver.MaxCapacity);
            Widgets.Label(new Rect(x + 220f, y, 70f, 22f), $"{quiver.DartCount}/{quiver.MaxCapacity}");

            // Priority dropdown
            Widgets.Label(new Rect(x + 300f, y, 50f, 22f), "Priority:");
            if (Widgets.ButtonText(new Rect(x + 352f, y, 28f, 22f), quiver.Priority.ToString()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                for (int p = 1; p <= 10; p++)
                {
                    int priority = p;
                    options.Add(new FloatMenuOption(p.ToString(), () => quiver.SetPriority(priority)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            // Status
            string status = quiver.IsInert ? "INERT" : (quiver.DartCount == 0 ? "EMPTY" : "OK");
            Color statusColor = quiver.IsInert ? Color.red : (quiver.DartCount == 0 ? Color.yellow : Color.green);
            GUI.color = statusColor;
            Widgets.Label(new Rect(x + 400f, y, 60f, 22f), status);
            GUI.color = Color.white;

            // Row 2: Position
            y += 25f;
            Widgets.Label(new Rect(x, y, 300f, 22f), $"Position: ({quiver.Position.x}, {quiver.Position.z})");
        }

        private void DrawLatticeCard(Rect rect, Building_Lattice lattice)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.1f));
            Widgets.DrawBox(rect);

            float x = rect.x + 10f;
            float y = rect.y + 8f;

            // Power status
            bool powered = lattice.IsPoweredOn();
            string powerStatus = powered ? "[ONLINE]" : "[OFFLINE]";
            Color powerColor = powered ? Color.green : Color.red;
            GUI.color = powerColor;
            Widgets.Label(new Rect(x, y, 140f, 22f), $"Power: {powerStatus}");
            GUI.color = Color.white;

            // Threats detected
            Widgets.Label(new Rect(x + 180f, y, 160f, 22f), $"Active Threats: {lattice.ActiveThreatCount}");

            // Detection range
            y += 22f;
            Widgets.Label(new Rect(x, y, 180f, 22f), "Detection: Map-wide");

            // DART flight status
            y += 22f;
            Widgets.Label(new Rect(x, y, rect.width - 20f, 22f),
                $"In Flight: {lattice.DartsInFlight}    " +
                $"Returning: {lattice.DartsReturning}    " +
                $"Awaiting Assignment: {lattice.DartsAwaiting}");
        }

        #endregion
    }
}
