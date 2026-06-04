using System;
using Godot;

namespace FluidSimulation;

/// <summary>
///     TileMap 障碍物绘制器
///     负责将 TileMapLayer 中的瓦片绘制到流体障碍物纹理中。
///     支持两种过滤模式（仅碰撞瓦片/所有非空瓦片）和两种填充精度（整格/多边形）。
/// </summary>
internal class TileMapObstacleDrawer
{
    /// <summary>瓦片填充精度。</summary>
    internal TileFillMode FillMode = TileFillMode.WholeCell;

    /// <summary>瓦片障碍物过滤模式。</summary>
    internal TileObstacleMode ObstacleMode = TileObstacleMode.CollisionOnly;

    /// <summary>TileSet 物理层索引。</summary>
    internal int PhysicsLayerIndex;

    /// <summary>
    ///     将 TileMapLayer 中符合条件的瓦片绘制到障碍物纹理数据中。
    /// </summary>
    internal void Draw(TileMapLayer tileMapLayer, Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var tileSet = tileMapLayer.TileSet;
        if (tileSet == null) return;

        var usedCells = tileMapLayer.GetUsedCells();
        var tileSize = tileSet.GetTileSize();
        var gt = tileMapLayer.GlobalTransform;
        var physLayerCount = tileSet.GetPhysicsLayersCount();
        var validPhysLayer = PhysicsLayerIndex >= 0 && PhysicsLayerIndex < physLayerCount;

        foreach (var cell in usedCells)
        {
            var tileData = tileMapLayer.GetCellTileData(cell);
            if (tileData == null) continue;

            if (ObstacleMode == TileObstacleMode.CollisionOnly)
            {
                if (!validPhysLayer) continue;
                if (tileData.GetCollisionPolygonsCount(PhysicsLayerIndex) <= 0) continue;
            }

            var cellLocalPos = tileMapLayer.MapToLocal(cell);

            if (FillMode == TileFillMode.WholeCell || !validPhysLayer)
            {
                DrawTileCell(gt, cellLocalPos, tileSize, obsData, obsW, obsH,
                    domainCenter, worldSize, resolution);
            }
            else
            {
                var polygonCount = tileData.GetCollisionPolygonsCount(PhysicsLayerIndex);
                for (var p = 0; p < polygonCount; p++)
                {
                    var points = tileData.GetCollisionPolygonPoints(PhysicsLayerIndex, p);
                    if (points.Length < 3) continue;
                    DrawTilePolygon(gt, cellLocalPos, points, obsData, obsW, obsH,
                        domainCenter, worldSize, resolution);
                }
            }
        }
    }

    /// <summary>
    ///     将单个瓦片格绘制为矩形障碍物，使用 PolygonRasterizer.FillRect 填充整个瓦片区域。
    /// </summary>
    /// <param name="gt">TileMapLayer 的全局变换。</param>
    /// <param name="cellLocalPos">瓦片在 TileMap 坐标系中的局部位置。</param>
    /// <param name="tileSize">瓦片尺寸（像素）。</param>
    /// <param name="obsData">障碍物纹理数据。</param>
    /// <param name="obsW">障碍物纹理宽度。</param>
    /// <param name="obsH">障碍物纹理高度。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域的世界尺寸。</param>
    /// <param name="resolution">流体模拟分辨率。</param>
    private static void DrawTileCell(Transform2D gt, Vector2 cellLocalPos, Vector2I tileSize,
        Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var halfSize = new Vector2(tileSize.X, tileSize.Y) / 2.0f;
        var worldMin = gt * (cellLocalPos - halfSize);
        var worldMax = gt * (cellLocalPos + halfSize);
        var min = new Vector2(Mathf.Min(worldMin.X, worldMax.X), Mathf.Min(worldMin.Y, worldMax.Y));
        var max = new Vector2(Mathf.Max(worldMin.X, worldMax.X), Mathf.Max(worldMin.Y, worldMax.Y));
        PolygonRasterizer.FillRect(min, max, obsData, obsW, obsH, domainCenter, worldSize, resolution);
    }

    /// <summary>
    ///     将瓦片的碰撞多边形绘制为障碍物，使用 PolygonRasterizer.FillPolygon 精确绘制多边形轮廓。将局部坐标的碰撞多边形顶点转换为世界坐标后进行光栅化。
    /// </summary>
    /// <param name="gt">TileMapLayer 的全局变换。</param>
    /// <param name="cellLocalPos">瓦片在 TileMap 坐标系中的局部位置。</param>
    /// <param name="localPoints">碰撞多边形在瓦片局部坐标系中的顶点数组。</param>
    /// <param name="obsData">障碍物纹理数据。</param>
    /// <param name="obsW">障碍物纹理宽度。</param>
    /// <param name="obsH">障碍物纹理高度。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域的世界尺寸。</param>
    /// <param name="resolution">流体模拟分辨率。</param>
    private static void DrawTilePolygon(Transform2D gt, Vector2 cellLocalPos, Vector2[] localPoints,
        Span<byte> obsData, int obsW, int obsH,
        Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var worldPoints = new Vector2[localPoints.Length];
        for (var i = 0; i < localPoints.Length; i++)
            worldPoints[i] = gt * (cellLocalPos + localPoints[i]);

        PolygonRasterizer.FillPolygon(worldPoints, obsData, obsW, obsH, domainCenter, worldSize, resolution);
    }
}