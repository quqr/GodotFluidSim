namespace FluidSimulation;

/// <summary>
///     力模式枚举
///     定义力发射器施加力的方向计算方式
/// </summary>
public enum ForcePattern
{
    /// <summary>固定方向力，所有位置的力向量相同（如风力、水流）</summary>
    Directional,

    /// <summary>从中心点辐射的力，力向量沿径向方向（正值为斥力，负值为引力）</summary>
    Point,

    /// <summary>旋涡力，力向量沿切线方向，产生旋转效果</summary>
    Vortex,

    /// <summary>向心力，力向量指向中心点（引力井效果）</summary>
    Attractor,

    /// <summary>离心力，力向量背离中心点（斥力场效果）</summary>
    Repulsor
}
