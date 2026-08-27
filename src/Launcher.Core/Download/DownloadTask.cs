using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Diagnostics;

namespace Launcher.Core.Download;

public enum DownloadTaskState { Queued, Downloading, Verifying, Completed, Failed, Canceled, Paused }

/// <summary>
/// 下载任务（全局下载中心 UI 数据源）：叶子任务（下载一个文件）或组任务（版本下载，Children 为各文件）。
/// 组任务聚合子进度（按 Weight 加权），状态推导：有失败→失败、取消→取消、否则完成。
/// 属性更新通过 Enqueue 时捕获的 SynchronizationContext 封送（测试环境为 null → 同步直跑）。
/// </summary>
public partial class DownloadTask : ObservableObject
{
    private static readonly Dictionary<DownloadTaskState, string> StateTexts = new()
    {
        [DownloadTaskState.Queued] = "排队",
        [DownloadTaskState.Downloading] = "下载中",
        [DownloadTaskState.Verifying] = "校验中",
        [DownloadTaskState.Completed] = "完成",
        [DownloadTaskState.Failed] = "失败",
        [DownloadTaskState.Canceled] = "已取消",
        [DownloadTaskState.Paused] = "已暂停",
    };

    private CancellationTokenSource _cts = new();
    private readonly SynchronizationContext? _ui;
    private readonly Stopwatch _watch = new();
    private readonly object _lock = new();
    private readonly List<CancellationTokenRegistration> _externalCancellations = [];
    private long _lastBytes = -1;
    // REVIEW-速度：滑动窗口计速——近 2 秒瞬时速度采样点 (时间, 字节)。旧实现用「累计平均」
    // （基线到现在的全程/总耗时），前快后慢时显示虚高数倍（真机 8-11：显示十几 MB/s 实际几 MB/s）。
    // 文件切换/字节回退（重试、新子任务）时清空重采样。
    private readonly List<(double Time, long Bytes)> _speedSamples = [];

    // 暂停/继续：保留 work 委托与用户暂停标记；恢复时重放（文件断点续传）
    private Func<DownloadProgressHandler, CancellationToken, Task>? _work;
    private Func<DownloadGroupContext, CancellationToken, Task>? _groupWork;
    private volatile bool _suspendRequested;

    // REVIEW-B2：自动重试排程标记——排程期间不完成 Completion（TCS 只应在「真正终态」完成：
    // 成功 / 重试耗尽 / 取消 / 暂停）。旧代码首次失败就 TrySetResult，调用方（安装完成判定、
    // 失败弹窗、历史记录、自动移除）在重试还在排期时就收尾 → 网络抖动误报「安装失败」，
    // 重试实际成功却 UI 永久停在失败。重试开始（Retry/Resume）复位。
    private volatile bool _retryPending;

    /// <summary>8-18 自动重试排程中（UI 用：首败将重试 → 弹「正在自动重试」而非「失败」）</summary>
    public bool IsAutoRetryPending => _retryPending;

    /// <summary>8-18 自动重试排程事件（UI 订阅弹提示；Attempt/Total 供文案）</summary>
    public sealed record AutoRetryArgs(int Attempt, int Total);

    public event EventHandler<AutoRetryArgs>? AutoRetryScheduled;

    // REVIEW-B R-01：重试代际——手动 Retry/Resume 抢先接管时递增，旧排程的 Delay 到点后
    // 发现代际不符即作废（否则手动重跑耗尽失败后，旧排程仍触发 Retry → 终态后幽灵重跑）
    private int _retryGeneration;

    /// <summary>失败诊断（AL44 统一诊断：原因+建议+修复动作；UI 错误区显示）</summary>
    [ObservableProperty]
    public partial DiagnosticHit? Diagnosis { get; set; }

    /// <summary>已自动重试次数（AL69.1 多轮机会：网络间歇恢复需要试几次才放弃；用户手动 Retry 不重置）</summary>
    private int _autoRetryCount;

    /// <summary>自动重试上限（叶子 2 次 = 共 3 次尝试；网络类失败才触发——重试耗尽才弹失败窗）</summary>
    private const int MaxAutoRetries = 2;

    /// <summary>组任务的子任务（AddChild 创建）：不自动重试——组内多子任务同时失败全重试=风暴，组语义靠子任务终态推导</summary>
    internal bool IsGroupChild;

    public string Name { get; }

    /// <summary>来源直链（第三方下载；下载历史「重新下载」用；普通下载为 null）</summary>
    public string? SourceUrl { get; set; }

    /// <summary>目标路径（下载历史「打开位置」用；普通下载为 null）</summary>
    public string? TargetPath { get; set; }

    /// <summary>
    /// 完成信号（内部 TCS，**终态**（完成/失败/取消）才完成；暂停不完成——Resume 后继续等待）。
    /// 对象稳定：Resume 重跑不替换（下游 await/自动移除/角标订阅始终有效）。
    /// </summary>
    private readonly TaskCompletionSource _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completionTcs.Task;

    public bool IsActive => State is DownloadTaskState.Queued or DownloadTaskState.Downloading or DownloadTaskState.Verifying;

    /// <summary>组任务：子任务集合（叶子为空）。Children 不进 DownloadManager.Tasks，不参与 ActiveCount</summary>
    public DownloadTask? Parent { get; }
    public ObservableCollection<DownloadTask> Children { get; } = [];
    public bool IsGroup { get; }
    public bool HasChildren => Children.Count > 0;

    /// <summary>聚合权重（预估字节；0 = 进度不确定）</summary>
    public long Weight { get; internal set; }

    [ObservableProperty]
    public partial DownloadTaskState State { get; set; } = DownloadTaskState.Queued;

    /// <summary>8-18 排队序号（并发门等待位；0 = 未排队）。入队时快照，用于「排队（前面 N 个任务）」提示</summary>
    public int QueuePosition { get; internal set; }

    /// <summary>
    /// 同步终态（内部）：State 通过 UI Post 异步生效，而 Completion 同步完成——
    /// 组任务推导（WhenAll 后查子任务状态）若读 State 会读到旧值（AL5 竞态：子失败误判父完成）。
    /// 终态在 Post 前同步记录；Retry/Resume 重置。
    /// </summary>
    internal volatile DownloadTaskState TerminalState;

    [ObservableProperty]
    public partial string Stage { get; set; } = "排队等待…";

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial long BytesDone { get; set; }

    [ObservableProperty]
    public partial long TotalBytes { get; set; }

    [ObservableProperty]
    public partial double SpeedBps { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string StateText => StateTexts[State];
    public bool HasError => Error is not null;
    public bool IsPaused => State == DownloadTaskState.Paused;
    public bool IsFailed => State == DownloadTaskState.Failed;
    public bool HasProgress => ProgressPercent > 0;

    public string SpeedText => SpeedBps >= 1024 * 1024
        ? $"{SpeedBps / 1024 / 1024:0.0} MB/s"
        : SpeedBps >= 1024 ? $"{SpeedBps / 1024:0} KB/s" : "";

    public string EtaText
    {
        get
        {
            if (State != DownloadTaskState.Downloading || SpeedBps <= 0 || TotalBytes <= BytesDone) return "";
            var ts = TimeSpan.FromSeconds((TotalBytes - BytesDone) / SpeedBps);
            return $"剩余 {ts.Minutes}:{ts.Seconds:00}";
        }
    }

    public string BytesText => $"{FormatBytes(BytesDone)} / {FormatBytes(TotalBytes)}";

    /// <summary>已用/总耗时（不含挂起时间；API 查询阶段等无速度段的时间一眼可见——下载页第四列）</summary>
    public string ElapsedText
    {
        get
        {
            var ts = TimeSpan.FromSeconds(_watch.Elapsed.TotalSeconds);
            var text = ts.TotalMinutes >= 1 ? $"{ts.Minutes}分{ts.Seconds:00}秒" : $"{ts.Seconds}秒";
            return State == DownloadTaskState.Completed || State == DownloadTaskState.Failed
                ? $"总耗时 {text}"
                : $"已用 {text}";
        }
    }

    /// <summary>子任务迷你行文本（叶子任务的百分比文字；无总量时显示已下载字节——压缩包无 Content-Length 也有进度感）</summary>
    public string ChildProgressText => HasProgress ? $"{ProgressPercent:0}%" : (BytesDone > 0 ? FormatBytes(BytesDone) : "…");

    public IRelayCommand CancelCommand { get; }
    public IRelayCommand PauseCommand { get; }
    public IRelayCommand ResumeCommand { get; }
    public IRelayCommand RetryCommand { get; }

    // ---------- 构造 ----------

    /// <summary>叶子任务（下载一个文件）</summary>
    internal DownloadTask(string name, Func<DownloadProgressHandler, CancellationToken, Task> work, SynchronizationContext? ui)
        : this(name, ui)
    {
        _work = work;
        _ = RunAsync(work);
    }

    /// <summary>组任务（下载一个版本；children 由 DownloadGroupContext 创建）</summary>
    internal DownloadTask(string name, Func<DownloadGroupContext, CancellationToken, Task> groupWork, SynchronizationContext? ui)
        : this(name, ui)
    {
        IsGroup = true;
        _groupWork = groupWork;
        _ = RunGroupAsync(groupWork);
    }

    private DownloadTask(string name, SynchronizationContext? ui)
    {
        Name = name;
        _ui = ui;
        CancelCommand = new RelayCommand(Cancel);
        PauseCommand = new RelayCommand(Suspend);
        ResumeCommand = new RelayCommand(Resume);
        RetryCommand = new RelayCommand(Retry);
    }

    // ---------- 叶子生命周期 ----------

    private async Task RunAsync(Func<DownloadProgressHandler, CancellationToken, Task> work)
    {
        var myCts = _cts; // 8-22 修复（BUGS#17）：快照本 run 的 cts——Suspend 取消的是它；Resume/Retry 换新 cts 后
                          // 旧 run 靠引用比较识别自己已过期，catch/finally 一律静默退出，不碰共享状态
                          // （否则旧 run 的 OCE 晚到会误判 Failed + 排程幽灵重试，或提前完成 TCS）
        try
        {
            await Task.Run(async () =>
            {
                _watch.Start();
                SetState(DownloadTaskState.Downloading);
                await work(Report, myCts.Token);
                // 终态先同步记录（组任务推导依赖），再 Post 到 UI（State 异步生效）
                var final = myCts.IsCancellationRequested
                    ? (_suspendRequested ? DownloadTaskState.Paused : DownloadTaskState.Canceled)
                    : DownloadTaskState.Completed;
                TerminalState = final;
                SetState(final);
                // AL62：完成文案——「已下载 19MB」替代停在「下载中…」的错觉（100% 与 Stage 同一 Post）
                if (!myCts.IsCancellationRequested)
                    Post(() => { ProgressPercent = 100; Stage = $"已下载 {FormatBytes(BytesDone)}"; });
            });
        }
        catch (OperationCanceledException) when (myCts.IsCancellationRequested)
        {
            if (myCts != _cts) return; // 已 Resume/Retry——旧 run 过期，静默退出（新 run 负责终态）
            var s = _suspendRequested ? DownloadTaskState.Paused : DownloadTaskState.Canceled;
            TerminalState = s;
            SetState(s);
            // 8-19 生态修缮阶段3：真取消清理中间产物（.parts/.tmp/.race*）；暂停保留 .parts 续传材料
            if (s == DownloadTaskState.Canceled) CleanupOnTerminalFailure();
        }
        catch (OperationCanceledException ex)
        {
            // AL34：token 未被请求的 OCE（HttpClient 超时等内部泄漏）→ 失败并带信息——
            // 静默"已取消"在 UI 上不可重试（RetryCommand 只认 Failed）还丢错误原因（探针 08-09 asm 即此）
            if (myCts != _cts) return; // 8-22：旧 run 过期——不误判 Failed、不排程幽灵重试
            TerminalState = DownloadTaskState.Failed;
            var msg = $"下载中断（{ex.GetType().Name}: {ex.Message}）";
            SetState(DownloadTaskState.Failed, msg);
            // AL68 停滞透明化：失败原因进 Stage——组任务推导时显示「失败：…」而非无信息的「正在完成…」
            SetStage($"失败：{msg}");
            ScheduleAutoRetry(ex, allowRetry: true);
        }
        catch (Exception ex)
        {
            if (myCts != _cts) return; // 8-22：旧 run 过期
            TerminalState = DownloadTaskState.Failed;
            // AL30：Error 与 State 同一 Post 内先 Error 后 State——PropertyChanged(State) 触发下游
            // （下载历史 Record）时错误已可见；旧写法分开 Post，Error 晚于 State 生效 → 历史记 Error=null
            // （真机 08-07 10:37 失败原因丢失即此，诊断全靠猜）。
            SetState(DownloadTaskState.Failed, ex.Message);
            // AL68：失败原因进 Stage（同上——停滞期用户看到「失败：连接被拒…」而非死寂）
            SetStage($"失败：{ex.Message}");
            ScheduleAutoRetry(ex, allowRetry: true);
        }
        finally
        {
            _watch.Stop();
            // 8-22 修复（BUGS#17）：旧 run（已被 Resume/Retry 取代）不碰 Completion——新 run 的 finally 负责收尾
            // （C# 禁止 finally 内 return——用条件合并）
            // 终态（含 Paused？否——暂停只是挂起，Resume 后继续；这里 Paused 由 Suspend 的 Cancel 触发，
            // 需区分：用户暂停 → 不完成；取消 → 完成）。用 _suspendRequested 判定。
            // REVIEW-B2：自动重试排程中（_retryPending）也暂不完成——等重试最终结果，
            // 避免调用方在重试耗尽前误收尾（误报失败弹窗/历史记失败）
            if (myCts == _cts && !_suspendRequested && !_retryPending) _completionTcs.TrySetResult();
        }
    }

    // ---------- 组生命周期 ----------

    private async Task RunGroupAsync(Func<DownloadGroupContext, CancellationToken, Task> groupWork)
    {
        var myCts = _cts; // 8-22 修复（BUGS#17）：同叶子——旧 run 快照 cts，Resume/Retry 换新后静默退出
        try
        {
            await Task.Run(async () =>
            {
                _watch.Start();
                SetState(DownloadTaskState.Downloading);
                var ctx = new DownloadGroupContext(this, _ui);
                await groupWork(ctx, myCts.Token);                 // 编排：创建并等待全部子任务（本 run 快照——旧 run 取消不影响新 run）
                // REVIEW-卡完成：首败早退——任一子任务终态失败（组内叶子无自动重试，失败即终态）
                // 立即进失败分支（取消其余兄弟 + 组失败），不再等 2000 个 assets 全部下完才报错
                // （BUGS#4/#5 级联取消失效/「正在完成」死寂的根治；正常全成功路径与 WhenAll 等价）
                await Task.WhenAny(Task.WhenAll(ctx.Children.Select(c => c.Completion)), ctx.FirstFailure);

                // AL5：用子任务同步终态推导——子任务 State 经 UI Post 异步生效，Completion 同步完成，
                // WhenAll 返回时读 State 会读到旧值（Downloading）→ 子失败误判父完成（下载历史误报"完成"）
                var failed = ctx.Children.FirstOrDefault(c => c.TerminalState == DownloadTaskState.Failed);
                // 任一子任务失败 → 级联取消其余兄弟（停止无效下载/写盘，如版本下载中一个库 404 不再白白下 assets）
                if (failed is not null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    foreach (var c in ctx.Children) c.Cancel();
                }
                // 失败优先（内部级联取消后父任务仍是 Failed）；用户主动取消时子任务全 Canceled → 走 Canceled 分支
                if (failed is not null)
                {
                    TerminalState = DownloadTaskState.Failed;
                    SetState(DownloadTaskState.Failed, failed.Error ?? "子任务失败");
                    // REVIEW-节流：终态后聚合推导被守卫挡住——失败 Stage 须显式设置；
                    // 内容延迟到队列内解析（Post 在子任务 Error Post 之后 FIFO——
                    // 首败早退时 Error 的 Post 可能还没执行，直接读会拿到 null）
                    Post(() => SetStage($"失败：{failed.Error ?? "子任务失败"}"));
                }
                else if (_cts.IsCancellationRequested)
                {
                    var s = _suspendRequested ? DownloadTaskState.Paused : DownloadTaskState.Canceled;
                    TerminalState = s;
                    SetState(s);
                }
                else
                {
                    TerminalState = DownloadTaskState.Completed;
                    SetState(DownloadTaskState.Completed);
                    _selfStage = null; // AL70：终态清自设 Stage（防「获取资源清单…」残留）
                    Post(() => { ProgressPercent = 100; Stage = "已完成"; });
                }
            });
        }
        catch (OperationCanceledException) when (myCts.IsCancellationRequested)
        {
            if (myCts != _cts) return; // 已 Resume/Retry——旧 run 过期，静默退出
            var s = _suspendRequested ? DownloadTaskState.Paused : DownloadTaskState.Canceled;
            TerminalState = s;
            SetState(s);
            // 8-19 生态修缮阶段3：真取消清理中间产物；暂停保留 .parts 续传材料
            if (s == DownloadTaskState.Canceled) CleanupOnTerminalFailure();
        }
        catch (OperationCanceledException ex)
        {
            // AL34：token 未被请求的 OCE（内部泄漏）→ 失败并带信息，避免"神秘已取消"
            if (myCts != _cts) return; // 8-22：旧 run 过期
            TerminalState = DownloadTaskState.Failed;
            var msg = $"安装中断（{ex.GetType().Name}: {ex.Message}）";
            SetState(DownloadTaskState.Failed, msg);
            SetStage($"失败：{msg}"); // AL68：组失败也亮明原因（终态后 Stage 推导不再跑，须自己设）
            ScheduleAutoRetry(ex, allowRetry: true); // AL69.1：同下
        }
        catch (Exception ex)
        {
            if (myCts != _cts) return; // 8-22：旧 run 过期
            TerminalState = DownloadTaskState.Failed;
            // AL30：Error 与 State 同一 Post 内先 Error 后 State——PropertyChanged(State) 触发下游
            // （下载历史 Record）时错误已可见；旧写法分开 Post，Error 晚于 State 生效 → 历史记 Error=null
            // （真机 08-07 10:37 失败原因丢失即此，诊断全靠猜）。
            SetState(DownloadTaskState.Failed, ex.Message);
            SetStage($"失败：{ex.Message}"); // AL68：同上
            // AL69.1：组任务编排层抛错也自动重试（网络间歇多轮机会）——叶子失败聚合（failed 分支）
            // 不经过这里，不会重复重试风暴
            ScheduleAutoRetry(ex, allowRetry: true);
        }
        finally
        {
            _watch.Stop();
            // 8-22 修复（BUGS#17）：旧 run 不碰 Completion（新 run 负责收尾）
            // REVIEW-B2：同叶子——组编排层抛错排程了重试（_retryPending）时不完成，等重试最终结果
            if (myCts == _cts && !_suspendRequested && !_retryPending) _completionTcs.TrySetResult();
        }
    }

    // ---------- 暂停 / 继续 ----------

    /// <summary>暂停：取消当前执行（文件断点保留），状态置"已暂停"。
    /// 用 _suspendRequested（volatile）判断，不依赖 State 的即时性（UI 线程 Post 异步时状态可能滞后）。</summary>
    public void Suspend()
    {
        if (_suspendRequested) return;
        _suspendRequested = true;
        foreach (var child in Children) child.Suspend();
        _cts.Cancel();
    }

    /// <summary>失败重试：清错误重跑 work（断点续传已下载部分）</summary>
    /// <summary>
    /// AL44 失败诊断 + 自动重试（AL69.1 多轮：叶子上限 2 次 = 共 3 次尝试——网络间歇恢复需要机会，
    /// 全部用尽才弹失败窗；组任务编排层抛错也重试 2 次，叶子失败聚合不重试防风暴）。
    /// 网络/校验类失败 Post 排队重跑（State 经 Post 异步生效，直接调会空转）；取消/暂停竞态守卫挡住。
    /// </summary>
    private void ScheduleAutoRetry(Exception ex, bool allowRetry)
    {
        var hit = FailureDiagnostics.ForDownload(ex, _autoRetryCount >= MaxAutoRetries);
        if (hit is not null) Post(() => Diagnosis = hit);
        // 8-18 真正终态失败（不可重试/自动重试也耗尽）：清中间产物不留垃圾——但保留 .parts 给
        // 重试续传的语义只在「还有后续尝试」时成立；终态后 .parts/.tmp 是纯垃圾（Task 层判定，
        // Service 层 attempt 耗尽不清理——Task 自动重试还要靠它换源续传）
        if (!allowRetry || _autoRetryCount >= MaxAutoRetries || IsGroupChild)
        {
            CleanupOnTerminalFailure();
            return;
        }
        if (hit is not { Fix: FixKind.RetryDownload or FixKind.Redownload })
        {
            CleanupOnTerminalFailure();
            return;
        }
        _autoRetryCount++;
        _retryPending = true; // REVIEW-B2：排程重试 → 本 finally 不完成 Completion，等重试最终结果
        var attempt = _autoRetryCount;
        var gen = _retryGeneration; // R-01：本次排程的代际快照
        // 与 SetState 的 Post 同队列 FIFO：State=Failed 生效后本 Post 才执行 → Retry 不空转
        Post(() =>
        {
            if (_cts.IsCancellationRequested || _suspendRequested)
            {
                // 想重试但用户已取消/暂停 → 这就是最终状态：复位标记并完成 TCS
                // （否则 finally 因 _retryPending 跳过完成，调用方 await Completion 永久挂起）
                _retryPending = false;
                _completionTcs.TrySetResult();
                return;
            }
            // AL68 停滞透明化：重试前先亮明原因 + 第几轮（UI 线程置 Stage），延迟后真正重跑——
            // 否则「失败→瞬态重试」用户看不到停滞原因，体感=死寂「正在完成…」
            Stage = $"网络异常，自动重试第 {attempt}/{MaxAutoRetries} 次…";
            AutoRetryScheduled?.Invoke(this, new AutoRetryArgs(attempt, MaxAutoRetries)); // 8-18：UI 弹「正在自动重试」提示
            _ = Task.Run(async () =>
            {
                await Task.Delay(attempt == 1 ? 800 : 3000); // 第 2 轮退避（网络恢复窗口）
                if (_cts.IsCancellationRequested || _suspendRequested)
                {
                    // REVIEW-B2：排程期间被取消/暂停 → 重试作废。必须复位 + 完成 TCS，
                    // 否则 _retryPending 永真（work 已终态，finally 不再跑）→ 调用方 await Completion 永久挂起
                    _retryPending = false;
                    _completionTcs.TrySetResult();
                    return;
                }
                if (_retryGeneration != gen) return; // R-01：期间被手动 Retry/Resume 接管 → 本排程作废
                Post(() => Retry());
            });
        });
    }

    /// <summary>
    /// 8-18 终态失败清理：任务有自有目标路径（第三方下载）→ 清中间产物（.tmp/.parts/.race*），
    /// destPath 本体不动（幂等语义）。组任务/叶子 TargetPath=null 不清——组重试时叶子续传材料保留。
    /// </summary>
    private void CleanupOnTerminalFailure()
    {
        if (TargetPath is { } tp) DownloadService.CleanupResiduals(tp);
    }

    public void Retry()
    {
        if (State != DownloadTaskState.Failed) return;
        _retryPending = false; // REVIEW-B2：重跑开始——本次结果的 finally 决定是否完成 TCS
        _retryGeneration++; // R-01：新代际——作废所有旧排程的重试
        _suspendRequested = false;
        TerminalState = default; // 重置同步终态（重跑后重新记录）
        _cts = new CancellationTokenSource();
        Post(() => { Error = null; Stage = "重试中…"; }); // AL68：重跑前清失败 Stage（避免旧「失败：…」残留）
        if (IsGroup) Post(() => Children.Clear());
        if (IsGroup && _groupWork is not null)
            _ = RunGroupAsync(_groupWork);
        else if (_work is not null)
            _ = RunAsync(_work);
    }

    /// <summary>继续：重放 work（断点续传已下载部分）</summary>
    public void Resume()
    {
        if (!_suspendRequested) return;
        _suspendRequested = false;
        _retryGeneration++; // R-01：新代际——作废所有旧排程的重试
        TerminalState = default; // 重置同步终态（重跑后重新记录）
        _cts = new CancellationTokenSource();
        if (IsGroup) Post(() => Children.Clear()); // 清掉暂停的旧子任务，重跑会新建
        if (IsGroup && _groupWork is not null)
            _ = RunGroupAsync(_groupWork);
        else if (_work is not null)
            _ = RunAsync(_work);
    }

    /// <summary>
    /// 挂载子任务并订阅聚合（由 DownloadGroupContext 在线程池调用）。
    /// 整个挂载过程封送 UI 线程：Children（ObservableCollection）的全部读写
    /// （Add / OnChildPropertyChanged / RecomputeAggregate / Cancel 遍历）收敛到同一线程，
    /// 消除"线程池 Add 与 UI 线程枚举"的 Collection was modified 竞态（曾导致闪退）。
    /// </summary>
    internal void AttachChild(DownloadTask child)
    {
        Post(() =>
        {
            lock (_lock)
            {
                Children.Add(child);
                child.PropertyChanged += OnChildPropertyChanged;
                // 父取消级联：覆盖"父先取消、子后创建"的时序（Children 级联只覆盖已存在的子任务）
                child._externalCancellations.Add(_cts.Token.Register(child.Cancel));
                ScheduleRecompute(); // 挂载也走节流入口（窗口内合并，总量随尾算发布）
            }
        });
    }

    /// <summary>组聚合节流窗口（毫秒）：并行子任务多时（131 库）旧代码每次进度变化同步重算 +
    /// 父属性变化逐个 Post UI → 每秒几百次 Post 积压 → 进度显示滞后（真机 8-12「数据跟不上」）。
    /// 窗口内合并：最多 250ms 一次广播 + 60ms 防抖尾算保证最终一致。</summary>
    private const long AggregateWindowMs = 250;
    private long _lastAggregateMs; // Interlocked.Read/Exchange 访问（long 不可 volatile）
    private int _aggregateDirty;
    private int _aggregateTimerPending;

    private void OnChildPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgressPercent) or nameof(TotalBytes)
            or nameof(State) or nameof(Stage) or nameof(Error))
        {
            ScheduleRecompute();
        }
    }

    private void ScheduleRecompute()
    {
        // REVIEW-节流重构：同步算快照（O(children) 可接受），只节流「发布」。
        // 发布值 = 窗口内最大 percent（_pendingPercent 单调）——节流不吞峰值：
        // 旧实现「当前值单调」在窗口内被新挂载覆盖（99 被 69.3 吞掉）→ 爬不回去（真机 8-12）
        ComputeSnapshot();
        var now = _watch.ElapsedMilliseconds;
        var last = Interlocked.Read(ref _lastAggregateMs);
        if (now - last >= AggregateWindowMs
            && Interlocked.CompareExchange(ref _lastAggregateMs, now, last) == last)
        {
            PublishAggregate();
            return;
        }
        if (Interlocked.Exchange(ref _aggregateTimerPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            Interlocked.Exchange(ref _aggregateTimerPending, 0);
            if (Volatile.Read(ref _aggregateDirty) == 0) return;
            ComputeSnapshot();
            Interlocked.Exchange(ref _lastAggregateMs, _watch.ElapsedMilliseconds);
            PublishAggregate();
        });
    }

    /// <summary>窗口内待发布的最大聚合值（percent 单调基准——节流不丢峰值）</summary>
    private double _pendingPercent = -1;
    private string? _pendingStage;
    private long _pendingTotal;

    /// <summary>加权聚合快照（节流版计算侧）：同步算 TotalBytes=ΣWeight、percent=Σ(Weight×child%)/Σ、
    /// Stage=最后活动子任务——结果进 _pending*（窗口内 max 语义），发布由 PublishAggregate 节流执行。
    /// 与 AttachChild 共用锁（Monitor 可重入）保证 Children 迭代/修改互斥，防御偶发 NRE/竞态。</summary>
    private void ComputeSnapshot()
    {
        if (!IsGroup) return;
        // 终态后迟到快照不得覆盖——完成路径已设 Stage="已完成"/percent=100（真机 8-12 测试暴露）
        if (State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled) return;

        lock (_lock)
        {
            long total = 0;
            double weighted = 0;
            DownloadTask? active = null;
            foreach (var c in Children)
            {
                if (c is null) continue; // 防御
                var w = Math.Max(c.Weight, 0);
                total += w;
                weighted += w * c.ProgressPercent;
                if (c.IsActive) active = c;
            }
            active ??= Children.LastOrDefault(c => c is not null && c.State == DownloadTaskState.Downloading);
            // AL68 停滞透明化：叶子失败（判死/重试排期）时组显示其「失败：原因」Stage——
            // 不再退回无信息的「正在完成…」（用户卡在末尾看到死寂文案的根因）
            active ??= Children.LastOrDefault(c => c is not null
                && c.State == DownloadTaskState.Failed && !string.IsNullOrEmpty(c.Stage));

            var percent = total > 0 ? weighted / total : 0;
            // REVIEW-进度：窗口内最大值（_pendingPercent）单调——节流合并不吞峰值
            // （c1 收敛 99 后挂载新任务，快照 69.3 —— max 保住 99，发布不回落）
            if (percent > _pendingPercent) _pendingPercent = percent;
            // AL38：叶子全完成后 active=null——组在收尾（VerifyInstalled 校验 + 打标记）。
            // AL70：无 active 叶子时优先显示组 SetStage 的值，从未 SetStage 才回兜底。
            var anyRunning = Children.Any(c => c is not null
                && c.State is DownloadTaskState.Queued or DownloadTaskState.Downloading);
            _pendingStage = active?.Stage ?? _selfStage
                ?? (total > 0 ? (anyRunning ? "正在下载…" : "正在完成…") : "准备中…");
            _pendingTotal = total;
            Interlocked.Exchange(ref _aggregateDirty, 1);
        }
    }

    /// <summary>聚合发布（节流窗口到期/尾算）：写 UI 属性。percent 取「窗口内最大值」与「已发布值」的
    /// 较大者（单调不回跳）；封顶 99——100 只由 RunGroupAsync 完成路径 Post 给出（AL33）。
    /// AL32 教训（clamp 卡 100%）：percent 冻结期间 BytesDone=total×percent 仍随新 total 推进。</summary>
    private void PublishAggregate()
    {
        // 终态后发布的旧排期快照不得覆盖——完成路径已设 Stage="已完成"/percent=100
        if (State is DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled) return;
        lock (_lock)
        {
            var percent = Math.Min(Math.Max(_pendingPercent, ProgressPercent), 99);
            ProgressPercent = percent;
            BytesDone = (long)(_pendingTotal * percent / 100);
            TotalBytes = _pendingTotal;
            if (_pendingStage is not null) Stage = _pendingStage;
            // 聚合计速（滑动窗口：近 2s 瞬时——聚合字节单调但窗口随新子任务挂载重置）
            UpdateSpeedSample(BytesDone);
        }
        _pendingPercent = -1;
        _pendingStage = null;
        _pendingTotal = 0;
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(BytesText));
        // 8-27 已用时间 0秒：组任务聚合发布漏发 ElapsedText——组不走叶子 Report 路径，
        // ElapsedText 只靠 SetStage/SetState 刷新（开局一次后整场停摆「已用 0秒」）。补上随节流窗口刷新。
        OnPropertyChanged(nameof(ElapsedText));
    }

    // ---------- 控制 ----------

    /// <summary>滑动窗口瞬时速度：近 2 秒字节差/时间差（调用方需持锁；字节回退时调用方已清队列）</summary>
    private double SampleSpeed(double now, long bytes)
    {
        _speedSamples.Add((now, bytes));
        // 至少保留最近 2 点（裁剪过度会把窗口内点全删光 → 无速度可算 → 停在旧值）
        while (_speedSamples.Count > 2 && now - _speedSamples[0].Time > 2.0)
            _speedSamples.RemoveAt(0);
        if (_speedSamples.Count < 2) return SpeedBps;
        var first = _speedSamples[0];
        var last = _speedSamples[^1];
        var dt = last.Time - first.Time;
        var db = last.Bytes - first.Bytes;
        return dt > 0.25 && db >= 0 ? db / dt : SpeedBps;
    }

    /// <summary>聚合采样入口：字节回退（新子任务挂载）时清窗口，避免负速/虚高。
    /// 8-24 补 AL70 封顶：与叶子 Report 一致（min(inst, 全程平均×1.5)）——组任务开局大量
    /// 小库完成/多源竞速首字节并发时聚合字节猛跳，无封顶会显示几百 MB/s 假爆发。</summary>
    private void UpdateSpeedSample(long bytes)
    {
        var now = _watch.Elapsed.TotalSeconds;
        if (_lastBytes < 0 || bytes < _lastBytes)
        {
            _speedSamples.Clear();
            _lastBytes = -1;
        }
        var inst = SampleSpeed(now, bytes);
        var avg = now > 0.05 ? bytes / now : inst;
        SpeedBps = Math.Min(inst, avg * 1.5); // 与叶子 AL70 封顶一致的纯截断
        _lastBytes = bytes;
    }

    public void Cancel()
    {
        _cts.Cancel();
        foreach (var child in Children) child.Cancel();
    }

    /// <summary>报告进度（可来自任意线程）：滑动窗口计速（近 2s 瞬时——非旧实现的全程累计平均）</summary>
    private void Report(DownloadProgress p)
    {
        string stage, speedText, etaText, bytesText;
        double speed, overall;
        long done, total;
        lock (_lock)
        {
            var now = _watch.Elapsed.TotalSeconds;
            if (_lastBytes < 0 || p.FileBytesDone < _lastBytes)
            {
                _speedSamples.Clear(); // 文件切换/回退：清窗口重采样
                _lastBytes = -1;
            }
            var inst = SampleSpeed(now, p.FileBytesDone);
            _lastBytes = p.FileBytesDone;
            // AL70 防爆表：竞速完成瞬间剩余字节挤进窗口尾部 → 窗口瞬时虚高数倍（实机 19MB 真下 10s 显示几百 MB/s）。
            // 封顶 = 全程平均×1.5（允许多源并发短期增益，但压住数倍爆表）——纯截断不动窗口语义：
            // 慢速时窗口如实显示慢速，只在瞬时远超全程平均时截断。开局（0.05s 内）也封——多源首字节并发
            // 瞬间的"爆发"同样会骗人（100MB 总量下 6s，开局窜 100+MB/s 即此）。
            var avg = now > 0.05 ? p.FileBytesDone / now : inst;
            speed = Math.Min(inst, avg * 1.5);

            stage = string.IsNullOrEmpty(p.Stage) ? "下载中…" : p.Stage;
            done = p.FileBytesDone;
            total = p.FileTotalBytes;
            overall = Math.Clamp(p.OverallPercent, 0, 99); // AL33：叶子 100 只由完成 Post 给

            speedText = speed >= 1024 * 1024 ? $"{speed / 1024 / 1024:0.0} MB/s"
                : speed >= 1024 ? $"{speed / 1024:0} KB/s" : "";
            etaText = speed > 0 && total > done
                ? $"剩余 {TimeSpan.FromSeconds((total - done) / speed):m\\:ss}"
                : "";
            bytesText = $"{FormatBytes(done)} / {FormatBytes(total)}";
        }
        Post(() =>
        {
            Stage = stage;
            BytesDone = done;
            TotalBytes = total;
            if (overall > ProgressPercent) { ProgressPercent = overall; OnPropertyChanged(nameof(HasProgress)); }
            SpeedBps = speed;
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(EtaText));
            OnPropertyChanged(nameof(BytesText));
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(ChildProgressText));
        });
    }

    private void SetState(DownloadTaskState state, string? error = null)
    {
        Post(() =>
        {
            if (error is not null) Error = error; // AL30：先 Error 后 State（同一 Post），见失败路径注释
            State = state;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(ElapsedText)); // 完成/失败切换"已用"→"总耗时"文案
        });
    }

    /// <summary>组任务自身最近一次 SetStage（AL70：无 active 叶子时兜底显示——index 获取等编排阶段）</summary>
    private string? _selfStage;

    /// <summary>跨线程更新 Stage（AL62 质检文案用——下载线程 → UI 线程安全）</summary>
    public void SetStage(string stage)
    {
        if (IsGroup) _selfStage = stage; // 同步记录——推导 Post 可能晚于本 Post，读 _selfStage 拿最新
        Post(() =>
        {
            Stage = stage;
            OnPropertyChanged(nameof(ElapsedText)); // API 查询等无字节阶段也刷新耗时（否则时间停摆）
        });
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return bytes > 0 ? $"{bytes} B" : "--"; // AL10.2：大小未知（weight=0 子任务）显示 "--" 而非 "0 B"
    }
}
