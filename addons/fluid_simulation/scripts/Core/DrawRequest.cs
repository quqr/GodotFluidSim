using Godot;

namespace FluidSimulation;

/// <summary>
///     流体绘制请求数据类，封装了一次流体发射（emit）操作所需的全部参数。
///     当用户在场景中进行交互（如鼠标点击或拖拽）时，会生成 DrawRequest 来描述
///     在某个位置注入颜色和速度的影响，从而驱动流体模拟产生流动效果。
/// </summary>
public struct DrawRequest
{
    /// <summary>
    ///     注入的颜料颜色（RGBA），用于在流体中添加可见的色彩扩散效果。
    ///     颜色会通过高斯衰减在 ColorRadius 范围内散布到周围的流体网格中。
    /// </summary>
    public Color Color;

    /// <summary>
    ///     颜色注入的影响半径，控制颜料在流体中扩散的范围大小。
    ///     半径越大，颜色涂抹覆盖的区域越广。
    /// </summary>
    public float ColorRadius;

    /// <summary>
    ///     绘制位置，即流体注入的世界坐标（2D）。
    ///     该位置决定了颜色和速度场影响的中心点。
    /// </summary>
    public Vector2 Position;

    /// <summary>
    ///     注入的速度向量，表示施加给流体的外力方向和大小。
    ///     例如用户拖拽鼠标时，拖拽方向和速度会转换为该向量，推动流体运动。
    /// </summary>
    public Vector2 Velocity;

    /// <summary>
    ///     速度注入的影响半径，控制外力施加的范围大小。
    ///     半径越大，速度场影响的区域越广，推动流体的范围越大。
    /// </summary>
    public float VelocityRadius;
}