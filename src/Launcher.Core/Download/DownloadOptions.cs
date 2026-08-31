using Launcher.Core.Utils;

namespace Launcher.Core.Download;

/// <summary>
/// 下载性能配置（PCL2 参考：库并发 63 上限；本启动器默认保守合理值）。
/// </summary>
public sealed class DownloadOptions
{
    /// <summary>库文件并行数</summary>
    public int LibraryConcurrency { get; init; } = 8;

    /// <summary>资源文件并行数</summary>
    public int AssetConcurrency { get; init; } = 16;

    /// <summary>大文件分片连接数</summary>
    public int ChunkCount { get; init; } = 8;

    /// <summary>分片读取缓冲区（字节）</summary>
    public int BufferSize { get; init; } = 81920;

    /// <summary>整轮尝试数（每轮遍历全部候选源；2 轮足够——连接 15s 超时 + 0.5s 退避下轮间开销极低）</summary>
    public int MaxSourceAttempts { get; init; } = 2;

    /// <summary>下载源策略（官方优先 / 镜像优先 / 仅镜像）——8-18 默认镜像优先：GitHub 下载先走加速镜像</summary>
    public DownloadSourcePreference DownloadSource { get; init; } = DownloadSourcePreference.MirrorFirst;

    /// <summary>全局下载限速（字节/秒；0 = 不限速）</summary>
    public long BytesPerSecond { get; init; }

    /// <summary>轮间退避（测试注入 0 加速；null → RetryPolicy.Backoff）</summary>
    public Func<int, TimeSpan>? BackoffProvider { get; init; }

    /// <summary>竞速淘汰评估间隔（AL59：到点无赢家 → 取消非领先源；测试注入短值加速）</summary>
    public TimeSpan RaceEliminateInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>源死亡判定：实时速度持续低于此值（字节/秒；AL61 下载中自动换源）</summary>
    public long SlowSpeedBps { get; init; } = 100 * 1024;

    /// <summary>
    /// 响应头超时（毫秒，AL64）：TCP 半开连接上 SendAsync(ResponseHeadersRead) 永不返回
    /// → 子任务卡死 → 组任务 WhenAll 挂 10 小时「下载中」（真机 08-11：26.2+Fabric 148.2/148.5MB
    /// 满速卡死）。响应头 N 秒拿不到判源死换路；body 下载不受限（大文件慢网继续）。
    /// </summary>
    public long ResponseHeaderTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// body 读心跳超时（毫秒，AL66）：每次数据到达重置 N 秒定时器，ReadAsync 挂起
    /// （TCP 半开静默——响应头到了、body 中途断流）→ 超时判源死抛可重试错误换路。
    /// 修复 AL61 慢速检测挂在数据循环体内、读挂起时永不执行的洞（真机 08-11：fabric-api
    /// 单候选卡 0.2MB 3 分钟+——头超时管不到、慢速检测跑不到、单候选无竞速）。
    /// </summary>
    public long ReadStallTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// 竞速幸存源 watchdog 停滞阈值（毫秒，8-14）：唯一幸存源（其余已被淘汰）连续零进度
    /// 超过此值且未收尾 → 摘除弃用进下一轮重赛（不等待任务——挂死任务可能无视取消）。
    /// 兜底源内三道防线（AL61/AL64/AL66）未覆盖的洞
    /// （实机：OBS 128MB ghproxy.net 赢家静默断流，任务无限挂起 8 分钟无任何日志）。
    /// 进度上报 250ms 精细粒度，健康源（≥判死阈值）必有增量，30s 阈值不误杀。
    /// </summary>
    public long RaceWatchdogStallMs { get; init; } = 30000;

    // ---------- 陪跑（Pace Runner，批次 41）：赢家降速时后台提前重赛、新源顶替 ----------

    /// <summary>陪跑总开关（false = 零行为变化，回滚保险）</summary>
    public bool PaceEnabled { get; init; } = true;

    /// <summary>陪跑源数（同时决定 RaceProgress/采样数组扩容大小与陪跑 idx 分配——改动须同步主循环两处）</summary>
    public int PaceMaxSources { get; init; } = 2;

    /// <summary>监督采样节拍（毫秒；测试注入 100ms 加速）</summary>
    public long PaceProbeIntervalMs { get; init; } = 1000;

    /// <summary>触发所需连续下降样本数（×节拍 = 连续下降秒数，默认 5s）</summary>
    public int PaceDeclineSamples { get; init; } = 5;

    /// <summary>窗口峰值样本数（×节拍 = 峰值窗口，默认 30s）</summary>
    public int PacePeakWindowSamples { get; init; } = 30;

    /// <summary>触发速度比：当前速度 &lt; 窗口次高值 × 此值才可触发（次高值防开局突发抬线；防稳定源抖动误触）</summary>
    public double PaceDeclineRatio { get; init; } = 0.5;

    /// <summary>顶替所需稳定领先样本数（×节拍 = 稳定领先秒数，默认 3s）</summary>
    public int PaceStableLeadSamples { get; init; } = 3;

    /// <summary>大文件门槛（小于此值不陪跑——小文件来不及降速就下完了）</summary>
    public long PaceMinTotalBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>剩余量守卫（剩余不足此值不触发——99% 收尾不折腾）</summary>
    public long PaceMinRemainBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>触发冷却（毫秒；防假触发后立即重复开赛）</summary>
    public long PaceCooldownMs { get; init; } = 30000;

    /// <summary>触发后淘汰评估宽限（毫秒；新入局的陪跑源免遭 eta 外推秒杀）</summary>
    public long PaceEliminateGraceMs { get; init; } = 10000;

    /// <summary>源死亡判定：采样间隔（毫秒）</summary>
    public long SlowProbeMs { get; init; } = 5000;

    /// <summary>源死亡判定：连续低速采样数（默认 5s×2 = 持续 10s 龟速判死——8-31 从 30s 缩到 10s：
    /// 前期不乏力，慢源 10s 内退场换源；SlowsSourceDetector 强制 ≥2 次采样防慢启动误杀）</summary>
    public int SlowSamples { get; init; } = 2;

    public static DownloadOptions Default { get; } = new();

    /// <summary>按设置生成：并发档位 → 分片/库/资源并发；MaxConcurrentDownloads 优先于档位（改动即时生效）</summary>
    public static DownloadOptions FromSettings(LauncherSettings s)
    {
        var tier = (int)s.DownloadTier;
        return new DownloadOptions
        {
            ChunkCount = s.ChunkCount > 0 ? s.ChunkCount : tier,
            BufferSize = s.BufferSize > 0 ? s.BufferSize : 81920,
            LibraryConcurrency = s.MaxConcurrentDownloads > 0 ? s.MaxConcurrentDownloads : tier,
            AssetConcurrency = s.MaxConcurrentDownloads > 0 ? Math.Max(s.MaxConcurrentDownloads * 2, 16) : tier * 2,
            DownloadSource = s.DownloadSource,
            BytesPerSecond = s.DownloadSpeedLimitKbps > 0 ? s.DownloadSpeedLimitKbps * 1024 : 0,
        };
    }
}
