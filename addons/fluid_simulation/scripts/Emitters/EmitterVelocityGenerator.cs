using Godot;

namespace FluidSimulation;

/// <summary>
///     发射器速度生成器，根据发射器的速度模式配置为每个采样点计算发射速度。
///     <para>
///         支持三种速度模式：定向（固定方向）、径向（从中心向外，带衰减）、随机（随机方向）。
///         可选叠加旋转扰动（Swirl），为每个粒子添加切向速度分量产生旋涡效果。
///     </para>
/// </summary>
public static class EmitterVelocityGenerator
{
    /// <summary>
    ///     为一组采样点生成发射速度向量。
    /// </summary>
    /// <param name="points">采样点的世界坐标数组（已转换为流体坐标）。</param>
    /// <param name="emitter">发射器实例，提供速度模式、方向、衰减和旋转配置。</param>
    /// <returns>与 points 等长的速度向量数组。</returns>
    public static Vector2[] Generate(Vector2[] points, FluidEmitter emitter)
    {
        var velocities = new Vector2[points.Length];

        switch (emitter.VelocityPatternType)
        {
            // 定向模式：所有粒子沿固定方向发射
            case VelocityPattern.Directional:
                for (var i = 0; i < points.Length; i++)
                    velocities[i] = emitter.EmitVelocity;
                break;

            // 径向模式：从中心向外发射，速度随距离衰减
            case VelocityPattern.Radial:
                for (var i = 0; i < points.Length; i++)
                {
                    var dir = (points[i] - emitter.GlobalPosition).Normalized();
                    var dist = points[i].DistanceTo(emitter.GlobalPosition);
                    var maxDist = emitter.EmissionShapeType == EmissionShape.Circle ? emitter.ShapeSize.X : 1.0f;
                    var falloff = 1.0f - Mathf.Pow(dist / maxDist, emitter.VelocityFalloff);
                    velocities[i] = dir * emitter.EmitVelocity.Length() * falloff;
                }

                break;

            // 随机模式：随机方向发射
            case VelocityPattern.Random:
                for (var i = 0; i < points.Length; i++)
                {
                    var angle = GD.Randf() * Mathf.Tau;
                    velocities[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * emitter.EmitVelocity.Length();
                }

                break;
        }

        // 叠加旋转扰动：添加切向速度分量
        if (emitter.SwirlStrength > 0.0f)
            for (var i = 0; i < points.Length; i++)
            {
                var dir = (points[i] - emitter.GlobalPosition).Normalized();
                var tangent = new Vector2(-dir.Y, dir.X);
                velocities[i] += tangent * emitter.SwirlStrength;
            }

        return velocities;
    }
}