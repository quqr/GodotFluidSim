namespace FluidSimulation;

/// <summary>
///     瓦片障碍物过滤模式
///     控制哪些瓦片被标记为流体障碍物
/// </summary>
public enum TileObstacleMode
{
    /// <summary>所有非空瓦片：任何有内容的瓦片都会被标记为障碍物</summary>
    AllNonEmpty,

    /// <summary>仅碰撞瓦片：只有包含碰撞多边形的瓦片才会被标记为障碍物</summary>
    CollisionOnly
}