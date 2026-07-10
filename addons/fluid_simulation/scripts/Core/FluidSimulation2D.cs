using System.Collections.Generic;
using Godot;

namespace FluidSimulation;

/// <summary>
///     力发射器参数结构体，用于从 CPU 传递到 GPU。布局与 GLSL 中的 ForceEmitter struct 严格对应（48 字节）。
/// </summary>
internal struct ForceEmitterParams
{
    internal float CenterX, CenterY;     // vec2 center | offset 0
    internal float ForceX, ForceY;       // vec2 force  | offset 8
    internal float ShapeSizeX, ShapeSizeY; // vec2 shapeSize | offset 16
    internal float ForceRadius;          // float       | offset 24
    internal float FalloffExponent;      // float       | offset 28
    internal float SwirlStrength;        // float       | offset 32
    internal int ForcePattern;           // int         | offset 36
    internal int EmissionShape;          // int         | offset 40
    internal float _pad;                 // padding     | offset 44 → 48
}

/// <summary>
///     2D 流体模拟节点，基于 Navier-Stokes 方程，使用 GPU Compute Shader 实现实时流体效果。
///     <para>
///         WFS 管线流程（每帧在渲染线程执行）：
///         1. 处理批量绘制队列（Splat）
///         2. 更新障碍物纹理 + 域偏移（Shift Texture）
///         3. 应用 GPU 力发射器 + 外部输入力/颜色 + Splat 请求
///         4. 计算障碍物对流体的排斥力
///         5. 计算 Curl（涡度场）
///         6. 涡度增强（Vorticity Confinement）
///         7. 计算速度场散度（Divergence）
///         8. 压力衰减（ClearPressure，PRESSURE×0.8）
///         9. Jacobi 迭代求解压力场（Pressure Solve）
///         10. 从速度场中减去压力梯度（Gradient Subtract）
///         11. 边界条件处理（Boundary）
///         12. 速度场平流（Advect Velocity）
///         13. 颜色/染料场平流（Advect Dye）
///         14. 复制当前障碍物纹理到上一帧缓冲
///         15. Display 后处理（Shading）
///     </para>
/// </summary>
[GlobalClass]
public partial class FluidSimulation2D : Node
{
    /// <summary>批量绘制队列的最大容量，防止无限堆积导致内存溢出。</summary>
    public const int MaxBatchPoints = 4096;

    /// <summary>批量调度阈值，当待处理点数低于此值时退化为逐点 QueueDraw，高于此值时使用批量调度。</summary>
    public const int BatchDispatchThreshold = 16;

    // ======================== 内部组件 ========================

    private GPUResourceManager _gpu;
    private Texture2Drd _outputTexture;

    /// <summary>上一帧的 Enabled 状态，用于检测开关切换以执行一次性清理。</summary>
    private bool _previousEnabled = true;

    private FluidRenderPipeline _renderPipeline;

    /// <summary>批量绘制的颜色列表，与 BatchPoints 一一对应。</summary>
    public List<Color> BatchColors = [];

    // ======================== 批量绘制缓冲 ========================

    /// <summary>批量绘制的点位坐标列表。</summary>
    public List<Vector2> BatchPoints = [];

    /// <summary>批量绘制的半径列表，同时用作颜色半径和速度半径。</summary>
    public List<float> BatchRadii = [];

    /// <summary>批量绘制的速度列表，与 BatchPoints 一一对应。</summary>
    public List<Vector2> BatchVelocities = [];

    /// <summary>Splat 半径配置值（UV 空间），匹配 WFS 的 SPLAT_RADIUS。实际传入 shader 时除以 100。</summary>
    [Export] public float SplatRadius = 0.25f;

    /// <summary>缓存的障碍物原始字节数据（Rgba32f 格式），直接上传到 GPU。</summary>
    internal byte[] CachedObstacleData;

    /// <summary>流体颜色纹理的清除颜色，也是流体的初始背景颜色。</summary>
    [Export] public Color ClearColor = new(0, 0, 0, 0);

    /// <summary>颜色/密度场每帧的衰减量（WFS DENSITY_DISSIPATION）。值越大衰减越快。</summary>
    [Export] public float DensityDissipation = 1.0f;

    /// <summary>当前帧待处理的绘制请求数量，每帧渲染后重置为 0。</summary>
    public int DrawRequestCount;

    // ======================== 绘制请求队列 ========================

    /// <summary>待处理的绘制请求列表，在每帧渲染时批量执行 Splat 操作。</summary>
    public DrawRequest[] DrawRequests = new DrawRequest[MaxBatchPoints];

    /// <summary>当前帧活跃的力发射器参数列表。由 FluidForceEmitter 在 _Process 中填充，每帧渲染前快照到 ForceEmittersForRender 后清空。</summary>
    internal List<ForceEmitterParams> ForceEmitters = [];

    /// <summary>渲染线程读取的力发射器参数快照（前一帧收集的数据）。</summary>
    internal List<ForceEmitterParams> ForceEmittersForRender = [];

    [Export] public bool Enabled = true;

    /// <summary>是否启用涡度增强（Vorticity Confinement），可增加流体的旋转细节。</summary>
    [Export] public bool EnableVorticity = true;

    /// <summary>当前帧流体域相对于上一帧的归一化偏移量（UV 空间），用于 Shift Texture 计算。</summary>
    public Vector2 FluidDomainOffset = Vector2.Zero;

    /// <summary>流体域在世界空间中的实际尺寸（宽 × 高），用于世界坐标到 UV 坐标的转换。</summary>
    [Export] public Vector2 FluidWorldSize = new(1920f, 1080f);

    // ======================== 公共状态 ========================

    /// <summary>跟随节点引用，流体域会跟随此节点的移动进行偏移。</summary>
    public Node2D FollowNode;

    /// <summary>跟随节点的路径，若设置则流体域会跟随该节点移动并产生相对位移偏移。</summary>
    [Export] public NodePath FollowNodePath;

    /// <summary>输入颜色场纹理脏标记，为 true 时下一帧会上传 InputColorsImg 到 GPU。</summary>
    internal bool InputColorsDirty = true;

    /// <summary>每帧外部输入的颜色场图像（RGBAf 格式），用于 ApplyColors 计算着色器读取并叠加到颜色场上。</summary>
    public Image InputColorsImg;

    /// <summary>输入力场纹理脏标记，为 true 时下一帧会上传 InputForcesImg 到 GPU。</summary>
    internal bool InputForcesDirty = true;

    /// <summary>每帧外部输入的力场图像（RGBAf 格式），用于 ApplyForces 计算着色器读取并施加到速度场上。</summary>
    public Image InputForcesImg;

    /// <summary>Jacobi 迭代求解压力场的迭代次数（WFS PRESSURE_ITERATIONS）。次数越多，不可压缩性约束越好，但开销越大。</summary>
    [Export] public int PressureIterations = 20;

    /// <summary>障碍物脏标记，为 true 时会在下一帧渲染时将 CachedObstacleData 上传到 GPU。</summary>
    public bool ObstacleDirty;

    /// <summary>障碍物对流体施加的排斥力强度系数。</summary>
    [Export] public float ObstacleForceStrength = 5.0f;

    /// <summary>跟随节点上一帧的世界坐标位置，用于计算帧间位移。</summary>
    public Vector2 PreviousPosition = Vector2.Zero;

    // ======================== 可导出的配置属性 ========================

    /// <summary>模拟网格分辨率（宽 × 高），决定物理计算纹理的精度。速度/压力/散度场在此分辨率计算。</summary>
    [Export] public Vector2 SimulationResolution = new(228, 128);

    /// <summary>染料/颜色场分辨率（宽 × 高），决定颜色纹理的精度。通常高于 SimulationResolution 以获得更好的视觉效果。</summary>
    [Export] public Vector2 DyeResolution = new(1820, 1024);

    /// <summary>向后兼容：Resolution 返回 SimulationResolution。</summary>
    public Vector2 Resolution => SimulationResolution;

    /// <summary>速度场衰减系数（WFS VELOCITY_DISSIPATION）。正值会使速度逐渐减小。</summary>
    [Export] public float VelocityDissipation = 0.2f;

    /// <summary>涡度增强的强度系数（WFS CURL）。值越大流体旋转越明显。</summary>
    [Export] public float Curl = 30.0f;

    /// <summary>压力衰减系数（WFS PRESSURE）。每帧压力场乘以此值，实现压力残留衰减。</summary>
    [Export] public float Pressure = 0.8f;

    /// <summary>Splat 力度系数（WFS SPLAT_FORCE）。控制鼠标/输入产生的速度强度。</summary>
    [Export] public float SplatForce = 6000f;

    // ======================== 后处理参数 ========================

    /// <summary>是否启用 Display 着色效果（WFS shading），使流体看起来更有立体感。</summary>
    [Export] public bool EnableShading = true;

    // ======================== 计算调度参数 ========================

    /// <summary>计算着色器在 X 维度的工作组数量，ceil(SimulationResolution.X / 8)。</summary>
    private int _simXGroups;

    /// <summary>计算着色器在 Y 维度的工作组数量，ceil(SimulationResolution.Y / 8)。</summary>
    private int _simYGroups;

    /// <summary>颜色场计算着色器在 X 维度的工作组数量，ceil(DyeResolution.X / 8)。</summary>
    private int _dyeXGroups;

    /// <summary>颜色场计算着色器在 Y 维度的工作组数量，ceil(DyeResolution.Y / 8)。</summary>
    private int _dyeYGroups;

    // ======================== 输出纹理 ========================

    /// <summary>
    ///     流体模拟的输出纹理（Texture2Drd 类型）。设置时会自动绑定当前的颜色纹理 RID，
    ///     并同步关联 TextureRect（如已赋值）的 Size 与 FluidWorldSize 匹配。
    ///     外部可将此纹理分配给 Sprite2D 等节点进行显示。
    /// </summary>
    public Texture2Drd OutputTexture
    {
        get => _outputTexture;
        set
        {
            _outputTexture = value;
            if (_outputTexture != null && _gpu != null) _outputTexture.TextureRdRid = _gpu.TexIdColor;
        }
    }

    /// <summary>关联的显示节点，用于自动同步 TextureRect 的 Size 与 FluidWorldSize。</summary>
    public TextureRect DisplayTarget;

    /// <summary>
    ///     同步关联 TextureRect 的 Size 与 FluidWorldSize 匹配。
    ///     当 TextureRect 以锚点模式（anchor 0.5, 0.5）布局时，
    ///     设置 offset_left = -FluidWorldSize.X/2，offset_right = +FluidWorldSize.X/2。
    /// </summary>
    public void SyncDisplaySize()
    {
        if (DisplayTarget == null) return;
        DisplayTarget.OffsetLeft = -FluidWorldSize.X / 2f;
        DisplayTarget.OffsetTop = -FluidWorldSize.Y / 2f;
        DisplayTarget.OffsetRight = FluidWorldSize.X / 2f;
        DisplayTarget.OffsetBottom = FluidWorldSize.Y / 2f;
    }

    // ======================== 生命周期方法 ========================

    /// <summary>
    ///     节点就绪时调用。初始化计算调度组大小、创建输入图像、
    ///     获取跟随节点引用，并将自身添加到 "fluid_sim_nodes" 组。
    ///     渲染设备的初始化（Initialize）会被推迟到渲染线程执行。
    /// </summary>
    public override void _Ready()
    {
        _simXGroups = (int)((SimulationResolution.X - 1) / 8 + 1);
        _simYGroups = (int)((SimulationResolution.Y - 1) / 8 + 1);
        _dyeXGroups = (int)((DyeResolution.X - 1) / 8 + 1);
        _dyeYGroups = (int)((DyeResolution.Y - 1) / 8 + 1);

        _gpu = new GPUResourceManager();
        _renderPipeline = new FluidRenderPipeline();
        RenderingServer.CallOnRenderThread(Callable.From(InitializeGPU));

        InputForcesImg = Image.CreateEmpty((int)SimulationResolution.X, (int)SimulationResolution.Y, false, Image.Format.Rgbah);
        InputColorsImg = Image.CreateEmpty((int)DyeResolution.X, (int)DyeResolution.Y, false, Image.Format.Rgbah);

        if (FollowNodePath != null)
        {
            FollowNode = GetNodeOrNull<Node2D>(FollowNodePath);
            if (FollowNode != null) PreviousPosition = FollowNode.GlobalPosition;
        }

        AddToGroup("fluid_sim_nodes");
    }

    /// <summary>
    ///     通知回调。在节点即将被销毁时（Predelete），
    ///     调度 Terminate 方法到渲染线程释放所有 GPU 资源。
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationPredelete)
            RenderingServer.CallOnRenderThread(Callable.From(() => _gpu?.Terminate()));
    }

    /// <summary>
    ///     每帧处理方法（主线程）。计算跟随节点的帧间位移并转换为归一化的流体域偏移量，
    ///     然后将实际的渲染/模拟计算调度到渲染线程执行。
    /// </summary>
    /// <param name="dt">帧时间间隔（秒）。</param>
    public override void _Process(double dt)
    {
        if (!Enabled)
        {
            if (_previousEnabled)
            {
                ClearFluid();
                if (OutputTexture != null) OutputTexture.TextureRdRid = new Rid();
                _previousEnabled = false;
            }

            FluidDomainOffset = Vector2.Zero;
            return;
        }

        _previousEnabled = true;

        if (FollowNode != null && IsInstanceValid(FollowNode))
        {
            var currentPos = FollowNode.GlobalPosition;
            var offset = currentPos - PreviousPosition;
            FluidDomainOffset = new Vector2(offset.X / FluidWorldSize.X, offset.Y / FluidWorldSize.Y);
            PreviousPosition = currentPos;
        }

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            // Snapshot force emitter params for this frame's render pipeline
            ForceEmittersForRender.Clear();
            ForceEmittersForRender.AddRange(ForceEmitters);
            ForceEmitters.Clear();
            _renderPipeline.Execute((float)dt, this);
        }));
    }

    // ======================== GPU 初始化 ========================

    /// <summary>
    ///     在渲染线程中初始化 GPU 资源管理器和渲染管线。
    /// </summary>
    private void InitializeGPU()
    {
        _gpu.ClearUniformSetCache();
        _gpu.Initialize(SimulationResolution, DyeResolution, ClearColor, MaxBatchPoints);
        _renderPipeline.Initialize(_gpu, _simXGroups, _simYGroups, _dyeXGroups, _dyeYGroups);
    }

    // ======================== 公共 API ========================
    /// <summary>
    ///     标记输入力场纹理需要更新。
    ///     当外部修改了 InputForcesImg 后调用此方法，确保在下一帧渲染时上传到 GPU。
    /// </summary>
    public void MarkInputForcesDirty()
    {
        InputForcesDirty = true;
    }

    /// <summary>
    ///     标记输入颜色场纹理需要更新。
    ///     当外部修改了 InputColorsImg 后调用此方法，确保在下一帧渲染时上传到 GPU。
    /// </summary>
    public void MarkInputColorsDirty()
    {
        InputColorsDirty = true;
    }

    /// <summary>
    ///     清除流体模拟的所有状态（速度场、压力场、颜色场、散度场），
    ///     将流体重置为空白状态。操作在渲染线程中异步执行。
    /// </summary>
    public void ClearFluid()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            _gpu?.ClearTextures(ClearColor)));
    }

    /// <summary>
    ///     将世界坐标转换为流体模拟的像素坐标（基于 SimulationResolution）。
    ///     以跟随节点（或世界原点）为中心，将世界坐标映射到 [0, SimulationResolution] 范围内。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <returns>对应的流体模拟像素坐标。</returns>
    public Vector2 WorldToFluidPos(Vector2 worldPos)
    {
        var domainCenter = Vector2.Zero;
        if (FollowNode != null && IsInstanceValid(FollowNode)) domainCenter = FollowNode.GlobalPosition;
        var localPos = worldPos - domainCenter;
        var uv = new Vector2(
            localPos.X / FluidWorldSize.X + 0.5f,
            localPos.Y / FluidWorldSize.Y + 0.5f
        );
        return uv * SimulationResolution;
    }

    /// <summary>
    ///     将世界坐标转换为染料场的像素坐标（基于 DyeResolution）。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <returns>对应的染料场像素坐标。</returns>
    public Vector2 WorldToDyePos(Vector2 worldPos)
    {
        var domainCenter = Vector2.Zero;
        if (FollowNode != null && IsInstanceValid(FollowNode)) domainCenter = FollowNode.GlobalPosition;
        var localPos = worldPos - domainCenter;
        var uv = new Vector2(
            localPos.X / FluidWorldSize.X + 0.5f,
            localPos.Y / FluidWorldSize.Y + 0.5f
        );
        return uv * DyeResolution;
    }

    /// <summary>
    ///     将世界坐标转换为流体模拟的 UV 坐标 [0,1]。
    ///     以跟随节点（或世界原点）为中心，将世界坐标映射到 [0,1] 范围。
    ///     用于 Splat 操作（匹配 WFS 的 UV 空间 splat 机制）。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <returns>对应的流体模拟 UV 坐标 [0,1]。</returns>
    public Vector2 WorldToFluidUV(Vector2 worldPos)
    {
        var domainCenter = Vector2.Zero;
        if (FollowNode != null && IsInstanceValid(FollowNode)) domainCenter = FollowNode.GlobalPosition;
        var localPos = worldPos - domainCenter;
        return new Vector2(
            localPos.X / FluidWorldSize.X + 0.5f,
            localPos.Y / FluidWorldSize.Y + 0.5f
        );
    }

    /// <summary>
    ///     将一次绘制请求加入队列，在下一帧渲染时统一对流体注入速度和颜色。
    /// </summary>
    /// <param name="uvPosition">注入位置的 UV 坐标 [0,1]。</param>
    /// <param name="color">注入的颜色值（已包含亮度缩放）。</param>
    /// <param name="velocity">注入的速度向量（UV 空间）。</param>
    /// <param name="colorRadius">颜色注入的半径倍率（乘以 SplatRadius/100）。</param>
    /// <param name="velocityRadius">速度注入的半径倍率（乘以 SplatRadius/100）。</param>
    public void QueueDraw(Vector2 uvPosition, Color color, Vector2 velocity, float colorRadius, float velocityRadius)
    {
        if (DrawRequestCount >= DrawRequests.Length)
        {
            GD.PushWarning("FluidSimulation2D: DrawRequest queue full, dropping request");
            return;
        }

        DrawRequests[DrawRequestCount] = new DrawRequest
        {
            Position = uvPosition, Color = color, Velocity = velocity,
            ColorRadius = colorRadius, VelocityRadius = velocityRadius
        };
        DrawRequestCount++;
    }

    /// <summary>
    ///     将一批绘制点加入批量队列。当累积点数超过 BatchDispatchThreshold 时，
    ///     将在渲染时使用批量调度以提高性能；否则退化为逐点 QueueDraw。
    /// </summary>
    public void QueueDrawBatch(Vector2[] points, Color[] colors, Vector2[] velocities, float[] radii)
    {
        var available = MaxBatchPoints - BatchPoints.Count;
        if (available <= 0)
        {
            GD.PushWarning("FluidSimulation2D: Batch queue full, dropping remaining points");
            return;
        }

        var count = Mathf.Min(points.Length, available);
        if (count < points.Length)
            GD.PushWarning($"FluidSimulation2D: Batch truncated from {points.Length} to {count} points");
        BatchPoints.AddRange(points[..count]);
        BatchColors.AddRange(colors[..count]);
        BatchVelocities.AddRange(velocities[..count]);
        BatchRadii.AddRange(radii[..count]);
    }

    /// <summary>
    ///     设置障碍物原始数据（Rgba32f 格式，每像素 16 字节）。
    ///     数据直接上传到 GPU 纹理，无需经过 Image 转换。
    ///     设置后会在下一帧渲染时上传到 GPU。
    /// </summary>
    /// <param name="rawData">障碍物像素数据（RGBA float32，非零区域被视为障碍物）。</param>
    public void SetObstacleRawData(byte[] rawData)
    {
        if (rawData == null) return;
        CachedObstacleData = rawData;
        ObstacleDirty = true;
    }
}