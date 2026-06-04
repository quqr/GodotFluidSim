# LiquidSim - 2D 流体模拟插件

<video width="320" height="240" controls> 
  <source src="./Readme/LiquidSim.mp4" type="video/mp4"> 
</video>

基于 Godot 4.6 的实时 2D 流体模拟插件，使用 GPU Compute Shader 实现高性能流体效果。

## 特性

- **GPU 加速计算** - 基于 Navier-Stokes 方程，所有计算在 GPU 上并行执行
- **多种发射器** - 支持流体发射器（FluidEmitter）和力发射器（FluidForceEmitter）
- **障碍物交互** - 流体与刚体、角色控制器等物理对象实时交互
- **丰富预设** - 内置爆炸、烟雾、火焰、喷泉等 7 种预设效果
- **高度可配置** - 分辨率、衰减、涡度增强等参数均可调节
- **跟随系统** - 流体域可跟随摄像机或任意节点移动

### 核心节点

| 节点 | 说明 |
|------|------|
| `FluidSimulation2D` | 流体模拟主节点，管理 GPU 资源和渲染管线 |
| `FluidEmitter` | 流体发射器，向流体中注入颜色和速度 |
| `FluidForceEmitter` | 力发射器，向流体施加方向性外力 |
| `FluidObstacle2D` | 障碍物组件，使流体与物理对象交互 |

## 示例场景

### WorldFluidTest

**路径**: `Scenes/world_fluid_test.tscn`

演示完整的流体模拟流程：
- 鼠标绘制流体交互
- 摄像机跟随流体域
- 障碍物自动检测和绘制

## 参考

[godot-fluid-simulation](https://github.com/Jules5/godot-fluid-simulation)

[让流体模拟融入你的2D游戏中~光影~碰撞~无限距离~](https://www.bilibili.com/video/BV1RPb8zAEzY)
