namespace OverInk.Models;

/// <summary>Which part of the desktop the iPad sees and sketches on (an explicit region, when set, overrides this).</summary>
public enum StreamTarget
{
    /// <summary>The monitor the toolbar currently sits on.</summary>
    FollowToolbar = 0,

    /// <summary>One monitor picked in Settings (<see cref="AppSettings.StreamMonitor"/>).</summary>
    SpecificMonitor = 1,

    /// <summary>Every monitor as one wide frame (the whole virtual desktop).</summary>
    AllMonitors = 2
}
