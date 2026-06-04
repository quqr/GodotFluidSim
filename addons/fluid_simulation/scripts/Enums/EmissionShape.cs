namespace FluidSimulation;

/// <summary>
///     发射形状枚举
///     定义粒子发射的空间分布形状
/// </summary>
public enum EmissionShape
{
    /// <summary>点状发射，所有粒子从发射器中心点发出</summary>
    Point,

    /// <summary>圆形发射，粒子在圆形区域内均匀采样</summary>
    Circle,

    /// <summary>矩形发射，粒子在矩形区域内均匀采样</summary>
    Rect,

    /// <summary>线段发射，粒子沿水平线段采样，垂直方向有微小随机偏移</summary>
    Line,

    /// <summary>纹理遮罩发射，根据遮罩纹理的 Alpha 通道决定粒子分布位置</summary>
    TextureMask
}