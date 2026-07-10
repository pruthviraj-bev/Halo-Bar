using System;

namespace DynamicIsland.Helpers;

/// <summary>
/// A reusable, single-axis spring simulation solver using basic Euler integration.
/// </summary>
public class SpringSimulation
{
    private readonly MotionConfig _config;

    public double Current { get; set; }
    public double Target { get; set; }
    public double Velocity { get; set; }

    public SpringSimulation(double initialValue, MotionConfig config)
    {
        Current = initialValue;
        Target = initialValue;
        Velocity = 0;
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Updates the spring simulation for a time delta (dt).
    /// </summary>
    public void Update(double dt)
    {
        double force = -_config.Stiffness * (Current - Target) - _config.Damping * Velocity;
        double acceleration = force / _config.Mass;
        Velocity += acceleration * dt;
        Current += Velocity * dt;
    }

    /// <summary>
    /// Checks if the spring has settled close to its target value.
    /// </summary>
    public bool IsSettled()
    {
        return Math.Abs(Current - Target) < _config.SnapThreshold && 
               Math.Abs(Velocity) < _config.VelocityThreshold;
    }

    /// <summary>
    /// Instantly snaps the spring value directly to the target.
    /// </summary>
    public void SnapToTarget()
    {
        Current = Target;
        Velocity = 0;
    }
}
