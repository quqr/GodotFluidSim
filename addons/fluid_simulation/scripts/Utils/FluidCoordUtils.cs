using Godot;

namespace FluidSimulation;

/// <summary>
///     流体模拟坐标转换工具类。
///     提供世界坐标、UV 坐标、像素坐标之间的相互转换，
///     消除 FluidSimulation2D 与 FluidObstacleDrawer 之间的代码重复。
/// </summary>
public static class FluidCoordUtils
{
    /// <summary>
    ///     将世界坐标转换为流体模拟的像素坐标。
    ///     以 domainCenter 为中心，将世界坐标映射到 [0, resolution] 范围内。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="fluidWorldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <returns>对应的流体模拟像素坐标。</returns>
    public static Vector2 WorldToFluidPos(Vector2 worldPos, Vector2 domainCenter, Vector2 fluidWorldSize,
        Vector2 resolution)
    {
        var localPos = worldPos - domainCenter;
        var uv = new Vector2(
            localPos.X / fluidWorldSize.X + 0.5f,
            localPos.Y / fluidWorldSize.Y + 0.5f
        );
        return uv * resolution;
    }

    /// <summary>
    ///     将世界坐标转换为 UV 坐标。
    ///     UV 坐标原点在左下角 (0,0)，右上角为 (1,1)。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <returns>对应的 UV 坐标。</returns>
    public static Vector2 WorldToUV(Vector2 worldPos, Vector2 domainCenter, Vector2 worldSize)
    {
        var localPos = worldPos - domainCenter;
        return new Vector2(
            localPos.X / worldSize.X + 0.5f,
            localPos.Y / worldSize.Y + 0.5f
        );
    }

    /// <summary>
    ///     将世界坐标转换为像素坐标（截断取整）。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <returns>对应的像素坐标（截断取整）。</returns>
    public static Vector2I WorldToPixel(Vector2 worldPos, Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var uv = WorldToUV(worldPos, domainCenter, worldSize);
        return new Vector2I((int)(uv.X * resolution.X), (int)(uv.Y * resolution.Y));
    }

    /// <summary>
    ///     将世界坐标转换为像素坐标的最小边界（向上取整 -0.5）。
    ///     用于获取障碍物覆盖的最小像素坐标。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <returns>像素坐标的最小边界。</returns>
    public static Vector2I WorldToPixelMin(Vector2 worldPos, Vector2 domainCenter, Vector2 worldSize,
        Vector2 resolution)
    {
        var uv = WorldToUV(worldPos, domainCenter, worldSize);
        return new Vector2I(Mathf.CeilToInt(uv.X * resolution.X - 0.5f), Mathf.CeilToInt(uv.Y * resolution.Y - 0.5f));
    }

    /// <summary>
    ///     将世界坐标转换为像素坐标的最大边界（向下取整 +1）。
    ///     用于获取障碍物覆盖的最大像素坐标（不含）。
    /// </summary>
    /// <param name="worldPos">世界空间坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <returns>像素坐标的最大边界（不含）。</returns>
    public static Vector2I WorldToPixelMax(Vector2 worldPos, Vector2 domainCenter, Vector2 worldSize,
        Vector2 resolution)
    {
        var uv = WorldToUV(worldPos, domainCenter, worldSize);
        return new Vector2I(Mathf.FloorToInt(uv.X * resolution.X - 0.5f) + 1,
            Mathf.FloorToInt(uv.Y * resolution.Y - 0.5f) + 1);
    }

    /// <summary>
    ///     将像素 Y 坐标转换为世界 Y 坐标。
    /// </summary>
    /// <param name="py">像素 Y 坐标。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <returns>对应的世界 Y 坐标。</returns>
    public static float PixelToWorldY(int py, Vector2 resolution, Vector2 worldSize, Vector2 domainCenter)
    {
        var uvY = py / resolution.Y;
        return (uvY - 0.5f) * worldSize.Y + domainCenter.Y;
    }

    /// <summary>
    ///     将像素 X 坐标转换为世界 X 坐标。
    /// </summary>
    /// <param name="px">像素 X 坐标。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <returns>对应的世界 X 坐标。</returns>
    public static float PixelToWorldX(int px, Vector2 resolution, Vector2 worldSize, Vector2 domainCenter)
    {
        var uvX = px / resolution.X;
        return (uvX - 0.5f) * worldSize.X + domainCenter.X;
    }

    /// <summary>
    ///     将世界 X 坐标转换为最小像素 X 坐标（向上取整 -0.5）。
    /// </summary>
    /// <param name="worldX">世界 X 坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <returns>最小像素 X 坐标。</returns>
    public static int WorldToPixelMinX(float worldX, Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var localX = worldX - domainCenter.X;
        var uvX = localX / worldSize.X + 0.5f;
        return Mathf.CeilToInt(uvX * resolution.X - 0.5f);
    }

    /// <summary>
    ///     将世界 X 坐标转换为最大像素 X 坐标（向下取整 +1）。
    /// </summary>
    /// <param name="worldX">世界 X 坐标。</param>
    /// <param name="domainCenter">流体域中心的世界坐标。</param>
    /// <param name="worldSize">流体域在世界空间中的尺寸。</param>
    /// <param name="resolution">流体模拟的像素分辨率。</param>
    /// <returns>最大像素 X 坐标（不含）。</returns>
    public static int WorldToPixelMaxX(float worldX, Vector2 domainCenter, Vector2 worldSize, Vector2 resolution)
    {
        var localX = worldX - domainCenter.X;
        var uvX = localX / worldSize.X + 0.5f;
        return Mathf.FloorToInt(uvX * resolution.X - 0.5f) + 1;
    }
}