using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace FluidSimulation;

/// <summary>
///     流体模拟渲染管线，负责执行所有 GPU 计算着色器调度步骤。
///     <para>
///         从 FluidSimulation2D 中提取的渲染逻辑，按固定顺序编排 15 个模拟计算阶段：
///         批量队列处理 → 域偏移 → Splat → GPU 力发射器 → 外部输入 → 障碍物力 →
///         速度平流 → 扩散 → 涡度增强 → 散度计算 → 压力求解 →
///         压力梯度减法 → 边界条件 → 颜色平流 → 障碍物纹理复制。
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
    private int _xGroups, _yGroups;

    private static void WritePushConstant<T>(in T value, Span<byte> target) where T : unmanaged
    {
        MemoryMarshal.Write(target, in value);
    }

    /// <summary>
    ///     初始化渲染管线，绑定 GPU 资源管理器和计算调度组大小。
    /// </summary>
    /// <param name="gpu">GPU 资源管理器实例，提供渲染设备和纹理资源访问。</param>
    /// <param name="xGroups">X 维度的工作组数量。</param>
    /// <param name="yGroups">Y 维度的工作组数量。</param>
    internal void Initialize(GPUResourceManager gpu, int xGroups, int yGroups)
    {
        _gpu = gpu;
        _xGroups = xGroups;
        _yGroups = yGroups;
    }

    /// <summary>
    ///     执行完整的流体模拟渲染管线，按固定顺序调度所有计算着色器步骤。
    /// </summary>
    /// <param name="dt">帧时间间隔（秒）。</param>
    /// <param name="sim">流体模拟实例，提供物理参数、管线和绘制队列。</param>
    internal void Execute(float dt, FluidSimulation2D sim)
    {
        ProcessBatchQueue(sim);
        ProcessDomainShift(sim);
        var subtractiveVal = sim.SubtractiveMixing ? 1.0f : 0.0f;
        ApplySplatRequests(sim, subtractiveVal);
        ApplyForceEmitters(sim);
        ApplyExternalInputs(sim, subtractiveVal);
        ApplyObstacleForce(sim, dt);
        var rdx = 1.0f / sim.GridScale;
        AdvectVelocity(sim, dt);
        DiffuseVelocity(sim, dt);
        ApplyVorticity(sim, dt);
        ComputeDivergence(sim, rdx);
        SolvePressure(sim, dt);
        SubtractPressureGradient(sim, rdx);
        ApplyBoundary(sim);
        AdvectColor(sim, dt);
        CopyObstacleTexture(sim);
        sim.FluidDomainOffset = Vector2.Zero;
        if (sim.OutputTexture != null) sim.OutputTexture.TextureRdRid = _gpu.TexIdColor;
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

    // ======================== GPU 计算调度 ========================

    /// <summary>
    ///     执行一次 GPU 计算着色器调度。封装了完整的绑定流程：
    ///     1. 创建并绑定 Uniform Set（采样器纹理 + 图像纹理 + 尾部采样器纹理）
    ///     2. 开始计算列表
    ///     3. 绑定管线和 Uniform Set
    ///     4. 设置 Push Constants
    ///     5. 调度工作组
    ///     6. 结束计算列表
    ///     7. 释放临时 Uniform Set
    /// </summary>
    /// <param name="pipeline">要使用的计算管线。</param>
    /// <param name="samplerTexture">需要通过采样器绑定的纹理 RID（如无需则传入无效 RID）。</param>
    /// <param name="imageTextures">需要通过图像方式绑定的纹理 RID 数组。</param>
    /// <param name="pushConstants">Push Constants 字节数组，传递常量参数给着色器。</param>
    /// <param name="pushConstantSize">Push Constants 的字节大小。</param>
    /// <param name="trailingSamplers">在图像纹理之后绑定的采样器纹理数组（可选）。</param>
    private void RunCompute(ComputePipeline pipeline, Rid samplerTexture, Rid[] imageTextures, byte[] pushConstants,
        uint pushConstantSize, Rid[] trailingSamplers = null)
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
        _gpu.Device.ComputeListDispatch(computeList, (uint)_xGroups, (uint)_yGroups, 1);
        _gpu.Device.ComputeListEnd();
    }

    // ======================== 渲染步骤方法 ========================

    /// <summary>
    ///     处理批量绘制队列。根据队列大小选择策略：
    ///     - 小于 BatchDispatchThreshold：逐点调用 QueueDraw（避免计算着色器调度开销过大）
    ///     - 大于等于 BatchDispatchThreshold：使用 DispatchBatch 批量处理
    ///     处理完成后清空所有批量缓冲。
    /// </summary>
    private void ProcessBatchQueue(FluidSimulation2D sim)
    {
        switch (sim.BatchPoints.Count)
        {
            case 0:
                return;
            case < FluidSimulation2D.BatchDispatchThreshold:
            {
                for (var i = 0; i < sim.BatchPoints.Count; i++)
                    sim.QueueDraw(sim.BatchPoints[i], sim.BatchColors[i], sim.BatchVelocities[i], sim.BatchRadii[i],
                        sim.BatchRadii[i]);
                break;
            }
            default:
                DispatchBatch(sim, sim.BatchPoints, sim.BatchColors, sim.BatchVelocities, sim.BatchRadii);
                break;
        }

        sim.BatchPoints.Clear();
        sim.BatchColors.Clear();
        sim.BatchVelocities.Clear();
        sim.BatchRadii.Clear();
    }

    /// <summary>
    ///     批量调度绘制点。使用 SplatBatchPipeline 进行单次 GPU 调度处理颜色注入，
    ///     然后逐点注入速度场。
    /// </summary>
    private void DispatchBatch(FluidSimulation2D sim, List<Vector2> points, List<Color> colors,
        List<Vector2> velocities, List<float> radii)
    {
        var count = points.Count;
        var subtractiveVal = sim.SubtractiveMixing ? 1.0f : 0.0f;
        var aspect = sim.Resolution.X / sim.Resolution.Y;

        var dataSpan = _gpu.BatchPointData.AsSpan();
        for (var i = 0; i < count; i++)
        {
            var offset = i * 40;
            var pos = points[i];
            var vel = velocities[i];
            var r = radii[i];
            WritePushConstant(in pos, dataSpan[offset..]);
            WritePushConstant(in vel, dataSpan[(offset + 8)..]);
            var c = colors[i];
            WritePushConstant(in c, dataSpan[(offset + 16)..]);
            WritePushConstant(in r, dataSpan[(offset + 32)..]);
            WritePushConstant(in r, dataSpan[(offset + 36)..]);
        }

        _gpu.Device.BufferUpdate(_gpu.BatchBuffer, 0, (uint)(count * 40), _gpu.BatchPointData);

        var pc = _pc32.AsSpan();
        WritePushConstant(in sim.Resolution, pc);
        WritePushConstant(in aspect, pc[8..]);
        WritePushConstant(in count, pc[12..]);
        WritePushConstant(in subtractiveVal, pc[16..]);
        WritePushConstant(in sim.DensityScale, pc[20..]);

        var uniform0 = _gpu.CreateUniformSet(_gpu.SplatBatchPipeline, _gpu.TexIdColor, 0);
        var uniform1 = _gpu.CreateSamplerUniformSet(_gpu.SplatBatchPipeline, _gpu.TexIdTemp, 1);
        var uniform2 = _gpu.CreateStorageBufferUniformSet(_gpu.SplatBatchPipeline, _gpu.BatchBuffer, 0, 2);

        var computeList = _gpu.Device.ComputeListBegin();
        _gpu.Device.ComputeListBindComputePipeline(computeList, _gpu.SplatBatchPipeline.PipelineId);
        _gpu.Device.ComputeListBindUniformSet(computeList, uniform0, 0);
        _gpu.Device.ComputeListBindUniformSet(computeList, uniform1, 1);
        _gpu.Device.ComputeListBindUniformSet(computeList, uniform2, 2);
        _gpu.Device.ComputeListSetPushConstant(computeList, _pc32, 24);
        _gpu.Device.ComputeListDispatch(computeList, (uint)_xGroups, (uint)_yGroups, 1);
        _gpu.Device.ComputeListEnd();

        _gpu.Device.FreeRid(uniform0);
        _gpu.Device.FreeRid(uniform1);
        _gpu.Device.FreeRid(uniform2);
        SwapTexColor();

        for (var i = 0; i < count; i++)
        {
            var pos = points[i];
            var vel = velocities[i];
            var velRadius = sim.BrushSize * radii[i];
            var pc48 = _pc48.AsSpan();
            WritePushConstant(in sim.Resolution, pc48);
            WritePushConstant(in pos, pc48[8..]);
            WritePushConstant(in velRadius, pc48[16..]);
            WritePushConstant(in aspect, pc48[20..]);
            WritePushConstant(in vel.X, pc48[24..]);
            WritePushConstant(in vel.Y, pc48[28..]);
            pc48[32..].Clear();
            RunCompute(_gpu.SplatPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp], _pc48, 48);
            SwapTexVelocity();
        }
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
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in sim.FluidDomainOffset, pc[8..]);

            RunCompute(_gpu.ShiftTexturePipeline, _gpu.TexIdColor, [_gpu.TexIdTemp], _pc16, 16);
            SwapTexColor();

            RunCompute(_gpu.ShiftTexturePipeline, _gpu.TexIdVelocity, [_gpu.TexIdTemp], _pc16, 16);
            SwapTexVelocity();
        }
    }

    /// <summary>
    ///     处理绘制请求队列，将所有 DrawRequest 逐个执行 Splat 操作。
    ///     每个请求先注入速度场再注入颜色场，完成后清空队列。
    /// </summary>
    private void ApplySplatRequests(FluidSimulation2D sim, float subtractiveVal)
    {
        for (var ri = 0; ri < sim.DrawRequestCount; ri++)
        {
            var req = sim.DrawRequests[ri];
            var aspect = sim.Resolution.X / sim.Resolution.Y;

            var velRadius = sim.BrushSize * req.VelocityRadius;
            var pc48 = _pc48.AsSpan();
            WritePushConstant(in sim.Resolution, pc48);
            WritePushConstant(in req.Position, pc48[8..]);
            WritePushConstant(in velRadius, pc48[16..]);
            WritePushConstant(in aspect, pc48[20..]);
            WritePushConstant(in req.Velocity.X, pc48[24..]);
            WritePushConstant(in req.Velocity.Y, pc48[28..]);
            pc48[32..].Clear();
            RunCompute(_gpu.SplatPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp], _pc48, 48);
            SwapTexVelocity();

            var colRadius = sim.BrushSize * req.ColorRadius;
            pc48 = _pc48.AsSpan();
            WritePushConstant(in sim.Resolution, pc48);
            WritePushConstant(in req.Position, pc48[8..]);
            WritePushConstant(in colRadius, pc48[16..]);
            WritePushConstant(in aspect, pc48[20..]);
            WritePushConstant(in req.Color.R, pc48[24..]);
            WritePushConstant(in req.Color.G, pc48[28..]);
            WritePushConstant(in req.Color.B, pc48[32..]);
            WritePushConstant(in req.Color.A, pc48[36..]);
            WritePushConstant(in subtractiveVal, pc48[40..]);
            WritePushConstant(in sim.DensityScale, pc48[44..]);
            RunCompute(_gpu.SplatColorPipeline, new Rid(), [_gpu.TexIdColor, _gpu.TexIdTemp], _pc48, 48);
            SwapTexColor();
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

            MemoryMarshal.Write(span[..4], ref e.CenterX);
            MemoryMarshal.Write(span[4..8], ref e.CenterY);
            MemoryMarshal.Write(span[8..12], ref e.ForceX);
            MemoryMarshal.Write(span[12..16], ref e.ForceY);
            MemoryMarshal.Write(span[16..20], ref e.ShapeSizeX);
            MemoryMarshal.Write(span[20..24], ref e.ShapeSizeY);
            MemoryMarshal.Write(span[24..28], ref e.ForceRadius);
            MemoryMarshal.Write(span[28..32], ref e.FalloffExponent);
            MemoryMarshal.Write(span[32..36], ref e.SwirlStrength);
            MemoryMarshal.Write(span[36..40], ref e.ForcePattern);
            MemoryMarshal.Write(span[40..44], ref e.EmissionShape);
            // offset 44: _pad (unused, stays 0)
        }

        _gpu.Device.BufferUpdate(_gpu.ForceEmitterBuffer, 0, (uint)(count * stride), data);

        var pc = _pc16.AsSpan();
        WritePushConstant(in sim.Resolution, pc);
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
        _gpu.Device.ComputeListDispatch(computeList, (uint)_xGroups, (uint)_yGroups, 1);
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
    private void ApplyExternalInputs(FluidSimulation2D sim, float subtractiveVal)
    {
        if (!sim.InputForcesDirty && !sim.InputColorsDirty)
            return;

        if (sim.InputForcesDirty)
        {
            _gpu.Device.TextureUpdate(_gpu.TexIdInputForces, 0, sim.InputForcesImg.GetData());
            {
                var pc = _pc16.AsSpan();
                WritePushConstant(in sim.Resolution, pc);
                pc[8..].Clear();
            }
            RunCompute(_gpu.ApplyForcesPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdInputForces, _gpu.TexIdTemp],
                _pc16, 16, [_gpu.TexIdObstacle]);
            SwapTexVelocity();
            sim.InputForcesDirty = false;
        }

        if (sim.InputColorsDirty)
        {
            _gpu.Device.TextureUpdate(_gpu.TexIdInputColors, 0, sim.InputColorsImg.GetData());
            {
                var pc = _pc16.AsSpan();
                WritePushConstant(in sim.Resolution, pc);
                WritePushConstant(in subtractiveVal, pc[8..]);
                WritePushConstant(in sim.DensityScale, pc[12..]);
            }
            RunCompute(_gpu.ApplyColorsPipeline, new Rid(), [_gpu.TexIdColor, _gpu.TexIdInputColors, _gpu.TexIdTemp],
                _pc16, 16, [_gpu.TexIdObstacle]);
            SwapTexColor();
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
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in sim.ObstacleForceStrength, pc[8..]);
            WritePushConstant(in dt, pc[12..]);
            WritePushConstant(in sim.FluidDomainOffset, pc[16..]);
            pc[24..].Clear();
        }
        RunCompute(_gpu.ObstacleForcePipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp],
            _pc32, 32, [_gpu.TexIdObstacle, _gpu.TexIdObstaclePre]);
        SwapTexVelocity();
    }

    /// <summary>
    ///     速度场平流。根据当前速度场搬运速度值本身（半拉格朗日方法），
    ///     同时应用速度衰减和扩散强度参数。
    /// </summary>
    private void AdvectVelocity(FluidSimulation2D sim, float dt)
    {
        var rdx = 1.0f / sim.GridScale;
        {
            var pc = _pc48.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in dt, pc[8..]);
            WritePushConstant(in rdx, pc[12..]);
            WritePushConstant(in sim.VelocityDecay, pc[16..]);
            WritePushConstant(in sim.ColorDecay, pc[20..]);
            var one = 1.0f;
            WritePushConstant(in one, pc[24..]);
            WritePushConstant(in sim.DiffusionStrength, pc[28..]);
            pc[32..].Clear();
        }
        RunCompute(_gpu.AdvectPipeline, _gpu.TexIdVelocity, [_gpu.TexIdVelocity, _gpu.TexIdTemp],
            _pc48, 48, [_gpu.TexIdObstacle]);
        SwapTexVelocity();
    }

    /// <summary>
    ///     Jacobi 迭代求解速度场扩散。通过多次迭代逼近扩散方程的解。
    /// </summary>
    private void DiffuseVelocity(FluidSimulation2D sim, float dt)
    {
        var deltaX = 1.0f / sim.GridScale;
        var alpha = deltaX * deltaX / dt;
        var rbeta = 1.0f / (4.0f + alpha);
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in alpha, pc[8..]);
            WritePushConstant(in rbeta, pc[12..]);
        }
        for (var i = 0; i < sim.JacobiVelocityIterations; i++)
        {
            RunCompute(_gpu.JacobiPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdVelocity, _gpu.TexIdTemp], _pc16,
                16,
                [_gpu.TexIdObstacle]);
            SwapTexVelocity();
        }
    }

    /// <summary>
    ///     涡度增强。计算速度场的涡度并施加涡度增强力以增加流体旋转细节。
    ///     仅在 EnableVorticity 为 true 时执行。
    /// </summary>
    private void ApplyVorticity(FluidSimulation2D sim, float dt)
    {
        if (sim.EnableVorticity)
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in dt, pc[8..]);
            WritePushConstant(in sim.VorticityAmount, pc[12..]);
            RunCompute(_gpu.VorticityPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp], _pc16, 16);
            SwapTexVelocity();
        }
    }

    /// <summary>
    ///     计算速度场的散度，用于后续压力泊松方程求解。
    /// </summary>
    private void ComputeDivergence(FluidSimulation2D sim, float rdx)
    {
        var halfRdx = rdx * 0.5f;
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in halfRdx, pc[8..]);
            pc[12..].Clear();
        }
        RunCompute(_gpu.DivergencePipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdDivergence],
            _pc16, 16, [_gpu.TexIdObstacle]);
    }

    /// <summary>
    ///     Jacobi 迭代求解压力场（压力泊松方程）。
    ///     迭代次数由 JacobiPressureIterations 控制。
    /// </summary>
    private void SolvePressure(FluidSimulation2D sim, float dt)
    {
        var deltaX = 1.0f / sim.GridScale;
        var alpha = -(deltaX * deltaX);
        var rbeta = 0.25f;
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in alpha, pc[8..]);
            WritePushConstant(in rbeta, pc[12..]);
        }
        for (var i = 0; i < sim.JacobiPressureIterations; i++)
        {
            RunCompute(_gpu.JacobiPipeline, new Rid(), [_gpu.TexIdPressure, _gpu.TexIdDivergence, _gpu.TexIdTemp],
                _pc16, 16,
                [_gpu.TexIdObstacle]);
            SwapTexPressure();
        }
    }

    /// <summary>
    ///     从速度场中减去压力梯度，以满足不可压缩条件。
    /// </summary>
    private void SubtractPressureGradient(FluidSimulation2D sim, float rdx)
    {
        var halfRdx = rdx * 0.5f;
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in halfRdx, pc[8..]);
            pc[12..].Clear();
        }
        RunCompute(_gpu.SubtractPipeline, new Rid(), [_gpu.TexIdPressure, _gpu.TexIdVelocity, _gpu.TexIdTemp],
            _pc16, 16, [_gpu.TexIdObstacle]);
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
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in boundScale, pc[8..]);
            pc[12..].Clear();
        }
        RunCompute(_gpu.BoundaryPipeline, new Rid(), [_gpu.TexIdVelocity, _gpu.TexIdTemp], _pc16, 16,
            [_gpu.TexIdObstacle]);
        SwapTexVelocity();

        boundScale = 1.0f;
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in boundScale, pc[8..]);
            pc[12..].Clear();
        }
        RunCompute(_gpu.BoundaryPipeline, new Rid(), [_gpu.TexIdPressure, _gpu.TexIdTemp], _pc16, 16,
            [_gpu.TexIdObstacle]);
        SwapTexPressure();
    }

    /// <summary>
    ///     颜色/密度场平流。根据速度场搬运颜色值，
    ///     同时应用颜色衰减参数。
    /// </summary>
    private void AdvectColor(FluidSimulation2D sim, float dt)
    {
        var rdx = 1.0f / sim.GridScale;
        {
            var pc = _pc48.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            WritePushConstant(in dt, pc[8..]);
            WritePushConstant(in rdx, pc[12..]);
            WritePushConstant(in sim.VelocityDecay, pc[16..]);
            WritePushConstant(in sim.ColorDecay, pc[20..]);
            pc[24..].Clear();
        }
        RunCompute(_gpu.AdvectPipeline, _gpu.TexIdColor, [_gpu.TexIdVelocity, _gpu.TexIdTemp],
            _pc48, 48, [_gpu.TexIdObstacle]);
        SwapTexColor();
    }

    /// <summary>
    ///     将当前帧的障碍物纹理复制到上一帧缓冲中，
    ///     用于下帧的障碍物排斥力计算中检测障碍物变化。
    /// </summary>
    private void CopyObstacleTexture(FluidSimulation2D sim)
    {
        {
            var pc = _pc16.AsSpan();
            WritePushConstant(in sim.Resolution, pc);
            pc[8..].Clear();
        }
        RunCompute(_gpu.CopyTexturePipeline, new Rid(), [_gpu.TexIdObstacle, _gpu.TexIdObstaclePre], _pc16, 16);
    }
}