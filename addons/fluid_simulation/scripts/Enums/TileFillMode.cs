namespace FluidSimulation;

/// <summary>
///     瓦片障碍物填充精度
///     控制瓦片在障碍物纹理中的绘制方式
/// </summary>
public enum TileFillMode
{
    /// <summary>整格填充：将瓦片占据的整个矩形区域标记为障碍物，速度快但精度低</summary>
    WholeCell,

    /// <summary>多边形填充：使用瓦片的碰撞多边形精确绘制障碍物轮廓，精度高但开销略大</summary>
    Polygon
}