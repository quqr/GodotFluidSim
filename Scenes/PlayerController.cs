using FluidSimulation;
using Godot;

namespace Tests;

/// <summary>
///     玩家控制器，演示 IFluidObstacle 接口的使用方式。
///     <para>
///         作为可移动的障碍物参与流体模拟：玩家移动时，
///         流体会根据 GetObjectLinearVelocity 返回的速度被推开。
///     </para>
/// </summary>
public partial class PlayerController : CharacterBody2D, IFluidObstacle
{
    /// <summary>移动速度（像素/秒）。</summary>
    [Export]
    public float MoveSpeed { get; set; } = 200.0f;

    /// <summary>返回角色的当前线速度，供流体障碍物系统读取。</summary>
    public Vector2 GetObjectLinearVelocity()
    {
        return Velocity;
    }

    /// <summary>返回角色的角速度，固定为 0（角色不旋转）。</summary>
    public float GetObjectAngularVelocity()
    {
        return 0.0f;
    }

    /// <summary>返回角色的中心位置，用于计算障碍物速度场。</summary>
    public Vector2 GetObjectCenter()
    {
        return GlobalPosition;
    }

    /// <summary>每物理帧处理输入，根据方向键移动角色。</summary>
    public override void _PhysicsProcess(double delta)
    {
        var inputDir = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        Velocity = inputDir * MoveSpeed;
        MoveAndSlide();
    }
}