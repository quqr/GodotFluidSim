using Godot;

namespace FluidSimulation;

/// <summary>
///     流体发射器节点，负责按配置的形状、速度和颜色向流体模拟中注入粒子。
///     <para>
///         支持三种发射模式（持续、单次爆发、周期性爆发）、五种发射形状（点、圆、矩形、线段、纹理遮罩）、
///         三种速度模式（定向、径向、随机）。
///     </para>
///     <para>
///         使用方式：将此节点添加到场景中，配置发射参数，它会自动查找场景中的 FluidSimulation2D 节点并注入粒子。
///         可通过 CollisionShape2D 子节点定义发射范围形状。
///     </para>
/// </summary>
[GlobalClass]
public partial class FluidEmitter : Node2D, IFluidEmitter
{
    private Color[] _cachedColors;
    private float[] _cachedRadii;

    /// <summary>检测到的碰撞范围形状，由 _Ready 时自动从子节点中查找。</summary>
    public CollisionShape2D RangeShape { get; set; }

    /// <summary>范围采样模式，当使用 CollisionShape2D 定义发射范围时，控制粒子在范围形状内的分布方式（内部采样或边缘采样）。</summary>
    [Export]
    public RangeSampleMode SampleMode { get; set; } = RangeSampleMode.Interior;

    /// <summary>发射颜色，RGBA 格式。Alpha 通道控制注入强度。</summary>
    [Export]
    public Color EmitColor { get; set; } = new(0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>发射速度方向和基础速度大小（定向模式下为固定方向，径向/随机模式下仅使用长度）。</summary>
    [Export]
    public Vector2 EmitVelocity { get; set; } = new(0, -1.0f);

    /// <summary>颜色注入半径，控制颜色高斯分布的扩散范围。值越大颜色扩散越广。</summary>
    [Export]
    public float ColorRadius { get; set; } = 0.5f;

    /// <summary>速度注入半径，控制速度高斯分布的扩散范围。值越大速度影响范围越广。</summary>
    [Export]
    public float VelocityRadius { get; set; } = 0.8f;

    /// <summary>持续发射模式下的发射间隔（秒）。值越小发射越频繁。</summary>
    [Export]
    public float EmitInterval { get; set; } = 0.05f;

    /// <summary>是否激活发射器。为 false 时停止发射粒子。</summary>
    [Export]
    public bool Active { get; set; } = true;

    /// <summary>发射形状类型，决定粒子在空间中的分布方式。</summary>
    [Export]
    public EmissionShape EmissionShapeType { get; set; } = EmissionShape.Point;

    /// <summary>发射形状尺寸。Circle 模式下 X 为半径，Rect/Line 模式下 X/Y 为宽高，TextureMask 模式下为遮罩缩放。</summary>
    [Export]
    public Vector2 ShapeSize { get; set; } = new(1.0f, 1.0f);

    /// <summary>纹理遮罩，当 EmissionShapeType 为 TextureMask 时，根据此纹理的 Alpha 通道决定粒子分布。</summary>
    [Export]
    public Texture2D MaskTexture { get; set; }

    /// <summary>是否使用遮罩纹理的颜色作为发射颜色，替代 EmitColor。</summary>
    [Export]
    public bool UseTextureColor { get; set; }

    /// <summary>速度方向模式，决定粒子发射时的速度方向计算方式。</summary>
    [Export]
    public VelocityPattern VelocityPatternType { get; set; } = VelocityPattern.Directional;

    /// <summary>速度衰减指数，径向模式下速度随距离衰减的幂次。值越大边缘速度越慢。</summary>
    [Export]
    public float VelocityFalloff { get; set; }

    /// <summary>旋转扰动强度，为每个粒子叠加切向速度，产生旋涡效果。值为 0 时无旋转。</summary>
    [Export]
    public float SwirlStrength { get; set; }

    /// <summary>发射模式，决定发射时机和频率。</summary>
    [Export]
    public EmissionMode EmissionModeType { get; set; } = EmissionMode.Continuous;

    /// <summary>爆发模式下每次爆发的粒子数量。</summary>
    [Export]
    public int BurstCount { get; set; } = 10;

    /// <summary>周期性爆发模式下的爆发间隔（秒）。</summary>
    [Export]
    public float BurstInterval { get; set; } = 1.0f;

    /// <summary>单次爆发的最大粒子数上限，防止爆发数量过大导致性能问题。</summary>
    [Export]
    public int MaxBurstCount { get; set; } = 512;

    /// <summary>发射器生命周期（秒）。到期后根据 AutoDestroy 决定行为。值为 0 表示永不过期。</summary>
    [Export]
    public float Lifetime { get; set; }

    /// <summary>生命周期到期后是否自动销毁节点。为 false 时仅停止发射。</summary>
    [Export]
    public bool AutoDestroy { get; set; }

    /// <summary>距离上次发射的累计时间（秒）。</summary>
    public float TimeSinceEmit { get; set; }

    /// <summary>距离上次爆发的累计时间（秒）。</summary>
    public float TimeSinceBurst { get; set; }

    /// <summary>单次爆发模式下是否已经爆发过。</summary>
    public bool HasBurst { get; set; }

    /// <summary>关联的流体模拟节点引用，由 _Process 中自动查找。</summary>
    public FluidSimulation2D FluidSim { get; set; }

    /// <summary>
    ///     按发射间隔向流体模拟中注入单个粒子。由 IFluidEmitter 接口定义，持续发射模式下每帧调用。
    /// </summary>
    /// <param name="fluidSim">目标流体模拟节点。</param>
    /// <param name="dt">帧间隔时间（秒）。</param>
    public void EmitFluid(FluidSimulation2D fluidSim, float dt)
    {
        TimeSinceEmit += dt;
        if (TimeSinceEmit >= EmitInterval)
        {
            TimeSinceEmit = 0.0f;
            EmitInternal(fluidSim, 1);
        }
    }

    /// <summary>
    ///     内部发射方法，采样指定数量的粒子点并注入流体模拟。
    ///     先用世界坐标生成速度（径向/旋涡方向需要世界坐标一致），
    ///     再将点位转换为 UV [0,1] 坐标供 WFS splat shader 使用。
    /// </summary>
    /// <param name="fluidSim">目标流体模拟节点。</param>
    /// <param name="count">要发射的粒子数量。</param>
    private void EmitInternal(FluidSimulation2D fluidSim, int count)
    {
        var points = EmitterShapeSampler.Sample(count, this);
        var velocities = EmitterVelocityGenerator.Generate(points, this);
        var colors = MakeColorArray(count);
        var radii = MakeRadiusArray(count);
        for (var i = 0; i < points.Length; i++) points[i] = fluidSim.WorldToFluidUV(points[i]);
        fluidSim.QueueDrawBatch(points, colors, velocities, radii);
    }

    /// <summary>节点初始化，将自身加入 fluid_emitters 组，初始化颜色/半径缓存数组，并从子节点中检测 CollisionShape2D 作为发射范围形状。</summary>
    public override void _Ready()
    {
        AddToGroup("fluid_emitters");
        _cachedColors = new Color[MaxBurstCount];
        _cachedRadii = new float[MaxBurstCount];
        RangeShape = EmitterShapeSampler.DetectRangeShape(this);
    }

    /// <summary>每帧处理：管理生命周期、查找流体模拟节点、根据发射模式执行发射逻辑。</summary>
    public override void _Process(double delta)
    {
        var dt = (float)delta;

        if (!Active)
            return;

        if (Lifetime > 0.0f)
        {
            Lifetime -= dt;
            if (Lifetime <= 0.0f)
            {
                if (AutoDestroy)
                    QueueFree();
                else
                    Active = false;
                return;
            }
        }

        if (FluidSim == null)
        {
            FluidSim = GetTree().GetFirstNodeInGroup("fluid_sim_nodes") as FluidSimulation2D;
            if (FluidSim == null)
                return;
        }

        switch (EmissionModeType)
        {
            case EmissionMode.Continuous:
                EmitFluid(FluidSim, dt);
                break;
            case EmissionMode.SingleBurst:
                if (!HasBurst)
                {
                    EmitBurst(FluidSim);
                    HasBurst = true;
                }

                break;
            case EmissionMode.PeriodicBurst:
                TimeSinceBurst += dt;
                if (TimeSinceBurst >= BurstInterval)
                {
                    TimeSinceBurst = 0.0f;
                    EmitBurst(FluidSim);
                }

                break;
        }
    }

    /// <summary>执行一次爆发发射，发射数量为 BurstCount（不超过 MaxBurstCount）。</summary>
    private void EmitBurst(FluidSimulation2D fluidSim)
    {
        var count = Mathf.Min(BurstCount, MaxBurstCount);
        EmitInternal(fluidSim, count);
    }

    /// <summary>生成指定数量的颜色数组，所有元素填充为 EmitColor。使用预分配缓存避免每帧 GC 分配。</summary>
    private Color[] MakeColorArray(int count)
    {
        for (var i = 0; i < count; i++)
            _cachedColors[i] = EmitColor;
        return _cachedColors;
    }

    /// <summary>生成指定数量的半径数组，所有元素填充为 ColorRadius。使用预分配缓存避免每帧 GC 分配。</summary>
    private float[] MakeRadiusArray(int count)
    {
        for (var i = 0; i < count; i++)
            _cachedRadii[i] = ColorRadius;
        return _cachedRadii;
    }

    /// <summary>重置发射器状态，将爆发标志、计时器和激活状态恢复为初始值。</summary>
    public void Reset()
    {
        HasBurst = false;
        TimeSinceEmit = 0.0f;
        TimeSinceBurst = 0.0f;
        Active = true;
    }
}