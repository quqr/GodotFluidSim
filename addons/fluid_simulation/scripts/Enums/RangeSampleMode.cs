namespace FluidSimulation;

/// <summary>
///     范围采样模式枚举
///     当使用 CollisionShape2D 定义发射范围时，
///     控制粒子在范围形状内的分布方式
/// </summary>
public enum RangeSampleMode
{
    /// <summary>内部采样：粒子在形状内部区域均匀随机分布</summary>
    Interior,

    /// <summary>边缘采样：粒子仅在形状边缘/边界上分布</summary>
    Edge
}