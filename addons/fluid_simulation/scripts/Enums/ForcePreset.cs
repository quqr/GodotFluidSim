namespace FluidSimulation;

/// <summary>
///     力预设枚举
///     定义常用的力发射器预设效果
/// </summary>
public enum ForcePreset
{
    /// <summary>自定义配置，不修改任何参数</summary>
    Custom,

    /// <summary>单向风，沿固定方向持续施加力</summary>
    Wind,

    /// <summary>向下的重力效果</summary>
    Gravity,

    /// <summary>旋涡效果，产生旋转流</summary>
    Vortex,

    /// <summary>爆炸冲击波，从中心向外辐射的力</summary>
    Explosion,

    /// <summary>磁场引力，向中心吸引的力</summary>
    Magnetic
}
