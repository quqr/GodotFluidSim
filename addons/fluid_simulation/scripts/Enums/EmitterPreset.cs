namespace FluidSimulation;

/// <summary>
///     发射器预设枚举
///     预定义的发射参数组合，适用于不同类型的流体效果。
///     选择预设后会自动填充发射参数（颜色、速度、形状等），
///     可手动微调任何参数。设为 Custom 可完全自定义。
/// </summary>
public enum EmitterPreset
{
    /// <summary>自定义模式，不自动填充任何参数，完全由用户手动配置</summary>
    Custom,

    /// <summary>爆炸效果：单次爆发、圆形分布、径向速度、带旋转扰动</summary>
    Explosion,

    /// <summary>水雾效果：持续发射、矩形分布、向上方向速度、低透明度</summary>
    WaterMist,

    /// <summary>烟雾效果：持续发射、圆形分布、向上方向速度、灰色半透明</summary>
    Smoke,

    /// <summary>喷泉效果：持续发射、圆形分布、强向上径向速度</summary>
    Fountain,

    /// <summary>蒸汽效果：周期性爆发、圆形分布、随机速度方向、白色半透明</summary>
    Steam,

    /// <summary>火焰效果：持续发射、矩形分布、向上方向速度、橙红色高透明度</summary>
    Fire
}