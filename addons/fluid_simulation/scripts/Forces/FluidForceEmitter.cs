using Godot;

namespace FluidSimulation;

/// <summary>
///     力发射器节点，负责向流体模拟施加方向性的外力。
///     <para>
///         支持五种力模式（固定方向、点辐射、旋涡、向心、离心）、五种作用形状（点、圆、矩形、线段、纹理遮罩）。
///     </para>
///     <para>
///         优化说明：非 TextureMask 形状使用 GPU 并行计算力场，彻底消除 CPU 端逐像素 GetPixel/SetPixel 瓶颈。
///         TextureMask 形状因需要逐像素纹理采样，仍使用 CPU 路径。
///     </para>
/// </summary>
[GlobalClass]
public partial class FluidForceEmitter : Node2D
{
    /// <summary>检测到的碰撞范围形状，由 _Ready 时自动从子节点中查找。</summary>
    public CollisionShape2D RangeShape { get; set; }

    /// <summary>是否激活力发射器。为 false 时停止施加力。</summary>
    [Export]
    public bool Active { get; set; } = true;

    /// <summary>力的方向模式，决定力向量的计算方式。</summary>
    [Export]
    public ForcePattern ForcePatternType { get; set; } = ForcePattern.Directional;

    /// <summary>力的方向和基础大小。Directional 模式下为固定方向向量，其他模式下主要用于控制力的大小。</summary>
    [Export]
    public Vector2 Force { get; set; } = new(0, -1.0f);

    /// <summary>力的作用半径（世界单位），控制力的影响范围。值越大影响区域越广。</summary>
    [Export]
    public float ForceRadius { get; set; } = 100.0f;

    /// <summary>力的作用形状类型，决定力在空间中的分布方式。</summary>
    [Export]
    public EmissionShape EmissionShapeType { get; set; } = EmissionShape.Circle;

    /// <summary>力的作用形状尺寸。Circle 模式下 X 为半径，Rect/Line 模式下 X/Y 为宽高。</summary>
    [Export]
    public Vector2 ShapeSize { get; set; } = new(1.0f, 1.0f);

    /// <summary>纹理遮罩，当 EmissionShapeType 为 TextureMask 时，根据此纹理的 Alpha 通道决定力的作用区域。</summary>
    [Export]
    public Texture2D MaskTexture { get; set; }

    /// <summary>旋涡力的旋转强度，产生切向力的倍率。值为 0 时无旋转效果。</summary>
    [Export]
    public float SwirlStrength { get; set; } = 1.0f;

    /// <summary>力的衰减指数，值越大力随距离衰减越快。0 表示均匀力场。</summary>
    [Export]
    public float FalloffExponent { get; set; } = 2.0f;

    /// <summary>力发射器生命周期（秒）。到期后根据 AutoDestroy 决定行为。值为 0 表示永不过期。</summary>
    [Export]
    public float Lifetime { get; set; }

    /// <summary>生命周期到期后是否自动销毁节点。为 false 时仅停止施加力。</summary>
    [Export]
    public bool AutoDestroy { get; set; }

    /// <summary>关联的流体模拟节点引用，由 _Process 中自动查找。</summary>
    public FluidSimulation2D FluidSim { get; set; }

    /// <summary>
    ///     每帧向流体模拟施加力。
    ///     非 TextureMask 形状：将发射器参数注册到 FluidSim.ForceEmitters 列表，
    ///     由渲染管线在 GPU 端并行计算力场。
    ///     TextureMask 形状：回退到 CPU 逐像素路径。
    /// </summary>
    public void EmitFluid(FluidSimulation2D fluidSim, float dt)
    {
        if (!Active)
            return;

        // TextureMask 形状回退到 CPU 路径（需要逐像素纹理采样，无法高效 GPU 化）
        if (EmissionShapeType == EmissionShape.TextureMask || RangeShape != null)
        {
            ApplyForceToImageCPU(fluidSim);
            fluidSim.MarkInputForcesDirty();
            return;
        }

        // GPU 路径：注册力发射器参数
        RegisterForceParams(fluidSim);
    }

    /// <summary>
    ///     将发射器参数注册到流体模拟的力发射器列表中，供 GPU 管线读取。
    /// </summary>
    private void RegisterForceParams(FluidSimulation2D fluidSim)
    {
        var resolution = fluidSim.Resolution;
        var worldSize = fluidSim.FluidWorldSize;
        var center = fluidSim.WorldToFluidPos(GlobalPosition);
        var pixelRadius = Mathf.Max(5f, ForceRadius / worldSize.X * resolution.X);

        // ShapeSize 作为比例使用，不需要转换到像素空间
        // Circle: 使用 forceRadius 作为半径
        // Rect: halfSize = forceRadius * shapeSize
        // Line: halfWidth = forceRadius * shapeSize.x
        fluidSim.ForceEmitters.Add(new ForceEmitterParams
        {
            CenterX = center.X,
            CenterY = center.Y,
            ForceX = Force.X,
            ForceY = Force.Y,
            ShapeSizeX = ShapeSize.X,
            ShapeSizeY = ShapeSize.Y,
            ForceRadius = pixelRadius,
            FalloffExponent = FalloffExponent,
            SwirlStrength = SwirlStrength,
            ForcePattern = (int)ForcePatternType,
            EmissionShape = (int)EmissionShapeType
        });
    }

    // ======================== CPU 回退路径（仅 TextureMask / CollisionShape2D 使用） ========================

    /// <summary>
    ///     CPU 端逐像素力写入（回退路径）。仅当 EmissionShapeType 为 TextureMask 或使用了 CollisionShape2D 时使用。
    ///     此路径与优化前的行为一致。
    /// </summary>
    private void ApplyForceToImageCPU(FluidSimulation2D fluidSim)
    {
        var img = fluidSim.InputForcesImg;
        if (img == null) return;

        var res = fluidSim.Resolution;
        var width = (int)res.X;
        var height = (int)res.Y;
        
        var center = fluidSim.WorldToFluidPos(GlobalPosition);
        var pixelRadius = Mathf.Max(5f, ForceRadius / fluidSim.FluidWorldSize.X * width);
        
        var minX = Mathf.Max(0, (int)(center.X - pixelRadius));
        var minY = Mathf.Max(0, (int)(center.Y - pixelRadius));
        var maxX = Mathf.Min(width, (int)(center.X + pixelRadius));
        var maxY = Mathf.Min(height, (int)(center.Y + pixelRadius));

        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                if (!IsPixelInForceRegion(new Vector2(x, y), fluidSim))
                    continue;

                var forceVector = CalculateForceAtPixel(new Vector2(x, y), center, fluidSim);
                var falloff = CalculateFalloff(new Vector2(x, y), center, pixelRadius);
                
                var forceX = forceVector.X * falloff;
                var forceY = forceVector.Y * falloff;
                
                if (Mathf.Abs(forceX) < 0.0001f && Mathf.Abs(forceY) < 0.0001f)
                    continue;

                var currentColor = img.GetPixel(x, y);
                img.SetPixel(x, y, new Color(
                    currentColor.R + forceX,
                    currentColor.G + forceY,
                    currentColor.B,
                    currentColor.A
                ));
            }
        }
    }

    private bool IsPixelInForceRegion(Vector2 pixelPos, FluidSimulation2D fluidSim)
    {
        if (RangeShape != null && RangeShape.Shape != null)
        {
            var worldPos = PixelToWorld(pixelPos, fluidSim);
            var shape = RangeShape.Shape;
            var localPos = RangeShape.GlobalPosition - worldPos;
            
            return shape switch
            {
                RectangleShape2D rect => 
                    Mathf.Abs(localPos.X) < rect.Size.X / 2f && Mathf.Abs(localPos.Y) < rect.Size.Y / 2f,
                CircleShape2D circle => 
                    localPos.LengthSquared() < circle.Radius * circle.Radius,
                _ => true
            };
        }

        var center = fluidSim.WorldToFluidPos(GlobalPosition);
        var offset = pixelPos - center;

        return EmissionShapeType switch
        {
            EmissionShape.Point => offset.LengthSquared() < 4f,
            EmissionShape.Circle => offset.LengthSquared() < ShapeSize.X * ShapeSize.X * (float)(fluidSim.Resolution.X / fluidSim.FluidWorldSize.X) * (float)(fluidSim.Resolution.X / fluidSim.FluidWorldSize.X),
            EmissionShape.Rect => Mathf.Abs(offset.X) < ShapeSize.X * (float)(fluidSim.Resolution.X / fluidSim.FluidWorldSize.X) && Mathf.Abs(offset.Y) < ShapeSize.Y * (float)(fluidSim.Resolution.Y / fluidSim.FluidWorldSize.Y),
            EmissionShape.Line => Mathf.Abs(offset.X) < ShapeSize.X * (float)(fluidSim.Resolution.X / fluidSim.FluidWorldSize.X) && Mathf.Abs(offset.Y) < 2f,
            EmissionShape.TextureMask => IsInTextureMask(pixelPos, fluidSim),
            _ => false
        };
    }

    private Vector2 CalculateForceAtPixel(Vector2 pixelPos, Vector2 center, FluidSimulation2D fluidSim)
    {
        var offset = pixelPos - center;
        var dist = offset.Length();
        if (dist < 0.001f) dist = 0.001f;
        var dir = offset / dist;

        return ForcePatternType switch
        {
            ForcePattern.Directional => Force,
            ForcePattern.Point => dir * Force.Length(),
            ForcePattern.Vortex => new Vector2(-dir.Y, dir.X) * Force.Length() * SwirlStrength + dir * Force.Length() * 0.1f,
            ForcePattern.Attractor => -dir * Force.Length(),
            ForcePattern.Repulsor => dir * Force.Length(),
            _ => Force
        };
    }

    private float CalculateFalloff(Vector2 pixelPos, Vector2 center, float pixelRadius)
    {
        var dist = (pixelPos - center).Length();
        var normalizedDist = dist / pixelRadius;
        if (normalizedDist >= 1f) return 0f;
        
        var falloff = 1f - normalizedDist;
        return Mathf.Pow(falloff, FalloffExponent);
    }

    private Vector2 PixelToWorld(Vector2 pixelPos, FluidSimulation2D fluidSim)
    {
        var uv = pixelPos / fluidSim.Resolution;
        var worldPos = (uv - new Vector2(0.5f, 0.5f)) * fluidSim.FluidWorldSize;
        var followPos = FluidSim?.FollowNode?.GlobalPosition ?? Vector2.Zero;
        return worldPos + followPos;
    }

    private bool IsInTextureMask(Vector2 pixelPos, FluidSimulation2D fluidSim)
    {
        var image = MaskTexture?.GetImage();
        if (image == null) return false;
        
        var textureSize = image.GetSize();
        var uv = pixelPos / fluidSim.Resolution;
        var texX = Mathf.Clamp((int)(uv.X * textureSize.X), 0, textureSize.X - 1);
        var texY = Mathf.Clamp((int)(uv.Y * textureSize.Y), 0, textureSize.Y - 1);
        
        var pixel = image.GetPixel(texX, texY);
        return pixel.A > 0.01f;
    }

    // ======================== 生命周期 ========================

    public override void _Ready()
    {
        AddToGroup("fluid_force_emitters");
        RangeShape = ForceShapeSampler.DetectRangeShape(this);
    }

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

        EmitFluid(FluidSim, dt);
    }

    public void Reset()
    {
        Active = true;
        Lifetime = 0f;
    }
}