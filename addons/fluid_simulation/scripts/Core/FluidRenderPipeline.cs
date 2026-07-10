using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace FluidSimulation;

/// <summary>
///     流体模拟渲染管线，负责执行所有 GPU 计算着色器调度步骤。
///     <para>
///         WFS 管线顺序：
///         批量队列 → 域偏移 → 力发射器 → 外部输入 → Splat → 障碍物力 →
///         Curl → Vorticity → Divergence → ClearPressure → Pressure(Jacobi) →
///         GradientSubtract → Boundary → AdvectVelocity → AdvectDye →
///         障碍物纹理复制 → Display。
///     </para>
/// </summary>
public class FluidRenderPipeline
{
    private readonly byte[] _pc16 = new byte[16];
    private readonly byte[] _pc32 = new byte[32];
    private readonly byte[] _pc48 = new byte[48];
    private readonly Rid[] _uniforms = new Rid[4];
    private GPUResourceManager _gpu;
    private int _uniformCount;
    private int _simXGroups, _simYGroups, _dyeXGroups, _dyeYGroups;

    private static void WritePushConstant<T>(in T value, Span<byte> target) where T : unmanaged
    {
        MemoryMarshal.Write(target, in value);
    }

    /// <summary>
    ///     初始化渲染管线，绑定 GPU 资源管理器和计算调度组大小。
    /// </summary>
    internal void Initialize(GPUResourceManager gpu, int simXGroups, int simYGroups, int dyeXGroups, int dyeYGroups)
    {
        _gpu = gpu;
        _simXGroups = simXGroups;
        _simYGroups = simYGroups;
        _dyeXGroups = dyeXGroups;
        _dyeYGroups = dyeYGroups;
    }

    /// <summary>
    ///     执行完整的流体模拟渲染管线（WFS 架构），按固定顺序调度所有计算着色器步骤。
    /// </summary>
    /// <param name="dt">帧时间间隔（秒）。</param>
    /// <param name="sim">流体模拟实例，提供物理参数、管线和绘制队列。</param>
    internal void Execute(float dt, FluidSimulation2D sim)
    {
        ProcessBatchQueue(sim);
        ProcessDomainShift(sim);

        // IMPORTANT: ApplyExternalInputs must run BEFORE ApplySplatRequests!
        // Both operations use TexIdTempDye as output buffer and call SwapTexColorDye().
        // If SplatRequests runs first, ExternalInputs will overwrite its results.
        ApplyForceEmitters(sim);
        ApplyExternalInputs(sim);
        ApplySplatRequests(sim);

        ApplyObstacleForce(sim, dt);
        ComputeCurl(sim);
        ApplyVorticity(sim, dt);
        ComputeDivergence(sim);
        ClearPressure(sim);
        SolvePressure(sim);
        SubtractPressureGradient(sim);
        ApplyBoundary(sim);
        AdvectVelocity(sim, dt);
        AdvectDye(sim, dt);
        CopyObstacleTexture(sim);
        sim.FluidDomainOffset = Vector2.Zero;

        // Post-processing
        var outputTex = _gpu.TexIdColor;
        if (sim.EnableShading)
        {
            Display(sim, outputTex);
            outputTex = _gpu.TexIdDisplayOutput;
        }

        if (sim.OutputTexture != null) sim.OutputTexture.TextureRdRid = outputTex;
    }

    // ======================== 纹理交换（乒乓缓冲） ========================

    /// <summary>交换速度场纹理和临时纹理的 RID，实现乒乓缓冲模式。</summary>
    private void SwapTexVelocity()
    {
        (_gpu.TexIdVelocity, _gpu.TexIdTemp) = (_gpu.TexIdTemp, _gpu.TexIdVelocity);
    }

    /// <summary>交换压力场纹理和临时纹理的 RID，实现乒乓缓冲模式。</summary>
    private void SwapTexPressure()
    {
        (_gpu.TexIdPressure, _gpu.TexIdTemp) = (_gpu.TexIdTemp, _gpu.TexIdPressure);
    }

    /// <summary>交换颜色场纹理和临时纹理的 RID，实现乒乓缓冲模式。</summary>
    private void SwapTexColor()
    {
        (_gpu.TexIdColor, _gpu.TexIdTemp) = (_gpu.TexIdTemp, _gpu.TexIdColor);
    }

    /// <summary>交换颜色场纹理和染料临时纹理的 RID，实现颜色场的乒乓缓冲模式（DyeResolution）。</summary>
    private void SwapTexColorDye()
    {
        (_gpu.TexIdColor, _gpu.TexIdTempDye) = (_gpu.TexIdTempDye, _gpu.TexIdColor);
    }

    // ======================== GPU 计算调度 ========================

    /// <summary>
    ///     执行一次 GPU 计算着色器调度（单前置采样器）。封装完整的绑定流程：
    ///     1. 创建并绑定 Uniform Set（采样器纹理 + 图像纹理 + 尾部采样器纹理）
    ///     2. 开始计算列表 → 绑定管线和 Uniform Set → 设置 Push Constants → 调度 → 结束
    /// </summary>
    private void RunCompute(ComputePipeline pipeline, Rid samplerTexture, Rid[] imageTextures, byte[] pushConstants,
        uint pushConstantSize, Rid[] trailingSamplers = null)
        => RunCompute(pipeline, samplerTexture, imageTextures, pushConstants, pushConstantSize, _simXGroups, _simYGroups, trailingSamplers);

    private void RunCompute(ComputePipeline pipeline, Rid samplerTexture, Rid[] imageTextures, byte[] pushConstants,
        uint pushConstantSize, int xGroups, int yGroups, Rid[] trailingSamplers = null)
    {
        _uniformCount = 0;

        if (samplerTexture.IsValid)
            _uniforms[_uniformCount++] =
                _gpu.CreateSamplerUniformSetCached(pipeline, samplerTexture, (uint)(_uniformCount - 1));

        foreach (var t in imageTextures)
            _uniforms[_uniformCount++] = _gpu.CreateUniformSetCached(pipeline, t, (uint)(_uniformCount - 1));

        if (trailingSamplers != null)
            foreach (var t in trailingSamplers)
                _uniforms[_uniformCount++] = _gpu.CreateSamplerUniformSetCached(pipeline, t, (uint)(_uniformCount - 1));

        var computeList = _gpu.Device.ComputeListBegin();
        _gpu.Device.ComputeListBindComputePipeline(computeList, pipeline.PipelineId);
        for (var i = 0; i < _uniformCount; i++)
            _gpu.Device.ComputeListBindUniformSet(computeList, _uniforms[i], (uint)i);
        _gpu.Device.ComputeListSetPushConstant(computeList, pushConstants, pushConstantSize);
        _gpu.Device.ComputeListDispatch(computeList, (uint)xGroups, (uint)yGroups, 1);
        _gpu.Device.ComputeListEnd();
    }

    /// <summary>
    ///     执行一次 GPU 计算着色器调度（多前置采样器）。用于 WFS 中 vorticity/jacobi/subtract/advect 等
    ///     需要多个 sampler2D 输入后再跟 image2D 输出的 shader。
    ///     绑定顺序：N 个前置采样器 → 1 个图像 → M 个尾部采样器。
    /// </summary>
    private void RunComputeSamplers(ComputePipeline pipeline, Rid[] leadingSamplers, Rid imageTexture,
        byte[] pushConstants, uint pushConstantSize, Rid[] trailingSamplers = null)
        => RunComputeSamplers(pipeline, leadingSamplers, imageTexture, pushConstants, pushConstantSize,
           _simXGroups, _simYGroups, trailingSamplers);

    private void RunComputeSamplers(ComputePipeline pipeline, Rid[] leadingSamplers, Rid imageTexture,
        byte[] pushConstants, uint pushConstantSize, int xGroups, int yGroups, Rid[] trailingSamplers = null)
    {
        _uniformCount = 0;
        foreach (var t in leadingSamplers)
            _uniforms[_uniformCount++] = _gpu.CreateSamplerUniformSetCached(pipeline, t, (uint)(_uniformCount - 1));
        _uniforms[_uniformCount++] = _gpu.CreateUniformSetCached(pipeline, imageTexture, (uint)(_uniformCount - 1));
        if (trailingSamplers != null)
            foreach (var t in trailingSamplers)
                _uniforms[_uniformCount++] = _gpu.CreateSamplerUniformSetCached(pipeline, t, (uint)(_uniformCount - 1));

        var computeList = _gpu.Device.ComputeListBegin();
        _gpu.Device.ComputeListBindComputePipeline(computeList, pipeline.PipelineId);
        for (var i = 0; i < _uniformCount; i++)
            _gpu.Device.ComputeListBindUniformSet(computeList, _uniforms[i], (uint)i);
        _gpu.Device.ComputeListSetPushConstant(computeList, pushConstants, pushConstantSize);
        _gpu.Device.ComputeListDispatch(computeList, (uint)xGroups, (uint)yGroups, 1);
        _gpu.Device.ComputeListEnd();
    }

    // ======================== 渲染步骤方法 ========================

    /// <summary>
    ///     处理批量绘制队列。全部退化为逐点 QueueDraw，由 ApplySplatRequests 统一处理。
    ///     处理完成后清空所有批量缓冲。
    /// </summary>
    private void ProcessBatchQueue(FluidSimulation2D sim)
    {
        for (var i = 0; i < sim.BatchPoints.Count; i++)
            sim.QueueDraw(sim.BatchPoints[i], sim.BatchColors[i], sim.BatchVelocities[i],
                sim.BatchRadii[i], sim.BatchRadii[i]);

        sim.BatchPoints.Clear();
        sim.BatchColors.Clear();
        sim.BatchVelocities.Clear();
        sim.BatchRadii.Clear();
    }

    /// <summary>
    ///     处理流体域偏移和障碍物纹理上传。
    ///     当障碍物脏标记为 true 时上传障碍物纹理；当域偏移量大于阈值时，
    ///     对颜色场和速度场执行纹理平移操作，保持流体跟随摄像机移动。
    /// </summary>
    private void ProcessDomainShift(FluidSimulation2D sim)
    {
        if (sim.ObstacleDirty && sim.CachedObstacleData != null)
        {
            _gpu.Device.TextureUpdate(_gpu.TexIdObstacle, 0, sim.CachedObstacleData);
            sim.ObstacleDirty = false;
        }

        if (sim.FluidDomainOffset.LengthSquared() > 0.000001f)
        {
            // Shift color texture at DyeResolution
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.DyeResolution, pc);
            WritePushConstant(in sim.FluidDomainOffset, pc[8..]);

            RunCompute(_gpu.ShiftTexturePipeline, _gpu.TexIdColor, [_gpu.TexIdTempDye], _pc16, 16, _dyeXGroups, _dyeYGroups);
            SwapTexColorDye();

            // Shift velocity texture at SimulationResolution
            pc = _pc16.AsSpan();
            WritePushConstant(in sim.SimulationResolution, pc);
            WritePushConstant(in sim.FluidDomainOffset, pc[8..]);

            RunCompute(_gpu.ShiftTexturePipeline, _gpu.TexIdVelocity, [_gpu.TexIdTemp], _pc16, 16);
            SwapTexVelocity();
        }
    }

    /// <summary>
    ///     处理绘制请求队列，将所有 DrawRequest 逐个执行 Splat 操作。
    ///     使用统一的 SplatPipeline（WFS splatShader），velocity 和 color 都用同一 shader。
    ///     每个请求先注入速度场再注入颜色场，完成后清空队列。
    /// </summary>
    private void ApplySplatRequests(FluidSimulation2D sim)
    {
        var simAspect = sim.SimulationResolution.X / sim.SimulationResolution.Y;
        var dyeAspect = sim.DyeResolution.X / sim.DyeResolution.Y;
        var splatRadius = sim.SplatRadius / 100.0f; // WFS: SPLAT_RADIUS / 100

        for (var ri = 0; ri < sim.DrawRequestCount; ri++)
        {
            var req = sim.DrawRequests[ri];

            // Velocity splat — SplatPipeline at SimResolution
            var velRadius = splatRadius * req.VelocityRadius;
            var velColor = new Color(req.Velocity.X, req.Velocity.Y, 0.0f, 0.0f);
            var pc = _pc48.AsSpan();
            WritePushConstant(in velColor, pc);                       // vec4 color (16B)
            WritePushConstant(in sim.SimulationResolution, pc[16..]); // vec2 size (8B)
            WritePushConstant(in req.Position, pc[24..]);             // vec2 point (8B)
            WritePushConstant(in velRadius, pc[32..]);                // float radius (4B)
            WritePushConstant(in simAspect, pc[36..]);                // float aspectRatio (4B)
            RunCompute(_gpu.SplatPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp], _pc48, 40);
            SwapTexVelocity();

            // Color splat — SplatPipeline at DyeResolution, position is UV [0,1]
            var colRadius = splatRadius * req.ColorRadius;
            pc = _pc48.AsSpan();
            WritePushConstant(in req.Color, pc);                  // vec4 color (16B)
            WritePushConstant(in sim.DyeResolution, pc[16..]);    // vec2 size (8B)
            WritePushConstant(in req.Position, pc[24..]);         // vec2 point (8B)
            WritePushConstant(in colRadius, pc[32..]);            // float radius (4B)
            WritePushConstant(in dyeAspect, pc[36..]);            // float aspectRatio (4B)
            RunCompute(_gpu.SplatPipeline, new Rid(), [_gpu.TexIdColor, _gpu.TexIdTempDye], _pc48, 40,
                _dyeXGroups, _dyeYGroups);
            SwapTexColorDye();
        }

        sim.DrawRequestCount = 0;
    }

    /// <summary>
    ///     应用 GPU 力发射器。读取所有活跃发射器的参数，通过 Storage Buffer 传入 GPU，
    ///     并行计算力场并直接叠加到速度场。完全替代了旧的 CPU 逐像素路径。
    ///     只在有活跃发射器时才会调度 GPU 计算。
    /// </summary>
    private void ApplyForceEmitters(FluidSimulation2D sim)
    {
        var count = sim.ForceEmittersForRender.Count;
        if (count == 0)
            return;

        var data = _gpu.ForceEmitterRawData;
        var list = sim.ForceEmittersForRender;
        const int stride = 48;

        for (var i = 0; i < count; i++)
        {
            var e = list[i];
            var offset = i * stride;
            var span = data.AsSpan(offset, stride);

            MemoryMarshal.Write(span[..4], in e.CenterX);
            MemoryMarshal.Write(span[4..8], in e.CenterY);
            MemoryMarshal.Write(span[8..12], in e.ForceX);
            MemoryMarshal.Write(span[12..16], in e.ForceY);
            MemoryMarshal.Write(span[16..20], in e.ShapeSizeX);
            MemoryMarshal.Write(span[20..24], in e.ShapeSizeY);
            MemoryMarshal.Write(span[24..28], in e.ForceRadius);
            MemoryMarshal.Write(span[28..32], in e.FalloffExponent);
            MemoryMarshal.Write(span[32..36], in e.SwirlStrength);
            MemoryMarshal.Write(span[36..40], in e.ForcePattern);
            MemoryMarshal.Write(span[40..44], in e.EmissionShape);
            // offset 44: _pad (unused, stays 0)
        }

        _gpu.Device.BufferUpdate(_gpu.ForceEmitterBuffer, 0, (uint)(count * stride), data);

        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        WritePushConstant(in count, pc[8..]);
        // pc[12..16] is _pad, already zero

        var u0 = _gpu.CreateUniformSet(_gpu.ApplyForceEmitterPipeline, _gpu.TexIdVelocity, 0);
        var u1 = _gpu.CreateUniformSet(_gpu.ApplyForceEmitterPipeline, _gpu.TexIdTemp, 1);
        var u2 = _gpu.CreateSamplerUniformSet(_gpu.ApplyForceEmitterPipeline, _gpu.TexIdObstacle, 2);
        var u3 = _gpu.CreateStorageBufferUniformSet(_gpu.ApplyForceEmitterPipeline, _gpu.ForceEmitterBuffer, 0, 3);

        var computeList = _gpu.Device.ComputeListBegin();
        _gpu.Device.ComputeListBindComputePipeline(computeList, _gpu.ApplyForceEmitterPipeline.PipelineId);
        _gpu.Device.ComputeListBindUniformSet(computeList, u0, 0);
        _gpu.Device.ComputeListBindUniformSet(computeList, u1, 1);
        _gpu.Device.ComputeListBindUniformSet(computeList, u2, 2);
        _gpu.Device.ComputeListBindUniformSet(computeList, u3, 3);
        _gpu.Device.ComputeListSetPushConstant(computeList, _pc16, 16);
        _gpu.Device.ComputeListDispatch(computeList, (uint)_simXGroups, (uint)_simYGroups, 1);
        _gpu.Device.ComputeListEnd();

        _gpu.Device.FreeRid(u0);
        _gpu.Device.FreeRid(u1);
        _gpu.Device.FreeRid(u2);
        _gpu.Device.FreeRid(u3);
        SwapTexVelocity();
    }

    /// <summary>
    ///     应用外部输入的力场和颜色场到流体模拟中。
    ///     仅在对应脏标记为 true 时执行纹理上传和计算着色器调度，
    ///     避免每帧重复上传未变化的输入数据。
    /// </summary>
    private void ApplyExternalInputs(FluidSimulation2D sim)
    {
        if (!sim.InputForcesDirty && !sim.InputColorsDirty)
            return;

        if (sim.InputForcesDirty)
        {
            _gpu.Device.TextureUpdate(_gpu.TexIdInputForces, 0, sim.InputForcesImg.GetData());
            {
                var pc = _pc16.AsSpan();
                WritePushConstant(in sim.SimulationResolution, pc);
                pc[8..].Clear();
            }
            RunCompute(_gpu.ApplyForcesPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdInputForces, _gpu.TexIdTemp],
                _pc16, 8, [_gpu.TexIdObstacle]);
            SwapTexVelocity();
            sim.InputForcesDirty = false;
        }

        if (sim.InputColorsDirty)
        {
            _gpu.Device.TextureUpdate(_gpu.TexIdInputColors, 0, sim.InputColorsImg.GetData());
            {
                var pc = _pc16.AsSpan();
                WritePushConstant(in sim.DyeResolution, pc);
            }
            RunCompute(_gpu.ApplyColorsPipeline, new Rid(), [_gpu.TexIdColor, _gpu.TexIdInputColors, _gpu.TexIdTempDye],
                _pc16, 8, _dyeXGroups, _dyeYGroups, [_gpu.TexIdObstacle]);
            SwapTexColorDye();
            sim.InputColorsDirty = false;
        }
    }

    /// <summary>
    ///     应用障碍物对流体的排斥力。
    ///     通过比较当前帧和上一帧的障碍物纹理，计算障碍物对速度场的排斥力，
    ///     阻止流体穿透运动中的障碍物。
    /// </summary>
    private void ApplyObstacleForce(FluidSimulation2D sim, float dt)
    {
        {
            var pc = _pc32.AsSpan();
            WritePushConstant(in sim.SimulationResolution, pc);
            WritePushConstant(in sim.ObstacleForceStrength, pc[8..]);
            WritePushConstant(in dt, pc[12..]);
            WritePushConstant(in sim.FluidDomainOffset, pc[16..]);
            pc[24..].Clear();
        }
        RunCompute(_gpu.ObstacleForcePipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp],
            _pc32, 24, [_gpu.TexIdObstacle, _gpu.TexIdObstaclePre]);
        SwapTexVelocity();
    }

    /// <summary>
    ///     计算 Curl（涡度场）。WFS curlShader 的 compute 版本，4-point stencil。
    ///     输出存储到 TexIdCurl，供 ApplyVorticity 使用。
    /// </summary>
    private void ComputeCurl(FluidSimulation2D sim)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        // set0=sampler2D(velocity), set1=image2D(curl_out)
        RunCompute(_gpu.CurlPipeline, _gpu.TexIdVelocity, [_gpu.TexIdCurl], _pc16, 8);
    }

    /// <summary>
    ///     涡度增强（Vorticity Confinement）。读取 curl 纹理，WFS 4-point stencil。
    ///     仅在 EnableVorticity 为 true 时执行。
    /// </summary>
    private void ApplyVorticity(FluidSimulation2D sim, float dt)
    {
        if (!sim.EnableVorticity) return;
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        WritePushConstant(in sim.Curl, pc[8..]);
        WritePushConstant(in dt, pc[12..]);
        // set0=sampler2D(velocity), set1=sampler2D(curl), set2=image2D(output)
        RunComputeSamplers(_gpu.VorticityPipeline, [_gpu.TexIdVelocity, _gpu.TexIdCurl], _gpu.TexIdTemp, _pc16, 16);
        SwapTexVelocity();
    }

    /// <summary>
    ///     计算速度场的散度，用于后续压力泊松方程求解。
    ///     WFS divergenceShader + inline boundary + obstacle reflect boundary。
    /// </summary>
    private void ComputeDivergence(FluidSimulation2D sim)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        // set0=sampler2D(velocity), set1=image2D(divergence), set2=sampler2D(obstacle)
        RunCompute(_gpu.DivergencePipeline, _gpu.TexIdVelocity, [_gpu.TexIdDivergence], _pc16, 8, [_gpu.TexIdObstacle]);
    }

    /// <summary>
    ///     压力衰减。WFS clearShader，每帧将压力场乘以 PRESSURE 系数（默认 0.8），
    ///     实现压力残留衰减，避免压力无限累积。
    /// </summary>
    private void ClearPressure(FluidSimulation2D sim)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        WritePushConstant(in sim.Pressure, pc[8..]);
        // set0=sampler2D(pressure_read), set1=image2D(pressure_write)
        RunCompute(_gpu.ClearPipeline, _gpu.TexIdPressure, [_gpu.TexIdTemp], _pc16, 12);
        SwapTexPressure();
    }

    /// <summary>
    ///     Jacobi 迭代求解压力场（压力泊松方程）。WFS pressureShader：
    ///     pressure = (L + R + B + T - divergence) * 0.25
    ///     迭代次数由 PressureIterations 控制。
    /// </summary>
    private void SolvePressure(FluidSimulation2D sim)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        for (var i = 0; i < sim.PressureIterations; i++)
        {
            // set0=sampler2D(pressure), set1=sampler2D(divergence), set2=image2D(output), set3=sampler2D(obstacle)
            RunComputeSamplers(_gpu.JacobiPipeline, [_gpu.TexIdPressure, _gpu.TexIdDivergence], _gpu.TexIdTemp,
                _pc16, 8, [_gpu.TexIdObstacle]);
            SwapTexPressure();
        }
    }

    /// <summary>
    ///     从速度场中减去压力梯度，以满足不可压缩条件。
    ///     WFS gradientSubtractShader: velocity -= vec2(R - L, T - B)
    /// </summary>
    private void SubtractPressureGradient(FluidSimulation2D sim)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);
        // set0=sampler2D(pressure), set1=sampler2D(velocity), set2=image2D(output), set3=sampler2D(obstacle)
        RunComputeSamplers(_gpu.SubtractPipeline, [_gpu.TexIdPressure, _gpu.TexIdVelocity], _gpu.TexIdTemp,
            _pc16, 8, [_gpu.TexIdObstacle]);
        SwapTexVelocity();
    }

    /// <summary>
    ///     边界条件处理。分别对速度场（boundScale = -1）和压力场（boundScale = 1）
    ///     执行边界条件计算，确保流体在障碍物和域边界处满足正确的边界条件。
    /// </summary>
    private void ApplyBoundary(FluidSimulation2D sim)
    {
        var boundScale = -1.0f;
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.SimulationResolution, pc);
            WritePushConstant(in boundScale, pc[8..]);
            pc[12..].Clear();
        }
        RunCompute(_gpu.BoundaryPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp], _pc16, 12,
            [_gpu.TexIdObstacle]);
        SwapTexVelocity();

        boundScale = 1.0f;
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.SimulationResolution, pc);
            WritePushConstant(in boundScale, pc[8..]);
            pc[12..].Clear();
        }
        RunCompute(_gpu.BoundaryPipeline, new Rid(), [_gpu.TexIdPressure, _gpu.TexIdTemp], _pc16, 12,
            [_gpu.TexIdObstacle]);
        SwapTexPressure();
    }

    /// <summary>
    ///     速度场平流。WFS 单 advection shader，velocitySize = sourceSize = SimulationResolution。
    ///     coord = vUv - dt * velocity * velocityTexel; result /= (1 + dissipation * dt)
    /// </summary>
    private void AdvectVelocity(FluidSimulation2D sim, float dt)
    {
        var pc = _pc32.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);        // velocitySize
        WritePushConstant(in sim.SimulationResolution, pc[8..]);   // sourceSize = velocitySize
        WritePushConstant(in dt, pc[16..]);
        WritePushConstant(in sim.VelocityDissipation, pc[20..]);
        // set0=sampler2D(velocity), set1=sampler2D(source=velocity), set2=image2D(temp), set3=sampler2D(obstacle)
        RunComputeSamplers(_gpu.AdvectPipeline, [_gpu.TexIdVelocity, _gpu.TexIdVelocity], _gpu.TexIdTemp,
            _pc32, 24, [_gpu.TexIdObstacle]);
        SwapTexVelocity();
    }

    /// <summary>
    ///     颜色/染料场平流。WFS 单 advection shader，跨分辨率：
    ///     velocitySize = SimulationResolution，sourceSize = DyeResolution。
    /// </summary>
    private void AdvectDye(FluidSimulation2D sim, float dt)
    {
        var pc = _pc32.AsSpan();
        WritePushConstant(in sim.SimulationResolution, pc);    // velocitySize
        WritePushConstant(in sim.DyeResolution, pc[8..]);      // sourceSize = dyeSize
        WritePushConstant(in dt, pc[16..]);
        WritePushConstant(in sim.DensityDissipation, pc[20..]);
        // set0=sampler2D(velocity), set1=sampler2D(source=dye), set2=image2D(tempDye), set3=sampler2D(obstacle)
        RunComputeSamplers(_gpu.AdvectPipeline, [_gpu.TexIdVelocity, _gpu.TexIdColor], _gpu.TexIdTempDye,
            _pc32, 24, _dyeXGroups, _dyeYGroups, [_gpu.TexIdObstacle]);
        SwapTexColorDye();
    }

    /// <summary>
    ///     将当前帧的障碍物纹理复制到上一帧缓冲中，
    ///     用于下帧的障碍物排斥力计算中检测障碍物变化。
    /// </summary>
    private void CopyObstacleTexture(FluidSimulation2D sim)
    {
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.SimulationResolution, pc);
            pc[8..].Clear();
        }
        RunCompute(_gpu.CopyTextureRgba32fPipeline, new Rid(), [_gpu.TexIdObstacle, _gpu.TexIdObstaclePre], _pc16, 8);
    }

    // ======================== 后处理步骤 ========================

    /// <summary>
    ///     WFS displayShader（仅 shading）。从染料场 RGB 的 length 梯度计算法线，
    ///     添加漫反射光照：diffuse = clamp(dot(n, l) + 0.7, 0.7, 1.0)。
    /// </summary>
    private void Display(FluidSimulation2D sim, Rid inputTex)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.DyeResolution, pc);
        // set0=sampler2D(dye), set1=image2D(display_output)
        RunCompute(_gpu.DisplayPipeline, inputTex, [_gpu.TexIdDisplayOutput], _pc16, 8, _dyeXGroups, _dyeYGroups);
    }
}
