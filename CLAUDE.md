# ARSENAL: Autonomous Cruise Missile Network - Claude Development Guide

## Project Overview

**Name:** ARSENAL: Autonomous Cruise Missile Network
**Type:** RimWorld Mod (v1.5+)
**Language:** C# (.NET Framework 4.7.2)
**Namespace:** `Arsenal`
**Output:** `Arsenal.dll`

This is a sophisticated RimWorld mod implementing three integrated defense/logistics systems:
1. **DAGGER Network** - Global autonomous cruise missile logistics
2. **DART System** - Local autonomous drone swarm defense
3. **MULE System** - Autonomous ground drones for mining and hauling

---

## RimWorld Source Reference

Decompiled vanilla + all DLC source is at `~/RimWorldSource/`.
All namespaces (RimWorld, Verse, Verse.AI, etc.) include Royalty, Ideology, Biotech, Anomaly, and Odyssey code.

**Key files to consult before implementing new systems:**
- `Skyfaller.cs`, `ActiveDropPod.cs` — orbital/air transit mechanics
- `ThingWithComps.cs`, `ThingComp.cs`, `CompProperties.cs` — component architecture
- `MapComponent.cs`, `GameComponent.cs`, `WorldComponent.cs` — singleton managers
- `Building.cs`, `Building_WorkTable.cs` — building behavior patterns
- `JobDriver.cs`, `Toil.cs` — job/work systems
- `ITab.cs`, `Window.cs` — UI patterns
- `CompTransporter.cs`, `TransportPodsArrivalAction.cs` — transport pod/shuttle systems
- `WorldObject.cs`, `TravelingTransportPods.cs` — world map travel

**Always consult relevant vanilla source before writing new systems. Match vanilla and DLC patterns.**

Use `grep -r` and `find` to locate relevant classes when unsure of exact filenames.

---

## Directory Structure

```
Arsenal/
├── About/
│   └── arsenal_about.xml          # Mod metadata (packageId, name, dependencies)
├── Defs/                          # XML game definitions
│   ├── ThingDefs/                 # Buildings, items, apparel
│   ├── RecipeDefs/                # Crafting recipes
│   ├── ResearchProjectDefs/       # Research tree
│   ├── MithrilProductDefs/        # Custom product definitions (DAGGER, DART)
│   └── WorldObjectDefs/           # World map objects
├── Languages/                     # Localization files
├── Source/Arsenal/                # C# source code (~9,163 lines, 26 files)
├── Textures/                      # Graphics assets
└── build.sh                       # macOS build script
```

---

## Core Architecture

### Static Network Manager Pattern

All systems are coordinated through `ArsenalNetworkManager` - a static class acting as the central registry:

```csharp
public static class ArsenalNetworkManager
{
    // Component registries (Lists and Dictionaries)
    private static List<Building_Arsenal> arsenals;
    private static List<Building_Hub> hubs;
    private static List<Building_Hop> hops;
    private static List<Building_Lattice> lattices;
    private static List<Building_Quiver> quivers;
    private static List<Building_ARGUS> argusUnits;
    private static Dictionary<int, Building_HERALD> heraldsPerTile;
    private static WorldObject_SkyLinkSatellite orbitalSatellite;
    private static List<Building_SkyLinkTerminal> terminals;
    private static List<Pawn> hawkeyePawns;
}
```

**Key Pattern:** Buildings register in `SpawnSetup()` and deregister in `DeSpawn()`:
```csharp
public override void SpawnSetup(Map map, bool respawningAfterLoad)
{
    base.SpawnSetup(map, respawningAfterLoad);
    ArsenalNetworkManager.RegisterXxx(this);
}

public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
{
    ArsenalNetworkManager.DeregisterXxx(this);
    base.DeSpawn(mode);
}
```

### GameComponent for State Reset

`GameComponent_ArsenalNetwork` resets static state on new game/load:
```csharp
public override void LoadedGame()
{
    ArsenalNetworkManager.Reset(); // Buildings re-register in SpawnSetup
}
```

---

## System 1: DAGGER Network (Global Cruise Missiles)

### Components

| Building | File | Purpose |
|----------|------|---------|
| **ARSENAL** | `building_arsenal.cs` | Manufacturing facility with 3 production lines |
| **HUB** | `building_hub.cs` | Missile storage (10 max) and launch platform |
| **HOP** | `building_hop.cs` | In-transit refueling station |
| **HERALD** | `Building_HERALD.cs` | Remote tile network relay |

### Flow

```
ARSENAL manufactures DAGGER → Skyfaller launch → WorldObject_TravelingMissile
    → HOPs for refueling (if needed) → Destination HUB storage

HUB launch → WorldObject_MissileStrike → Skyfaller impact → Explosion
```

### Key Classes

**ManufacturingLine** (`ManufacturingLine.cs`)
- Each ARSENAL has 3 independent lines
- States: `Paused`, `Idle`, `WaitingResources`, `DestinationUnreachable`, `Manufacturing`
- Modes: `Auto` (picks least-full HUB) or `Locked` (specific destination)

**MithrilProductDef** (`MithrilProductDef.cs`)
- Custom Def type for products
- Defines: `workAmount`, `costList`, `outputFlyer`, `destinationType`

### Network Connectivity

Remote tiles require the full chain:
```
LATTICE (powered) → Terminal (within 15 tiles) → SKYLINK satellite → HERALD (on remote tile)
```

Check via: `ArsenalNetworkManager.IsTileConnected(int worldTile)`

### Routing Logic

ARSENAL calculates routes through HOPs in `GetRouteToHub()`:
- Checks direct distance first
- If out of range, finds best HOP toward destination
- HOPs must have fuel (≥50) and network connectivity
- Loop detection prevents infinite routing

---

## System 2: DART System (Local Drone Swarm)

### Components

| Building | File | Purpose |
|----------|------|---------|
| **LATTICE** | `Building_Lattice.cs` | Central C&C node (only ONE per map) |
| **QUIVER** | `Building_Quiver.cs` | DART storage hub (10 max) |
| **ARGUS** | `Building_ARGUS.cs` | Threat detection sensor (45-tile radius) |
| **DART** | `DART_Flyer.cs` | Autonomous kamikaze drone |

### Detection Flow

```
ARGUS scans (60 tick interval) → Reports threats to LATTICE via ReportThreat()
    → LATTICE aggregates threats → Assigns DARTs from QUIVERs
    → DART flies to target → Impact explosion
```

### DART States (`DartState.cs`)

```csharp
public enum DartState
{
    Idle,        // Stored in QUIVER
    Delivery,    // Flying from ARSENAL to QUIVER
    Engaging,    // Flying to hostile target
    Returning,   // Flying back to QUIVER
    Reassigning  // Target died, waiting for new target
}
```

### Threat Evaluation

The LATTICE evaluates each threat to determine how many DARTs to assign:

```csharp
// Base threat values (Building_Lattice.cs)
BASE_THREAT_TRIBAL = 35f;
BASE_THREAT_PIRATE = 50f;
BASE_THREAT_MECHANOID = 150f;
DART_LETHALITY = 20f;  // Each DART handles ~20 threat points
```

**Evaluation Factors:**
| Factor | Effect |
|--------|--------|
| **Race Type** | Mechanoid=150, Pirate=50, Tribal=35, Animal=size-based |
| **Animals** | Predator: 30+(size×30), Non-predator: 20+(size×15), Manhunter: +30% |
| **Health %** | Threat × healthPct |
| **Ranged Weapon** | ×1.2 multiplier |
| **Spacer Weapon** | ×1.3 multiplier |
| **Armor** | 1.0× (naked) to 2.0× (100% armor), weighted 70% sharp / 30% blunt |
| **Shield Belt** | ×2.0 multiplier (detects `CompShield`) |

**Example DART Allocations:**
| Target | Threat | DARTs |
|--------|--------|-------|
| Naked tribal | 35 | 2 |
| Armored pirate (50% armor) | 75 | 4 |
| Shielded pirate | 100 | 5 |
| Manhunting thrumbo | 195 | 10 |
| Centipede | 225 | 12 |

DARTs needed = `Ceil(threatValue / DART_LETHALITY)`

### DART Assignment Logic

Per processing cycle (60 ticks), LATTICE assigns DARTs using a budget system:
- **Launch Budget:** `min(MAX_DARTS_PER_CYCLE, max(4, threat_count))`
- **MAX_DARTS_PER_CYCLE:** 8
- **Distribution:** Multi-pass - one DART per threat per pass until budget exhausted
- **QUIVER Selection:** Nearest QUIVER to target with available DARTs

### DART Re-Tasking (Target Dies Mid-Flight)

When a DART's target becomes invalid:
1. **Immediate Reassignment:** `RequestReassignment()` tries instant redirect
2. **Queue Fallback:** If no target found, DART added to `awaitingReassignment`
3. **Loiter Pattern:** DART circles (2-cell radius, half speed) while waiting
4. **Timeout:** After 180 ticks (3 sec), returns home
5. **Valid Targets:** Includes both ARGUS and HAWKEYE-detected threats

### Key Behaviors

- **Multi-Target Engagement:** Up to 8 DARTs launched per processing cycle across multiple threats
- **Immediate Reassignment:** DARTs redirect instantly when target dies (no queue delay)
- **Loiter Pattern:** DARTs circle while awaiting reassignment instead of freezing
- **Stale Threats:** Threats not seen for 180 ticks are removed
- **Chain Explosion:** QUIVER explodes based on stored DART count when destroyed
- **Assignment Tracking:** Properly decrements old target count on reassignment

---

## System 3: SKYLINK (Orbital Satellite)

### Components

| Component | File | Purpose |
|-----------|------|---------|
| **Launch Pad** | `Building_SkyLinkLaunchPad.cs` | Manufactures and launches satellite |
| **Terminal** | `Building_SkyLinkTerminal.cs` | Ground station linking LATTICE to satellite |
| **Satellite** | `WorldObject_SkyLinkSatellite.cs` | Orbiting communications backbone |
| **Renderer** | `WorldComponent_SkyLinkRenderer.cs` | Visual orbital rendering |

### Satellite Orbit

```csharp
// WorldObject_SkyLinkSatellite.cs
ORBIT_RADIUS = 115f;  // Distance from world center
ORBIT_HEIGHT = 10f;   // Height above equatorial plane
ORBIT_SPEED = 0.3f;   // Degrees per tick
```

---

## System 4: HAWKEYE (Mobile Tactical Suite)

**File:** `Apparel_HawkEye.cs`

Wearable helmet that acts as a mobile ARGUS node:
- 30-tile detection radius
- Requires SKYLINK connection
- Abilities:
  - **DAGGER Strike** - Designate cruise missile strikes
  - **MARK DART TARGET** - Priority target for DART convergence (30s duration, 30s cooldown)

**Important:** HAWKEYE-detected targets ARE valid for DART engagement even outside ARGUS range.

### Custom Rendering

HAWKEYE uses custom `DrawWornExtras()` for proper directional textures:
- **Reason:** RimWorld's default Overhead layer rendering flips east texture for west
- **Solution:** Custom rendering with `Graphic_Multi` respects all 4 directional textures
- **XML Config:** `<apparel Inherit="False">` prevents parent's wornGraphicPath
- **Textures:** `Arsenal/MITHRIL_HAWKEYE_north/south/east/west.png`

```csharp
// Apparel_HawkEye.cs
public override void DrawWornExtras()
{
    Rot4 facing = Wearer.Rotation;
    Material mat = cachedGraphic.MatAt(facing);  // Gets correct directional texture
    Vector3 drawPos = Wearer.DrawPos;
    drawPos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
    drawPos.z += 0.34f;  // Head offset
    // ... render with matrix transform
}
```

### HAWKEYE Integration with LATTICE

HAWKEYE threats are included in LATTICE processing via `GetAllValidThreats()`:
- `CompHawkeyeSensor.GlobalPriorityTarget` - marked priority target
- `comp.GetDetectedThreats()` - all threats in HAWKEYE's 30-tile LOS radius
- Used for both initial assignment AND reassignment of DARTs

---

## System 5: MULE System (Ground Utility Drones)

### Overview

MULE (Mobile Utility Logistics Engine) - Autonomous ground drones for mining and hauling tasks. MULEs are Pawn-based entities that leverage RimWorld's native pathfinding and job systems.

### Components

| Component | File | Purpose |
|-----------|------|---------|
| **MULE_Pawn** | `MULE_Pawn.cs` | Pawn subclass - autonomous drone with battery |
| **STABLE** | `Building_Stable.cs` | Storage/charging hub (10 MULE capacity) |
| **MORIA** | `Building_Moria.cs` | Resource sink for MULE deliveries |
| **Comp_MuleBattery** | `Comp_MuleBattery.cs` | Battery component for power management |
| **MuleTask** | `MuleTask.cs` | Task definition (mining, hauling) |
| **MuleState** | `MuleState.cs` | State enumeration |

### MULE States (`MuleState.cs`)

```csharp
public enum MuleState
{
    Idle,              // Docked in STABLE, fully charged, awaiting task
    Charging,          // Docked in STABLE, recharging battery
    Inert,             // Battery depleted, immobile
    DeliveringToStable,// Newly manufactured, traveling to STABLE
    ReturningHome,     // Returning to STABLE (low battery or no tasks)
    Deploying,         // Leaving STABLE for a task
    Mining,            // Actively mining
    Hauling            // Actively hauling
}
```

### Lifecycle Flow

```
ARSENAL manufactures MULE → Spawns near ARSENAL → Walks to nearest STABLE
    → Docks (despawns) → Charges to 100%

STABLE detects task via LATTICE → Deploys MULE (spawns) → MULE works
    → Task complete → Find next task OR return to STABLE

Low battery → Return to STABLE → Dock → Recharge → Deploy again
```

### Key Design Decisions

**Pawn-Based Architecture:**
- Extends `Pawn` class for native RimWorld pathfinding (avoids custom A* lag)
- Uses `AnimalThingBase` parent in XML (avoids Biotech mechanoid overseer requirements)
- Custom `ThinkTreeDef` with `JobGiver_Idle` fallback
- Jobs started via `jobs.StartJob()` with RimWorld's job system

**Dock/Deploy Pattern:**
- MULEs are **despawned** when docked in STABLE (stored in `dockedMules` list)
- MULEs are **spawned** when deployed for tasks
- Prevents idle MULEs from cluttering the map
- STABLE handles charging while docked via `TickRare()`

**Battery System:**
- `Comp_MuleBattery` ThingComp attached to MULE pawn
- Drains while working, charges while docked
- Returns to STABLE at 25% battery (`NeedsRecharge` threshold)
- Enters `Inert` state if fully depleted

### Task Management

**Task Sources:**
1. LATTICE task queue (`pendingMuleTasks`)
2. Local scanning (`ScanForLocalTask()`) for tiles without LATTICE

**Task Assignment Flow:**
```csharp
// STABLE.TryDeployForTasks() - runs in TickRare()
1. Find ready MULE (Idle + full battery)
2. Request task from LATTICE or scan locally
3. Deploy MULE (spawn + assign task)

// MULE_Pawn.OnTaskCompleted()
1. Clear current task
2. Check if needs recharge → ReturnToStable()
3. Try to find another task → TryFindAndStartTask()
4. No tasks → ReturnToStable()
```

**Reservation System:**
- Checks `Map.reservationManager.CanReserve()` before creating tasks
- Prevents multiple MULEs targeting same mineable/haulable
- Double-checks reservation in `StartJobForTask()` before starting job

### Task Validation (`IsTaskStillValid()`)

```csharp
// Mining: target mineable must still exist
Building mineable = targetCell.GetFirstMineable(Map);
return mineable != null && !mineable.Destroyed;

// Hauling: check carrying status FIRST, then spawn status
if (carryTracker?.CarriedThing == targetThing) return true;  // Carrying = valid
if (!targetThing.Spawned) return false;  // Not spawned, not carrying = invalid
return !targetThing.IsForbidden(Faction.OfPlayer);
```

**Important:** Check if carrying the item BEFORE checking spawn status. Carried items are not "spawned" on the ground.

### Body Definition

```xml
<BodyDef>
  <defName>MULE_Body</defName>
  <corePart>
    <def>MechanicalThorax</def>
    <parts>
      <!-- Required for manipulation/carrying -->
      <li><def>MechanicalArm</def><customLabel>left cargo arm</customLabel></li>
      <li><def>MechanicalArm</def><customLabel>right cargo arm</customLabel></li>
      <!-- Wheels for movement -->
      <li><def>MechanicalLeg</def><customLabel>front left wheel</customLabel></li>
      <li><def>MechanicalLeg</def><customLabel>front right wheel</customLabel></li>
      <li><def>MechanicalLeg</def><customLabel>rear left wheel</customLabel></li>
      <li><def>MechanicalLeg</def><customLabel>rear right wheel</customLabel></li>
    </parts>
  </corePart>
</BodyDef>
```

**Important:** MechanicalArm parts are required for manipulation capacity. Without them, carrying is limited to 1 item regardless of CarryingCapacity stat.

### Naming Pattern

```csharp
// Only assign name if not already set (persists through dock/deploy cycles)
if (string.IsNullOrEmpty(customName))
{
    customName = "MULE-" + muleCounter.ToString("D2");
    muleCounter++;
}
```

### MULE Parameters

| Parameter | Value | Notes |
|-----------|-------|-------|
| CarryingCapacity | 75 | Requires MechanicalArm body parts |
| MiningSpeed | 2.5 | Equivalent to ~skill 12 pawn |
| MiningYield | 1.0 | 100% yield |
| MoveSpeed | 3.5 | Cells per second |
| MaxHitPoints | 150 | |
| Battery Max | 100 | Comp_MuleBattery |
| Battery Drain | 0.005/tick | While working |
| Battery Charge | 0.05/tick | While docked |
| Recharge Threshold | 25% | Returns to STABLE |
| Idle Timeout | 300 ticks | Returns to STABLE if idle (5 sec) |

### Manufacturing Limits

MULE manufacturing is limited by STABLE capacity on the map:
```csharp
int totalStableCapacity = stables.Count * Building_Stable.MAX_MULE_CAPACITY;  // 10 per STABLE
int totalMulesOnMap = dockedMules + spawnedMules;
if (totalMulesOnMap >= totalStableCapacity) return null;  // Block manufacturing
```

### Common Issues

**MULEs not picking up full stacks:**
- Ensure body has `MechanicalArm` parts for manipulation
- CarryingCapacity stat alone is not enough

**MULEs stuck in loop between resources:**
- Check `IsTaskStillValid()` - must check carrying before spawn status
- Carried items are NOT spawned, so spawn check fails if done first

**Reservation conflicts (multiple MULEs same target):**
- `ScanForLocalTask()` must check `CanReserve()` for each target
- `StartJobForTask()` must verify reservation before starting job

**Names changing on deploy:**
- Check naming logic uses `string.IsNullOrEmpty(customName)` not `!respawningAfterLoad`
- `respawningAfterLoad` is false when deploying from STABLE

**Map generation errors:**
- Ensure `LifeStageDef` has all required properties:
  - `developmentalStage`, `bodySizeFactor`, `healthScaleFactor`, etc.

---

## Coding Patterns & Conventions

### 1. Building Lifecycle

```csharp
public override void SpawnSetup(Map map, bool respawningAfterLoad)
{
    base.SpawnSetup(map, respawningAfterLoad);

    // Get component references
    powerComp = GetComp<CompPowerTrader>();
    refuelableComp = GetComp<CompRefuelable>();

    // Register with network
    ArsenalNetworkManager.RegisterXxx(this);

    // Auto-name if new
    if (!respawningAfterLoad)
    {
        customName = "PREFIX-" + counter.ToString("D2");
        counter++;
    }
}
```

### 2. Power Checking

```csharp
public bool IsPoweredOn()
{
    return powerComp == null || powerComp.PowerOn;
}
```

### 3. Network Connectivity

```csharp
public bool HasNetworkConnection()
{
    if (Map == null) return false;
    return ArsenalNetworkManager.IsTileConnected(Map.Tile);
}
```

### 4. Tick Processing

- Use `TickRare()` (every 250 ticks) for expensive operations
- Use `IsHashIntervalTick(n)` for periodic checks
- Cache frequently accessed data with refresh intervals

```csharp
private const int CACHE_REFRESH_INTERVAL = 120;
private int lastCacheRefresh = -999;

public void RefreshNetworkCache()
{
    if (Find.TickManager.TicksGame - lastCacheRefresh < CACHE_REFRESH_INTERVAL)
        return;
    // ... refresh logic
    lastCacheRefresh = Find.TickManager.TicksGame;
}
```

### 5. Save/Load (ExposeData)

```csharp
public override void ExposeData()
{
    base.ExposeData();

    Scribe_Values.Look(ref customName, "customName");
    Scribe_Values.Look(ref intField, "intField", defaultValue);
    Scribe_References.Look(ref buildingRef, "buildingRef");
    Scribe_Collections.Look(ref list, "list", LookMode.Deep);
    Scribe_Defs.Look(ref defRef, "defRef");

    // Post-load cleanup
    if (Scribe.mode == LoadSaveMode.PostLoadInit)
    {
        list?.RemoveAll(x => x == null);
    }
}
```

### 6. Gizmos (UI Buttons)

```csharp
public override IEnumerable<Gizmo> GetGizmos()
{
    foreach (Gizmo g in base.GetGizmos())
        yield return g;

    yield return new Command_Action
    {
        defaultLabel = "Button Label",
        defaultDesc = "Description text.",
        icon = ContentFinder<Texture2D>.Get("UI/Path/To/Icon", false),
        action = delegate { /* action code */ }
    };

    // Dev-only gizmos
    if (Prefs.DevMode)
    {
        yield return new Command_Action { /* ... */ };
    }
}
```

### 7. Visual Effects

```csharp
// Smoke
FleckMaker.ThrowSmoke(position.ToVector3Shifted(), Map, 0.5f);

// Sparks
FleckMaker.ThrowMicroSparks(position.ToVector3Shifted(), Map);

// Fire glow
FleckMaker.ThrowFireGlow(position.ToVector3Shifted(), Map, 0.5f);

// Lightning glow (scanner pulse)
FleckMaker.ThrowLightningGlow(position.ToVector3Shifted(), Map, 0.5f);

// Dust
FleckMaker.ThrowDustPuff(position, Map, 1f);
```

### 8. Messages

```csharp
Messages.Message("Text", this, MessageTypeDefOf.PositiveEvent);
Messages.Message("Text", this, MessageTypeDefOf.NeutralEvent);
Messages.Message("Text", this, MessageTypeDefOf.NegativeEvent);
Messages.Message("Text", this, MessageTypeDefOf.RejectInput);
```

---

## Important Constants

### Ranges & Capacities

| Constant | Value | Location |
|----------|-------|----------|
| HUB missile capacity | 10 | `Building_Hub.cs` |
| HUB launch radius | 100 tiles | `Building_Hub.cs` |
| HUB base range | 18 tiles | `Building_Hub.cs` |
| HOP range extension | 12 tiles | `Building_Hop.cs` |
| HOP fuel capacity | 5000 | XML Def |
| QUIVER DART capacity | 10 | `Building_Quiver.cs` |
| ARGUS detection radius | 45 tiles | `Building_ARGUS.cs` |
| HAWKEYE detection radius | 30 tiles | `Apparel_HawkEye.cs` |
| Terminal-LATTICE max distance | 15 tiles | `Building_SkyLinkTerminal.cs` |
| STABLE MULE capacity | 10 | `Building_Stable.cs` |
| MULE carrying capacity | 75 | `ThingDefs_MULE.xml` |

### Timing (in ticks, 60 ticks = 1 second)

| Constant | Value | Purpose |
|----------|-------|---------|
| CACHE_REFRESH_INTERVAL | 120 | Network cache refresh |
| SCAN_INTERVAL | 60 | ARGUS threat scanning |
| PROCESS_INTERVAL | 60 | LATTICE threat processing |
| THREAT_STALE_TICKS | 180 | Remove unseen threats |
| MAX_DARTS_PER_CYCLE | 8 | Max DARTs launched per processing cycle |
| REASSIGN_TIMEOUT | 180 | DART waits for new target before returning |
| PATH_UPDATE_INTERVAL | 30 | DART path recalculation |
| REFUEL_TICKS | 3600 | HOP refueling time |
| MULE_IDLE_TIMEOUT | 300 | MULE returns to STABLE if idle (5 sec) |
| MULE_TASK_SCAN_INTERVAL | 60 | MULE scans for tasks while idle |

### DART Parameters

| Constant | Value |
|----------|-------|
| SPEED | 0.18 cells/tick |
| EXPLOSION_RADIUS | 2.5 tiles |
| EXPLOSION_DAMAGE | 65 |

---

## DefOf References

`ArsenalDefOf.cs` defines static references to XML defs:

```csharp
// Things
Arsenal_CruiseMissile      // DAGGER missile item
Arsenal_MissileFactory     // ARSENAL building
Arsenal_MissileHub         // HUB building
Arsenal_RefuelStation      // HOP building
Arsenal_Lattice            // LATTICE building
Arsenal_Quiver             // QUIVER building
Arsenal_DART_Flyer         // DART flyer (in-flight drone)
Arsenal_DART_Item          // DART item (craftable)

// Skyfallers
Arsenal_MissileLaunching   // Launch animation
Arsenal_MissileLanding     // Landing animation
Arsenal_MissileStrikeIncoming // Strike incoming animation

// WorldObjects
Arsenal_TravelingMissile   // Missile in transit on world map
Arsenal_MissileStrike      // Strike traveling to target

// Products
MITHRIL_Product_DAGGER     // DAGGER manufacturing definition
MITHRIL_Product_DART       // DART manufacturing definition
MITHRIL_Product_MULE       // MULE manufacturing definition

// MULE System
Arsenal_MULE_Race          // MULE pawn ThingDef
Arsenal_MULE_Kind          // MULE PawnKindDef
Arsenal_MULE_Item          // Packaged MULE item
Arsenal_Stable             // STABLE building
Arsenal_Moria              // MORIA resource sink
Arsenal_MULESystem         // Research project
```

---

## Common Development Tasks

### Adding a New Building

1. Create class inheriting from `Building`
2. Add registration to `ArsenalNetworkManager`
3. Add entry to `ArsenalDefOf.cs`
4. Create XML ThingDef in `Defs/ThingDefs/`

### Adding a New Product

1. Create `MithrilProductDef` XML in `Defs/MithrilProductDefs/`
2. Add static reference in `ArsenalDefOf.cs`
3. Handle in `Building_Arsenal.SpawnProductFlyer()`

### Debugging

Use Prefs.DevMode gizmos:
```csharp
if (Prefs.DevMode)
{
    yield return new Command_Action
    {
        defaultLabel = "DEV: Debug Action",
        action = delegate { /* debug code */ }
    };
}
```

Log with namespace prefix:
```csharp
Log.Message($"[ARSENAL] Debug info: {variable}");
Log.Warning("[ARSENAL] Warning message");
Log.Error("[ARSENAL] Error message");
```

---

## Network Dependency Chain

```
┌─────────────┐
│   LATTICE   │ (one per network, usually on home tile)
└──────┬──────┘
       │
       ▼
┌─────────────┐     ┌─────────────┐
│  Terminal   │────▶│   SKYLINK   │ (orbital satellite)
└─────────────┘     └──────┬──────┘
                           │
       ┌───────────────────┼───────────────────┐
       ▼                   ▼                   ▼
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│   HERALD    │     │   HERALD    │     │   HERALD    │
│  (Tile A)   │     │  (Tile B)   │     │  (Tile C)   │
└──────┬──────┘     └──────┬──────┘     └──────┬──────┘
       │                   │                   │
       ▼                   ▼                   ▼
   HUB/HOP/etc        HUB/HOP/etc         HUB/HOP/etc
   on remote          on remote           on remote
   tiles              tiles               tiles
```

Home tile (where LATTICE is) = always connected
Remote tiles = need SKYLINK + HERALD

---

## Testing Checklist

When making changes, verify:

- [ ] Buildings register/deregister correctly with ArsenalNetworkManager
- [ ] Save/Load works (ExposeData implemented correctly)
- [ ] Network connectivity checks pass/fail appropriately
- [ ] Visual effects play at correct times
- [ ] Gizmos appear/hide based on conditions
- [ ] Edge cases: destroyed buildings, unpowered state, missing dependencies
- [ ] No null reference exceptions in logs

---

## Build Process

**macOS:**
```bash
cd Arsenal
./build.sh
```

**Manual:**
```bash
mcs -target:library \
    -r:/path/to/RimWorld/Managed/Assembly-CSharp.dll \
    -r:/path/to/RimWorld/Managed/UnityEngine.CoreModule.dll \
    -out:Assemblies/Arsenal.dll \
    Source/Arsenal/*.cs
```

Output goes to `Arsenal/Assemblies/Arsenal.dll`

---

## Common Issues & Solutions

### "No LATTICE" errors
- Ensure only ONE LATTICE per map (enforced by `PlaceWorker_OnlyOneLattice`)
- Check LATTICE is powered

### Remote operations failing
- Verify full chain: LATTICE → Terminal → SKYLINK → HERALD
- Use `ArsenalNetworkManager.GetNetworkStatus(tile)` for diagnostics

### DARTs not launching
- Check QUIVER is not inert (needs LATTICE)
- Verify ARGUS is detecting threats (LOS check)
- Confirm rate limiting isn't blocking (15 tick delay)

### Missiles stuck/not routing
- Check HOP availability (`CanAcceptMissile()`)
- Verify HOP fuel levels (≥50 required)
- Check HOP network connectivity

### Static state persisting across saves
- Ensure `GameComponent_ArsenalNetwork.LoadedGame()` calls `Reset()`
- Buildings must re-register in `SpawnSetup()`

---

## System 6: SLING System (Autonomous Cargo Craft)

### Overview

SLING (Suborbital Logistics INterchange Glider) - Autonomous cargo craft for inter-base resource transport.

### Components

| Component | File | Purpose |
|-----------|------|---------|
| **SLING_Thing** | `SLING_Thing.cs` | Cargo craft (Building + IThingHolder) |
| **Building_PerchBeacon** | `Building_PerchBeacon.cs` | Landing zone corner beacon (NEW) |
| **Building_PERCH** | `Building_PERCH.cs` | Legacy dual-slot landing pad |
| **SlingLandingSkyfaller** | `SlingSkyfaller.cs` | Landing animation |
| **SlingLaunchingSkyfaller** | `SlingSkyfaller.cs` | Takeoff animation |
| **WorldObject_TravelingSling** | `WorldObject_TravelingSling.cs` | World map transit |

### SLING_Thing

```csharp
public class SLING_Thing : Building, IThingHolder
{
    public const int MAX_CARGO_CAPACITY = 750;
    private ThingOwner<Thing> cargoContainer;
    private bool isLoading;
    private Dictionary<ThingDef, int> targetCargo;

    public bool WantsItem(ThingDef def);
    public int TryAddCargo(Thing item);
    public void StartLoading(Dictionary<ThingDef, int> cargo);
    public bool CompleteLoading();
}
```

### SLING Parameters

| Parameter | Value |
|-----------|-------|
| Size | 6x10 |
| Cargo Capacity | 750 |
| Fuel Capacity | 150 |
| World Speed | 0.004 |

---

## System 7: PERCH Landing Beacon (Recommended)

The beacon system is **recommended** over the legacy PERCH. Place 4 beacons at corners to define a landing zone (similar to vanilla ship landing beacons).

### How It Works

1. Place 4 `Building_PerchBeacon` at rectangle corners
2. Beacons detect valid zones (minimum 9x12 for SLING 6x10 + margin)
3. System finds clear landing spot within zone
4. SLINGs land anywhere in validated zone

### Beacon Roles

```csharp
public enum PerchRole { Source, Sink }
```
- **Source**: Exports resources above threshold
- **Sink**: Imports resources to meet target levels

### Key Methods

```csharp
public CellRect? GetLandingZone();  // Find 4-beacon rectangle
public IntVec3 FindLandingSpot();   // Find clear spot in zone
public List<SLING_Thing> GetDockedSlings();
public Dictionary<ThingDef, int> GetAvailableForExport();
public Dictionary<ThingDef, int> GetResourcesNeeded();
```

### Beacon Constants

| Constant | Value |
|----------|-------|
| MIN_WIDTH | 9 |
| MIN_HEIGHT | 12 |
| MAX_BEACON_DISTANCE | 30 |
| ZONE_CHECK_INTERVAL | 120 ticks |

---

## System 8: Legacy PERCH Slot System

The original PERCH uses a large 8x24 building with fixed dual slots. Supported for backward compatibility.

### CRITICAL: Position/Cell Registration Bug

**ALWAYS use despawn/respawn when moving SLINGs:**

```csharp
// WRONG - Position set but cell registration NOT updated
sling.Position = newPos;

// CORRECT - Proper cell registration
sling.DeSpawn(DestroyMode.Vanish);
GenSpawn.Spawn(sling, newPos, Map, Rot4.North);
```

Setting Position directly causes:
- Correct Position value in debug
- WRONG visual location
- WRONG selection outline (white squares)
- WRONG occupied cells

### Position Validation on Load

```csharp
private bool needsPositionValidation = false;

// Set in PostLoadInit
if (Scribe.mode == LoadSaveMode.PostLoadInit)
    needsPositionValidation = true;

// In RepositionSlings - respawn if flag set OR mismatch
if (positionMismatch || needsPositionValidation)
{
    sling.DeSpawn(DestroyMode.Vanish);
    GenSpawn.Spawn(sling, correctPos, Map, Rot4.North);
}
needsPositionValidation = false;
```

---

## MULE Integration with SLING Loading

### CRITICAL: Finding Items for SLING Loading

```csharp
// WRONG - Excludes items in stockpiles/storage!
foreach (Thing item in Map.listerHaulables.ThingsPotentiallyNeedingHauling())

// CORRECT - Finds ALL haulable items including stored ones
foreach (Thing item in Map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableAlways))
```

`ThingsPotentiallyNeedingHauling()` excludes items already in valid storage, but those ARE valid for SLING loading.

### MULE Task Priority

1. **SLING Loading** - Highest when SLING.IsLoading
2. Mining
3. Hauling to MORIA

### Downed State Handling

Add `if (Downed) return;` before any pathing:
- `Tick()`
- `StartJobForTask()`
- `TryFindAndStartTask()`
- `GoToStable()`

---

## SLING-Related Issues & Solutions

### SLING position/visual mismatch
- Use despawn/respawn, NOT direct Position assignment
- Use `needsPositionValidation` flag on load
- Check selection outline matches visual

### MULEs not hauling to SLINGs
- Use `ThingsInGroup(HaulableAlways)` NOT `ThingsPotentiallyNeedingHauling()`

### MULEs "tried to path while downed"
- Add `if (Downed) return;` checks before pathing operations

### Beacon zone not detected
- Need exactly 4 beacons at rectangle corners
- Minimum 9x12 size
- All beacons must be powered

---

## SLING DefOf References

```csharp
Arsenal_SLING              // SLING cargo craft
Arsenal_PerchBeacon        // Landing beacon
Arsenal_PERCH              // Legacy landing pad
Arsenal_SlingLaunching     // Takeoff skyfaller
Arsenal_SlingLanding       // Landing skyfaller
Arsenal_TravelingSling     // World object
MITHRIL_Product_SLING      // Manufacturing def
```
