using Godot;

namespace FluidSimulation;

/// <summary>
///     流体模拟编辑器插件入口，负责在 Godot 编辑器中注册和注销流体模拟插件。当前为空实现，插件的节点类型通过 [GlobalClass] 特性自动注册。
/// </summary>
[Tool]
public partial class Plugin : EditorPlugin
{
    /// <summary>插件启用时调用，用于注册编辑器扩展（当前无额外注册需求）。</summary>
    public override void _EnablePlugin()
    {
    }

    /// <summary>插件禁用时调用，用于清理编辑器扩展（当前无清理需求）。</summary>
    public override void _DisablePlugin()
    {
    }


    /// <summary>插件进入场景树时调用，用于初始化编辑器界面（当前无界面需求）。</summary>
    public override void _EnterTree()
    {
    }

    /// <summary>插件离开场景树时调用，用于清理资源（当前无清理需求）。</summary>
    public override void _ExitTree()
    {
    }
}