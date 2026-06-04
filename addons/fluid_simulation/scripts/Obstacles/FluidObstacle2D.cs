using Godot;

namespace FluidSimulation;

/// <summary>
///     流体障碍物通用组件，提供 IFluidObstacle 的默认实现。
///     可作为 RigidBody2D 或 CharacterBody2D 的子节点使用，自动检测父节点的物理速度；也可手动设置速度值。
/// </summary>
[GlobalClass]
public partial class FluidObstacle2D : Node2D, IFluidObstacle
{
    /// <summary>缓存的角速度值，在 _PhysicsProcess 中更新。</summary>
    private float _cachedAngularVelocity;

    /// <summary>缓存的线速度值，在 _PhysicsProcess 中更新。</summary>
    private Vector2 _cachedVelocity;

    /// <summary>手动模式下的角速度（弧度/秒），仅当 AutoDetectVelocity = false 时生效。</summary>
    [Export] public float AngularVelocity;

    /// <summary>
    ///     是否自动从父节点检测物理速度。为 true 时从 RigidBody2D.LinearVelocity 或 CharacterBody2D.Velocity 获取；
    ///     为 false 时使用手动设置的 Velocity 和 AngularVelocity。
    /// </summary>
    [Export] public bool AutoDetectVelocity = true;

    /// <summary>手动模式下的线速度（像素/秒），仅当 AutoDetectVelocity = false 时生效。</summary>
    [Export] public Vector2 Velocity;

    /// <summary>获取物体的线速度（像素/秒），用于计算障碍物边界处的速度差驱动力。</summary>
    public Vector2 GetObjectLinearVelocity()
    {
        return _cachedVelocity;
    }

    /// <summary>获取物体的角速度（弧度/秒），用于计算障碍物边缘不同位置的局部线速度（v = v_linear + ω × r）。</summary>
    public float GetObjectAngularVelocity()
    {
        return _cachedAngularVelocity;
    }

    /// <summary>获取物体的质心世界坐标，作为角速度力矩计算的参考点。</summary>
    public Vector2 GetObjectCenter()
    {
        return GlobalPosition;
    }

    /// <summary>在物理帧中缓存速度，确保读取到最新的物理速度。</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (AutoDetectVelocity)
        {
            if (GetParent() is RigidBody2D rb)
            {
                _cachedVelocity = rb.LinearVelocity;
                _cachedAngularVelocity = rb.AngularVelocity;
            }
            else if (GetParent() is CharacterBody2D cb)
            {
                _cachedVelocity = cb.Velocity;
                _cachedAngularVelocity = 0.0f;
            }
        }
        else
        {
            _cachedVelocity = Velocity;
            _cachedAngularVelocity = AngularVelocity;
        }
    }
}