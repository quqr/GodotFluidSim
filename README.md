# fluid-simulation - 2D 流体模拟插件

基于 Godot 4.6 的实时 2D 流体模拟插件，使用 GPU Compute Shader 实现流体效果。

https://github.com/user-attachments/assets/a6270cdd-6036-4ba4-8051-58c2f97aa230

## 特性

- **GPU 加速计算** - 基于 Navier-Stokes 方程，所有计算在 GPU 上并行执行
- **多种发射器** - 支持流体发射器（FluidEmitter）和力发射器（FluidForceEmitter），含点/圆/矩形/线段/纹理遮罩五种发射形状
- **障碍物交互** - 流体与刚体、角色控制器等物理对象实时交互
- **高度可配置** - 分辨率、衰减、涡度增强、压力迭代等参数均可调节
- **跟随系统** - 流体域可跟随摄像机或任意节点移动，支持无限流体效果

### 核心节点

| 节点                  | 说明                     |
| ------------------- | ---------------------- |
| `FluidSimulation2D` | 流体模拟主节点，管理 GPU 资源和渲染管线 |
| `FluidEmitter`      | 流体发射器，向流体中注入颜色和速度      |
| `FluidForceEmitter` | 力发射器，向流体施加方向性外力        |
| `FluidObstacle2D`   | 障碍物组件，使流体与物理对象交互       |

## 示例场景

### WorldFluidTest

**路径**: `Scenes/world_fluid_test.tscn`

演示完整的流体模拟流程：

- 鼠标绘制流体交互
- 摄像机跟随流体域
- 障碍物自动检测和绘制

## 文档

完整的参数说明与使用指南见 [addons/fluid\_simulation/docs/使用说明.md](addons/fluid_simulation/docs/使用说明.md)，涵盖：

- `FluidSimulation2D` / `FluidEmitter` / `FluidForceEmitter` 全部导出参数详解
- 五种枚举类型参考（`EmissionShape` / `EmissionMode` / `VelocityPattern` / `ForcePattern` / `RangeSampleMode`）
- 坐标系统说明（世界坐标 / 流体 UV / 流体像素）
- 17 步渲染管线流程与参数对应关系
- 7 个常见用法示例（鼠标绘制 / 烟雾 / 爆炸 / 风力 / 旋涡 / 引力井 / 跟随摄像机）

## 参考

- [WebGL-Fluid-Simulation](https://github.com/PavelDoGreat/WebGL-Fluid-Simulation)
- [godot-fluid-simulation](https://github.com/Jules5/godot-fluid-simulation)
- [让流体模拟融入你的2D游戏中\~光影\~碰撞\~无限距离\~](https://www.bilibili.com/video/BV1RPb8zAEzY)
