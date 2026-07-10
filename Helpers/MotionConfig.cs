namespace DynamicIsland.Helpers;

/// <summary>
/// Exposes configurable parameters for the spring physics solver.
/// </summary>
public class MotionConfig
{
    public double Stiffness { get; set; } = 220.0;
    public double Damping { get; set; } = 20.0;
    public double Mass { get; set; } = 1.0;
    public double SnapThreshold { get; set; } = 0.5; // Distance threshold for snapping
    public double VelocityThreshold { get; set; } = 2.0; // Velocity threshold for settling (pixels/sec)
}
