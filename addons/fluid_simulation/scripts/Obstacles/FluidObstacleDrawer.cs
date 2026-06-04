using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace FluidSimulation;

/// <summary>
///     障碍物绘制器，负责将场景中的各类节点（ColorRect、Sprite2D、CollisionShape2D、TileMapLayer）
///     光栅化到障碍物纹理数据中。支持将运动物体的速度编码到纹理的 R/G 通道（rgba32f 格式），
///     用于 GPU 计算障碍物对流体的推动力。
/// </summary>
public class FluidObstacleDrawer
{
    /// <summary>哈希量化步长，将浮点坐标量化为整数以计算哈希</summary>
    private const float HashQuantize = 100.0f;

    /// <summary>纹理数据缓存，避免每帧重复获取 Sprite2D 的图像数据</summary>
    private readonly Dictionary<ulong, (Image img, byte[] data)> _texCache = new();

    /// <summary>TileMap 障碍物绘制器</summary>
    private readonly TileMapObstacleDrawer _tileMapDrawer = new();

    /// <summary>当前绘制节点的角速度，在 DrawNode 中设置</summary>
    private float _currentAngularVelocity;

    /// <summary>当前绘制节点是否具有速度信息，在 DrawNode 中设置</summary>
    private bool _currentHasVelocity;

    /// <summary>当前绘制节点的中心位置，在 DrawNode 中设置</summary>
    private Vector2 _currentObstacleCenter;

    /// <summary>当前绘制节点的线速度，在 DrawNode 中设置</summary>
    private Vector2 _currentObstacleVelocity;

    /// <summary>流体域中心</summary>
    private Vector2 _domainCenter;

    /// <summary>流体模拟实例引用</summary>
    private FluidSimulation2D _fluidSim;

    /// <summary>上一帧域中心，用于检测域偏移</summary>
    private Vector2 _lastDomainCenter = new(float.NaN, float.NaN);

    /// <summary>上一帧障碍物哈希值</summary>
    private int _lastObstacleHash;

    /// <summary>脏标记，为 true 时重绘障碍物纹理</summary>
    private bool _needsRedraw = true;

    /// <summary>障碍物纹理原始数据（rgba32f，每像素 16 字节）</summary>
    private byte[] _obsData;

    /// <summary>障碍物纹理高度</summary>
    private int _obsH;

    /// <summary>障碍物纹理宽度</summary>
    private int _obsW;

    /// <summary>流体域分辨率</summary>
    private Vector2 _res;

    /// <summary>流体域世界尺寸</summary>
    private Vector2 _worldSize;

    public TileObstacleMode ObstacleMode
    {
        get => _tileMapDrawer.ObstacleMode;
        set => _tileMapDrawer.ObstacleMode = value;
    }

    public TileFillMode FillMode
    {
        get => _tileMapDrawer.FillMode;
        set => _tileMapDrawer.FillMode = value;
    }

    public int PhysicsLayerIndex
    {
        get => _tileMapDrawer.PhysicsLayerIndex;
        set => _tileMapDrawer.PhysicsLayerIndex = value;
    }

    public Vector2 FluidDomainCenter
    {
        get => _domainCenter;
        set => _domainCenter = value;
    }

    public Vector2 FluidDomainSize
    {
        get => _worldSize;
        set => _worldSize = value;
    }

    /// <summary>初始化绘制器，分配障碍物数据缓冲区。</summary>
    /// <param name="sim">流体模拟实例</param>
    public void Initialize(FluidSimulation2D sim)
    {
        _fluidSim = sim;
        _obsW = (int)sim.Resolution.X;
        _obsH = (int)sim.Resolution.Y;
        _obsData = new byte[_obsW * _obsH * 16];
        _needsRedraw = true;
    }

    /// <summary>帧开始，检测域中心变化并设置脏标记。</summary>
    public void BeginFrame()
    {
        if (_fluidSim != null)
        {
            _worldSize = _fluidSim.FluidWorldSize;
            _res = _fluidSim.Resolution;
        }

        if (_domainCenter != _lastDomainCenter)
        {
            _needsRedraw = true;
            _lastDomainCenter = _domainCenter;
        }
    }

    /// <summary>标记障碍物需要重绘。</summary>
    public void MarkDirty()
    {
        _needsRedraw = true;
    }

    /// <summary>将障碍物数据上传到 GPU 纹理。</summary>
    public void Upload()
    {
        if (_obsData == null || _fluidSim == null) return;
        if (!_needsRedraw) return;

        var copy = new byte[_obsData.Length];
        _obsData.AsSpan().CopyTo(copy);
        _fluidSim.SetObstacleRawData(copy);
        _needsRedraw = false;
    }

    /// <summary>
    ///     绘制单个节点，自动向上查找 IFluidObstacle 接口以获取速度信息。
    ///     当当前节点不是 IFluidObstacle 时，向上遍历父节点查找 IFluidObstacle 实现，
    ///     这是为了支持"子节点在 obstacles 组中、父节点实现 IFluidObstacle"的场景结构。
    /// </summary>
    /// <param name="node">要绘制的画布节点</param>
    public void DrawNode(CanvasItem node)
    {
        _currentHasVelocity = false;
        var obstacle = node as IFluidObstacle;
        if (obstacle == null)
        {
            var parent = node.GetParent();
            while (parent != null)
            {
                obstacle = parent as IFluidObstacle;
                if (obstacle != null) break;
                parent = parent.GetParent();
            }
        }

        if (obstacle != null)
        {
            _currentObstacleVelocity = obstacle.GetObjectLinearVelocity();
            _currentAngularVelocity = obstacle.GetObjectAngularVelocity();
            _currentObstacleCenter = obstacle.GetObjectCenter();
            _currentHasVelocity = true;
        }

        switch (node)
        {
            case ColorRect cr:
                DrawColorRect(cr);
                break;
            case Sprite2D sprite:
                DrawSprite2D(sprite);
                break;
            case CollisionShape2D cs:
                DrawCollisionShape2D(cs);
                break;
            case TileMapLayer tileMapLayer:
                _tileMapDrawer.Draw(tileMapLayer, _obsData, _obsW, _obsH,
                    _domainCenter, _worldSize, _res);
                break;
        }
    }

    /// <summary>扫描并绘制父节点下的所有障碍物子节点。</summary>
    /// <param name="parent">父节点，遍历其子节点进行绘制</param>
    public void ScanAndDraw(Node parent)
    {
        var hash = ComputeObstacleHash(parent);
        if (hash != _lastObstacleHash)
        {
            _needsRedraw = true;
            _lastObstacleHash = hash;
        }

        if (!_needsRedraw) return;
        _obsData.AsSpan().Clear();
        foreach (var child in parent.GetChildren())
            if (child is CanvasItem item)
                DrawNode(item);
    }

    /// <summary>扫描并绘制指定组中的所有障碍物节点。</summary>
    /// <param name="tree">场景树</param>
    /// <param name="group">障碍物节点所在的组名</param>
    public void ScanAndDrawGroup(SceneTree tree, string group)
    {
        var hash = ComputeObstacleHash(tree, group);
        if (hash != _lastObstacleHash)
        {
            _needsRedraw = true;
            _lastObstacleHash = hash;
        }

        if (!_needsRedraw) return;
        _obsData.AsSpan().Clear();
        foreach (var node in tree.GetNodesInGroup(group))
            if (node is CanvasItem item)
                DrawNode(item);
    }

    /// <summary>计算指定组中障碍物状态哈希（位置/缩放/旋转），用于判断是否需要重绘。</summary>
    /// <param name="tree">场景树</param>
    /// <param name="group">障碍物节点所在的组名</param>
    /// <returns>障碍物状态哈希值</returns>
    private int ComputeObstacleHash(SceneTree tree, string group)
    {
        var hash = new HashCode();
        var count = 0;
        foreach (var node in tree.GetNodesInGroup(group))
        {
            if (node is not CanvasItem item || !item.Visible) continue;
            count++;
            if (node is Node2D n2d)
            {
                hash.Add(Mathf.RoundToInt(n2d.GlobalPosition.X * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.GlobalPosition.Y * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.Scale.X * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.Scale.Y * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.GlobalRotation * HashQuantize));
            }
            else
            {
                hash.Add(node.GetInstanceId());
            }
        }

        hash.Add(count);
        hash.Add(Mathf.RoundToInt(_domainCenter.X * HashQuantize));
        hash.Add(Mathf.RoundToInt(_domainCenter.Y * HashQuantize));
        return hash.ToHashCode();
    }

    /// <summary>计算父节点下障碍物子节点状态哈希（位置/缩放/旋转），用于判断是否需要重绘。</summary>
    /// <param name="parent">父节点</param>
    /// <returns>障碍物状态哈希值</returns>
    private int ComputeObstacleHash(Node parent)
    {
        var hash = new HashCode();
        var count = 0;
        foreach (var child in parent.GetChildren())
        {
            if (child is not CanvasItem item || !item.Visible) continue;
            count++;
            if (child is Node2D n2d)
            {
                hash.Add(Mathf.RoundToInt(n2d.GlobalPosition.X * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.GlobalPosition.Y * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.Scale.X * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.Scale.Y * HashQuantize));
                hash.Add(Mathf.RoundToInt(n2d.GlobalRotation * HashQuantize));
            }
            else
            {
                hash.Add(child.GetInstanceId());
            }
        }

        hash.Add(count);
        hash.Add(Mathf.RoundToInt(_domainCenter.X * HashQuantize));
        hash.Add(Mathf.RoundToInt(_domainCenter.Y * HashQuantize));
        return hash.ToHashCode();
    }

    /// <summary>获取纹理的缓存图像数据，避免每帧重复调用 GetImage。</summary>
    /// <param name="tex">纹理资源</param>
    /// <returns>图像及其原始数据元组</returns>
    private (Image img, byte[] data) GetCachedImageData(Texture2D tex)
    {
        var id = tex.GetInstanceId();
        if (_texCache.TryGetValue(id, out var cached))
            return cached;

        var img = tex.GetImage();
        if (img is null) return default;
        var data = img.GetData();
        _texCache[id] = (img, data);
        return (img, data);
    }

    /// <summary>使指定纹理的缓存失效并释放资源。</summary>
    /// <param name="tex">需要失效的纹理资源</param>
    public void InvalidateTextureCache(Texture2D tex)
    {
        if (tex != null && _texCache.Remove(tex.GetInstanceId(), out var cached))
            cached.img?.Dispose();
    }

    /// <summary>清空所有纹理缓存并释放资源。</summary>
    public void ClearTextureCache()
    {
        foreach (var kvp in _texCache)
            kvp.Value.img?.Dispose();
        _texCache.Clear();
    }

    /// <summary>
    ///     将障碍物像素数据写入缓冲区，编码速度到 R/G 通道。
    ///     速度公式：v = v_linear + ω × r，其中 r 为像素世界坐标到障碍物中心的向量。
    ///     R 通道存储 X 分量，G 通道存储 Y 分量，B 通道为 0，A 通道为 1。
    /// </summary>
    /// <param name="offset">在 _obsData 中的字节偏移</param>
    /// <param name="worldPos">像素对应的世界坐标</param>
    private void MarkObstaclePixel(int offset, Vector2 worldPos)
    {
        var floats = MemoryMarshal.Cast<byte, float>(_obsData.AsSpan(offset));
        if (_currentHasVelocity)
        {
            var r = worldPos - _currentObstacleCenter;
            floats[0] = _currentObstacleVelocity.X + -_currentAngularVelocity * r.Y;
            floats[1] = _currentObstacleVelocity.Y + _currentAngularVelocity * r.X;
            floats[2] = 0.0f;
            floats[3] = 1.0f;
        }
        else
        {
            floats[0] = 0.0f;
            floats[1] = 0.0f;
            floats[2] = 0.0f;
            floats[3] = 1.0f;
        }
    }

    /// <summary>写入无速度的障碍物像素，R/G/B 通道为 0，A 通道为 1。</summary>
    /// <param name="offset">在 _obsData 中的字节偏移</param>
    private void MarkObstaclePixelNoVelocity(int offset)
    {
        var floats = MemoryMarshal.Cast<byte, float>(_obsData.AsSpan(offset));
        floats[0] = 0.0f;
        floats[1] = 0.0f;
        floats[2] = 0.0f;
        floats[3] = 1.0f;
    }

    /// <summary>绘制 ColorRect 节点，将其矩形区域光栅化为障碍物。</summary>
    /// <param name="cr">ColorRect 节点</param>
    private void DrawColorRect(ColorRect cr)
    {
        var rect = cr.GetGlobalRect();
        if (_currentHasVelocity)
            PolygonRasterizer.FillRect(rect.Position, rect.Position + rect.Size,
                _obsData, _obsW, _obsH, _domainCenter, _worldSize, _res,
                _currentObstacleVelocity, _currentAngularVelocity, _currentObstacleCenter);
        else
            PolygonRasterizer.FillRect(rect.Position, rect.Position + rect.Size,
                _obsData, _obsW, _obsH, _domainCenter, _worldSize, _res);
    }

    /// <summary>绘制 Sprite2D 节点，根据纹理的 alpha 通道逐像素光栅化为障碍物。</summary>
    /// <param name="sprite">Sprite2D 节点</param>
    private void DrawSprite2D(Sprite2D sprite)
    {
        var tex = sprite.Texture;
        if (tex is null) return;
        var (img, imgData) = GetCachedImageData(tex);
        if (img is null) return;
        var gt = sprite.GlobalTransform;
        var spriteCenter = gt.Origin;
        var texSize = img.GetSize();
        var spriteScale = sprite.Scale;
        var scaledSize = new Vector2(texSize.X * Mathf.Abs(spriteScale.X), texSize.Y * Mathf.Abs(spriteScale.Y));
        var offsetLocal = -sprite.Offset;
        var localMin = offsetLocal - scaledSize / 2.0f;
        var localMax = offsetLocal + scaledSize / 2.0f;
        var worldMin = gt * localMin;
        var worldMax = gt * localMax;
        var pxMin = FluidCoordUtils.WorldToPixelMin(
            new Vector2(Mathf.Min(worldMin.X, worldMax.X), Mathf.Min(worldMin.Y, worldMax.Y)),
            _domainCenter, _worldSize, _res);
        var pxMax = FluidCoordUtils.WorldToPixelMax(
            new Vector2(Mathf.Max(worldMin.X, worldMax.X), Mathf.Max(worldMin.Y, worldMax.Y)),
            _domainCenter, _worldSize, _res);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(_obsW, _obsH));
        if (pxMax.X <= pxMin.X || pxMax.Y <= pxMin.Y) return;

        var imgW = texSize.X;
        var imgH = texSize.Y;
        var fmt = img.GetFormat();
        int bpp, alphaOff;
        switch (fmt)
        {
            case Image.Format.Rgba8:
                bpp = 4;
                alphaOff = 3;
                break;
            case Image.Format.Rgba4444:
                bpp = 2;
                alphaOff = -1;
                break;
            case Image.Format.Rgbaf:
                bpp = 8;
                alphaOff = 6;
                break;
            default:
                bpp = 4;
                alphaOff = 3;
                break;
        }

        var halfLocal = scaledSize / 2.0f;
        var spriteLocalMin = offsetLocal - halfLocal;
        var imgDataSpan = imgData.AsSpan();
        var rowByteWidth = imgW * bpp;
        for (var py = pxMin.Y; py < pxMax.Y; py++)
        {
            var worldY = FluidCoordUtils.PixelToWorldY(py, _res, _worldSize, _domainCenter);
            var localY = (worldY - spriteCenter.Y) / gt.Y.Y;
            var texYRatio = (localY - spriteLocalMin.Y) / scaledSize.Y;
            var texY = Mathf.Clamp((int)(texYRatio * imgH), 0, imgH - 1);
            var rowBase = py * _obsW * 16;
            var texRowBase = texY * rowByteWidth;
            var texRow = imgDataSpan.Slice(texRowBase, rowByteWidth);
            for (var px = pxMin.X; px < pxMax.X; px++)
            {
                var worldX = FluidCoordUtils.PixelToWorldX(px, _res, _worldSize, _domainCenter);
                var localX = (worldX - spriteCenter.X) / gt.X.X;
                var texXRatio = (localX - spriteLocalMin.X) / scaledSize.X;
                var texX = Mathf.Clamp((int)(texXRatio * imgW), 0, imgW - 1);

                bool opaque;
                if (alphaOff >= 0)
                {
                    var alphaByte = texRow[texX * bpp + alphaOff];
                    opaque = alphaByte > 25;
                }
                else
                {
                    var color = img.GetPixel(texX, texY);
                    opaque = color.A > 0.1f;
                }

                if (opaque)
                {
                    var offset = rowBase + px * 16;
                    MarkObstaclePixel(offset, new Vector2(worldX, worldY));
                }
            }
        }
    }

    /// <summary>绘制 CollisionShape2D 节点，根据碰撞形状类型分派到对应的绘制方法。</summary>
    /// <param name="cs">CollisionShape2D 节点</param>
    private void DrawCollisionShape2D(CollisionShape2D cs)
    {
        var shape = cs.Shape;
        if (shape is null) return;
        var gt = cs.GlobalTransform;
        var globalPos = gt.Origin;
        switch (shape)
        {
            case RectangleShape2D rectShape:
                DrawRectangleShape(globalPos, rectShape, gt);
                break;
            case CircleShape2D circleShape:
                DrawCircleShape(globalPos, circleShape, gt);
                break;
            case CapsuleShape2D capsuleShape:
                DrawCapsuleShape(globalPos, capsuleShape, gt);
                break;
            case ConvexPolygonShape2D convexShape:
                DrawConvexPolygonShape(globalPos, convexShape, gt);
                break;
            case ConcavePolygonShape2D concaveShape:
                DrawConcavePolygonShape(globalPos, concaveShape, gt);
                break;
        }
    }

    /// <summary>绘制矩形碰撞形状。</summary>
    private void DrawRectangleShape(Vector2 globalPos, RectangleShape2D shape, Transform2D gt)
    {
        var halfSize = shape.Size / 2.0f;
        var worldMin = gt * -halfSize;
        var worldMax = gt * halfSize;
        var min = new Vector2(Mathf.Min(worldMin.X, worldMax.X), Mathf.Min(worldMin.Y, worldMax.Y));
        var max = new Vector2(Mathf.Max(worldMin.X, worldMax.X), Mathf.Max(worldMin.Y, worldMax.Y));
        if (_currentHasVelocity)
            PolygonRasterizer.FillRect(min, max, _obsData, _obsW, _obsH, _domainCenter, _worldSize, _res,
                _currentObstacleVelocity, _currentAngularVelocity, _currentObstacleCenter);
        else
            PolygonRasterizer.FillRect(min, max, _obsData, _obsW, _obsH, _domainCenter, _worldSize, _res);
    }

    /// <summary>绘制圆形碰撞形状。</summary>
    private void DrawCircleShape(Vector2 globalPos, CircleShape2D shape, Transform2D gt)
    {
        var radius = shape.Radius;
        var scaleX = Mathf.Abs(gt.X.Length());
        var scaleY = Mathf.Abs(gt.Y.Length());
        var radiusWorldX = radius * scaleX;
        var radiusWorldY = radius * scaleY;
        var obsMin = globalPos - new Vector2(radiusWorldX, radiusWorldY);
        var obsMax = globalPos + new Vector2(radiusWorldX, radiusWorldY);
        var pxMin = FluidCoordUtils.WorldToPixelMin(obsMin, _domainCenter, _worldSize, _res);
        var pxMax = FluidCoordUtils.WorldToPixelMax(obsMax, _domainCenter, _worldSize, _res);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(_obsW, _obsH));
        var centerPx = FluidCoordUtils.WorldToPixel(globalPos, _domainCenter, _worldSize, _res);
        var radiusPxX = (int)(radiusWorldX / _worldSize.X * _res.X);
        var radiusPxY = (int)(radiusWorldY / _worldSize.Y * _res.Y);
        if (radiusPxX <= 0 || radiusPxY <= 0) return;
        var radiusSqX = radiusPxX * radiusPxX;
        var radiusSqY = radiusPxY * radiusPxY;
        for (var py = pxMin.Y; py < pxMax.Y; py++)
        {
            var dy = py - centerPx.Y;
            var dySq = dy * dy;
            var rowBase = py * _obsW * 16;
            for (var px = pxMin.X; px < pxMax.X; px++)
            {
                var dx = px - centerPx.X;
                if ((float)(dx * dx) / radiusSqX + (float)dySq / radiusSqY <= 1.0f)
                {
                    var offset = rowBase + px * 16;
                    if (_currentHasVelocity)
                    {
                        var worldX = FluidCoordUtils.PixelToWorldX(px, _res, _worldSize, _domainCenter);
                        var worldY = FluidCoordUtils.PixelToWorldY(py, _res, _worldSize, _domainCenter);
                        MarkObstaclePixel(offset, new Vector2(worldX, worldY));
                    }
                    else
                    {
                        MarkObstaclePixelNoVelocity(offset);
                    }
                }
            }
        }
    }

    /// <summary>绘制胶囊碰撞形状。</summary>
    private void DrawCapsuleShape(Vector2 globalPos, CapsuleShape2D shape, Transform2D gt)
    {
        var radius = shape.Radius;
        var height = shape.Height;
        var scaleX = Mathf.Abs(gt.X.Length());
        var scaleY = Mathf.Abs(gt.Y.Length());
        var radiusWorldX = radius * scaleX;
        var radiusWorldY = radius * scaleY;
        var halfHeightWorld = height / 2.0f * scaleY;
        var totalHalfHeight = halfHeightWorld + radiusWorldY;
        var obsMin = globalPos - new Vector2(radiusWorldX, totalHalfHeight);
        var obsMax = globalPos + new Vector2(radiusWorldX, totalHalfHeight);
        var pxMin = FluidCoordUtils.WorldToPixelMin(obsMin, _domainCenter, _worldSize, _res);
        var pxMax = FluidCoordUtils.WorldToPixelMax(obsMax, _domainCenter, _worldSize, _res);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(_obsW, _obsH));
        var centerPx = FluidCoordUtils.WorldToPixel(globalPos, _domainCenter, _worldSize, _res);
        var topPx = centerPx.Y - (int)(halfHeightWorld / _worldSize.Y * _res.Y);
        var bottomPx = centerPx.Y + (int)(halfHeightWorld / _worldSize.Y * _res.Y);
        var radiusPxX = (int)(radiusWorldX / _worldSize.X * _res.X);
        var radiusPxY = (int)(radiusWorldY / _worldSize.Y * _res.Y);
        if (radiusPxX <= 0 || radiusPxY <= 0) return;
        var radiusSqX = radiusPxX * radiusPxX;
        var radiusSqY = radiusPxY * radiusPxY;
        for (var py = pxMin.Y; py < pxMax.Y; py++)
        {
            var rowBase = py * _obsW * 16;
            for (var px = pxMin.X; px < pxMax.X; px++)
            {
                var inCapsule = false;
                if (py >= topPx && py <= bottomPx)
                {
                    inCapsule = Mathf.Abs(px - centerPx.X) <= radiusPxX;
                }
                else if (py < topPx)
                {
                    var dy = py - topPx;
                    var dx = px - centerPx.X;
                    inCapsule = (float)(dx * dx) / radiusSqX + (float)(dy * dy) / radiusSqY <= 1.0f;
                }
                else
                {
                    var dy = py - bottomPx;
                    var dx = px - centerPx.X;
                    inCapsule = (float)(dx * dx) / radiusSqX + (float)(dy * dy) / radiusSqY <= 1.0f;
                }

                if (inCapsule)
                {
                    var offset = rowBase + px * 16;
                    if (_currentHasVelocity)
                    {
                        var worldX = FluidCoordUtils.PixelToWorldX(px, _res, _worldSize, _domainCenter);
                        var worldY = FluidCoordUtils.PixelToWorldY(py, _res, _worldSize, _domainCenter);
                        MarkObstaclePixel(offset, new Vector2(worldX, worldY));
                    }
                    else
                    {
                        MarkObstaclePixelNoVelocity(offset);
                    }
                }
            }
        }
    }

    /// <summary>绘制凸多边形碰撞形状。</summary>
    private void DrawConvexPolygonShape(Vector2 globalPos, ConvexPolygonShape2D shape, Transform2D gt)
    {
        var points = shape.Points;
        if (points.Length < 3) return;
        var transformedPoints = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
            transformedPoints[i] = gt * points[i];

        if (_currentHasVelocity)
            PolygonRasterizer.FillPolygon(transformedPoints, _obsData, _obsW, _obsH,
                _domainCenter, _worldSize, _res,
                _currentObstacleVelocity, _currentAngularVelocity, _currentObstacleCenter);
        else
            PolygonRasterizer.FillPolygon(transformedPoints, _obsData, _obsW, _obsH,
                _domainCenter, _worldSize, _res);
    }

    /// <summary>绘制凹多边形碰撞形状。</summary>
    private void DrawConcavePolygonShape(Vector2 globalPos, ConcavePolygonShape2D shape, Transform2D gt)
    {
        var segments = shape.Segments;
        if (segments.Length < 2) return;
        var polygonPoints = new Vector2[segments.Length];
        for (var s = 0; s < segments.Length; s++)
            polygonPoints[s] = gt * segments[s];

        if (_currentHasVelocity)
            PolygonRasterizer.FillPolygon(polygonPoints, _obsData, _obsW, _obsH,
                _domainCenter, _worldSize, _res,
                _currentObstacleVelocity, _currentAngularVelocity, _currentObstacleCenter);
        else
            PolygonRasterizer.FillPolygon(polygonPoints, _obsData, _obsW, _obsH,
                _domainCenter, _worldSize, _res);
    }
}