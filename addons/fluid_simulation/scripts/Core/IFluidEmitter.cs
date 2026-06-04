namespace FluidSimulation;

/// <summary>
///     流体发射器接口，定义了向流体模拟系统注入流体数据的统一规范。
///     任何需要向流体模拟中添加颜色、速度等外部影响的组件都应实现此接口。
///     例如：鼠标输入处理器、粒子发射器、AI 控制的流体扰动源等。
///     通过接口解耦，可以方便地扩展不同类型的发射器而无需修改模拟核心代码。
/// </summary>
public interface IFluidEmitter
{
    /// <summary>
    ///     执行流体发射操作，将颜色和/或速度数据注入到流体模拟中。
    ///     实现者应根据自身逻辑构造 DrawRequest，并调用 FluidSimulation2D 的绘制方法提交请求。
    /// </summary>
    /// <param name="fluidSim">目标流体模拟实例，提供提交绘制请求的 API。</param>
    /// <param name="dt">当前帧的增量时间（秒），可用于基于时间的发射逻辑（如发射频率控制）。</param>
    void EmitFluid(FluidSimulation2D fluidSim, float dt);
}