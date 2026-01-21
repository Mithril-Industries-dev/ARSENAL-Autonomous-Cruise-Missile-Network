using System;

namespace Arsenal
{
    /// <summary>
    /// Defines the operational states for DART drones in the LATTICE system.
    /// </summary>
    public enum DartState
    {
        /// <summary>
        /// Flying from ARSENAL to assigned QUIVER after manufacturing.
        /// </summary>
        Delivery,

        /// <summary>
        /// Stored in QUIVER, awaiting orders from LATTICE.
        /// </summary>
        Idle,

        /// <summary>
        /// Flying toward hostile target for engagement.
        /// </summary>
        Engaging,

        /// <summary>
        /// No threats remain, flying back to home QUIVER.
        /// </summary>
        Returning,

        /// <summary>
        /// Target died mid-flight, awaiting new orders from LATTICE.
        /// </summary>
        Reassigning
    }
}
