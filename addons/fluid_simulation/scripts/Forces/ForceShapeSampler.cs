using Godot;

namespace FluidSimulation;

/// <summary>
///     力发射器形状采样器，提供静态方法用于在力的作用区域内采样像素坐标。
///     <para>
///         支持五种基础形状（点、圆、矩形、线段、纹理遮罩），
///         以及 CollisionShape2D 的区域采样（矩形、圆形、胶囊体、凹多边形）。
///     </para>
/// </summary>
public static class ForceShapeSampler
{
    /// <summary>
    ///     从父节点的子节点中检测 CollisionShape2D 作为力的作用范围形状。
    /// </summary>
    /// <param name="parent">要搜索的父节点。</param>
    /// <returns>找到的第一个有效 CollisionShape2D，未找到则返回 null。</returns>
    public static CollisionShape2D DetectRangeShape(Node parent)
    {
        foreach (var child in parent.GetChildren())
            if (child is CollisionShape2D cs && cs.Shape != null)
                return cs;

        return null;
    }

    /// <summary>
    ///     计算力的作用区域在纹理上的像素边界框。
    /// </summary>
    /// <param name="emitter">力发射器实例。</param>
    /// <param name="fluidSim">流体模拟实例，用于坐标转换。</param>
    /// <returns>边界框 (minX, minY, maxX, maxY)，以像素坐标表示。</returns>
    public static (int minX, int minY, int maxX, int maxY) GetForceBounds(FluidForceEmitter emitter, FluidSimulation2D fluidSim)
    {
        var center = fluidSim.WorldToFluidPos(emitter.GlobalPosition);
        var radius = emitter.ForceRadius * (float)fluidSim.Resolution.X / fluidSim.FluidWorldSize.X * 100f;
        
        // 确保半径至少覆盖一定范围
        if (radius < 5f) radius = 20f;
        
        var minX = Mathf.Max(0, (int)(center.X - radius));
        var minY = Mathf.Max(0, (int)(center.Y - radius));
        var maxX = Mathf.Min((int)fluidSim.Resolution.X, (int)(center.X + radius));
        var maxY = Mathf.Min((int)fluidSim.Resolution.Y, (int)(center.Y + radius));
        
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    ///     检查给定的像素坐标是否在力的作用形状范围内。
    /// </summary>
    /// <param name="pixelPos">像素坐标。</param>
    /// <param name="emitter">力发射器实例。</param>
    /// <param name="fluidSim">流体模拟实例。</param>
    /// <returns>如果像素在力作用范围内则返回 true。</returns>
    public static bool IsInForceRegion(Vector2 pixelPos, FluidForceEmitter emitter, FluidSimulation2D fluidSim)
    {
        var center = fluidSim.WorldToFluidPos(emitter.GlobalPosition);
        
        // 如果有 CollisionShape2D，使用它来判断
        if (emitter.RangeShape != null && emitter.RangeShape.Shape != null)
        {
            var worldPos = PixelToWorld(pixelPos, fluidSim, emitter);
            return IsPointInCollisionShape(worldPos, emitter.RangeShape);
        }

        // 根据发射形状类型判断
        var offset = pixelPos - center;
        return emitter.EmissionShapeType switch
        {
            EmissionShape.Point => offset.LengthSquared() < 4f, // 点状，小范围
            EmissionShape.Circle => offset.LengthSquared() < emitter.ShapeSize.X * emitter.ShapeSize.X,
            EmissionShape.Rect => Mathf.Abs(offset.X) < emitter.ShapeSize.X && Mathf.Abs(offset.Y) < emitter.ShapeSize.Y,
            EmissionShape.Line => Mathf.Abs(offset.X) < emitter.ShapeSize.X && Mathf.Abs(offset.Y) < 2f,
            EmissionShape.TextureMask => IsInTextureMask(pixelPos, emitter, fluidSim),
            _ => false
        };
    }

    private static Vector2 PixelToWorld(Vector2 pixelPos, FluidSimulation2D fluidSim, FluidForceEmitter emitter)
    {
        var uv = pixelPos / fluidSim.Resolution;
        var worldPos = (uv - new Vector2(0.5f, 0.5f)) * fluidSim.FluidWorldSize;
        var followPos = fluidSim.FollowNode?.GlobalPosition ?? Vector2.Zero;
        return worldPos + followPos;
    }

    /// <summary>
    ///     计算力场中的衰减因子，基于像素到力中心的距离。
    /// </summary>
    /// <param name="pixelPos">像素坐标。</param>
    /// <param name="emitter">力发射器实例。</param>
    /// <param name="fluidSim">流体模拟实例。</param>
    /// <returns>衰减因子 (0-1)，1 表示最大力，0 表示无力。</returns>
    public static float CalculateForceFalloff(Vector2 pixelPos, FluidForceEmitter emitter, FluidSimulation2D fluidSim)
    {
        var center = fluidSim.WorldToFluidPos(emitter.GlobalPosition);
        var distance = (pixelPos - center).Length();
        var radius = emitter.ForceRadius;
        
        if (radius <= 0f) return 1f;
        
        var normalizedDist = distance / radius;
        if (normalizedDist >= 1f) return 0f;
        
        // 使用平滑衰减：(1 - d/r)^2
        var falloff = 1f - normalizedDist;
        return falloff * falloff;
    }

    private static bool IsPointInCollisionShape(Vector2 worldPos, CollisionShape2D rangeShape)
    {
        var shape = rangeShape.Shape;
        var shapePos = rangeShape.GlobalPosition;
        var localPos = worldPos - shapePos;
        
        return shape switch
        {
            RectangleShape2D rect => 
                Mathf.Abs(localPos.X) < rect.Size.X / 2f && Mathf.Abs(localPos.Y) < rect.Size.Y / 2f,
            CircleShape2D circle => 
                localPos.LengthSquared() < circle.Radius * circle.Radius,
            _ => true
        };
    }

    private static bool IsInTextureMask(Vector2 pixelPos, FluidForceEmitter emitter, FluidSimulation2D fluidSim)
    {
        if (emitter.MaskTexture == null) return false;
        
        var image = emitter.MaskTexture.GetImage();
        if (image == null) return false;
        
        var textureSize = image.GetSize();
        var uv = pixelPos / fluidSim.Resolution;
        var texX = (int)(uv.X * textureSize.X);
        var texY = (int)(uv.Y * textureSize.Y);
        
        if (texX < 0 || texX >= textureSize.X || texY < 0 || texY >= textureSize.Y)
            return false;
        
        var pixel = image.GetPixel(texX, texY);
        return pixel.A > 0.01f;
    }
}
