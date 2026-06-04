using Godot;

namespace FluidSimulation;

/// <summary>
///     流体障碍物接口，所有需要与流体交互的运动物体必须实现此接口。
///     实现此接口的节点在障碍物绘制时会被检测到，其速度信息将被编码到障碍物纹理的 R/G 通道中，
///     用于 GPU 计算障碍物对流体的推动力。
/// </summary>
public interface IFluidObstacle
{
    /// <summary>获取物体的线速度（像素/秒），用于计算障碍物边界处的速度差驱动力。</summary>
    Vector2 GetObjectLinearVelocity();

    /// <summary>获取物体的角速度（弧度/秒），用于计算障碍物边缘不同位置的局部线速度（v = v_linear + ω × r）。</summary>
    float GetObjectAngularVelocity();

    /// <summary>获取物体的质心世界坐标，作为角速度力矩计算的参考点。</summary>
    Vector2 GetObjectCenter();
}