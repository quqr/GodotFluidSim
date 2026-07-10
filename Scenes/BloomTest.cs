using FluidSimulation;
using Godot;

namespace Tests;

/// <summary>
///     Bloom 独立后处理演示场景控制器。
///     <para>
///         接线：FluidSimulation2D.OutputTexture 与 BloomPostProcess.SourceTexture 共享同一 Texture2Drd 资源实例，
///         BloomPostProcess.OutputTexture 显示到 TextureRect。鼠标左键拖动注入彩色流体产生高亮区域供 bloom 提取。
///     </para>
/// </summary>
public partial class BloomTest : Node2D
{
    /// <summary>流体模拟节点。</summary>
    [Export] public FluidSimulation2D FluidSim;

    /// <summary>Bloom 后处理节点。</summary>
    [Export] public BloomPostProcess Bloom;

    /// <summary>显示 bloom 输出的纹理矩形。</summary>
    [Export] public TextureRect FluidDisplay;

    /// <summary>场景摄像机，流体域跟随摄像机移动。</summary>
    [Export] public Camera2D Camera;

    /// <summary>Bloom 强度滑块。</summary>
    [Export] public HSlider IntensitySlider;

    /// <summary>Bloom 阈值滑块。</summary>
    [Export] public HSlider ThresholdSlider;

    /// <summary>Bloom 迭代次数滑块。</summary>
    [Export] public HSlider IterationsSlider;

    /// <summary>Bloom 降采样滑块。</summary>
    [Export] public HSlider DownSampleSlider;

    /// <summary>启用 Bloom 勾选框。</summary>
    [Export] public CheckBox EnableBloomCheckBox;

    private bool _drawing;
    private Vector2 _prevFluidUV;
    private float _hue;

    public override void _Ready()
    {
        var fluidOutput = new Texture2Drd();
        FluidSim.OutputTexture = fluidOutput;
        Bloom.SourceTexture = fluidOutput;
        FluidDisplay.Texture = Bloom.OutputTexture;

        FluidSim.DisplayTarget = FluidDisplay;
        FluidSim.SyncDisplaySize();

        if (Camera is not null)
        {
            FluidSim.FollowNode = Camera;
            FluidSim.PreviousPosition = Camera.GlobalPosition;
        }

        if (IntensitySlider != null)
        {
            IntensitySlider.Value = Bloom.BloomIntensity;
            IntensitySlider.ValueChanged += v => Bloom.BloomIntensity = (float)v;
        }
        if (ThresholdSlider != null)
        {
            ThresholdSlider.Value = Bloom.BloomThreshold;
            ThresholdSlider.ValueChanged += v => Bloom.BloomThreshold = (float)v;
        }
        if (IterationsSlider != null)
        {
            IterationsSlider.Value = Bloom.BloomIterations;
            IterationsSlider.ValueChanged += v => Bloom.BloomIterations = (int)v;
        }
        if (DownSampleSlider != null)
        {
            DownSampleSlider.Value = Bloom.BloomDownSample;
            DownSampleSlider.ValueChanged += v => Bloom.BloomDownSample = (int)v;
        }
        if (EnableBloomCheckBox != null)
        {
            EnableBloomCheckBox.ButtonPressed = Bloom.EnableBloom;
            EnableBloomCheckBox.Toggled += pressed => Bloom.EnableBloom = pressed;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.ButtonIndex == MouseButton.Left)
            _drawing = mouseButton.Pressed;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _hue = (_hue + dt * 0.1f) % 1.0f;

        if (Camera is null || FluidSim is null) return;

        if (_drawing)
        {
            var worldPos = GetGlobalMousePosition();
            var fluidUV = FluidSim.WorldToFluidUV(worldPos);
            var deltaUV = fluidUV - _prevFluidUV;
            var vel = deltaUV * 6000.0f;
            var color = Color.FromHsv(_hue, 1.0f, 1.0f) * 0.15f;
            FluidSim.QueueDraw(fluidUV, color, vel, 0.8f, 1.0f);
            _prevFluidUV = fluidUV;
        }
        else
        {
            _prevFluidUV = FluidSim.WorldToFluidUV(GetGlobalMousePosition());
        }
    }
}
