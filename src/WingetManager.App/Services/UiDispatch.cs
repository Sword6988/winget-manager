using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WingetManager.Infra;

namespace WingetManager.AppServices;

/// <summary>
/// <see cref="DispatcherQueue"/> 封装：后台 Task 结果回到 UI 线程的唯一入口。
///
/// 为什么要集中一处：Core 层的 <c>EnrichmentWorker</c>／<c>WingetRepository</c> 的回调
/// 都在线程池线程上触发，散落各处的 <c>DispatcherQueue.TryEnqueue</c> 极易漏掉一个，
/// 漏掉的后果是「在非 UI 线程改 ObservableCollection」——WinUI 直接
/// <c>RPC_E_WRONG_THREAD</c> 崩给你看，而且崩点离真正的原因很远。
/// </summary>
public static class UiDispatch
{
    private static DispatcherQueue? _queue;

    /// <summary>是否已完成初始化（单元测试/设计器里可能没有 UI 线程）。</summary>
    public static bool IsInitialized => _queue is not null;

    /// <summary>在 App 启动时绑定主线程队列（只调一次）。</summary>
    /// <param name="queue">主线程的 <see cref="DispatcherQueue"/>。</param>
    public static void Initialize(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    /// <summary>
    /// 主线程队列。未初始化时抛异常——调用方应在 UI 线程上，这里是编程错误而非运行时状况。
    /// </summary>
    public static DispatcherQueue Queue =>
        _queue ?? throw new InvalidOperationException("UiDispatch 尚未初始化（应在 App.OnLaunched 里调用 Initialize）。");

    /// <summary>
    /// 安全地把动作投递到 UI 线程：已在 UI 线程则同步执行；否则入队。
    /// 动作内的异常只写日志，不会把异常抛回后台线程（否则 Task 变 Faulted，扫描会莫名失败）。
    /// </summary>
    /// <param name="action">要执行的动作。</param>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var queue = _queue;
        if (queue is null)
        {
            // 没有 UI 线程（测试）时直接执行，保持可测
            Invoke(action);
            return;
        }

        if (queue.HasThreadAccess)
        {
            Invoke(action);
            return;
        }

        if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, () => Invoke(action)))
        {
            // 审计 F5：TryEnqueue 只在队列关闭（应用退出中）时返回 false，动作被丢弃——
            // 至少留痕，否则「该刷的界面没刷」无从查起。
            LogService.Shared.Warn("UI 队列已关闭，动作被丢弃：{0}", action.Method.Name);
        }
    }

    /// <summary>把一次异步操作切回 UI 线程继续（等待期间不阻塞 UI 线程）。</summary>
    /// <param name="action">要在 UI 线程上执行的异步动作。</param>
    /// <returns>可等待的任务。</returns>
    public static Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var queue = _queue;
        if (queue is null || queue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
            {
                try
                {
                    await action().ConfigureAwait(true);
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetResult(null);
        }

        return completion.Task;
    }

    /// <summary>创建绑定到 UI 线程的定时器（扫描计时用）。</summary>
    /// <param name="interval">触发间隔。</param>
    /// <param name="tick">每次触发的动作。</param>
    /// <returns>已启动的定时器。</returns>
    public static DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        var timer = Queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => Invoke(tick);
        return timer;
    }

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // UI 侧异常不能反噬调用者（后台线程/await 链）
            LogService.Shared.Error("UI 线程动作异常：{0}", ex);
        }
    }
}
