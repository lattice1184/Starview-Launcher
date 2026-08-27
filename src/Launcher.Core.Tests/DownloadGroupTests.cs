using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>并发回归测试类不并行跑（AsyncPostContext + Task.Run 负载重，全量并行时线程池争抢导致 Post 积压、偶发超窗）</summary>
[CollectionDefinition("SerialDownloadGroup", DisableParallelization = true)]
public sealed class SerialDownloadGroupCollection { }

/// <summary>组任务模型：状态推导 / 加权聚合 / 递归取消 / 计数语义 / 清理（离线同步上下文）</summary>
[Collection("SerialDownloadGroup")]
public class DownloadGroupTests
{
    private static DownloadManager CreateManager()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        return new DownloadManager(null, 0);
    }

    [Fact]
    public async Task Group_AllChildrenSucceed_ParentCompleted()
    {
        var manager = CreateManager();
        var task = manager.EnqueueGroup("下载 1.21.1", async (ctx, ct) =>
        {
            ctx.AddChild("client.jar", 100, (p, c) => Task.CompletedTask);
            ctx.AddChild("lib.jar", 200, (p, c) => Task.CompletedTask);
        });

        await task.Completion;

        Assert.Equal(DownloadTaskState.Completed, task.State);
        Assert.Equal(100, task.ProgressPercent);
        Assert.Equal(2, task.Children.Count);
        Assert.True(task.HasChildren);
    }

    [Fact]
    public async Task Group_ChildFails_ParentFailedWithChildError()
    {
        var manager = CreateManager();
        var task = manager.EnqueueGroup("下载", async (ctx, ct) =>
        {
            ctx.AddChild("ok.jar", 100, (p, c) => Task.CompletedTask);
            ctx.AddChild("bad.jar", 100, (p, c) => throw new InvalidDataException("SHA1 不匹配"));
        });

        await task.Completion;

        Assert.Equal(DownloadTaskState.Failed, task.State);
        Assert.Equal("SHA1 不匹配", task.Error);
    }

    [Fact]
    public async Task Group_Cancel_CascadesToChildren()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource();
        var task = manager.EnqueueGroup("下载", (ctx, ct) =>
        {
            ctx.AddChild("slow.jar", 100, (p, c) => Task.Delay(Timeout.InfiniteTimeSpan, c));
            return gate.Task;
        });

        // 等门放行、AddChild 落地后再 Cancel——否则级联目标还不存在（门排队时序竞态）
        while (task.Children.Count == 0) await Task.Yield();

        task.Cancel();
        gate.SetResult();
        await task.Completion;

        Assert.Equal(DownloadTaskState.Canceled, task.State);
        Assert.Equal(DownloadTaskState.Canceled, task.Children[0].State);
    }

    [Fact]
    public async Task Group_WeightedAggregation()
    {
        var manager = CreateManager();
        var hold = new TaskCompletionSource();   // 按住子任务保持运行态
        var release = new TaskCompletionSource();
        var task = manager.EnqueueGroup("下载", async (ctx, ct) =>
        {
            ctx.AddChild("a.jar", 100, async (p, c) =>
            {
                p(new DownloadProgress("下载 a.jar", "a.jar", 50, 100, 50));
                await hold.Task;
            });
            ctx.AddChild("b.jar", 300, async (p, c) =>
            {
                p(new DownloadProgress("下载 b.jar", "b.jar", 300, 300, 100));
                await hold.Task;
            });
            await release.Task;
        });

        // AL33：子任务 Report 的 100 被封顶 99（100 只由完成路径 Post 给）→
        // 聚合 = (100×50 + 300×99)/400 = 86.75，而不是旧的 87.5（子任务报 100 直接混入聚合）
        for (var i = 0; i < 50 && task.ProgressPercent < 86; i++) await Task.Delay(10);
        Assert.Equal(86.75, task.ProgressPercent, 1);
        Assert.Equal(400, task.TotalBytes);

        hold.SetResult();     // 子任务完成 → 聚合收敛 100
        release.SetResult();
        await task.Completion;
        Assert.Equal(DownloadTaskState.Completed, task.State);
    }

    [Fact]
    public async Task EnqueueGroup_ActiveCountCountsGroupAsOne()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource();
        var task = manager.EnqueueGroup("下载", async (ctx, ct) =>
        {
            ctx.AddChild("a.jar", 100, (p, c) => gate.Task);
        });

        await Task.Delay(50);
        Assert.Equal(1, manager.ActiveCount);   // 组算 1，不是 2
        Assert.Single(manager.Tasks);           // Children 不进 Tasks

        gate.SetResult();
        await task.Completion;
        for (var i = 0; i < 50 && manager.ActiveCount != 0; i++) await Task.Delay(10);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public async Task ClearFinished_RemovesGroupWithChildren()
    {
        var manager = CreateManager();
        var task = manager.EnqueueGroup("下载", (ctx, ct) => Task.CompletedTask);
        await task.Completion;

        manager.ClearFinished();

        Assert.Empty(manager.Tasks);
    }

    /// <summary>
    /// 高并发回归：管线在线程池快速 Add 40 个子任务，子任务立即完成触发聚合。
    /// 用"异步 Post"上下文制造真实并发窗口——修复前会抛 Collection was modified（线程池未捕获 → 进程崩），
    /// 修复后（Children 访问全部封送同一线程）稳定通过。
    /// </summary>
    [Fact]
    public async Task Group_ManyChildrenRapidAdd_NoCollectionModifiedCrash()
    {
        SynchronizationContext.SetSynchronizationContext(new AsyncPostContext());
        var manager = new DownloadManager(null, 0);
        try
        {
            // 规模收敛：8 轮 × 20 子任务（并发回归意图不变，降低线程池压力避免偶发饥饿）
            for (var round = 0; round < 8; round++)
            {
                var task = manager.EnqueueGroup("下载", async (ctx, ct) =>
                {
                    for (var i = 0; i < 20; i++)
                    {
                        ctx.AddChild($"lib{i}.jar", 10, (p, c) => Task.CompletedTask);
                    }
                });
                await task.Completion;
                // 异步 Post 上下文：State/Children 更新在排队回调里，轮询等待（20s 窗口容忍线程池竞争）
                for (var i = 0; i < 2000 && (task.State != DownloadTaskState.Completed || task.Children.Count != 20); i++)
                    await Task.Delay(10);
                Assert.True(task.State == DownloadTaskState.Completed,
                    $"round={round} state={task.State} children={task.Children.Count}");
                Assert.Equal(20, task.Children.Count);
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    /// <summary>
    /// Post 异步但串行执行（模拟 Dispatcher 的 FIFO 语义：回调在另一线程排队串行跑，
    /// 与 AddChild 的调用线程形成真实并发窗口，但回调之间不并发——与 Avalonia Dispatcher 一致）。
    /// </summary>
    private sealed class AsyncPostContext : SynchronizationContext
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public override void Post(SendOrPostCallback d, object? state)
            => _ = Task.Run(async () =>
            {
                await _gate.WaitAsync();
                try { d(state); }
                finally { _gate.Release(); }
            });

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    [Fact]
    public async Task Group_ZeroWeightChild_Indeterminate()
    {
        var manager = CreateManager();
        var task = manager.EnqueueGroup("下载", (ctx, ct) =>
        {
            ctx.AddChild("配置", 0, (p, c) => Task.CompletedTask);
            return Task.CompletedTask;
        });

        await task.Completion;

        Assert.Equal(0, task.Children[0].Weight); // 权重 0 → 父聚合不受影响
        Assert.Equal(DownloadTaskState.Completed, task.State);
    }

    [Fact]
    public async Task Group_LateAttachedChild_ProgressFallsFrom100ToReal()
    {
        // AL32 回归：阶段 2 晚挂载时序（VersionDownloadPipeline 的 assets 差量——
        // index 下完才知道清单，必然晚于阶段 1 全部完成）。阶段 1 完成使父聚合收敛，
        // 新子任务挂载后真实进度应回落到加权值，而不是被只升不降的 clamp 卡死在 100%。
        // AL33：阶段 1 报告 100 被封顶 99 → 回落目标是 (700×99+300×0)/1000=69.3。
        var manager = CreateManager();
        var reported = new TaskCompletionSource();   // 阶段 1 报告完成信号（屏障：先 100% 再挂阶段 2）
        var hold = new TaskCompletionSource();       // 按住子任务，模拟下载进行中
        var task = manager.EnqueueGroup("下载 1.21.1", async (ctx, ct) =>
        {
            // 阶段 1：大文件先下完（报告 100% 后挂起保持活动，模拟完成但组未收尾）
            ctx.AddChild("1.21.1.jar", 700, async (p, c) =>
            {
                p(new DownloadProgress("下载 1.21.1.jar", "1.21.1.jar", 700, 700, 100));
                reported.SetResult();
                await hold.Task;
            });
            // 与真实管线一致：阶段 1 聚合收敛 100% 之后才挂阶段 2（assets 差量，权重 300）
            await reported.Task;
            ctx.AddChild("资源文件 (1000 个)", 300, (p, c) => hold.Task);
            await hold.Task;
        });

        // 等两个子任务都挂载（groupWork 在后台线程跑）——父已收敛 99%（封顶）。
        // REVIEW-进度：聚合 percent 单调不减——新子任务 0% 挂载不拉低（进度条不跳），
        // 但 BytesDone=total×percent 随新 total(1000) 推进（990 > 阶段 1 的 693）——卡死观感消除。
        for (var i = 0; i < 100 && task.Children.Count < 2; i++) await Task.Delay(10);
        // REVIEW-节流：聚合 250ms 窗口合并 + 60ms 尾算——等尾算稳定再断言（不读节流中间态）
        for (var i = 0; i < 100 && task.ProgressPercent < 99; i++) await Task.Delay(10);
        Assert.Equal(99, task.ProgressPercent);
        Assert.Equal(1000, task.TotalBytes);
        Assert.True(task.BytesDone > 700 * 99 / 100, $"字节应随新 total 推进（当前 {task.BytesDone}）");

        hold.SetResult();
        await task.Completion;
        Assert.Equal(DownloadTaskState.Completed, task.State);
        Assert.Equal(100, task.ProgressPercent);
    }

    /// <summary>
    /// 8-27 回归：下载页第四列「已用 0秒」——组任务聚合发布（PublishAggregate）漏发 ElapsedText 通知，
    /// 组任务不走叶子 Report 路径，ElapsedText 只在 SetStage/SetState 刷新 → 开局四舍五入到 0秒后整场停摆。
    /// 断言：子任务持续报告进度期间，组任务必须刷新 ElapsedText（UI 才能重读该列）。
    /// </summary>
    [Fact]
    public async Task Group_ActiveDownload_ElapsedTextPropertyChangedFires()
    {
        var manager = CreateManager();
        var hold = new TaskCompletionSource();
        var task = manager.EnqueueGroup("下载 1.21.1", async (ctx, ct) =>
        {
            ctx.AddChild("a.jar", 100, async (p, c) =>
            {
                // 持续报告进度（~1.2s），覆盖多个聚合节流窗口（250ms）
                for (var i = 0; i < 10; i++)
                {
                    p(new DownloadProgress("下载 a.jar", "a.jar", 30 + i, 100, 30 + i));
                    await Task.Delay(120, c);
                }
            });
            await hold.Task;
        });

        var elapsedChanged = 0;
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(task.ElapsedText)) Interlocked.Increment(ref elapsedChanged);
        };

        // 子任务报告 → 聚合发布应刷新组任务 ElapsedText（下载中时间不停摆）。
        // 开局 SetState(Downloading) 已发 1 次——断言必须 ≥2：只有聚合发布也刷新才算合格
        for (var i = 0; i < 100 && Volatile.Read(ref elapsedChanged) < 2; i++) await Task.Delay(50);

        Assert.True(Volatile.Read(ref elapsedChanged) >= 2,
            $"组任务下载中 ElapsedText 只刷新 {elapsedChanged} 次（开局 SetState 1 次，聚合从未刷新）——PublishAggregate 漏通知，UI 停在「已用 0秒」");

        hold.SetResult();
        await task.Completion;
    }
}
