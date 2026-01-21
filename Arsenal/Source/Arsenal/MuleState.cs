namespace Arsenal
{
    /// <summary>
    /// States for the MULE autonomous utility drone.
    /// </summary>
    public enum MuleState
    {
        /// <summary>Docked at STABLE, fully charged or charging.</summary>
        Idle,

        /// <summary>Docked at STABLE, actively charging.</summary>
        Charging,

        /// <summary>Newly manufactured, traveling from ARSENAL to assigned STABLE.</summary>
        DeliveringToStable,

        /// <summary>Traveling to task location.</summary>
        Deploying,

        /// <summary>Actively mining a designated tile.</summary>
        Mining,

        /// <summary>Carrying item to destination.</summary>
        Hauling,

        /// <summary>Heading back to STABLE (with or without cargo).</summary>
        ReturningHome,

        /// <summary>Battery depleted, passive recharge mode.</summary>
        Inert
    }

    /// <summary>
    /// Task types that a MULE can perform, in priority order.
    /// </summary>
    public enum MuleTaskType
    {
        /// <summary>No task assigned.</summary>
        None = 0,

        /// <summary>Mine vanilla-designated tiles, return with resources. Highest priority.</summary>
        Mine = 1,

        /// <summary>Haul items to stockpiles or storage.</summary>
        Haul = 2,

        /// <summary>Deliver resources to MORIA nodes based on priority/need.</summary>
        MoriaFeed = 3
    }
}
