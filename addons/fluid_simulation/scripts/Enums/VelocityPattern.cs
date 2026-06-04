namespace FluidSimulation;

/// <summary>
///     速度模式枚举
///     定义粒子发射时的速度方向计算方式
/// </summary>
public enum VelocityPattern
{
    /// <summary>定向模式：所有粒子沿 EmitVelocity 方向发射，速度大小一致</summary>
    Directional,

    /// <summary>径向模式：粒子从发射器中心向外径向发射，速度随距离衰减</summary>
    Radial,

    /// <summary>随机模式：粒子在随机方向上发射，速度大小等于 EmitVelocity 的长度</summary>
    Random
}