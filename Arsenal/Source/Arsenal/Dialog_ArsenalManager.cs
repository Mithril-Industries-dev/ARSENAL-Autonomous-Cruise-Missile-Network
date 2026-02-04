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

        private enum Tab { Production, DaggerNetwork, DartNetwork, Logistics }
        private Tab currentTab = Tab.Production;

        // Scroll positions
        private Vector2 productionScrollPos;
        private Vector2 daggerScrollPos;
        private Vector2 dartScrollPos;
        private Vector2 logisticsScrollPos;

        // Selected PERCH for configuration (legacy)
        private Building_PERCH selectedPerch;

        // Selected beacon zone (new system)
        private Building_PerchBeacon selectedBeaconZone;

        // Window settings
        public override Vector2 InitialSize => new Vector2(750f, 600f);

        public Dialog_ArsenalManager(Building_Arsenal arsenal)
        {
            this.arsenal = arsenal;
            this.doCloseX = true;
            this.absorbInputAroundWindow = false;
            this.closeOnClickedOutside = false;
            this.forcePause = false;
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
                case Tab.Logistics:
                    DrawLogisticsTab(contentRect);
                    break;
            }
        }

        private void DrawTabs(Rect rect)
        {
            float tabWidth = rect.width / 4f;

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
            if (Widgets.ButtonText(new Rect(tabWidth * 2, rect.y, tabWidth - 2f, rect.height), "DART Network"))
                currentTab = Tab.DartNetwork;

            // Logistics tab (SLING/PERCH)
            Color logColor = currentTab == Tab.Logistics ? Color.white : Color.gray;
            GUI.color = logColor;
            if (Widgets.ButtonText(new Rect(tabWidth * 3, rect.y, tabWidth, rect.height), "Logistics"))
                currentTab = Tab.Logistics;

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

        #region Logistics Tab (SLING/PERCH)

        private void DrawLogisticsTab(Rect rect)
        {
            float y = rect.y;

            // Fleet status at top
            int totalSlings = SlingLogisticsManager.GetTotalSlingCount();
            int maxSlings = SlingLogisticsManager.GetMaxSlingCount();

            Text.Font = GameFont.Medium;
            string fleetStatus = $"SLING Fleet: {totalSlings}/{maxSlings}";
            Color fleetColor = totalSlings < maxSlings ? Color.yellow : Color.green;
            GUI.color = fleetColor;
            Widgets.Label(new Rect(rect.xMax - 200f, y, 200f, 30f), fleetStatus);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            y += 35f;

            // Split view: Network overview (left) and PERCH config (right)
            float splitWidth = rect.width / 2f - 5f;
            Rect leftRect = new Rect(rect.x, y, splitWidth, rect.height - 45f);
            Rect rightRect = new Rect(rect.x + splitWidth + 10f, y, splitWidth, rect.height - 45f);

            DrawNetworkOverview(leftRect);
            DrawPerchConfiguration(rightRect);

            // Fleet status at bottom
            float summaryY = rect.y + rect.height - 35f;
            var slingsInTransit = SlingLogisticsManager.GetSlingsInTransit();
            int beaconSources = ArsenalNetworkManager.GetSourceBeacons().Count;
            int beaconSinks = ArsenalNetworkManager.GetSinkBeacons().Count;
            int legacySources = ArsenalNetworkManager.GetSourcePerches().Count;
            int legacySinks = ArsenalNetworkManager.GetSinkPerches().Count;
            Widgets.Label(new Rect(rect.x, summaryY, rect.width, 25f),
                $"In Transit: {slingsInTransit.Count}    " +
                $"Sources: {beaconSources + legacySources}    " +
                $"Sinks: {beaconSinks + legacySinks}");
        }

        private void DrawNetworkOverview(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f));
            Widgets.DrawBox(rect);

            float y = rect.y + 5f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 25f), "Network Overview");
            Text.Font = GameFont.Small;
            y += 30f;

            // Scrollable list of landing zones (both beacons and legacy PERCHes)
            Rect scrollRect = new Rect(rect.x + 5f, y, rect.width - 10f, rect.height - 40f);

            // Get beacon zones (primary beacons only) and legacy perches
            var beaconZones = ArsenalNetworkManager.GetAllPerchBeacons()
                .Where(b => b.IsPrimary && b.HasValidLandingZone)
                .ToList();
            var legacyPerches = ArsenalNetworkManager.GetAllPerches();

            float contentHeight = (beaconZones.Count * 50f) + (legacyPerches.Count * 50f) + 60f;
            Rect viewRect = new Rect(0, 0, scrollRect.width - 20f, contentHeight);

            Widgets.BeginScrollView(scrollRect, ref logisticsScrollPos, viewRect);

            float scrollY = 5f;

            // Beacon Landing Zones section
            if (beaconZones.Count > 0)
            {
                GUI.color = Color.cyan;
                Widgets.Label(new Rect(10f, scrollY, viewRect.width - 20f, 20f), "Beacon Landing Zones:");
                GUI.color = Color.white;
                scrollY += 22f;

                foreach (var beacon in beaconZones)
                {
                    Rect zoneRect = new Rect(5f, scrollY, viewRect.width - 10f, 45f);
                    DrawBeaconZoneListItem(zoneRect, beacon);
                    scrollY += 50f;
                }
                scrollY += 10f;
            }

            // Legacy PERCHes section
            if (legacyPerches.Count > 0)
            {
                GUI.color = Color.yellow;
                Widgets.Label(new Rect(10f, scrollY, viewRect.width - 20f, 20f), "Legacy PERCHes:");
                GUI.color = Color.white;
                scrollY += 22f;

                foreach (var perch in legacyPerches.OrderBy(p => p.priority))
                {
                    Rect perchRect = new Rect(5f, scrollY, viewRect.width - 10f, 45f);
                    DrawPerchListItem(perchRect, perch);
                    scrollY += 50f;
                }
            }

            if (beaconZones.Count == 0 && legacyPerches.Count == 0)
            {
                Widgets.Label(new Rect(10f, scrollY, viewRect.width - 20f, 44f),
                    "No landing zones found.\nPlace 4 PERCH beacons at corners to create a landing zone.");
            }

            Widgets.EndScrollView();
        }

        private void DrawBeaconZoneListItem(Rect rect, Building_PerchBeacon beacon)
        {
            bool isSelected = selectedBeaconZone == beacon;
            Color bgColor = isSelected ? new Color(0.2f, 0.3f, 0.35f) : new Color(0.12f, 0.12f, 0.12f);
            Widgets.DrawBoxSolid(rect, bgColor);
            Widgets.DrawBox(rect);

            if (Widgets.ButtonInvisible(rect))
            {
                selectedBeaconZone = beacon;
                selectedPerch = null;  // Deselect legacy perch
            }

            float x = rect.x + 8f;
            float y = rect.y + 4f;

            // Row 1: Zone name, Role, Status
            var zone = beacon.GetLandingZone();
            string displayName = !string.IsNullOrEmpty(beacon.ZoneName) ? beacon.ZoneName :
                                 (zone.HasValue ? $"Zone {zone.Value.Width}x{zone.Value.Height}" : "Invalid Zone");
            Widgets.Label(new Rect(x, y, 90f, 20f), displayName);

            // Role badge
            Color roleColor = beacon.IsSource ? new Color(0.3f, 0.7f, 0.3f) : new Color(0.7f, 0.5f, 0.2f);
            GUI.color = roleColor;
            Widgets.Label(new Rect(x + 95f, y, 60f, 20f), beacon.Role.ToString());
            GUI.color = Color.white;

            // Status
            string status = beacon.IsPoweredOn ? "Online" : "Offline";
            Color statusColor = beacon.IsPoweredOn ? Color.green : Color.red;
            GUI.color = statusColor;
            Widgets.Label(new Rect(rect.xMax - 60f, y, 50f, 20f), status);
            GUI.color = Color.white;

            // Row 2: SLING info
            y += 20f;
            var slings = beacon.GetDockedSlings();
            if (slings.Count > 0)
            {
                Widgets.Label(new Rect(x, y, 150f, 18f), $"SLINGs: {slings.Count}");
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(x, y, 150f, 18f), "SLINGs: None");
                GUI.color = Color.white;
            }
        }

        private void DrawPerchListItem(Rect rect, Building_PERCH perch)
        {
            bool isSelected = selectedPerch == perch;
            Color bgColor = isSelected ? new Color(0.25f, 0.25f, 0.35f) : new Color(0.12f, 0.12f, 0.12f);
            Widgets.DrawBoxSolid(rect, bgColor);
            Widgets.DrawBox(rect);

            if (Widgets.ButtonInvisible(rect))
            {
                selectedPerch = perch;
                selectedBeaconZone = null;  // Deselect beacon zone
            }

            float x = rect.x + 8f;
            float y = rect.y + 4f;

            // Row 1: Name, Role, Status
            Widgets.Label(new Rect(x, y, 100f, 20f), perch.Label);

            // Role badge
            Color roleColor = perch.role == PerchRole.SOURCE ? new Color(0.3f, 0.7f, 0.3f) : new Color(0.7f, 0.5f, 0.2f);
            GUI.color = roleColor;
            Widgets.Label(new Rect(x + 105f, y, 60f, 20f), perch.role.ToString());
            GUI.color = Color.white;

            // Priority (SINK only)
            if (perch.role == PerchRole.SINK)
            {
                Widgets.Label(new Rect(x + 170f, y, 40f, 20f), $"P{perch.priority}");
            }

            // Status
            string status = GetPerchStatus(perch);
            Color statusColor = GetPerchStatusColor(perch);
            GUI.color = statusColor;
            Widgets.Label(new Rect(rect.xMax - 80f, y, 70f, 20f), status);
            GUI.color = Color.white;

            // Row 2: SLING info
            y += 20f;
            if (perch.HasSlingOnPad)
            {
                string slingStatus = perch.IsBusy ? "Busy" : "Ready";
                Widgets.Label(new Rect(x, y, 150f, 18f), $"SLING: {slingStatus}");
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(x, y, 150f, 18f), "SLING: None");
                GUI.color = Color.white;
            }

            // Fuel
            Widgets.Label(new Rect(x + 160f, y, 100f, 18f), $"Fuel: {perch.FuelPercent * 100f:F0}%");
        }

        private string GetPerchStatus(Building_PERCH perch)
        {
            if (!perch.IsPoweredOn) return "No Power";
            if (!perch.HasNetworkConnection()) return "Offline";
            if (perch.IsBusy) return "Busy";
            if (perch.role == PerchRole.SINK && perch.HasDemand()) return "Demand";
            return "Idle";
        }

        private Color GetPerchStatusColor(Building_PERCH perch)
        {
            if (!perch.IsPoweredOn) return Color.red;
            if (!perch.HasNetworkConnection()) return Color.red;
            if (perch.IsBusy) return Color.yellow;
            if (perch.role == PerchRole.SINK && perch.HasDemand()) return new Color(1f, 0.6f, 0f);
            return Color.green;
        }

        private void DrawPerchConfiguration(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.08f, 0.08f));
            Widgets.DrawBox(rect);

            float y = rect.y + 5f;
            float x = rect.x + 10f;
            float width = rect.width - 20f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x, y, width, 25f), "Landing Zone Configuration");
            Text.Font = GameFont.Small;
            y += 30f;

            // Handle beacon zone configuration
            if (selectedBeaconZone != null)
            {
                DrawBeaconZoneConfiguration(rect, x, y, width);
                return;
            }

            if (selectedPerch == null)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(x, y, width, 22f), "Select a landing zone from the list to configure.");
                GUI.color = Color.white;
                return;
            }

            // PERCH name
            Widgets.Label(new Rect(x, y, 80f, 22f), "Name:");
            if (Widgets.ButtonText(new Rect(x + 85f, y, 140f, 22f), selectedPerch.Label))
            {
                Find.WindowStack.Add(new Dialog_RenamePerch(selectedPerch));
            }
            y += 28f;

            // Role toggle
            Widgets.Label(new Rect(x, y, 80f, 22f), "Role:");
            if (Widgets.ButtonText(new Rect(x + 85f, y, 80f, 22f), selectedPerch.role.ToString()))
            {
                selectedPerch.SetRole(selectedPerch.role == PerchRole.SOURCE ? PerchRole.SINK : PerchRole.SOURCE);
            }
            y += 28f;

            if (selectedPerch.role == PerchRole.SINK)
            {
                // Priority
                Widgets.Label(new Rect(x, y, 80f, 22f), "Priority:");
                if (Widgets.ButtonText(new Rect(x + 85f, y, 40f, 22f), selectedPerch.priority.ToString()))
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    for (int p = 1; p <= 10; p++)
                    {
                        int pVal = p;
                        string desc = p == 1 ? " (Highest)" : (p == 10 ? " (Lowest)" : "");
                        options.Add(new FloatMenuOption(p.ToString() + desc, () => selectedPerch.SetPriority(pVal)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                y += 28f;

                // Threshold targets section
                Widgets.Label(new Rect(x, y, width, 22f), "Threshold Targets:");
                y += 24f;

                // Add threshold button
                if (Widgets.ButtonText(new Rect(x, y, 120f, 22f), "+ Add Resource"))
                {
                    ShowAddThresholdMenu();
                }
                y += 28f;

                // Draw existing thresholds
                var thresholds = selectedPerch.thresholdTargets.ToList();
                foreach (var kvp in thresholds)
                {
                    Rect threshRect = new Rect(x, y, width, 22f);
                    DrawThresholdRow(threshRect, kvp.Key, kvp.Value);
                    y += 26f;
                }

                // Current demand (map-wide stock)
                y += 10f;
                var demand = selectedPerch.GetDemand();
                if (demand.Count > 0)
                {
                    GUI.color = new Color(1f, 0.6f, 0f);
                    Widgets.Label(new Rect(x, y, width, 22f), "Current Demand (Map Stock):");
                    y += 22f;
                    GUI.color = Color.white;

                    foreach (var d in demand)
                    {
                        int current = selectedPerch.GetMapStock(d.Key);
                        int target = selectedPerch.thresholdTargets.TryGetValue(d.Key, out int t) ? t : 0;
                        Widgets.Label(new Rect(x + 10f, y, width - 10f, 20f),
                            $"{d.Key.label}: {current}/{target} (need {d.Value})");
                        y += 20f;
                    }
                }
                else
                {
                    GUI.color = Color.green;
                    Widgets.Label(new Rect(x, y, width, 22f), "All thresholds satisfied");
                    GUI.color = Color.white;
                }
            }
            else // SOURCE
            {
                // Source filter toggle
                bool filterEnabled = selectedPerch.filterEnabled;
                Widgets.CheckboxLabeled(new Rect(x, y, width, 22f), "Filter exports", ref filterEnabled);
                selectedPerch.filterEnabled = filterEnabled;
                y += 26f;

                if (selectedPerch.filterEnabled)
                {
                    if (Widgets.ButtonText(new Rect(x, y, 120f, 22f), "+ Add Filter"))
                    {
                        ShowAddFilterMenu();
                    }
                    y += 28f;

                    foreach (var resource in selectedPerch.sourceFilter.ToList())
                    {
                        if (Widgets.ButtonText(new Rect(x, y, 20f, 20f), "X"))
                        {
                            selectedPerch.sourceFilter.Remove(resource);
                        }
                        Widgets.Label(new Rect(x + 25f, y, width - 25f, 20f), resource.label);
                        y += 22f;
                    }
                }

                // Available resources
                y += 10f;
                Widgets.Label(new Rect(x, y, width, 22f), "Available Resources:");
                y += 22f;

                var available = selectedPerch.GetAvailableResources();
                if (available.Count == 0)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(x + 10f, y, width - 10f, 20f), "None in adjacent storage");
                    GUI.color = Color.white;
                }
                else
                {
                    foreach (var r in available.Take(8))
                    {
                        Widgets.Label(new Rect(x + 10f, y, width - 10f, 20f),
                            $"{r.Key.label}: {r.Value}");
                        y += 20f;
                    }
                    if (available.Count > 8)
                    {
                        Widgets.Label(new Rect(x + 10f, y, width - 10f, 20f),
                            $"...and {available.Count - 8} more");
                    }
                }
            }
        }

        private void DrawBeaconZoneConfiguration(Rect rect, float x, float y, float width)
        {
            var beacon = selectedBeaconZone;
            var zone = beacon.GetLandingZone();

            // Zone info
            if (zone.HasValue)
            {
                Widgets.Label(new Rect(x, y, width, 22f), $"Zone Size: {zone.Value.Width}x{zone.Value.Height}");
            }
            y += 26f;

            // Role toggle
            Widgets.Label(new Rect(x, y, 80f, 22f), "Role:");
            if (Widgets.ButtonText(new Rect(x + 85f, y, 80f, 22f), beacon.Role.ToString()))
            {
                beacon.ToggleRole();
            }
            y += 28f;

            // SLINGs in zone
            var slings = beacon.GetDockedSlings();
            Widgets.Label(new Rect(x, y, width, 22f), $"SLINGs in Zone: {slings.Count}");
            y += 26f;

            Widgets.DrawLineHorizontal(x, y, width);
            y += 10f;

            if (beacon.IsSink)
            {
                // Threshold targets section
                Widgets.Label(new Rect(x, y, width, 22f), "Import Targets:");
                y += 24f;

                // Add threshold button
                if (Widgets.ButtonText(new Rect(x, y, 120f, 22f), "+ Add Resource"))
                {
                    ShowAddBeaconThresholdMenu();
                }
                y += 28f;

                // Draw existing thresholds
                var thresholds = beacon.ThresholdTargets.ToList();
                foreach (var kvp in thresholds)
                {
                    Rect threshRect = new Rect(x, y, width, 22f);
                    DrawBeaconThresholdRow(threshRect, kvp.Key, kvp.Value);
                    y += 26f;
                }

                // Current demand
                y += 10f;
                var needed = beacon.GetResourcesNeeded();
                if (needed.Count > 0)
                {
                    GUI.color = new Color(1f, 0.6f, 0f);
                    Widgets.Label(new Rect(x, y, width, 22f), "Resources Needed:");
                    y += 22f;
                    GUI.color = Color.white;

                    foreach (var n in needed.Take(6))
                    {
                        Widgets.Label(new Rect(x + 10f, y, width - 10f, 20f),
                            $"{n.Key.label}: {n.Value}");
                        y += 20f;
                    }
                }
                else if (thresholds.Count > 0)
                {
                    GUI.color = Color.green;
                    Widgets.Label(new Rect(x, y, width, 22f), "All targets satisfied");
                    GUI.color = Color.white;
                }
            }
            else // SOURCE
            {
                // Export filter section
                Widgets.Label(new Rect(x, y, width, 22f), "Export Filter:");
                y += 24f;

                if (Widgets.ButtonText(new Rect(x, y, 120f, 22f), "+ Add Resource"))
                {
                    ShowAddBeaconFilterMenu();
                }
                y += 28f;

                // Draw existing filters with thresholds
                var filter = beacon.SourceFilter.ToList();
                foreach (var resource in filter)
                {
                    if (Widgets.ButtonText(new Rect(x, y, 20f, 20f), "X"))
                    {
                        beacon.RemoveFromSourceFilter(resource);
                        continue;
                    }
                    Widgets.Label(new Rect(x + 25f, y, 100f, 20f), resource.label);

                    // Threshold (keep amount)
                    int threshold = beacon.GetThreshold(resource);
                    Widgets.Label(new Rect(x + 130f, y, 40f, 20f), "Keep:");
                    string buffer = threshold.ToString();
                    buffer = Widgets.TextField(new Rect(x + 170f, y, 50f, 20f), buffer);
                    if (int.TryParse(buffer, out int newThreshold) && newThreshold != threshold)
                    {
                        beacon.SetThreshold(resource, newThreshold);
                    }
                    y += 24f;
                }

                // Available for export
                y += 10f;
                var available = beacon.GetAvailableForExport();
                if (available.Count > 0)
                {
                    Widgets.Label(new Rect(x, y, width, 22f), "Available for Export:");
                    y += 22f;

                    foreach (var a in available.Take(6))
                    {
                        Widgets.Label(new Rect(x + 10f, y, width - 10f, 20f),
                            $"{a.Key.label}: {a.Value}");
                        y += 20f;
                    }
                }
                else if (filter.Count > 0)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(x, y, width, 22f), "Nothing available for export");
                    GUI.color = Color.white;
                }
            }
        }

        private void DrawBeaconThresholdRow(Rect rect, ThingDef resource, int target)
        {
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, 20f, 20f), "X"))
            {
                selectedBeaconZone.SetThreshold(resource, 0);
                return;
            }

            Widgets.Label(new Rect(rect.x + 25f, rect.y, 100f, 20f), resource.label);

            Widgets.Label(new Rect(rect.x + 130f, rect.y, 50f, 20f), "Target:");
            string buffer = target.ToString();
            buffer = Widgets.TextField(new Rect(rect.x + 180f, rect.y, 50f, 20f), buffer);
            if (int.TryParse(buffer, out int newTarget) && newTarget != target)
            {
                selectedBeaconZone.SetThreshold(resource, newTarget);
            }

            int current = selectedBeaconZone.Map?.resourceCounter.GetCount(resource) ?? 0;
            Color statusColor = current >= target ? Color.green : new Color(1f, 0.6f, 0f);
            GUI.color = statusColor;
            Widgets.Label(new Rect(rect.x + 240f, rect.y, 80f, 20f), $"({current}/{target})");
            GUI.color = Color.white;
        }

        private void ShowAddBeaconThresholdMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            ThingDef[] commonResources = new[]
            {
                ThingDefOf.Steel, ThingDefOf.Plasteel, ThingDefOf.ComponentIndustrial,
                ThingDefOf.ComponentSpacer, ThingDefOf.Chemfuel, ThingDefOf.Gold,
                ThingDefOf.Silver, ThingDefOf.Uranium
            };

            foreach (var resource in commonResources)
            {
                if (selectedBeaconZone.GetThreshold(resource) == 0)
                {
                    ThingDef r = resource;
                    options.Add(new FloatMenuOption(resource.label, () =>
                    {
                        selectedBeaconZone.SetThreshold(r, 100);
                    }));
                }
            }

            if (options.Count == 0)
                options.Add(new FloatMenuOption("All common resources already added", null));

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void ShowAddBeaconFilterMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            ThingDef[] commonResources = new[]
            {
                ThingDefOf.Steel, ThingDefOf.Plasteel, ThingDefOf.ComponentIndustrial,
                ThingDefOf.ComponentSpacer, ThingDefOf.Chemfuel, ThingDefOf.Gold,
                ThingDefOf.Silver, ThingDefOf.Uranium
            };

            foreach (var resource in commonResources)
            {
                if (!selectedBeaconZone.SourceFilter.Contains(resource))
                {
                    ThingDef r = resource;
                    options.Add(new FloatMenuOption(resource.label, () =>
                    {
                        selectedBeaconZone.AddToSourceFilter(r);
                    }));
                }
            }

            if (options.Count == 0)
                options.Add(new FloatMenuOption("All common resources already added", null));

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void DrawThresholdRow(Rect rect, ThingDef resource, int target)
        {
            // Remove button
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, 20f, 20f), "X"))
            {
                selectedPerch.thresholdTargets.Remove(resource);
                return;
            }

            // Resource name
            Widgets.Label(new Rect(rect.x + 25f, rect.y, 100f, 20f), resource.label);

            // Target amount
            Widgets.Label(new Rect(rect.x + 130f, rect.y, 50f, 20f), "Target:");
            string buffer = target.ToString();
            buffer = Widgets.TextField(new Rect(rect.x + 180f, rect.y, 50f, 20f), buffer);
            if (int.TryParse(buffer, out int newTarget) && newTarget != target)
            {
                selectedPerch.SetThreshold(resource, newTarget);
            }

            // Current amount (map-wide stock)
            int current = selectedPerch.GetMapStock(resource);
            Color statusColor = current >= target ? Color.green : new Color(1f, 0.6f, 0f);
            GUI.color = statusColor;
            Widgets.Label(new Rect(rect.x + 240f, rect.y, 80f, 20f), $"({current}/{target})");
            GUI.color = Color.white;
        }

        private void ShowAddThresholdMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            // Common resources
            ThingDef[] commonResources = new[]
            {
                ThingDefOf.Steel,
                ThingDefOf.Plasteel,
                ThingDefOf.ComponentIndustrial,
                ThingDefOf.ComponentSpacer,
                ThingDefOf.Chemfuel,
                ThingDefOf.Gold,
                ThingDefOf.Silver,
                ThingDefOf.Uranium
            };

            foreach (var resource in commonResources)
            {
                if (!selectedPerch.thresholdTargets.ContainsKey(resource))
                {
                    ThingDef r = resource;
                    options.Add(new FloatMenuOption(resource.label, () =>
                    {
                        selectedPerch.SetThreshold(r, 100);
                    }));
                }
            }

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("All common resources already added", null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void ShowAddFilterMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            ThingDef[] commonResources = new[]
            {
                ThingDefOf.Steel,
                ThingDefOf.Plasteel,
                ThingDefOf.ComponentIndustrial,
                ThingDefOf.ComponentSpacer,
                ThingDefOf.Chemfuel,
                ThingDefOf.Gold,
                ThingDefOf.Silver,
                ThingDefOf.Uranium
            };

            foreach (var resource in commonResources)
            {
                if (!selectedPerch.sourceFilter.Contains(resource))
                {
                    ThingDef r = resource;
                    options.Add(new FloatMenuOption(resource.label, () =>
                    {
                        selectedPerch.sourceFilter.Add(r);
                    }));
                }
            }

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("All common resources already added", null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        #endregion
    }
}
