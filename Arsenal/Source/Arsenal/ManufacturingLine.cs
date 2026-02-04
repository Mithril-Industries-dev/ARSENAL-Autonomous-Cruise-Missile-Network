using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    public enum DestinationMode
    {
        Auto,       // System picks least-full valid target
        Locked      // Always send to lockedDestination
    }

    public enum LineStatus
    {
        Paused,                 // Manually disabled (enabled = false)
        Idle,                   // Enabled, but all destinations full
        WaitingResources,       // Enabled, has destination, missing materials
        DestinationUnreachable, // Enabled, but destination HUB has no network/route
        Manufacturing           // Actively producing
    }

    /// <summary>
    /// Represents a single manufacturing line in ARSENAL.
    /// Each ARSENAL has 3 independent lines.
    /// </summary>
    public class ManufacturingLine : IExposable
    {
        // Identity
        public int index;                              // 0, 1, or 2

        // Configuration
        public bool enabled = false;                   // ON/OFF toggle
        public int priority = 2;                       // 1, 2, or 3 (1 = highest)
        public MithrilProductDef product;              // What to manufacture

        // Destination
        public DestinationMode destMode = DestinationMode.Auto;
        public Building lockedDestination;             // Specific HUB/QUIVER if locked

        // Runtime state
        public float progress = 0f;                    // Work done so far
        public Building currentDestination;            // Where current unit will go
        public LineStatus status = LineStatus.Paused;
        public bool resourcesConsumed = false;         // True if resources already consumed for current job

        // Parent reference (not saved, restored in SpawnSetup)
        [Unsaved]
        public Building_Arsenal arsenal;

        public float ProgressPercent => product != null && product.workAmount > 0
            ? progress / product.workAmount
            : 0f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref index, "index");
            Scribe_Values.Look(ref enabled, "enabled", false);
            Scribe_Values.Look(ref priority, "priority", 2);
            Scribe_Defs.Look(ref product, "product");
            Scribe_Values.Look(ref destMode, "destMode", DestinationMode.Auto);
            Scribe_References.Look(ref lockedDestination, "lockedDestination");
            Scribe_Values.Look(ref progress, "progress", 0f);
            Scribe_References.Look(ref currentDestination, "currentDestination");
            Scribe_Values.Look(ref status, "status", LineStatus.Paused);
            Scribe_Values.Look(ref resourcesConsumed, "resourcesConsumed", false);
        }

        /// <summary>
        /// Updates line status based on current conditions.
        /// </summary>
        public void UpdateStatus()
        {
            if (!enabled)
            {
                status = LineStatus.Paused;
                return;
            }

            if (product == null)
            {
                status = LineStatus.Paused;
                return;
            }

            // Find destination
            currentDestination = GetDestination();
            if (currentDestination == null)
            {
                // Check if it's because destination is unreachable vs just full
                if (HasUnreachableDestination())
                {
                    status = LineStatus.DestinationUnreachable;
                }
                else
                {
                    status = LineStatus.Idle;
                }
                return;
            }

            // For HUBs, verify route is still valid (in case HOPs went offline mid-production)
            if (resourcesConsumed && product.destinationType == typeof(Building_Hub))
            {
                Building_Hub hub = currentDestination as Building_Hub;
                if (hub != null && !arsenal.CanReachHubPublic(hub))
                {
                    status = LineStatus.DestinationUnreachable;
                    return;
                }
            }

            // If we've already consumed resources for this job, keep manufacturing
            if (resourcesConsumed)
            {
                status = LineStatus.Manufacturing;
                return;
            }

            // Check resources
            if (!arsenal.HasResourcesFor(product.costList))
            {
                status = LineStatus.WaitingResources;
                return;
            }

            status = LineStatus.Manufacturing;
        }

        /// <summary>
        /// Checks if there's a destination that exists but is unreachable due to network issues.
        /// </summary>
        private bool HasUnreachableDestination()
        {
            if (product == null) return false;

            if (product.destinationType == typeof(Building_Hub))
            {
                // Check if there are HUBs that exist but lack network connectivity
                var allHubs = ArsenalNetworkManager.GetAllHubs();
                foreach (var hub in allHubs)
                {
                    if (!hub.IsFull && hub.IsPoweredOn() && !hub.HasNetworkConnection())
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the destination for this line's output.
        /// Validates network connectivity for HUBs.
        /// </summary>
        public Building GetDestination()
        {
            if (destMode == DestinationMode.Locked)
            {
                // Verify locked destination is still valid, not full, and reachable
                if (lockedDestination != null &&
                    !lockedDestination.Destroyed &&
                    !IsDestinationFull(lockedDestination) &&
                    IsDestinationReachable(lockedDestination))
                {
                    return lockedDestination;
                }
                return null; // Locked but invalid/full/unreachable
            }

            // Auto mode: find least-full valid destination (already checks network connectivity)
            return arsenal.GetBestDestinationFor(product);
        }

        /// <summary>
        /// Checks if destination is reachable (network connectivity for HUBs, PERCHes, beacon zones).
        /// </summary>
        private bool IsDestinationReachable(Building dest)
        {
            if (dest is Building_Hub hub)
            {
                // HUB must have network connection AND be reachable via route
                if (!hub.HasNetworkConnection())
                    return false;
                return arsenal.CanReachHubPublic(hub);
            }
            if (dest is Building_PerchBeacon beacon)
            {
                // Beacon zones need network connectivity for SLING delivery
                return beacon.HasNetworkConnection() && beacon.IsPoweredOn;
            }
            if (dest is Building_PERCH perch)
            {
                // Legacy PERCHes need network connectivity for SLING delivery
                return perch.HasNetworkConnection() && perch.IsPoweredOn;
            }
            // QUIVERs and STABLEs are local, always reachable if not destroyed
            return true;
        }

        private bool IsDestinationFull(Building dest)
        {
            if (dest is Building_Hub hub)
                return hub.IsFull;
            if (dest is Building_Quiver quiver)
                return quiver.IsFull;
            if (dest is Building_Stable stable)
                return !stable.HasSpace;
            if (dest is Building_PerchBeacon beacon)
                return !beacon.HasSpaceForSling;
            if (dest is Building_PERCH perch)
                return perch.HasSlingOnPad;
            return true;
        }

        /// <summary>
        /// Resets the line after production completes or is cancelled.
        /// </summary>
        public void Reset()
        {
            progress = 0f;
            currentDestination = null;
            resourcesConsumed = false;
            UpdateStatus();
        }
    }
}
