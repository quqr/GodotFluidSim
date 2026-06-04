using Godot;

namespace FluidSimulation;

/// <summary>
///     计算管线封装类，用于管理 GPU 计算着色器的管线资源。
///     在流体模拟中，计算管线负责将计算着色器编译并绑定到渲染设备上，
///     以便后续通过 GPU 并行计算来加速流体模拟的物理运算（如扩散、平流、压力求解等）。
/// </summary>
public class ComputePipeline
{
    /// <summary>
    ///     管线的名称标识，用于区分不同的计算管线阶段（例如 "advect"、"pressure"、"diffuse" 等）。
    /// </summary>
    public string Name;

    /// <summary>
    ///     计算管线的资源 ID（Rid），由 Godot 渲染服务器创建，
    ///     代表完整的计算管线对象，用于派发计算工作组（dispatch）执行 GPU 计算任务。
    /// </summary>
    public Rid PipelineId;

    /// <summary>
    ///     计算着色器的资源 ID（Rid），由 Godot 渲染服务器创建，
    ///     代表已编译的计算着色器程序，用于后续绑定到管线。
    /// </summary>
    public Rid ShaderId;
}