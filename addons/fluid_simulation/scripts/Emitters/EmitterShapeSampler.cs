using Godot;

namespace FluidSimulation;

/// <summary>
///     发射器形状采样器，提供静态方法用于在各种几何形状中随机采样粒子发射点。
///     <para>
///         支持五种基础形状采样（点、圆、矩形、线段、纹理遮罩），
///         以及四种 Godot 碰撞形状的区域采样（矩形、圆形、胶囊体、凹多边形）。
///         每种区域形状均支持内部采样和边缘采样两种模式。
///     </para>
/// </summary>
public static class EmitterShapeSampler
{
    /// <summary>
    ///     从父节点的子节点中检测 CollisionShape2D 作为发射范围形状。
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
    ///     在发射器配置的形状中采样指定数量的粒子点。优先使用 CollisionShape2D 范围形状采样，不可用时回退到 EmissionShapeType 配置的形状。
    /// </summary>
    /// <param name="count">要采样的粒子数量。</param>
    /// <param name="emitter">发射器实例，提供形状配置和位置信息。</param>
    /// <param name="useRangeShape">是否尝试使用 CollisionShape2D 范围形状，默认 true。</param>
    /// <returns>采样得到的世界坐标点数组。</returns>
    public static Vector2[] Sample(int count, FluidEmitter emitter, bool useRangeShape = true)
    {
        if (useRangeShape && emitter.RangeShape != null && emitter.RangeShape.Shape != null)
        {
            var shape = emitter.RangeShape.Shape;
            if (shape is RectangleShape2D or CircleShape2D or CapsuleShape2D or ConcavePolygonShape2D)
                return SampleRangeShape(count, emitter.RangeShape, emitter.SampleMode, emitter.GlobalPosition);
            return FallbackToEmitterShape(count, emitter);
        }

        var points = new Vector2[count];

        switch (emitter.EmissionShapeType)
        {
            // 点状发射：所有粒子从中心点发出
            case EmissionShape.Point:
                for (var i = 0; i < count; i++)
                    points[i] = emitter.GlobalPosition;
                break;

            // 圆形发射：均匀面积采样（sqrt(rand) 保证均匀分布）
            case EmissionShape.Circle:
                for (var i = 0; i < count; i++)
                {
                    var angle = GD.Randf() * Mathf.Tau;
                    var r = Mathf.Sqrt(GD.Randf()) * emitter.ShapeSize.X;
                    var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                    points[i] = emitter.GlobalPosition + offset;
                }

                break;

            // 矩形发射：在矩形区域内均匀随机
            case EmissionShape.Rect:
                for (var i = 0; i < count; i++)
                {
                    var offset = new Vector2(
                        (float)GD.RandRange(-emitter.ShapeSize.X, emitter.ShapeSize.X),
                        (float)GD.RandRange(-emitter.ShapeSize.Y, emitter.ShapeSize.Y)
                    );
                    points[i] = emitter.GlobalPosition + offset;
                }

                break;

            // 线段发射：沿水平线段采样，垂直方向微小偏移
            case EmissionShape.Line:
                for (var i = 0; i < count; i++)
                {
                    var t = GD.Randf();
                    var offset = new Vector2(
                        Mathf.Lerp(-emitter.ShapeSize.X, emitter.ShapeSize.X, t),
                        (float)GD.RandRange(-0.1, 0.1)
                    );
                    points[i] = emitter.GlobalPosition + offset;
                }

                break;

            // 纹理遮罩发射：委托给 SampleTextureMask
            case EmissionShape.TextureMask:
                points = SampleTextureMask(count, emitter);
                break;
        }

        return points;
    }

    /// <summary>
    ///     根据遮罩纹理的 Alpha 通道采样粒子点。使用拒绝采样法：随机生成像素坐标，若该像素 Alpha 大于随机阈值则接受。最多尝试 count*10 次，不足部分用最后一个有效点填充。
    /// </summary>
    /// <param name="count">要采样的粒子数量。</param>
    /// <param name="emitter">发射器实例，需提供 MaskTexture 和 ShapeSize。</param>
    /// <returns>采样得到的世界坐标点数组。</returns>
    public static Vector2[] SampleTextureMask(int count, FluidEmitter emitter)
    {
        var points = new Vector2[count];

        if (emitter.MaskTexture == null)
        {
            for (var i = 0; i < count; i++)
                points[i] = emitter.GlobalPosition;
            return points;
        }

        var image = emitter.MaskTexture.GetImage();
        if (image == null)
        {
            for (var i = 0; i < count; i++)
                points[i] = emitter.GlobalPosition;
            return points;
        }

        var textureSize = image.GetSize();
        var sampledCount = 0;
        var maxAttempts = count * 10;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (sampledCount >= count)
                break;

            var x = (int)GD.Randi() % textureSize.X;
            var y = (int)GD.Randi() % textureSize.Y;
            var pixel = image.GetPixel(x, y);

            if (GD.Randf() < pixel.A)
            {
                var localPos = new Vector2(
                    ((float)x / textureSize.X - 0.5f) * emitter.ShapeSize.X * 2.0f,
                    ((float)y / textureSize.Y - 0.5f) * emitter.ShapeSize.Y * 2.0f
                );
                points[sampledCount] = emitter.GlobalPosition + localPos;
                sampledCount++;
            }
        }

        for (var i = sampledCount; i < count; i++)
            points[i] = sampledCount > 0 ? points[sampledCount - 1] : emitter.GlobalPosition;

        return points;
    }

    /// <summary>
    ///     根据 CollisionShape2D 的具体形状类型分派到对应的采样方法。
    /// </summary>
    /// <param name="count">要采样的粒子数量。</param>
    /// <param name="rangeShape">碰撞范围形状节点。</param>
    /// <param name="sampleMode">采样模式（内部/边缘）。</param>
    /// <param name="globalPosition">发射器世界坐标，作为采样失败时的回退位置。</param>
    /// <returns>采样得到的世界坐标点数组。</returns>
    public static Vector2[] SampleRangeShape(int count, CollisionShape2D rangeShape,
        RangeSampleMode sampleMode, Vector2 globalPosition)
    {
        return rangeShape.Shape switch
        {
            RectangleShape2D rect => SampleRectangleRange(count, rect, rangeShape.GlobalTransform, sampleMode),
            CircleShape2D circle => SampleCircleRange(count, circle, rangeShape.GlobalTransform, sampleMode),
            CapsuleShape2D capsule => SampleCapsuleRange(count, capsule, rangeShape.GlobalTransform, sampleMode),
            ConcavePolygonShape2D concave => SampleConcavePolygonRange(count, concave,
                rangeShape.GlobalTransform, sampleMode, globalPosition),
            _ => new Vector2[count]
        };
    }

    /// <summary>
    ///     在矩形碰撞形状中采样粒子点。内部模式：在矩形内均匀随机分布；边缘模式：沿矩形周长均匀分布。
    /// </summary>
    public static Vector2[] SampleRectangleRange(int count, RectangleShape2D shape,
        Transform2D gt, RangeSampleMode sampleMode)
    {
        var points = new Vector2[count];
        var halfSize = shape.Size / 2.0f;

        if (sampleMode == RangeSampleMode.Interior)
        {
            for (var i = 0; i < count; i++)
            {
                var localPos = new Vector2(
                    (float)GD.RandRange(-halfSize.X, halfSize.X),
                    (float)GD.RandRange(-halfSize.Y, halfSize.Y)
                );
                points[i] = gt * localPos;
            }
        }
        else
        {
            var perimeter = 2.0f * (shape.Size.X + shape.Size.Y);
            for (var i = 0; i < count; i++)
            {
                var t = GD.Randf() * perimeter;
                Vector2 localPos;
                if (t < shape.Size.X)
                    localPos = new Vector2(-halfSize.X + t, -halfSize.Y);
                else if (t < shape.Size.X + shape.Size.Y)
                    localPos = new Vector2(halfSize.X, -halfSize.Y + (t - shape.Size.X));
                else if (t < 2.0f * shape.Size.X + shape.Size.Y)
                    localPos = new Vector2(halfSize.X - (t - shape.Size.X - shape.Size.Y), halfSize.Y);
                else
                    localPos = new Vector2(-halfSize.X, halfSize.Y - (t - 2.0f * shape.Size.X - shape.Size.Y));

                points[i] = gt * localPos;
            }
        }

        return points;
    }

    /// <summary>
    ///     在圆形碰撞形状中采样粒子点。内部模式：使用 sqrt(rand) 保证面积均匀分布；边缘模式：仅在圆周上采样。
    /// </summary>
    public static Vector2[] SampleCircleRange(int count, CircleShape2D shape,
        Transform2D gt, RangeSampleMode sampleMode)
    {
        var points = new Vector2[count];

        if (sampleMode == RangeSampleMode.Interior)
            for (var i = 0; i < count; i++)
            {
                var angle = GD.Randf() * Mathf.Tau;
                var r = Mathf.Sqrt(GD.Randf()) * shape.Radius;
                var localPos = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
                points[i] = gt * localPos;
            }
        else
            for (var i = 0; i < count; i++)
            {
                var angle = GD.Randf() * Mathf.Tau;
                var localPos = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * shape.Radius;
                points[i] = gt * localPos;
            }

        return points;
    }

    /// <summary>
    ///     在胶囊体碰撞形状中采样粒子点。内部模式：按面积比例在矩形部分和两个半圆部分之间分配采样；边缘模式：按周长比例在直线段和半圆弧之间分配采样。
    /// </summary>
    public static Vector2[] SampleCapsuleRange(int count, CapsuleShape2D shape,
        Transform2D gt, RangeSampleMode sampleMode)
    {
        var points = new Vector2[count];
        var radius = shape.Radius;
        var halfHeight = shape.Height / 2.0f;

        if (sampleMode == RangeSampleMode.Interior)
        {
            var rectArea = shape.Height * radius * 2.0f;
            var circleArea = Mathf.Pi * radius * radius;
            var totalArea = rectArea + circleArea;

            for (var i = 0; i < count; i++)
            {
                var localPos = Vector2.Zero;
                if (GD.Randf() < rectArea / totalArea)
                {
                    localPos = new Vector2(
                        (float)GD.RandRange(-radius, radius),
                        (float)GD.RandRange(-halfHeight, halfHeight)
                    );
                }
                else
                {
                    var top = GD.Randf() < 0.5f;
                    var angle = GD.Randf() * Mathf.Tau;
                    var r = Mathf.Sqrt(GD.Randf()) * radius;
                    var cy = top ? -halfHeight : halfHeight;
                    localPos = new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r + cy);
                }

                points[i] = gt * localPos;
            }
        }
        else
        {
            var straightLen = shape.Height * 2.0f;
            var semicircleLen = Mathf.Pi * radius * 2.0f;
            var totalLen = straightLen + semicircleLen;

            for (var i = 0; i < count; i++)
            {
                var t = GD.Randf() * totalLen;
                Vector2 localPos;
                if (t < straightLen)
                {
                    var side = GD.Randf() < 0.5f ? -1.0f : 1.0f;
                    var y = -halfHeight + t / 2.0f;
                    localPos = new Vector2(side * radius, y);
                }
                else
                {
                    var rem = t - straightLen;
                    var top = GD.Randf() < 0.5f;
                    var angle = top
                        ? Mathf.Lerp(Mathf.Pi, 0, rem / semicircleLen)
                        : Mathf.Lerp(0, Mathf.Pi, rem / semicircleLen);
                    var cy = top ? -halfHeight : halfHeight;
                    localPos = new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius + cy);
                }

                points[i] = gt * localPos;
            }
        }

        return points;
    }

    /// <summary>
    ///     在凹多边形碰撞形状中采样粒子点。内部模式：使用包围盒拒绝采样 + IsPointInPolygon 判断；边缘模式：按线段长度加权在多边形边上插值采样。
    /// </summary>
    public static Vector2[] SampleConcavePolygonRange(int count, ConcavePolygonShape2D shape,
        Transform2D gt, RangeSampleMode sampleMode, Vector2 globalPosition)
    {
        var points = new Vector2[count];
        var segments = shape.Segments;
        if (segments.Length < 2)
            return FallbackToEmitterShape(count, null);

        var worldPoints = new Vector2[segments.Length];
        for (var s = 0; s < segments.Length; s++)
            worldPoints[s] = gt * segments[s];

        if (sampleMode == RangeSampleMode.Interior)
        {
            var minX = worldPoints[0].X;
            var maxX = worldPoints[0].X;
            var minY = worldPoints[0].Y;
            var maxY = worldPoints[0].Y;
            for (var i = 1; i < worldPoints.Length; i++)
            {
                if (worldPoints[i].X < minX) minX = worldPoints[i].X;
                if (worldPoints[i].X > maxX) maxX = worldPoints[i].X;
                if (worldPoints[i].Y < minY) minY = worldPoints[i].Y;
                if (worldPoints[i].Y > maxY) maxY = worldPoints[i].Y;
            }

            var sampled = 0;
            var maxAttempts = count * 20;
            for (var attempt = 0; attempt < maxAttempts && sampled < count; attempt++)
            {
                var testPoint = new Vector2(
                    (float)GD.RandRange(minX, maxX),
                    (float)GD.RandRange(minY, maxY)
                );
                if (PolygonRasterizer.IsPointInPolygon(testPoint, worldPoints))
                {
                    points[sampled] = testPoint;
                    sampled++;
                }
            }

            for (var i = sampled; i < count; i++)
                points[i] = sampled > 0 ? points[sampled - 1] : globalPosition;
        }
        else
        {
            var segmentLengths = new float[worldPoints.Length];
            var totalLength = 0.0f;
            for (var i = 0; i < worldPoints.Length; i++)
            {
                var j = (i + 1) % worldPoints.Length;
                segmentLengths[i] = worldPoints[i].DistanceTo(worldPoints[j]);
                totalLength += segmentLengths[i];
            }

            for (var i = 0; i < count; i++)
            {
                var t = GD.Randf() * totalLength;
                var accumulated = 0.0f;
                for (var s = 0; s < worldPoints.Length; s++)
                {
                    if (accumulated + segmentLengths[s] >= t)
                    {
                        var localT = (t - accumulated) / segmentLengths[s];
                        var j = (s + 1) % worldPoints.Length;
                        points[i] = worldPoints[s].Lerp(worldPoints[j], localT);
                        break;
                    }

                    accumulated += segmentLengths[s];
                }
            }
        }

        return points;
    }

    /// <summary>
    ///     回退到发射器配置的 EmissionShapeType 进行采样（跳过 CollisionShape2D）。
    /// </summary>
    public static Vector2[] FallbackToEmitterShape(int count, FluidEmitter emitter)
    {
        if (emitter == null)
            return new Vector2[count];
        return Sample(count, emitter, false);
    }
}