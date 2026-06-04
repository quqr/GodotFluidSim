namespace FluidSimulation;

/// <summary>
///     发射模式枚举
///     定义发射器的发射时机和频率
/// </summary>
public enum EmissionMode
{
    /// <summary>持续发射，按 EmitInterval 间隔每帧发射粒子</summary>
    Continuous,

    /// <summary>单次爆发，仅发射一次后停止</summary>
    SingleBurst,

    /// <summary>周期性爆发，按 BurstInterval 间隔周期性发射一组粒子</summary>
    PeriodicBurst
}