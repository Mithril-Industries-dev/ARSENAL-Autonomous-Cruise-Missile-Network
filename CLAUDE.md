# ARSENAL: Autonomous Cruise Missile Network - Claude Development Guide

## Project Overview

**Name:** ARSENAL: Autonomous Cruise Missile Network
**Type:** RimWorld Mod (v1.5+)
**Language:** C# (.NET Framework 4.7.2)
**Namespace:** `Arsenal`
**Output:** `Arsenal.dll`

This is a sophisticated RimWorld mod implementing two integrated defense/logistics systems:
1. **DAGGER Network** - Global autonomous cruise missile logistics
2. **DART System** - Local autonomous drone swarm defense

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

```csharp
// Base threat values (Building_Lattice.cs)
BASE_THREAT_TRIBAL = 35f;
BASE_THREAT_PIRATE = 50f;
BASE_THREAT_MECHANOID = 150f;
DART_LETHALITY = 20f;  // Each DART handles ~20 threat points
```

DARTs needed = `Ceil(threatValue / DART_LETHALITY)`

### Key Behaviors

- **Rate Limiting:** 15 ticks between DART launches
- **Reassignment:** If target dies mid-flight, DART requests new target from LATTICE
- **Stale Threats:** Threats not seen for 180 ticks are removed
- **Chain Explosion:** QUIVER explodes based on stored DART count when destroyed

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

### Timing (in ticks, 60 ticks = 1 second)

| Constant | Value | Purpose |
|----------|-------|---------|
| CACHE_REFRESH_INTERVAL | 120 | Network cache refresh |
| SCAN_INTERVAL | 60 | ARGUS threat scanning |
| PROCESS_INTERVAL | 60 | LATTICE threat processing |
| THREAT_STALE_TICKS | 180 | Remove unseen threats |
| LAUNCH_DELAY_TICKS | 15 | Between DART launches |
| REASSIGN_TIMEOUT | 180 | DART waits for new target |
| PATH_UPDATE_INTERVAL | 30 | DART path recalculation |
| REFUEL_TICKS | 3600 | HOP refueling time |

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
