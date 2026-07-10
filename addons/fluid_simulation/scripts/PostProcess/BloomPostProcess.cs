using System;
using System.Runtime.InteropServices;
using Godot;

namespace FluidSimulation;

/// <summary>
///     独立 Bloom 后处理节点。从 <see cref="SourceTexture" /> 读取实时输入纹理，
///     执行 WFS 软膝高亮提取 + 高斯模糊 + 合并，输出到 <see cref="OutputTexture" />。
///     自管 GPU 资源（管线、采样器、纹理），与流体模拟完全解耦。
///     <para>
///         用法：将源节点的实时 Texture2Drd 资源实例赋给 SourceTexture，
///         将 OutputTexture 分配给 TextureRect/Sprite2D 显示，或链式传入下一个后处理节点。
///         首次检测到有效源纹理 RID 时惰性初始化，之后分辨率固定。
///     </para>
/// </summary>
public partial class BloomPostProcess : Node
{
    // ======================== Export 参数 ========================

    /// <summary>输入源纹理（实时 Texture2Drd 资源，由源节点每帧更新其 TextureRdRid）。</summary>
    [Export] public Texture2Drd SourceTexture;

    /// <summary>是否启用 Bloom 辉光后处理。关闭时输出直通源纹理（零拷贝 RID 别名）。</summary>
    [Export] public bool EnableBloom = true;

    /// <summary>Bloom 强度系数。控制叠加到最终输出的辉光亮度倍率。</summary>
    [Export(PropertyHint.Range, "0.0,4.0,0.01")] public float BloomIntensity = 1.0f;

    /// <summary>Bloom 亮度阈值。高于此亮度的像素才会被提取为辉光源。</summary>
    [Export(PropertyHint.Range, "0.0,4.0,0.01")] public float BloomThreshold = 0.6f;

    /// <summary>软膝系数（0-1）。控制阈值附近的平滑过渡宽度，0 为硬截断。</summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float BloomSoftKnee = 0.7f;

    /// <summary>高斯模糊迭代次数。每次迭代执行一遍水平+垂直模糊，值越大辉光越柔和但开销越高。</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int BloomIterations = 3;

    /// <summary>高斯模糊标准差。控制单次模糊的扩散范围。</summary>
    [Export(PropertyHint.Range, "0.5,10.0,0.1")] public float BloomSigma = 2.5f;

    /// <summary>高斯模糊核半径（像素）。实际核大小 = 2*radius+1。</summary>
    [Export(PropertyHint.Range, "1,30,1")] public int BloomRadius = 8;

    /// <summary>Bloom 降采样系数。bloomResolution = 源分辨率 / BloomDownSample，值越大性能越好但辉光越粗糙。</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int BloomDownSample = 2;

    // ======================== 私有字段 ========================

    private RenderingDevice _device;
    private Rid _sampler;

    private Rid _prefilterShaderId, _blurShaderId, _combineShaderId;
    private Rid _prefilterPipelineId, _blurPipelineId, _combinePipelineId;
    private volatile bool _pipelinesReady;

    private Rid _texBloomA, _texBloomB, _texOutput;
    private Texture2Drd _outputTexture;
    private bool _initialized;

    private Vector2 _sourceResolution;
    private Vector2 _bloomResolution;
    private int _sourceXGroups, _sourceYGroups, _bloomXGroups, _bloomYGroups;

    private readonly byte[] _pc16 = new byte[16];
    private readonly byte[] _pc32 = new byte[32];

    // ======================== 输出属性 ========================

    /// <summary>后处理输出纹理。用户将其分配给 TextureRect/Sprite2D 或链式传入下一个后处理节点。</summary>
    public Texture2Drd OutputTexture => _outputTexture;

    // ======================== 生命周期 ========================

    public override void _Ready()
    {
        _device = RenderingServer.GetRenderingDevice();
        _outputTexture = new Texture2Drd();
        RenderingServer.CallOnRenderThread(Callable.From(InitializePipelines));
    }

    public override void _Process(double delta)
    {
        if (!_pipelinesReady) return;
        RenderingServer.CallOnRenderThread(Callable.From(Execute));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete)
            RenderingServer.CallOnRenderThread(Callable.From(TerminateGPU));
    }

    // ======================== GPU 初始化 ========================

    private void InitializePipelines()
    {
        _prefilterShaderId = LoadShader("res://addons/fluid_simulation/shaders/postprocess/bloom_prefilter.glsl");
        _blurShaderId = LoadShader("res://addons/fluid_simulation/shaders/postprocess/gaussian_blur.glsl");
        _combineShaderId = LoadShader("res://addons/fluid_simulation/shaders/postprocess/bloom_combine.glsl");

        _prefilterPipelineId = _device.ComputePipelineCreate(_prefilterShaderId);
        _blurPipelineId = _device.ComputePipelineCreate(_blurShaderId);
        _combinePipelineId = _device.ComputePipelineCreate(_combineShaderId);

        var samplerState = new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        };
        _sampler = _device.SamplerCreate(samplerState);

        _pipelinesReady = true;
    }

    private Rid LoadShader(string path)
    {
        var shaderFile = GD.Load<RDShaderFile>(path);
        var spirv = shaderFile.GetSpirV();
        return _device.ShaderCreateFromSpirV(spirv);
    }

    // ======================== 惰性初始化 ========================

    private void LazyInit(Rid sourceRid)
    {
        var fmt = _device.TextureGetFormat(sourceRid);
        _sourceResolution = new Vector2(fmt.Width, fmt.Height);
        _bloomResolution = _sourceResolution / BloomDownSample;

        _sourceXGroups = (int)((_sourceResolution.X - 1) / 8 + 1);
        _sourceYGroups = (int)((_sourceResolution.Y - 1) / 8 + 1);
        _bloomXGroups = (int)((_bloomResolution.X - 1) / 8 + 1);
        _bloomYGroups = (int)((_bloomResolution.Y - 1) / 8 + 1);

        var bloomFormat = CreateRgba16fFormat(_bloomResolution);
        _texBloomA = _device.TextureCreate(bloomFormat, new RDTextureView());
        _texBloomB = _device.TextureCreate(bloomFormat, new RDTextureView());
        _device.TextureClear(_texBloomA, Colors.Black, 0, 1, 0, 1);
        _device.TextureClear(_texBloomB, Colors.Black, 0, 1, 0, 1);

        var outputFormat = CreateRgba16fFormat(_sourceResolution);
        _texOutput = _device.TextureCreate(outputFormat, new RDTextureView());
        _device.TextureClear(_texOutput, Colors.Black, 0, 1, 0, 1);

        _outputTexture.TextureRdRid = _texOutput;

        _initialized = true;
    }

    private RDTextureFormat CreateRgba16fFormat(Vector2 size) => new()
    {
        Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
        TextureType = RenderingDevice.TextureType.Type2D,
        Width = (uint)size.X,
        Height = (uint)size.Y,
        Mipmaps = 1,
        UsageBits =
            RenderingDevice.TextureUsageBits.SamplingBit |
            RenderingDevice.TextureUsageBits.StorageBit |
            RenderingDevice.TextureUsageBits.CanCopyToBit |
            RenderingDevice.TextureUsageBits.CanUpdateBit
    };

    // ======================== Bloom 执行 ========================

    private void Execute()
    {
        if (SourceTexture == null || !SourceTexture.TextureRdRid.IsValid) return;
        if (!_initialized) LazyInit(SourceTexture.TextureRdRid);

        if (!EnableBloom)
        {
            _outputTexture.TextureRdRid = SourceTexture.TextureRdRid;
            return;
        }

        ExecuteBloom();
    }

    private void ExecuteBloom()
    {
        var sourceRid = SourceTexture.TextureRdRid;

        // 1. Prefilter: source → BloomA (bloomResolution)
        DispatchPrefilter(sourceRid, _texBloomA);

        // 2. Blur iterations: BloomA ↔ BloomB
        for (var i = 0; i < BloomIterations; i++)
        {
            DispatchGaussianBlur(_texBloomA, _texBloomB, 0); // H: A→B
            DispatchGaussianBlur(_texBloomB, _texBloomA, 1); // V: B→A
        }

        // 3. Combine: source + BloomA → Output (sourceResolution)
        DispatchCombine(sourceRid, _texBloomA, _texOutput);

        _outputTexture.TextureRdRid = _texOutput;
    }

    // ======================== Dispatch 方法 ========================

    private void DispatchPrefilter(Rid input, Rid output)
    {
        var knee = BloomThreshold * BloomSoftKnee + 1e-5f;
        var curve = new Vector3(
            BloomThreshold - knee,
            knee * 2.0f,
            0.25f / knee
        );

        var pc = _pc32.AsSpan();
        WritePushConstant(in _bloomResolution, pc);       // vec2 size (offset 0)
        pc[8..16].Clear();                                 // 2 pads (offset 8)
        WritePushConstant(in curve, pc[16..]);             // vec3 curve (offset 16)
        WritePushConstant(in BloomThreshold, pc[28..]);    // float threshold (offset 28)

        var set0 = CreateSamplerUniformSet(_prefilterShaderId, input, _sampler, 0);
        var set1 = CreateImageUniformSet(_prefilterShaderId, output, 1);

        var cl = _device.ComputeListBegin();
        _device.ComputeListBindComputePipeline(cl, _prefilterPipelineId);
        _device.ComputeListBindUniformSet(cl, set0, 0);
        _device.ComputeListBindUniformSet(cl, set1, 1);
        _device.ComputeListSetPushConstant(cl, _pc32, 32);
        _device.ComputeListDispatch(cl, (uint)_bloomXGroups, (uint)_bloomYGroups, 1);
        _device.ComputeListEnd();

        _device.FreeRid(set0);
        _device.FreeRid(set1);
    }

    private void DispatchGaussianBlur(Rid input, Rid output, int direction)
    {
        var pc = _pc32.AsSpan();
        WritePushConstant(in _bloomResolution, pc);   // vec2 size (offset 0)
        WritePushConstant(in BloomSigma, pc[8..]);     // float sigma (offset 8)
        WritePushConstant(in BloomRadius, pc[12..]);   // int radius (offset 12)
        WritePushConstant(in direction, pc[16..]);     // int direction (offset 16)
        pc[20..32].Clear();                            // 3 pads (offset 20)

        var set0 = CreateSamplerUniformSet(_blurShaderId, input, _sampler, 0);
        var set1 = CreateImageUniformSet(_blurShaderId, output, 1);

        var cl = _device.ComputeListBegin();
        _device.ComputeListBindComputePipeline(cl, _blurPipelineId);
        _device.ComputeListBindUniformSet(cl, set0, 0);
        _device.ComputeListBindUniformSet(cl, set1, 1);
        _device.ComputeListSetPushConstant(cl, _pc32, 32);
        _device.ComputeListDispatch(cl, (uint)_bloomXGroups, (uint)_bloomYGroups, 1);
        _device.ComputeListEnd();

        _device.FreeRid(set0);
        _device.FreeRid(set1);
    }

    private void DispatchCombine(Rid shaded, Rid bloom, Rid output)
    {
        var pc = _pc16.AsSpan();
        WritePushConstant(in _sourceResolution, pc);   // vec2 size (offset 0)
        WritePushConstant(in BloomIntensity, pc[8..]);  // float intensity (offset 8)
        pc[12..16].Clear();                             // 1 pad (offset 12)

        var set0 = CreateSamplerUniformSet(_combineShaderId, shaded, _sampler, 0);
        var set1 = CreateImageUniformSet(_combineShaderId, output, 1);
        var set2 = CreateSamplerUniformSet(_combineShaderId, bloom, _sampler, 2);

        var cl = _device.ComputeListBegin();
        _device.ComputeListBindComputePipeline(cl, _combinePipelineId);
        _device.ComputeListBindUniformSet(cl, set0, 0);
        _device.ComputeListBindUniformSet(cl, set1, 1);
        _device.ComputeListBindUniformSet(cl, set2, 2);
        _device.ComputeListSetPushConstant(cl, _pc16, 16);
        _device.ComputeListDispatch(cl, (uint)_sourceXGroups, (uint)_sourceYGroups, 1);
        _device.ComputeListEnd();

        _device.FreeRid(set0);
        _device.FreeRid(set1);
        _device.FreeRid(set2);
    }

    // ======================== Uniform Set 辅助 ========================

    private static Rid CreateSamplerUniformSet(Rid shaderId, Rid texture, Rid sampler, int setIndex)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = 0
        };
        uniform.AddId(sampler);
        uniform.AddId(texture);
        return RenderingServer.GetRenderingDevice().UniformSetCreate([uniform], shaderId, (uint)setIndex);
    }

    private static Rid CreateImageUniformSet(Rid shaderId, Rid texture, int setIndex)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 0
        };
        uniform.AddId(texture);
        return RenderingServer.GetRenderingDevice().UniformSetCreate([uniform], shaderId, (uint)setIndex);
    }

    private static void WritePushConstant<T>(in T value, Span<byte> target) where T : unmanaged
        => MemoryMarshal.Write(target, in value);

    // ======================== 资源释放 ========================

    private void TerminateGPU()
    {
        if (!_pipelinesReady) return;

        if (_texBloomA.IsValid) _device.FreeRid(_texBloomA);
        if (_texBloomB.IsValid) _device.FreeRid(_texBloomB);
        if (_texOutput.IsValid) _device.FreeRid(_texOutput);
        if (_sampler.IsValid) _device.FreeRid(_sampler);
        if (_prefilterPipelineId.IsValid) _device.FreeRid(_prefilterPipelineId);
        if (_blurPipelineId.IsValid) _device.FreeRid(_blurPipelineId);
        if (_combinePipelineId.IsValid) _device.FreeRid(_combinePipelineId);
        if (_prefilterShaderId.IsValid) _device.FreeRid(_prefilterShaderId);
        if (_blurShaderId.IsValid) _device.FreeRid(_blurShaderId);
        if (_combineShaderId.IsValid) _device.FreeRid(_combineShaderId);

        _pipelinesReady = false;
        _initialized = false;
    }
}
