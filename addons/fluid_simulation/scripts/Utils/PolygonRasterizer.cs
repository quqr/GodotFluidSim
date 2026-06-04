using System;
using System.Runtime.InteropServices;
using Godot;

namespace FluidSimulation;

/// <summary>
///     多边形光栅化器，使用扫描线算法将多边形和矩形绘制到障碍物纹理数据中。
///     <para>
///         障碍物纹理使用 RGBA32F 格式，每个像素 16 字节：
///         R/G 通道存储障碍物速度（vx, vy），A 通道为障碍物标记（1.0 = 障碍物）。
///         无速度的静态障碍物 R=G=0, A=1；有速度的运动障碍物 R=vx, G=vy, A=1。
///     </para>
///     <para>
///         扫描线算法流程：对每条水平扫描线，计算与多边形边的交点（crossings），
///         排序后成对填充交点之间的像素。使用预分配的 _crossingsBuffer 避免每帧 GC 分配。
///     </para>
/// </summary>
public static class PolygonRasterizer
{
    /// <summary>扫描线交点缓冲区，预分配以避免每帧 GC 分配。按需扩容。</summary>
    private static float[] _crossingsBuffer = new float[64];

    /// <summary>标记静态障碍物像素：速度归零，Alpha 设为 1.0。</summary>
    /// <param name="data">障碍物纹理数据。</param>
    /// <param name="offset">像素在数据中的字节偏移。</param>
    private static void MarkObstaclePixel(Span<byte> data, int offset)
    {
        var floats = MemoryMarshal.Cast<byte, float>(data[offset..]);
        floats[0] = 0.0f;
        floats[1] = 0.0f;
        floats[2] = 0.0f;
        floats[3] = 1.0f;
    }

    /// <summary>标记运动障碍物像素：写入速度分量和 Alpha=1.0。速度编码：R=vx, G=vy, B=0, A=1。</summary>
    /// <param name="data">障碍物纹理数据。</param>
    /// <param name="offset">像素在数据中的字节偏移。</param>
    /// <param name="vx">障碍物在 X 方向的线速度。</param>
    /// <param name="vy">障碍物在 Y 方向的线速度。</param>
    private static void MarkObstaclePixel(Span<byte> data, int offset, float vx, float vy)
    {
        var floats = MemoryMarshal.Cast<byte, float>(data[offset..]);
        floats[0] = vx;
        floats[1] = vy;
        floats[2] = 0.0f;
        floats[3] = 1.0f;
    }

    /// <summary>确保扫描线交点缓冲区容量足够，不足时扩容为当前需求的 2 倍。</summary>
    /// <param name="minSize">所需的最小容量。</param>
    private static void EnsureCrossingsBuffer(int minSize)
    {
        if (_crossingsBuffer.Length < minSize)
            Array.Resize(ref _crossingsBuffer, minSize * 2);
    }

    /// <summary>使用扫描线算法将多边形绘制为静态障碍物（速度为零）。</summary>
    /// <param name="worldPoints">多边形顶点的世界坐标数组。</param>
    /// <param name="obsData">障碍物纹理数据。</param>
    /// <param name="obsW">障碍物纹理宽度（像素）。</param>
    /// <param name="obsH">障碍物纹理高度（像素）。</param>
    /// <param name="domainCenter">模拟域中心的世界坐标。</param>
    /// <param name="worldSize">模拟域的世界尺寸。</param>
    /// <param name="resolution">像素分辨率（每个像素对应的世界尺寸）。</param>
    public static void FillPolygon(
        Vector2[] worldPoints,
        Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        if (worldPoints.Length < 3) return;

        // 计算包围盒
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

        // 转换为像素坐标范围
        var pxMin = FluidCoordUtils.WorldToPixelMin(new Vector2(minX, minY), domainCenter, worldSize, resolution);
        var pxMax = FluidCoordUtils.WorldToPixelMax(new Vector2(maxX, maxY), domainCenter, worldSize, resolution);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(obsW, obsH));

        EnsureCrossingsBuffer(worldPoints.Length);

        // 对每条水平扫描线
        for (var py = pxMin.Y; py < pxMax.Y; py++)
        {
            var worldY = FluidCoordUtils.PixelToWorldY(py, resolution, worldSize, domainCenter);
            var crossingsCount = 0;
            var n = worldPoints.Length;
            // 计算扫描线与多边形边的交点
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                var p1 = worldPoints[i];
                var p2 = worldPoints[j];
                if (p1.Y > worldY != p2.Y > worldY)
                {
                    var t = (worldY - p1.Y) / (p2.Y - p1.Y);
                    _crossingsBuffer[crossingsCount++] = p1.X + t * (p2.X - p1.X);
                }
            }

            // 排序交点，成对填充
            _crossingsBuffer.AsSpan(0, crossingsCount).Sort();
            var rowBase = py * obsW * 16;
            for (var k = 0; k < crossingsCount - 1; k += 2)
            {
                var xStart = FluidCoordUtils.WorldToPixelMinX(_crossingsBuffer[k], domainCenter, worldSize, resolution);
                var xEnd = FluidCoordUtils.WorldToPixelMaxX(_crossingsBuffer[k + 1], domainCenter, worldSize,
                    resolution);
                for (var px = xStart; px < xEnd; px++)
                    if (px >= 0 && px < obsW)
                    {
                        var offset = rowBase + px * 16;
                        MarkObstaclePixel(obsData, offset);
                    }
            }
        }
    }

    /// <summary>
    ///     使用扫描线算法将多边形绘制为运动障碍物，编码线速度和角速度。
    ///     <para>
    ///         速度计算：v = linearVelocity + angularVelocity × r，其中 r 是像素世界坐标相对于障碍物中心的向量，
    ///         叉积在 2D 中展开为 vx = linearVelocity.X - angularVelocity * r.Y，
    ///         vy = linearVelocity.Y + angularVelocity * r.X。
    ///     </para>
    /// </summary>
    /// <param name="worldPoints">多边形顶点的世界坐标数组。</param>
    /// <param name="obsData">障碍物纹理数据。</param>
    /// <param name="obsW">障碍物纹理宽度（像素）。</param>
    /// <param name="obsH">障碍物纹理高度（像素）。</param>
    /// <param name="domainCenter">模拟域中心的世界坐标。</param>
    /// <param name="worldSize">模拟域的世界尺寸。</param>
    /// <param name="resolution">像素分辨率（每个像素对应的世界尺寸）。</param>
    /// <param name="linearVelocity">障碍物的线速度。</param>
    /// <param name="angularVelocity">障碍物的角速度（弧度/秒）。</param>
    /// <param name="obstacleCenter">障碍物中心的世界坐标，用于计算旋转速度。</param>
    public static void FillPolygon(
        Vector2[] worldPoints,
        Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution,
        Vector2 linearVelocity, float angularVelocity, Vector2 obstacleCenter)
    {
        if (worldPoints.Length < 3) return;

        // 计算包围盒
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

        // 转换为像素坐标范围
        var pxMin = FluidCoordUtils.WorldToPixelMin(new Vector2(minX, minY), domainCenter, worldSize, resolution);
        var pxMax = FluidCoordUtils.WorldToPixelMax(new Vector2(maxX, maxY), domainCenter, worldSize, resolution);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(obsW, obsH));

        EnsureCrossingsBuffer(worldPoints.Length);

        // 对每条水平扫描线
        for (var py = pxMin.Y; py < pxMax.Y; py++)
        {
            var worldY = FluidCoordUtils.PixelToWorldY(py, resolution, worldSize, domainCenter);
            var crossingsCount = 0;
            var n = worldPoints.Length;
            // 计算扫描线与多边形边的交点
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                var p1 = worldPoints[i];
                var p2 = worldPoints[j];
                if (p1.Y > worldY != p2.Y > worldY)
                {
                    var t = (worldY - p1.Y) / (p2.Y - p1.Y);
                    _crossingsBuffer[crossingsCount++] = p1.X + t * (p2.X - p1.X);
                }
            }

            // 排序交点，成对填充
            _crossingsBuffer.AsSpan(0, crossingsCount).Sort();
            var rowBase = py * obsW * 16;
            for (var k = 0; k < crossingsCount - 1; k += 2)
            {
                var xStart = FluidCoordUtils.WorldToPixelMinX(_crossingsBuffer[k], domainCenter, worldSize, resolution);
                var xEnd = FluidCoordUtils.WorldToPixelMaxX(_crossingsBuffer[k + 1], domainCenter, worldSize,
                    resolution);
                for (var px = xStart; px < xEnd; px++)
                    if (px >= 0 && px < obsW)
                    {
                        var offset = rowBase + px * 16;
                        var worldX = FluidCoordUtils.PixelToWorldX(px, resolution, worldSize, domainCenter);
                        // 计算旋转速度：v = linearVelocity + angularVelocity × r
                        var r = new Vector2(worldX - obstacleCenter.X, worldY - obstacleCenter.Y);
                        var vx = linearVelocity.X + -angularVelocity * r.Y;
                        var vy = linearVelocity.Y + angularVelocity * r.X;
                        MarkObstaclePixel(obsData, offset, vx, vy);
                    }
            }
        }
    }

    /// <summary>将矩形区域绘制为静态障碍物（速度为零）。</summary>
    /// <param name="worldMin">矩形最小角的世界坐标。</param>
    /// <param name="worldMax">矩形最大角的世界坐标。</param>
    /// <param name="obsData">障碍物纹理数据。</param>
    /// <param name="obsW">障碍物纹理宽度（像素）。</param>
    /// <param name="obsH">障碍物纹理高度（像素）。</param>
    /// <param name="domainCenter">模拟域中心的世界坐标。</param>
    /// <param name="worldSize">模拟域的世界尺寸。</param>
    /// <param name="resolution">像素分辨率（每个像素对应的世界尺寸）。</param>
    public static void FillRect(
        Vector2 worldMin, Vector2 worldMax,
        Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var pxMin = FluidCoordUtils.WorldToPixelMin(worldMin, domainCenter, worldSize, resolution);
        var pxMax = FluidCoordUtils.WorldToPixelMax(worldMax, domainCenter, worldSize, resolution);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(obsW, obsH));
        for (var y = pxMin.Y; y < pxMax.Y; y++)
        {
            var rowBase = y * obsW * 16;
            for (var x = pxMin.X; x < pxMax.X; x++)
            {
                var offset = rowBase + x * 16;
                MarkObstaclePixel(obsData, offset);
            }
        }
    }

    /// <summary>
    ///     将矩形区域绘制为运动障碍物，编码线速度和角速度。速度计算方式同 FillPolygon 的运动版本。
    /// </summary>
    /// <param name="worldMin">矩形最小角的世界坐标。</param>
    /// <param name="worldMax">矩形最大角的世界坐标。</param>
    /// <param name="obsData">障碍物纹理数据。</param>
    /// <param name="obsW">障碍物纹理宽度（像素）。</param>
    /// <param name="obsH">障碍物纹理高度（像素）。</param>
    /// <param name="domainCenter">模拟域中心的世界坐标。</param>
    /// <param name="worldSize">模拟域的世界尺寸。</param>
    /// <param name="resolution">像素分辨率（每个像素对应的世界尺寸）。</param>
    /// <param name="linearVelocity">障碍物的线速度。</param>
    /// <param name="angularVelocity">障碍物的角速度（弧度/秒）。</param>
    /// <param name="obstacleCenter">障碍物中心的世界坐标，用于计算旋转速度。</param>
    public static void FillRect(
        Vector2 worldMin, Vector2 worldMax,
        Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution,
        Vector2 linearVelocity, float angularVelocity, Vector2 obstacleCenter)
    {
        var pxMin = FluidCoordUtils.WorldToPixelMin(worldMin, domainCenter, worldSize, resolution);
        var pxMax = FluidCoordUtils.WorldToPixelMax(worldMax, domainCenter, worldSize, resolution);
        pxMin = pxMin.Max(Vector2I.Zero);
        pxMax = pxMax.Min(new Vector2I(obsW, obsH));
        for (var y = pxMin.Y; y < pxMax.Y; y++)
        {
            var worldY = FluidCoordUtils.PixelToWorldY(y, resolution, worldSize, domainCenter);
            var rowBase = y * obsW * 16;
            for (var x = pxMin.X; x < pxMax.X; x++)
            {
                var worldX = FluidCoordUtils.PixelToWorldX(x, resolution, worldSize, domainCenter);
                // 计算旋转速度：v = linearVelocity + angularVelocity × r
                var r = new Vector2(worldX - obstacleCenter.X, worldY - obstacleCenter.Y);
                var vx = linearVelocity.X + -angularVelocity * r.Y;
                var vy = linearVelocity.Y + angularVelocity * r.X;
                var offset = rowBase + x * 16;
                MarkObstaclePixel(obsData, offset, vx, vy);
            }
        }
    }

    /// <summary>
    ///     判断点是否在多边形内部，使用射线法（Ray Casting）。从点向右发射水平射线，
    ///     统计与多边形边的交叉次数，奇数次则在内部。
    /// </summary>
    /// <param name="point">待检测的世界坐标点。</param>
    /// <param name="polygon">多边形顶点数组（无需闭合，首尾自动连接）。</param>
    /// <returns>点在多边形内部返回 true，否则返回 false。</returns>
    public static bool IsPointInPolygon(Vector2 point, Vector2[] polygon)
    {
        var inside = false;
        var n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
            if (polygon[i].Y > point.Y != polygon[j].Y > point.Y &&
                point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) /
                (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;

        return inside;
    }
}