using FluidSimulation;
using Godot;

namespace Tests;

/// <summary>
///     流体模拟测试场景控制器，演示流体模拟的完整使用流程。
///     <para>
///         功能包括：初始化流体模拟和障碍物绘制器、处理鼠标绘制交互、
///         将摄像机位置同步到流体域中心、键盘快捷键（Space/P/C/F5）、
///         HSV 颜色轮换的 Colorful 模式。
///     </para>
/// </summary>
public partial class WorldFluidTest : Node2D
{
    private FluidObstacleDrawer _drawer;
    private bool _initialized;

    /// <summary>场景中的摄像机，流体域会跟随摄像机移动。</summary>
    [Export] public Camera2D Camera;

    /// <summary>是否启用 Colorful 模式，颜色 H 值随时间自动轮换。</summary>
    [Export] public bool ColorfulMode = true;

    /// <summary>流体显示区域，用于渲染流体模拟的输出纹理。</summary>
    [Export] public TextureRect FluidDisplay;

    /// <summary>流体域节点，其位置会同步到摄像机位置。</summary>
    [Export] public Node2D FluidDomain;

    /// <summary>流体模拟节点实例。</summary>
    [Export] public FluidSimulation2D FluidSim;

    /// <summary>是否正在用鼠标绘制流体。</summary>
    public bool IsDrawing;

    /// <summary>模拟是否暂停。</summary>
    private bool _paused;

    /// <summary>Colorful 模式下的色相值，随时间自动轮换。</summary>
    private float _hue;

    /// <summary>上一帧的流体 UV 坐标，用于计算 UV 增量速度。</summary>
    private Vector2 _prevFluidUV;

    /// <summary>
    ///     场景初始化：创建障碍物绘制器、配置输出纹理、设置摄像机跟随。若流体模拟尚未就绪（分辨率未设置），会在 _Process 中延迟初始化。
    /// </summary>
    public override void _Ready()
    {
        if (FluidSim?.Resolution is { X: > 0, Y: > 0 })
        {
            _drawer = new FluidObstacleDrawer();
            _drawer.Initialize(FluidSim);
            _initialized = true;
        }

        var tex = new Texture2Drd();
        FluidSim.OutputTexture = tex;
        FluidDisplay.Texture = tex;
        if (Camera is not null)
        {
            FluidSim.FollowNode = Camera;
            FluidSim.PreviousPosition = Camera.GlobalPosition;
        }

        FluidSim.DisplayTarget = FluidDisplay;
        FluidSim.SyncDisplaySize();
    }

    /// <summary>
    ///     输入事件处理：跟踪鼠标移动速度、检测左键绘制状态、键盘快捷键。
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mouseButton:
            {
                if (mouseButton.ButtonIndex == MouseButton.Left) IsDrawing = mouseButton.Pressed;
                break;
            }
            case InputEventKey { Pressed: true } keyEvent:
            {
                switch (keyEvent.Keycode)
                {
                    case Key.F5:
                        GetTree().ReloadCurrentScene();
                        break;
                    case Key.Space:
                        RandomSplat();
                        break;
                    case Key.P:
                        _paused = !_paused;
                        if (FluidSim != null) FluidSim.Enabled = !_paused;
                        break;
                    case Key.C:
                        FluidSim?.ClearFluid();
                        break;
                }
                break;
            }
        }
    }

    /// <summary>
    ///     每帧处理：同步流体域位置、更新障碍物纹理、处理鼠标绘制交互。
    /// </summary>
    public override void _Process(double delta)
    {
        var dt = (float)delta;
        if (Camera is null || FluidSim is null) return;

        if (!_initialized)
        {
            if (FluidSim?.Resolution is { X: > 0, Y: > 0 })
            {
                _drawer = new FluidObstacleDrawer();
                _drawer.Initialize(FluidSim);
                _initialized = true;
            }
            else
            {
                return;
            }
        }

        FluidDomain.GlobalPosition = Camera.GlobalPosition;

        _drawer.FluidDomainCenter = Camera.GlobalPosition;
        _drawer.BeginFrame();
        _drawer.MarkDirty();
        _drawer.ScanAndDrawGroup(GetTree(), "obstacles");
        _drawer.Upload();

        if (IsDrawing)
        {
            var worldPos = GetGlobalMousePosition();
            var fluidUV = FluidSim.WorldToFluidUV(worldPos);
            var deltaUV = fluidUV - _prevFluidUV;
            var vel = deltaUV * 6000.0f; // WFS SPLAT_FORCE
            var baseColor = ColorfulMode ? Color.FromHsv(_hue, 1.0f, 1.0f) : new Color(GD.Randf(), GD.Randf(), GD.Randf());
            var color = baseColor * 0.15f; // WFS generateColor() × 0.15
            FluidSim.QueueDraw(fluidUV, color, vel, 0.8f, 1.0f);
            _prevFluidUV = fluidUV;
        }
        else
        {
            // 未绘制时仍更新 prevUV，避免下次按下时产生大跳跃
            var worldPos = GetGlobalMousePosition();
            _prevFluidUV = FluidSim.WorldToFluidUV(worldPos);
        }

        // Advance hue for Colorful mode
        if (ColorfulMode)
            _hue = (_hue + dt * 0.1f) % 1.0f;
    }

    /// <summary>
    ///     在随机位置生成多个 Splat，模拟 WFS 的 randomSplats 功能。
    /// </summary>
    private void RandomSplat()
    {
        var count = (int)(GD.RandRange(5, 15));
        for (var i = 0; i < count; i++)
        {
            var fluidUV = new Vector2(GD.Randf(), GD.Randf()); // UV [0,1]
            var vel = new Vector2((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0)) * 500.0f; // WFS: ±500
            var baseColor = ColorfulMode ? Color.FromHsv(_hue, 1.0f, 1.0f) : new Color(GD.Randf(), GD.Randf(), GD.Randf());
            var color = baseColor * 1.5f; // WFS: generateColor() × 0.15 × 10 = × 1.5
            FluidSim.QueueDraw(fluidUV, color, vel, 1.5f, 2.0f);
            _hue = (_hue + 0.05f) % 1.0f;
        }
    }
}
