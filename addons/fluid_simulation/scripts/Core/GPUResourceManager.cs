using System.Collections.Generic;
using Godot;

namespace FluidSimulation;

/// <summary>
///     GPU 资源生命周期管理器，负责创建、维护和释放流体模拟所需的全部 GPU 资源。
///     <para>
///         包括：渲染设备引用、纹理采样器、所有模拟纹理（速度场、压力场、颜色场、散度场等）、
///         批量绘制缓冲区以及全部 14 个计算管线。从 FluidSimulation2D 中提取而来，
///         以实现 GPU 资源管理的单一职责。
///     </para>
/// </summary>
public class GPUResourceManager
{
    private readonly Dictionary<(ulong, ulong, uint, int), Rid> _uniformSetCache = new();

    /// <summary>平流（Advection）计算管线，用于根据速度场搬运速度/颜色。</summary>
    internal ComputePipeline AdvectPipeline;

    /// <summary>应用外部颜色管线，将输入颜色场图像叠加到颜色场上。</summary>
    internal ComputePipeline ApplyColorsPipeline;

    /// <summary>GPU 力发射器管线，直接将发射器参数传入 GPU 并行计算力场并叠加到速度场。替代 CPU 逐像素路径。</summary>
    internal ComputePipeline ApplyForceEmitterPipeline;

    /// <summary>应用外部力管线，将输入力场图像施加到速度场上。</summary>
    internal ComputePipeline ApplyForcesPipeline;

    /// <summary>批量绘制的 Storage Buffer RID，存储批量点位的位置、速度、颜色和半径数据。</summary>
    internal Rid BatchBuffer;

    /// <summary>批量绘制的 CPU 端数据缓冲区，每个点占 40 字节（位置 8 + 速度 8 + 颜色 16 + 半径 8）。</summary>
    internal byte[] BatchPointData;

    /// <summary>力发射器 GPU Storage Buffer RID，存储所有活跃发射器的参数，每个发射器 48 字节。</summary>
    internal Rid ForceEmitterBuffer;

    /// <summary>力发射器 CPU 端参数缓冲区，每帧序列化后上传到 ForceEmitterBuffer。</summary>
    internal byte[] ForceEmitterRawData;

    /// <summary>边界条件处理管线，确保流体在障碍物和域边界处满足正确的边界条件。</summary>
    internal ComputePipeline BoundaryPipeline;

    /// <summary>纹理复制管线，用于将障碍物纹理复制到上一帧缓冲中。</summary>
    internal ComputePipeline CopyTexturePipeline;

    /// <summary>Godot 渲染设备实例，用于创建和管理 GPU 资源（纹理、着色器、管线等）。</summary>
    internal RenderingDevice Device;

    /// <summary>散度计算管线，计算速度场的散度用于压力求解。</summary>
    internal ComputePipeline DivergencePipeline;

    /// <summary>Jacobi 迭代求解器管线，用于求解扩散和压力泊松方程。</summary>
    internal ComputePipeline JacobiPipeline;

    /// <summary>障碍物排斥力管线，根据障碍物对流体施加排斥力，阻止流体穿透障碍物。</summary>
    internal ComputePipeline ObstacleForcePipeline;

    /// <summary>纹理采样器 RID，使用线性过滤和 ClampToEdge 模式，用于在着色器中采样纹理。</summary>
    internal Rid Sampler;

    /// <summary>纹理偏移管线，当流体域跟随节点移动时，对颜色和速度纹理进行整体平移。</summary>
    internal ComputePipeline ShiftTexturePipeline;

    /// <summary>批量 Splat 管线，一次性向流体注入多个点的速度/颜色（性能优化）。</summary>
    internal ComputePipeline SplatBatchPipeline;

    /// <summary>颜色 Splat 管线，在指定位置向颜色场注入颜色（圆形高斯分布）。</summary>
    internal ComputePipeline SplatColorPipeline;

    /// <summary>速度 Splat 管线，在指定位置向速度场注入速度（圆形高斯分布）。</summary>
    internal ComputePipeline SplatPipeline;

    /// <summary>压力梯度减法管线，从速度场中减去压力梯度以满足不可压缩条件。</summary>
    internal ComputePipeline SubtractPipeline;

    /// <summary>颜色场纹理 RID（RGBA 通道存储流体颜色和不透明度）。</summary>
    internal Rid TexIdColor;

    /// <summary>散度场纹理 RID，存储速度场的散度值，用于压力泊松方程求解。</summary>
    internal Rid TexIdDivergence;

    /// <summary>输入颜色场纹理 RID，由 InputColorsImg CPU 图像每帧上传更新。</summary>
    internal Rid TexIdInputColors;

    /// <summary>输入力场纹理 RID，由 InputForcesImg CPU 图像每帧上传更新。</summary>
    internal Rid TexIdInputForces;

    /// <summary>当前帧的障碍物纹理 RID，标记流体域中哪些区域是固体障碍物。</summary>
    internal Rid TexIdObstacle;

    /// <summary>上一帧的障碍物纹理 RID，用于障碍物排斥力管线中检测障碍物的变化。</summary>
    internal Rid TexIdObstaclePre;

    /// <summary>压力场纹理 RID（R 通道存储标量压力值）。</summary>
    internal Rid TexIdPressure;

    /// <summary>临时纹理 RID，作为乒乓缓冲（Ping-Pong Buffer）的中间交换纹理使用。</summary>
    internal Rid TexIdTemp;

    /// <summary>速度场纹理 RID（RG 通道存储二维速度向量）。</summary>
    internal Rid TexIdVelocity;

    /// <summary>涡度计算管线，计算速度场的涡度并施加涡度增强力以增加流体旋转细节。</summary>
    internal ComputePipeline VorticityPipeline;

    /// <summary>
    ///     在渲染线程中初始化所有 GPU 资源。
    ///     包括：创建所有计算管线、分配流体模拟纹理、配置纹理采样器和批量绘制缓冲区。
    /// </summary>
    /// <param name="resolution">模拟网格分辨率（宽 × 高），决定流体纹理的精度。</param>
    /// <param name="subtractiveMixing">是否使用减色混合模式（CMY），为 true 时颜色初始为白色。</param>
    /// <param name="clearColor">流体颜色纹理的清除颜色，也是流体的初始背景颜色。</param>
    /// <param name="maxBatchPoints">批量绘制队列的最大容量。</param>
    internal void Initialize(Vector2 resolution, bool subtractiveMixing, Color clearColor, int maxBatchPoints)
    {
        Device = RenderingServer.GetRenderingDevice();

        AdvectPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/advect.glsl");
        JacobiPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/jacobi.glsl");
        ApplyForcesPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/apply_forces.glsl");
        ApplyColorsPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/apply_colors.glsl");
        DivergencePipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/divergence.glsl");
        SubtractPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/subtract.glsl");
        BoundaryPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/boundary.glsl");
        ShiftTexturePipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/shift_texture.glsl");
        VorticityPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/vorticity.glsl");
        ObstacleForcePipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/obstacle_force.glsl");
        SplatPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/splat.glsl");
        SplatColorPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/splat_color.glsl");
        SplatBatchPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/splat_batch.glsl");
        CopyTexturePipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/copy_texture.glsl");
        ApplyForceEmitterPipeline = CreateComputePipeline("res://addons/fluid_simulation/shaders/apply_force_emitter.glsl");

        var texFormat = new RDTextureFormat();
        texFormat.Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat;
        texFormat.TextureType = RenderingDevice.TextureType.Type2D;
        texFormat.Width = (uint)resolution.X;
        texFormat.Height = (uint)resolution.Y;
        texFormat.Mipmaps = 1;
        texFormat.UsageBits =
            RenderingDevice.TextureUsageBits.SamplingBit |
            RenderingDevice.TextureUsageBits.StorageBit |
            RenderingDevice.TextureUsageBits.CanCopyToBit |
            RenderingDevice.TextureUsageBits.CanUpdateBit;

        TexIdVelocity = CreateTexture(texFormat, clearColor);
        TexIdPressure = CreateTexture(texFormat, clearColor);
        TexIdColor = CreateTexture(texFormat, clearColor);
        TexIdDivergence = CreateTexture(texFormat, clearColor);
        TexIdInputForces = CreateTexture(texFormat, clearColor);
        TexIdInputColors = CreateTexture(texFormat, clearColor);
        TexIdTemp = CreateTexture(texFormat, clearColor);
        TexIdObstacle = CreateTexture(texFormat, clearColor);
        TexIdObstaclePre = CreateTexture(texFormat, clearColor);

        if (subtractiveMixing)
        {
            Device.TextureClear(TexIdColor, new Color(1, 1, 1), 0, 1, 0, 1);
            Device.TextureClear(TexIdTemp, new Color(1, 1, 1), 0, 1, 0, 1);
        }

        var samplerState = new RDSamplerState();
        samplerState.MinFilter = RenderingDevice.SamplerFilter.Linear;
        samplerState.MagFilter = RenderingDevice.SamplerFilter.Linear;
        samplerState.RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge;
        samplerState.RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge;
        Sampler = Device.SamplerCreate(samplerState);

        BatchPointData = new byte[maxBatchPoints * 40];
        BatchBuffer = Device.StorageBufferCreate((uint)(maxBatchPoints * 40));

        const int maxForceEmitters = 32;
        const int forceEmitterStride = 48;
        ForceEmitterRawData = new byte[maxForceEmitters * forceEmitterStride];
        ForceEmitterBuffer = Device.StorageBufferCreate((uint)(maxForceEmitters * forceEmitterStride));
    }

    /// <summary>
    ///     在渲染线程中释放所有 GPU 资源。
    ///     包括：释放采样器、释放所有模拟纹理、释放批量缓冲区、释放所有计算管线。
    /// </summary>
    internal void Terminate()
    {
        ClearUniformSetCache();

        Sampler = FreeRid(Sampler);

        TexIdVelocity = FreeRid(TexIdVelocity);
        TexIdPressure = FreeRid(TexIdPressure);
        TexIdColor = FreeRid(TexIdColor);
        TexIdDivergence = FreeRid(TexIdDivergence);
        TexIdInputForces = FreeRid(TexIdInputForces);
        TexIdInputColors = FreeRid(TexIdInputColors);
        TexIdTemp = FreeRid(TexIdTemp);
        TexIdObstacle = FreeRid(TexIdObstacle);
        TexIdObstaclePre = FreeRid(TexIdObstaclePre);

        BatchBuffer = FreeRid(BatchBuffer);
        ForceEmitterBuffer = FreeRid(ForceEmitterBuffer);

        FreePipeline(AdvectPipeline);
        FreePipeline(JacobiPipeline);
        FreePipeline(ApplyForcesPipeline);
        FreePipeline(ApplyColorsPipeline);
        FreePipeline(DivergencePipeline);
        FreePipeline(SubtractPipeline);
        FreePipeline(BoundaryPipeline);
        FreePipeline(ShiftTexturePipeline);
        FreePipeline(VorticityPipeline);
        FreePipeline(ObstacleForcePipeline);
        FreePipeline(SplatPipeline);
        FreePipeline(SplatColorPipeline);
        FreePipeline(SplatBatchPipeline);
        FreePipeline(CopyTexturePipeline);
        FreePipeline(ApplyForceEmitterPipeline);
    }

    /// <summary>
    ///     使用指定格式创建 GPU 纹理，并以指定清除颜色进行初始清除。
    /// </summary>
    /// <param name="format">纹理格式描述（分辨率、格式、用途标记等）。</param>
    /// <param name="clearColor">纹理的初始清除颜色。</param>
    /// <returns>新创建纹理的 RID。</returns>
    internal Rid CreateTexture(RDTextureFormat format, Color clearColor)
    {
        var rid = Device.TextureCreate(format, new RDTextureView());
        Device.TextureClear(rid, clearColor, 0, 1, 0, 1);
        return rid;
    }

    /// <summary>
    ///     从 GLSL 着色器文件路径创建计算管线。
    ///     加载着色器文件、编译为 SPIR-V、创建 GPU 着色器和计算管线。
    /// </summary>
    /// <param name="shaderPath">GLSL 着色器文件的资源路径。</param>
    /// <returns>包含管线名、着色器 RID 和管线 RID 的 ComputePipeline 对象。</returns>
    internal ComputePipeline CreateComputePipeline(string shaderPath)
    {
        var shaderFile = GD.Load<RDShaderFile>(shaderPath);
        var shaderSpirv = shaderFile.GetSpirV();
        var pipeline = new ComputePipeline
        {
            Name = shaderPath.GetFile().GetBaseName(),
            ShaderId = Device.ShaderCreateFromSpirV(shaderSpirv)
        };
        pipeline.PipelineId = Device.ComputePipelineCreate(pipeline.ShaderId);
        return pipeline;
    }

    /// <summary>
    ///     安全释放 RID 资源。如果 RID 有效则释放，返回一个无效的空 RID。
    /// </summary>
    /// <param name="objRid">要释放的 RID。</param>
    /// <returns>无效的空 RID，用于赋值给原变量以避免悬空引用。</returns>
    internal Rid FreeRid(Rid objRid)
    {
        if (objRid.IsValid) Device.FreeRid(objRid);
        return new Rid();
    }

    /// <summary>
    ///     释放计算管线及其关联的着色器资源。
    /// </summary>
    /// <param name="pipeline">要释放的计算管线。</param>
    internal void FreePipeline(ComputePipeline pipeline)
    {
        if (pipeline.PipelineId.IsValid)
        {
            pipeline.PipelineId = FreeRid(pipeline.PipelineId);
            pipeline.ShaderId = FreeRid(pipeline.ShaderId);
        }
    }

    /// <summary>
    ///     创建图像类型（Image）的 Uniform Set，将纹理绑定到着色器的指定 binding 槽位。
    /// </summary>
    /// <param name="pipeline">目标计算管线。</param>
    /// <param name="textureRd">要绑定的纹理 RID。</param>
    /// <param name="uniformSet">Uniform Set 的索引编号。</param>
    /// <returns>创建的 Uniform Set 的 RID。</returns>
    internal Rid CreateUniformSet(ComputePipeline pipeline, Rid textureRd, int uniformSet)
    {
        var uniform = new RDUniform();
        uniform.UniformType = RenderingDevice.UniformType.Image;
        uniform.Binding = 0;
        uniform.AddId(textureRd);
        return Device.UniformSetCreate([uniform], pipeline.ShaderId, (uint)uniformSet);
    }

    /// <summary>
    ///     创建采样器+纹理类型（SamplerWithTexture）的 Uniform Set，将采样器和纹理一起绑定到着色器。
    ///     用于需要在着色器中进行纹理采样（而非图像读写）的场景。
    /// </summary>
    /// <param name="pipeline">目标计算管线。</param>
    /// <param name="textureRd">要绑定的纹理 RID。</param>
    /// <param name="uniformSet">Uniform Set 的索引编号。</param>
    /// <returns>创建的 Uniform Set 的 RID。</returns>
    internal Rid CreateSamplerUniformSet(ComputePipeline pipeline, Rid textureRd, int uniformSet)
    {
        var uniform = new RDUniform();
        uniform.UniformType = RenderingDevice.UniformType.SamplerWithTexture;
        uniform.Binding = 0;
        uniform.AddId(Sampler);
        uniform.AddId(textureRd);
        return Device.UniformSetCreate([uniform], pipeline.ShaderId, (uint)uniformSet);
    }

    /// <summary>
    ///     创建 Storage Buffer 类型的 Uniform Set，将存储缓冲区绑定到着色器的指定 binding 槽位。
    ///     用于批量 Splat 等需要传递结构化数组数据的场景。
    /// </summary>
    /// <param name="pipeline">目标计算管线。</param>
    /// <param name="bufferRid">要绑定的 Storage Buffer RID。</param>
    /// <param name="binding">着色器中的 binding 编号。</param>
    /// <param name="uniformSet">Uniform Set 的索引编号。</param>
    /// <returns>创建的 Uniform Set 的 RID。</returns>
    internal Rid CreateStorageBufferUniformSet(ComputePipeline pipeline, Rid bufferRid, uint binding, int uniformSet)
    {
        var uniform = new RDUniform();
        uniform.UniformType = RenderingDevice.UniformType.StorageBuffer;
        uniform.Binding = (int)binding;
        uniform.AddId(bufferRid);
        return Device.UniformSetCreate([uniform], pipeline.ShaderId, (uint)uniformSet);
    }

    /// <summary>
    ///     创建或获取缓存的 Image Uniform Set，用于将纹理绑定为计算着色器的 Image 资源。相同 (ShaderId, TextureId, UniformSet) 组合会复用已创建的 Uniform
    ///     Set，避免重复创建。
    /// </summary>
    /// <param name="pipeline">计算管线，提供 Shader RID。</param>
    /// <param name="textureRd">要绑定的纹理 RID。</param>
    /// <param name="uniformSet">Uniform Set 索引（对应着色器中的 layout(set=N)）。</param>
    /// <returns>创建或缓存的 Uniform Set RID。</returns>
    internal Rid CreateUniformSetCached(ComputePipeline pipeline, Rid textureRd, uint uniformSet)
    {
        var key = (pipeline.ShaderId.Id, textureRd.Id, uniformSet, (int)RenderingDevice.UniformType.Image);
        if (_uniformSetCache.TryGetValue(key, out var cached))
            return cached;

        var rid = CreateUniformSet(pipeline, textureRd, (int)uniformSet);
        _uniformSetCache[key] = rid;
        return rid;
    }

    /// <summary>
    ///     创建或获取缓存的 Sampler Uniform Set，用于将纹理绑定为计算着色器的采样器资源。与 CreateUniformSetCached 的区别在于 Uniform 类型为 SamplerWithTexture 而非
    ///     Image。
    /// </summary>
    /// <param name="pipeline">计算管线，提供 Shader RID。</param>
    /// <param name="textureRd">要绑定的纹理 RID。</param>
    /// <param name="uniformSet">Uniform Set 索引（对应着色器中的 layout(set=N)）。</param>
    /// <returns>创建或缓存的 Uniform Set RID。</returns>
    internal Rid CreateSamplerUniformSetCached(ComputePipeline pipeline, Rid textureRd, uint uniformSet)
    {
        var key = (pipeline.ShaderId.Id, textureRd.Id, uniformSet, (int)RenderingDevice.UniformType.SamplerWithTexture);
        if (_uniformSetCache.TryGetValue(key, out var cached))
            return cached;

        var rid = CreateSamplerUniformSet(pipeline, textureRd, (int)uniformSet);
        _uniformSetCache[key] = rid;
        return rid;
    }

    /// <summary>
    ///     清空 Uniform Set 缓存并释放所有已缓存的 RID。在 GPU 资源释放时调用，确保所有 Uniform Set 被正确回收。
    /// </summary>
    internal void ClearUniformSetCache()
    {
        foreach (var kvp in _uniformSetCache)
            if (kvp.Value.IsValid)
                Device.FreeRid(kvp.Value);
        _uniformSetCache.Clear();
    }

    /// <summary>
    ///     清除流体模拟的核心纹理状态（速度场、压力场、颜色场、散度场），
    ///     将流体重置为空白状态。
    /// </summary>
    /// <param name="clearColor">颜色场和临时纹理的清除颜色，速度场、压力场和散度场清除为黑色。</param>
    internal void ClearTextures(Color clearColor)
    {
        Device.TextureClear(TexIdVelocity, new Color(0, 0, 0), 0, 1, 0, 1);
        Device.TextureClear(TexIdPressure, new Color(0, 0, 0), 0, 1, 0, 1);
        Device.TextureClear(TexIdColor, clearColor, 0, 1, 0, 1);
        Device.TextureClear(TexIdDivergence, new Color(0, 0, 0), 0, 1, 0, 1);
    }
}