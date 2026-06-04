using FluidSimulation;
using Godot;

namespace Tests;

/// <summary>
///     流体模拟测试场景控制器，演示流体模拟的完整使用流程。
///     <para>
///         功能包括：初始化流体模拟和障碍物绘制器、处理鼠标绘制交互、
///         将摄像机位置同步到流体域中心、支持 F5 重载场景。
///     </para>
/// </summary>
public partial class WorldFluidTest : Node2D
{
    private FluidObstacleDrawer _drawer;
    private bool _initialized;

    /// <summary>场景中的摄像机，流体域会跟随摄像机移动。</summary>
    [Export] public Camera2D Camera;

    /// <summary>流体显示区域，用于渲染流体模拟的输出纹理。</summary>
    [Export] public TextureRect FluidDisplay;

    /// <summary>流体域节点，其位置会同步到摄像机位置。</summary>
    [Export] public Node2D FluidDomain;

    /// <summary>流体模拟节点实例。</summary>
    [Export] public FluidSimulation2D FluidSim;

    /// <summary>是否正在用鼠标绘制流体。</summary>
    public bool IsDrawing;

    /// <summary>鼠标移动速度（屏幕像素/帧），用于计算绘制时的流体速度。</summary>
    public Vector2I MouseVelocity;

    /// <summary>鼠标速度缩放因子，将屏幕像素速度转换为流体速度。</summary>
    [Export] public float MouseVelocityScale = 0.1f;

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
    ///     输入事件处理：跟踪鼠标移动速度、检测左键绘制状态、F5 重载场景。
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion mouseMotion:
                MouseVelocity = (Vector2I)mouseMotion.ScreenVelocity;
                break;
            case InputEventMouseButton mouseButton:
            {
                if (mouseButton.ButtonIndex == MouseButton.Left) IsDrawing = mouseButton.Pressed;
                break;
            }
            case InputEventKey { Pressed: true } keyEvent:
            {
                if (keyEvent.Keycode == Key.F5) GetTree().ReloadCurrentScene();
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
            var fluidPos = FluidSim.WorldToFluidPos(worldPos);
            var vel = new Vector2(MouseVelocity.X, MouseVelocity.Y) * dt * MouseVelocityScale;
            var color = new Color(GD.Randf(), GD.Randf(), GD.Randf());
            FluidSim.QueueDraw(fluidPos, 
                color, vel, 0.8f, 1.0f);
        }

        MouseVelocity = Vector2I.Zero;
    }
}