namespace Wcs.Core.Common.Interfaces;

/// <summary>
/// 服务定位器接口
/// </summary>
public interface IServiceLocator
{
    /// <summary>
    /// 获取服务
    /// </summary>
    T GetService<T>() where T : class;

    /// <summary>
    /// 尝试获取服务
    /// </summary>
    bool TryGetService<T>(out T? service) where T : class;

    /// <summary>
    /// 获取所有指定类型的服务
    /// </summary>
    IEnumerable<T> GetServices<T>() where T : class;
}

/// <summary>
/// 初始化接口
/// </summary>
public interface IInitializable
{
    /// <summary>
    /// 初始化
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 可启动接口
/// </summary>
public interface IStartable : IInitializable
{
    /// <summary>
    /// 启动
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取运行状态
    /// </summary>
    bool IsRunning { get; }
}

/// <summary>
/// 健康检查接口
/// </summary>
public interface IHealthCheck
{
    /// <summary>
    /// 检查健康状态
    /// </summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取健康检查详情
    /// </summary>
    Task<string> GetHealthDetailsAsync(CancellationToken cancellationToken = default);
}
