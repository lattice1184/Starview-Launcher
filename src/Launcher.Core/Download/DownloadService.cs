using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;
using PCL.Core.Logging;

namespace Launcher.Core.Download;

/// <summary>
/// 下载服务：大文件多连接 Range 分片并发 + 小文件单连接断点续传。
/// 外层换源回退：官方失败 → 镜像（BMCLAPI）→ 指数退避重试整轮；SHA1 校验 + 幂等 + 416 防御。
/// </summary>
public sealed class DownloadService
{
    /// <summary>同目标文件并发下载锁（同一 destPath 串行——避免并发写同一 jar 写坏）。
    /// 8-19 内存瘦身：+使用计数用完即删——旧实现只增不减（每下载一个文件驻留 entry：
    /// 路径串 + SemaphoreSlim + 内核句柄），长会话数千 entry 数 MB 且永不回收</summary>
    private sealed class FileLockEntry
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public int Users;
    }

    private static readonly ConcurrentDictionary<string, FileLockEntry> FileLocks = new();

    // 256KB 以上走 8 连接分片：国内直连 Modrinth CDN 单连接被限速（几十 KB/s），
    // 多连接分片可显著提速；弱网分片失败自动回退单连接（DownloadChunkedAsync catch）
    private const long ChunkThreshold = 256 * 1024;

    private readonly HttpClient _http;
    private readonly IDlSourceResolver _resolver;
    private readonly DownloadOptions _options;
    private readonly string _gameDirectory;
    private readonly SourceStats _sourceStats = new();
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<bool>> _networkChecker;

    public DownloadService(HttpClient? http = null, IDlSourceMapper? sourceMapper = null, string? gameDirectory = null)
        : this(http, sourceMapper is null ? ResolvingDlSourceMapper.Default : new ResolvingDlSourceMapper(sourceMapper),
            null, gameDirectory)
    {
    }

    public DownloadService(HttpClient? http, IDlSourceResolver? resolver, DownloadOptions? options, string? gameDirectory,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? networkChecker = null)
    {
        _http = http ?? CreateClient();
        _resolver = resolver ?? ResolvingDlSourceMapper.Default;
        _options = options ?? DownloadOptions.FromSettings(LauncherSettings.Current);
        _gameDirectory = gameDirectory ?? GameDirectory.Detect();
        // 8-22 限速根治（共享节流）：_limitPerStream 就是总限速——旧实现按 ChunkCount 均分到「每流」，
        // 但并发流数会变：渐进升片（1→8 片）早期只有 1/8 速率（Fabric API 等小文件磨死），
        // 多源竞速/升片后总流数 × 单流配额又超设定值（一瞬间速度超额）——两个症状同根。
        // 现由任务级共享累加器保证总吞吐恒=设定值（见 ThrottleState / ThrottleStreamAsync）。
        _limitPerStream = Math.Max(_options.BytesPerSecond, 0);
        _networkChecker = networkChecker
            ?? ((hosts, ct) => NetworkChecker.CheckAsync(hosts, TimeSpan.FromSeconds(3), ct));
    }

    /// <summary>总限速（任务内所有源/片/探测段共享一个节流累加器 → 总吞吐恒=设定值，与并发数无关）</summary>
    private readonly long _limitPerStream;

    /// <summary>
    /// 慢速判死阈值（BUGS#1 修复）：限速时阈值随限速下调——限速 50KB/s 时实测速度恒 50KB/s，
    /// 若阈值仍用默认 100KB/s 则 30s 必判死（限速用户全部下载失败）。取 min(默认, 限速×0.8)；
    /// 分片总吞吐 = 每流限速×片数，实际速度远超该阈值，安全。
    /// </summary>
    private long SlowThresholdForLimit()
        => _limitPerStream > 0
            ? Math.Min(_options.SlowSpeedBps, (long)(_limitPerStream * 0.8))
            : _options.SlowSpeedBps;

    /// <summary>任务级共享限速状态（同一文件所有源/片/探测段共用一个累加器——总吞吐=设定值恒成立；
    /// 旧实现每流独立，渐进升片/多源竞速下并发数变化 → 总和偏离设定值：片少时慢、片多源多时超）。
    /// 多流并发结算 → 访问由 ThrottleStreamAsync 内 lock 保护。</summary>
    internal sealed class ThrottleState
    {
        public long Bytes;
        public readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    }

    /// <summary>
    /// 分片进度共享上报（AL31）：各分片 Interlocked 累加已读字节 + 抢占式节流——
    /// 旧实现每片完成才 Invoke 一次，大文件进度/速度文字每片周期才刷新（观感延迟）。
    /// CompareExchange 抢占：同一时刻最多一个分片触发上报，节流窗口内最多报一次。
    /// </summary>
    private sealed class ChunkProgress
    {
        public long Bytes;
        public long LastReportMs;
        /// <summary>已上报的最大进度（ReportOnce 护栏：只允许递增上报，杜绝快照读+晚 Invoke 的倒序）</summary>
        public long Reported;
        public readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>节流窗口（毫秒）；下载任务 UI 刷新间隔在此内不可感知，且避免高频 Post</summary>
        public const long WindowMs = 250;
    }

    /// <summary>
    /// 竞速进度合并器（AL57/AL58）：竞速时多源同时下载同一文件的不同副本，共享同一进度回调。
    /// 透传各源绝对字节会混合虚高（字节回退 → 计速基线重置 → 完成瞬间速度爆表），
    /// 而多源累加又造成"下完了"错觉（每源拉完整副本，累加必然虚满——限并发镜像下
    /// 没有任何源能赢，UI 却停在 99%）。
    /// 正确语义：进度 = 领先源的真实进度（所有源里已完成字节最大者）——单调、不超 total、
    /// 不虚满。UI 端"累计/总时间"平均速度同样回到真实值。
    /// </summary>
    internal sealed class RaceProgress
    {
        /// <summary>竞速所有源共享：跨源最大字节 + 惰性定的文件总大小</summary>
        private sealed class Shared
        {
            public long Total;    // 首个源报告时定（各源 total 一致）
            public long Max;      // 所有源里已完成字节最大者（单调）
            public long LastSent; // 已转发的字节（同值不重复转发——按源记录会让落后源重复报全局值）
        }

        private readonly Shared _shared;
        private readonly DownloadProgressHandler? _inner;

        private RaceProgress(Shared shared, DownloadProgressHandler? inner)
        {
            _shared = shared;
            _inner = inner;
        }

        private readonly int _index;      // 本源序号（per-source 字节记录用）
        private readonly long[] _bytes;   // per-source 已下载字节（淘汰制评估"谁领先"用）
        private readonly long[] _pushTicks; // per-source 最近逐读推拍 tick（陪跑采样用；0=尚无）

        private RaceProgress(Shared shared, DownloadProgressHandler? inner, int index, long[] bytes, long[] pushTicks)
        {
            _shared = shared;
            _inner = inner;
            _index = index;
            _bytes = bytes;
            _pushTicks = pushTicks;
        }

        /// <summary>为 count 个候选源创建共享同一比较器的转发器（一一对应）。
        /// 8-14 批次41：progress 可空——headless（无 UI 回调）下仍需要每源字节采样/淘汰评估；
        /// 陪跑源复用本组件的 Max 单调转发（slot = candidates.Count+k），UI 永不回退。</summary>
        public static RaceProgress Wrap(int count, DownloadProgressHandler? progress)
        {
            var shared = new Shared();
            var bytes = new long[count];
            var pushTicks = new long[count];
            var arr = new DownloadProgressHandler[count];
            var liveArr = new Action<long, long>[count];
            for (var i = 0; i < count; i++)
            {
                var rp = new RaceProgress(shared, progress, i, bytes, pushTicks);
                arr[i] = rp.Invoke;
                liveArr[i] = rp.Push;
            }
            return new RaceProgress(shared, progress, 0, bytes, pushTicks) { Handlers = arr, LiveHandlers = liveArr };
        }

        /// <summary>每个候选源对应的转发器（与 Wrap 的 count 一一对应）</summary>
        public DownloadProgressHandler[] Handlers { get; private init; } = [];

        /// <summary>每源逐读推拍委托（陪跑采样用，无节流）：读循环每次读完调用（与 Handlers 一一对应）</summary>
        public Action<long, long>[] LiveHandlers { get; private init; } = [];

        /// <summary>某源最近一次推拍 tick（0 = 尚无；陪跑采样判「无新读」用）</summary>
        public long GetPushTick(int index) => Interlocked.Read(ref _pushTicks[index]);

        /// <summary>逐读推拍：字节单调守卫（并发分片互抢时不回退），tick 仅在字节前进时更新</summary>
        private void Push(long bytes, long tick)
        {
            if (bytes > Interlocked.Read(ref _bytes[_index]))
            {
                Interlocked.Exchange(ref _bytes[_index], bytes);
                Interlocked.Exchange(ref _pushTicks[_index], tick);
            }
        }

        /// <summary>某源的已下载字节（竞速淘汰评估用——Interlocked 读，任意线程安全）</summary>
        public long GetBytes(int index) => Interlocked.Read(ref _bytes[index]);

        /// <summary>8-13 文件总大小（惰性定——首个源报告时写入；淘汰评估速度外推用）</summary>
        public long GetTotal() => Interlocked.Read(ref _shared.Total);

        private void Invoke(DownloadProgress p)
        {
            if (_shared.Total <= 0 && p.FileTotalBytes > 0) _shared.Total = p.FileTotalBytes;
            // 本源字节记录（淘汰评估用）
            if (p.FileBytesDone > Interlocked.Read(ref _bytes[_index]))
                Interlocked.Exchange(ref _bytes[_index], p.FileBytesDone);
            // 更新领先值（CompareExchange 读 + 条件写：多源并发写 Max 安全）
            long cur;
            while ((cur = Interlocked.Read(ref _shared.Max)) < p.FileBytesDone
                   && Interlocked.CompareExchange(ref _shared.Max, p.FileBytesDone, cur) != cur) { }
            if (_shared.Total <= 0) return;
            var bytes = Math.Min(Interlocked.Read(ref _shared.Max), _shared.Total);
            if (bytes <= 0) return;
            if (Interlocked.Exchange(ref _shared.LastSent, bytes) == bytes) return; // 同值不重复转发
            _inner?.Invoke(new DownloadProgress(p.Stage, p.CurrentFile, bytes, _shared.Total,
                Math.Min(bytes * 100.0 / _shared.Total, 99)));
        }
    }

    /// <summary>任务级共享限速节流：每 64KB 结算一次，超出配额则等待。锁内记账、锁外 Delay
    /// （async 锁内等待会重入死锁）——多流/多源并发下同一累加器，总吞吐恒=设定值。</summary>
    private static async Task ThrottleStreamAsync(int n, CancellationToken ct, ThrottleState st, long limit)
    {
        if (limit <= 0) return;
        TimeSpan? wait = null;
        lock (st)
        {
            st.Bytes += n;
            if (st.Bytes >= 65536)
            {
                var target = (double)st.Bytes / limit;
                var elapsed = st.Sw.Elapsed.TotalSeconds;
                if (elapsed < target)
                    wait = TimeSpan.FromSeconds(target - elapsed);
                st.Bytes = 0;
                st.Sw.Restart();
            }
        }
        if (wait is { } w) await Task.Delay(w, ct);
    }

    private static HttpClient CreateClient()
    {
        // AL45：共享连接池（HttpClientPool）——连接复用 + HTTP/2，消除每文件的 TCP+TLS 握手开销。
        // 不设整体 Timeout——body 下载不受限（51MB 大文件慢网也要 1 分钟+，整体超时会误杀正常下载）
        return HttpClientPool.Shared;
    }

    /// <summary>
    /// 下载文件。外层循环：每轮遍历候选源（官方→镜像），全失败后指数退避进入下一轮。
    /// 校验失败（InvalidDataException）与网络错误（HttpRequestException）都触发换源。
    /// </summary>
    public async Task DownloadFileAsync(
        string url, string destPath, string? expectedSha1, long? expectedSize,
        DownloadProgressHandler? progress = null, CancellationToken ct = default)
    {
        // 同目标串行（并发任务下载同一 jar 时避免互相覆盖/写坏）。
        // 8-19 用完即删：Users 归零且字典仍指向本条目才 TryRemove（条件删除原子）；
        // 等待期间条目被删的竞态 → 校验字典指向 → 不成立则释放重试一轮（串行语义不破）
        FileLockEntry entry;
        while (true)
        {
            entry = FileLocks.GetOrAdd(destPath, _ => new FileLockEntry());
            Interlocked.Increment(ref entry.Users);
            await entry.Sem.WaitAsync(ct);
            if (FileLocks.TryGetValue(destPath, out var cur) && ReferenceEquals(cur, entry))
                break;
            entry.Sem.Release(); // 条目已被替换：让出，重试拿新条目
            Interlocked.Decrement(ref entry.Users);
        }
        try
        {
            await DownloadFileCoreAsync(url, destPath, expectedSha1, expectedSize, progress, ct);
        }
        finally
        {
            entry.Sem.Release();
            if (Interlocked.Decrement(ref entry.Users) == 0)
                FileLocks.TryRemove(new KeyValuePair<string, FileLockEntry>(destPath, entry));
        }
    }

    private async Task DownloadFileCoreAsync(
        string url, string destPath, string? expectedSha1, long? expectedSize,
        DownloadProgressHandler? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        // 8-22 共享节流器：整个文件（所有源/片/探测段）共用一个累加器，跨换源轮保持
        // 速率不重置——限速 = 「这个文件的下载速率」而非「每连接的速率」（根治慢+超额）
        var throttle = new ThrottleState();

        // 幂等：完整文件且校验通过（SHA1 或大小）→ 跳过
        if (File.Exists(destPath))
        {
            var len = new FileInfo(destPath).Length;
            if (expectedSha1 is not null && await Sha1MatchesAsync(destPath, expectedSha1, ct))
                return;
            if (expectedSha1 is null && expectedSize is { } s && len == s)
                return;
        }

        var backoff = _options.BackoffProvider ?? RetryPolicy.Backoff;
        Exception? last = null;
        // 网络检查用 host 集合（每轮累积；ghapi 占位非 http URL 不参与 host 检查）
        var hosts = new HashSet<string>();
        var abandonedKeys = new HashSet<string>(); // 8-14 watchdog 摘除的源（URL 哈希）——后续轮跳过
        var swDl = System.Diagnostics.Stopwatch.StartNew(); // 8-19 下载日志：总耗时

        for (var attempt = 0; attempt < _options.MaxSourceAttempts; attempt++)
        {
            // 候选源在每轮内重解（AL58b）：ghapi 签名 URL 约 1 小时过期——403 失败后下一轮
            // 重新 Resolve → 重新换链拿新签名；镜像死了也天然剔除
            var resolved = _resolver.Resolve(url);
            var candidates = _options.DownloadSource switch
            {
                // 8-22 修正：MirrorFirst 保留全部候选（镜像优先排前）——旧逻辑只取 [镜像,官方] 丢第三候选
                // （mcimirror 等后续源被整个丢弃，Modrinth 文件只剩官方/cdn-alt 竞速）
                DownloadSourcePreference.MirrorFirst => resolved.Count > 1
                    ? [resolved[1], resolved[0], .. resolved.Skip(2)] : resolved,
                DownloadSourcePreference.MirrorOnly => resolved.Count > 1 ? [resolved[1]] : resolved,
                _ => _sourceStats.Rank(resolved), // OfficialFirst：官方+镜像按历史速度排序（最快优先）
            };
            // 8-16：ghapi 候选预换链 + 签名 URL 套镜像展开——签名直链（objects.githubusercontent.com）
            // 实测国内 64KB/s（OBS 大文件骤降几百 KB 的兜底源），镜像转发可提速；换链失败/过期 →
            // 本轮剔除（下一轮重新 Resolve 重试），避免下载时才失败浪费一轮。
            if (candidates.Any(c => c.StartsWith(GitHubApiDirect.Scheme)))
            {
                var expanded = new List<string>(candidates.Count + 4);
                foreach (var c in candidates)
                {
                    if (!c.StartsWith(GitHubApiDirect.Scheme)) { expanded.Add(c); continue; }
                    var signed = await GitHubApiDirect.GetSignedUrlAsync(c, ct);
                    if (signed is null) continue; // 换链失败 → 剔除（下一轮重试）
                    expanded.Add(signed); // 签名直连（镜像全挂时的兜底）
                    foreach (var m in ThirdPartyDlSourceResolver.Mirrors)
                        expanded.Add($"{m}/{signed}"); // 镜像转发签名 URL（提速路径）
                }
                candidates = expanded;
            }
            foreach (var c in candidates)
                if (!c.StartsWith(GitHubApiDirect.Scheme) && Uri.TryCreate(c, UriKind.Absolute, out var cu))
                    hosts.Add(cu.Host);
            // 8-14 watchdog 摘除的源本轮直接剔除：挂死任务可能无视取消、仍锁着 .race/.tmp 文件
            // （复用会撞 FileShare.None 抛 IOException），跳过避免整轮再次被它拖死
            candidates = candidates.Where(c => !abandonedKeys.Contains(RaceKey(c))).ToList();
            if (candidates.Count == 0) continue; // 全部被摘除 → 无源可试，直接下一轮（不等待退避）
            // 8-19 下载日志：每轮候选源（分析「哪个源赢/为什么慢」用——HTTP 层看不到竞速业务语义）。
            // 8-22 升级 Info：原 Debug 被 DownloadLogFile 的 level<Info 过滤，候选源证据永不落盘——
            // 「为什么慢」查不到（与 DownloadLogFile 头注释「候选源」矛盾）
            LogWrapper.Info($"[下载] 第{attempt + 1}轮 {url} 候选({candidates.Count}): {string.Join(" | ", candidates.Select(ShortUrl))}");
            if (candidates.Count == 1)
            {
                // 单候选（不可映射 URL）：走直接路径——保留断点续传（dest.tmp 预写 → Range 续传）
                // 与原子 rename 语义；竞速只用于多源场景
                try
                {
                    // 8-20：单候选禁慢速判死——无镜像可换，判死 = 把能下的慢源杀掉（6GB ISO 报错的根因）
                    await DownloadFromSourceAsync(candidates[0], destPath, expectedSha1, expectedSize, progress, null, ct, throttle,
                        allowSlowDeath: false);
                    LogWrapper.Info($"[下载] 完成 {ShortUrl(url)} 耗时{swDl.Elapsed.TotalSeconds:0.0}s");
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
                {
                    last = ex;
                    ThirdPartyDlSourceResolver.MarkFailed(candidates[0]); // 8-18 失败记忆：下轮排末位
                    LogWrapper.Warn($"[下载] 单候选失败 {ShortUrl(candidates[0])}: {ex.Message}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // AL34：HttpClient.Timeout（默认 100s 等响应头）超时抛 TaskCanceledException——源级故障，
                    // 不是用户取消。此前漏出 → 叶子任务误判"已取消"（无错误、UI 不可重试、文件缺失）；
                    // 实机 08-09 探针 asm-9.10.1.jar（maven.fabricmc.net 单候选）即此。转可重试错误走退避下一轮。
                    last = new HttpRequestException("等待响应头超时（>100s）", null);
                }
                catch (OperationCanceledException) { throw; } // 用户取消原样上抛
                if (attempt < _options.MaxSourceAttempts - 1)
                {
                    var delay = backoff(attempt);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                }
                continue;
            }
            // AL32 并行竞速（秒接）：一轮内所有候选源同时发起，先到先得——官方卡 5s 超时时
            // 镜像已在同步下载，不再串行等满一轮（旧实现最坏 2 轮×2 源×5~15s）。
            // 每源独立 race 目标（destPath.race{URL 哈希}）→ 中间文件（.tmp/.parts）天然隔离；
            // 首个校验通过的源赢：rename 到真名，取消其余源并清理残留。
            // 8-13 轮间不清残留：同 URL 跨轮复用已完成片集（判死换路后下轮从断点续——「中途换源不丢进度」）；
            // 赢家出现后清输家、终态失败 CleanupResiduals 全清——正常路径无积累
            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // AL59 淘汰制：每源独立 cts（linked 到 raceCts）——可取消指定源而不动其他源
            var pending = new List<(int Index, string Src, Task<(bool Ok, Exception? Error)> Task, CancellationTokenSource Cts)>();
            // AL57 竞速进度合并：多源同时拉同一文件的不同副本（.race{i}）且共享同一进度回调时，
            // 直接透传各源绝对字节会混合虚高——字节交替回退触发计速基线重置，完成瞬间剩余字节
            // 全挤进最后 0.25s 窗口 → 速度爆表（实机：19MB 真下 10s，显示几百 MB/s）。
            // 语义为"领先源进度"（AL58b）：每源独立转发器，全局取领先值单调转发。
            // 批次 41 陪跑：Wrap 恒创建（headless 下采样/淘汰评估同样可用）；陪跑源复用共享 Max 槽位
            // （slot = candidates.Count+k），UI 单调领先值永无回退
            var slotCount = candidates.Count + _options.PaceMaxSources;
            var perSourceProgress = RaceProgress.Wrap(slotCount, progress);
            for (var i = 0; i < candidates.Count; i++)
            {
                var idx = i;
                var src = candidates[i];
                var srcCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
                pending.Add((idx, src, Task.Run(() => RaceOneAsync(idx, src, destPath,
                    expectedSha1, expectedSize, perSourceProgress.Handlers[idx], perSourceProgress.LiveHandlers[idx],
                    srcCts.Token, ct, throttle), ct), srcCts));
            }
            Exception? raceLast = null;
            var won = false;
            // AL59 淘汰评估计时：到点且无赢家 → 取消非领先源（限并发镜像 3 源×8 片=24 连接是灾难，
            // 收敛到领先源让它的 ramp-up 自决并发；无 progress 数据时无法评估——跳过）
            var evalSw = System.Diagnostics.Stopwatch.StartNew();
            var evalInterval = _options.RaceEliminateInterval;
            var lastEvalBytes = new long[slotCount]; // 8-13 速度外推评估：上次评估各源字节
            var noProgressSince = new long[slotCount]; // 8-14 watchdog：各源连续零增量起始 tick（0=有进度）
            // 批次 41 陪跑状态（本轮内有效；全部只在主循环单线程上读写）
            var demotedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 被顶替下台的源（不进 abandonedKeys——其 .parts 下轮可复用）
            var pace = new PaceTracker(_options);
            var paceActive = false;        // 陪跑源在局中
            var paceGraceUntil = 0L;       // 触发后淘汰评估宽限截止 tick
            var paceSw = System.Diagnostics.Stopwatch.StartNew();
            var paceInterval = TimeSpan.FromMilliseconds(_options.PaceProbeIntervalMs);
            while (pending.Count > 0)
            {
                var remaining = evalInterval - evalSw.Elapsed;
                var evalDelay = remaining > TimeSpan.Zero ? Task.Delay(remaining, ct) : Task.CompletedTask;
                var paceRemaining = paceInterval - paceSw.Elapsed;
                var paceDelay = _options.PaceEnabled && paceRemaining > TimeSpan.Zero
                    ? Task.Delay(paceRemaining, ct) : Task.CompletedTask;
                var done = await Task.WhenAny(pending.Select(p => p.Task).Cast<Task>().Append(evalDelay).Append(paceDelay));
                if (done == paceDelay)
                {
                    // 批次 41 陪跑节拍：采样/触发/顶替（全部主循环单线程，零锁）
                    PaceTick();
                    paceSw.Restart();
                    continue;
                }
                if (done == evalDelay)
                {
                    if (perSourceProgress is not null && pending.Count > 0)
                    {
                        // 8-13 淘汰评估改「预计剩余时间」（速度外推）：总量评估错杀稳定镜像——
                        // CDN 直连开局快（首字节毫秒级）总量领先 → ghproxy 镜像（握手慢但全程 2MB/s）
                        // 15s 评估被淘汰 → 赢家是后段限速 64KB/s 的 CDN（实测 PowerToys 271MB 均 755KB/s）。
                        // eta = 剩余 × 窗口 / 增量：合并中源（bytes=Total）eta=0 自然保留；卡死源（增量 0）被淘汰
                        var total = perSourceProgress.GetTotal();
                        var cur = pending.Select(p => perSourceProgress.GetBytes(p.Index)).ToArray();
                        var prev = pending.Select(p => lastEvalBytes[p.Index]).ToArray();
                        var leadPos = PickRaceLeader(cur, prev, total, evalInterval.TotalSeconds);
                        for (var i = 0; i < pending.Count; i++)
                        {
                            var p = pending[i];
                            lastEvalBytes[p.Index] = cur[i];
                            if (cur[i] <= prev[i])
                            {
                                if (noProgressSince[p.Index] == 0) noProgressSince[p.Index] = Environment.TickCount64;
                            }
                            else noProgressSince[p.Index] = 0;
                        }
                        // 8-22 修「已下完还在等」：某源字节已写满 total 但 Task 未收尾（卡在读心跳/验证）——
                        // 竞速赢家只在 done(Task 完成) 时触发，字节满的源若 Task 不结束就永远不赢。
                        // 条件收紧：字节已满 + 该源连续停滞超过 2 个评估间隔（真卡住）才提前完成——
                        // 防止抢在陪跑顶替前结束（赢家降速但仍有增量时不触发，陪跑语义保留）。
                        if (total > 0 && pending.Count > 0)
                        {
                            var stalledFull = pending.FirstOrDefault(p =>
                                perSourceProgress.GetBytes(p.Index) >= total
                                && noProgressSince[p.Index] > 0
                                && Environment.TickCount64 - noProgressSince[p.Index] >= (long)(evalInterval.TotalMilliseconds * 2));
                            // 8-23 高危修复：.race{key} 不存在 = 该源还在 chunked 合并（字节满但未落盘）——
                            // 此时 Move 必抛 FileNotFoundException，且 Cancel 会杀掉真正合并的 Task → 假成功文件丢失。
                            // 仅当 .race 真实存在（合并已落盘、确实卡在收尾）才提前完成；否则跳过交正常 done 路径。
                            if (stalledFull.Src is not null
                                && File.Exists($"{destPath}.race{RaceKey(stalledFull.Src)}"))
                            {
                                try { File.Move($"{destPath}.race{RaceKey(stalledFull.Src)}", destPath, true); }
                                catch (FileNotFoundException) { /* 竞态：源 Task 恰好同时收尾 rename——正常流程接管 */ }
                                LogWrapper.Info($"[下载] 字节已满且停滞即完成({stalledFull.Index}): {ShortUrl(stalledFull.Src)} 耗时{swDl.Elapsed.TotalSeconds:0.0}s");
                                raceCts.Cancel();
                                var stragglers = pending.ToList();
                                if (stragglers.Count > 0)
                                    _ = Task.Run(() =>
                                    {
                                        foreach (var p in stragglers) { try { p.Task.Wait(); } catch { } }
                                        foreach (var p in stragglers) CleanupRaceFiles(destPath, RaceKey(p.Src));
                                    });
                                CleanupRaceSweep(destPath, stragglers.Select(p => RaceKey(p.Src)));
                                won = true;
                                break;
                            }
                        }
                        if (pending.Count > 1)
                        {
                            // 陪跑宽限期内不淘汰（新入局源免遭 eta 外推秒杀）；到期后正常收敛
                            if (!paceActive || Environment.TickCount64 >= paceGraceUntil)
                            {
                                var leadIndex = pending[leadPos].Index;
                                foreach (var p in pending.Where(p => p.Index != leadIndex))
                                {
                                    p.Cts.Cancel(); // 淘汰落后源——取消后 Task 走 OCE→(false,null)，WhenAny 收掉
                                    ThirdPartyDlSourceResolver.MarkFailed(p.Src); // 8-18 失败记忆：下轮排末位
                                }
                            }
                            // 陪跑期领先源停滞兜底（防多源全死挂起——watchdog 对 bytes 最大者生效）
                            if (paceActive)
                            {
                                var top = pending.OrderByDescending(p => perSourceProgress.GetBytes(p.Index)).First();
                                var topBytes = perSourceProgress.GetBytes(top.Index);
                                if ((total <= 0 || topBytes < total)
                                    && noProgressSince[top.Index] > 0
                                    && Environment.TickCount64 - noProgressSince[top.Index] >= _options.RaceWatchdogStallMs)
                                {
                                    AbandonDoomed(top);
                                }
                            }
                        }
                        else if ((total <= 0 || cur[0] < total)
                                 && noProgressSince[pending[0].Index] > 0
                                 && Environment.TickCount64 - noProgressSince[pending[0].Index] >= _options.RaceWatchdogStallMs)
                        {
                            // 8-14 幸存源 watchdog：唯一源连续墙钟 {RaceWatchdogStallMs}ms 零增量且未收尾
                            // → 摘除出本轮（不等待任务结束——挂死任务可能无视取消 token，等它 = 无限等），
                            // 本轮结束进下一轮重赛（其余源片集跨轮复用，不丢进度）。
                            // 实机（OBS 128MB）：赢家 ghproxy.net 静默断流后源内判死/读心跳未触发
                            // （流读 token 失效的洞），整轮无限挂起、日志死寂 8 分钟——外层兜底必须存在。
                            // 墙钟而非 tick 数：进度上报是 250ms 精细粒度，健康源（≥判死阈值）必有增量；
                            // tick 数随评估间隔缩放，测试加速间隔会误杀慢启动源
                            AbandonDoomed(pending[0]);
                        }
                    }
                    evalSw.Restart();
                    continue;
                }
                var entry = pending.First(p => p.Task == done);
                pending.Remove(entry);
                var (ok, err) = await (Task<(bool Ok, Exception? Error)>)done;
                if (ok)
                {
                    // 陪跑状态收尾：任一源完成即走既有赢家路径（旧赢家完成=陪跑白跑，陪跑完成=直接赢）
                    // AL58 赢家先落盘再收尾：旧实现先等所有 pending 停止再 rename——慢源（限并发镜像
                    // 的龟速分片）取消传播要几十秒，UI 停在"下完了"静默；且慢源永远赢不了时
                    // 任务被拖死（376MB 限并发实测 0.1MB/s）。rename 先行：任务立即完成，
                    // 输家取消 + 残留清理放后台（先 Wait 等它停再删，防边写边删）。
                    File.Move($"{destPath}.race{RaceKey(entry.Src)}", destPath, true); // 赢家 → 真名
                    LogWrapper.Info($"[下载] 竞速赢家({entry.Index}): {ShortUrl(entry.Src)} 耗时{swDl.Elapsed.TotalSeconds:0.0}s");
                    raceCts.Cancel(); // 其余源取消
                    var stragglers = pending.ToList();
                    if (stragglers.Count > 0)
                        _ = Task.Run(() =>
                        {
                            foreach (var p in stragglers) { try { p.Task.Wait(); } catch { } }
                            foreach (var p in stragglers) CleanupRaceFiles(destPath, RaceKey(p.Src));
                        });
                    // 8-14 同步清扫已完成输家片集：eval 淘汰的源早已离开 pending（任务结束、文件解锁），
                    // 旧实现只清 stragglers → 桌面残留 .race*.parts（真机 OBS 下完留 3 个目录 10MB）。
                    // 跳过 stragglers 的键（仍在后台写）；watchdog 摘除源尝试删（锁着则静默失败）
                    CleanupRaceSweep(destPath, stragglers.Select(p => RaceKey(p.Src)));
                    won = true;
                    break;
                }
                if (err is not null) raceLast = err;
            }
            if (won) return;

            // ---------- 批次 41 陪跑本地函数（仅主循环线程调用；闭包捕获本轮状态） ----------

            // 陪跑节拍：收敛阶段采样+触发；陪跑阶段稳定领先判定+顶替
            void PaceTick()
            {
                if (perSourceProgress is null || pending.Count == 0) return;
                var now = Environment.TickCount64;
                var total = perSourceProgress.GetTotal();

                if (paceActive)
                {
                    // 陪跑阶段：旧赢家 = Index < candidates.Count 的条目；全部退场则回归普通竞速
                    var nonPace = pending.Where(p => p.Index < candidates.Count).ToList();
                    if (nonPace.Count == 0)
                    {
                        paceActive = false;
                        pace.ResetSampling();
                        return;
                    }
                    long winnerBytes = 0;
                    foreach (var p in nonPace)
                        winnerBytes = Math.Max(winnerBytes, perSourceProgress.GetBytes(p.Index));
                    long bestPace = 0;
                    foreach (var p in pending)
                        if (p.Index >= candidates.Count)
                            bestPace = Math.Max(bestPace, perSourceProgress.GetBytes(p.Index));
                    if (bestPace > winnerBytes) pace.NoteStableLead();
                    else pace.ResetStableLead();
                    if (PaceTracker.ShouldTakeover(_options, bestPace, winnerBytes, pace.StableLeadTicks, total))
                    {
                        foreach (var p in nonPace)
                        {
                            demotedKeys.Add(RaceKey(p.Src)); // 不进 abandonedKeys——其 .parts 下轮可复用
                            p.Cts.Cancel();
                        }
                        paceActive = false;
                        pace.ResetSampling();
                        LogWrapper.Info($"[下载] 陪跑顶替：取消旧赢家 {string.Join(" | ", nonPace.Select(p => ShortUrl(p.Src)))}（陪跑领先 {bestPace / 1024 / 1024}MB > {winnerBytes / 1024 / 1024}MB）");
                    }
                    return;
                }

                // 收敛阶段：仅剩唯一幸存源时采样与触发
                if (pending.Count != 1 || total <= 0) return;
                var lead = pending[0];
                var leadBytes = perSourceProgress.GetBytes(lead.Index);
                if (leadBytes >= total) return; // 收尾（合并/校验）不触发
                var pushTick = perSourceProgress.GetPushTick(lead.Index);
                // 8-15 断流：无新推拍 = 源已断流（健康源逐读必每秒有推拍）——零速度采样让下降
                // 计数累积触发陪跑（此前空拍跳过 → 断流永不陪跑，只等 watchdog 30s——真机卡死）
                if (pushTick == pace.LastPushTick) pace.SampleStall();
                else pace.Sample(leadBytes, pushTick);
                if (!pace.ShouldTrigger(total, leadBytes, now)) return;

                // 触发：按历史速度排名挑落选源入局（排除 abandoned/demoted/在局源）
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                used.UnionWith(abandonedKeys);
                used.UnionWith(demotedKeys);
                used.UnionWith(pending.Select(p => RaceKey(p.Src)));
                var pool = _sourceStats.Rank(candidates.Where(c => !used.Contains(RaceKey(c))).ToList());
                var added = 0;
                for (var k = 0; k < pool.Count && added < _options.PaceMaxSources; k++)
                {
                    var src = pool[k];
                    var idx = candidates.Count + added; // 槽位 candidates.Count+k（与 Wrap 扩容对齐）
                    var srcCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
                    pending.Add((idx, src, Task.Run(() => RaceOneAsync(idx, src, destPath,
                        expectedSha1, expectedSize, perSourceProgress.Handlers[idx], perSourceProgress.LiveHandlers[idx],
                        srcCts.Token, ct, throttle), ct), srcCts));
                    lastEvalBytes[idx] = 0;
                    noProgressSince[idx] = 0;
                    added++;
                }
                if (added == 0) return;
                var lastKbps = pace.LastSpeed * 1000 / 1024;
                var peakKbps = pace.PeakSpeed * 1000 / 1024;
                paceActive = true;
                pace.MarkTriggered(now); // 起冷却 + 清采样（注意：先取速度后标记）
                paceGraceUntil = now + _options.PaceEliminateGraceMs;
                LogWrapper.Info($"[下载] 陪跑开赛 +{added} 源（领先源 {ShortUrl(lead.Src)} 降速 {lastKbps:0}KB/s，峰值 {peakKbps:0}KB/s）：{string.Join(" | ", pool.Take(added).Select(ShortUrl))}");
            }

            // 摘除卡死源（不等待任务结束——挂死任务可能无视取消 token；进 abandonedKeys 后续轮跳过）
            void AbandonDoomed((int Index, string Src, Task<(bool Ok, Exception? Error)> Task, CancellationTokenSource Cts) doomed)
            {
                pending.Remove(doomed);
                abandonedKeys.Add(RaceKey(doomed.Src)); // 其 .race 文件可能被挂死任务锁着
                doomed.Cts.Cancel(); // 能取消就取消（省连接/句柄），不能也无所谓——任务后台自生自灭
                var stalledMs = Environment.TickCount64 - noProgressSince[doomed.Index];
                LogWrapper.Warn($"[下载] watchdog 摘除 {ShortUrl(doomed.Src)} 零增量{stalledMs / 1000}s——进下一轮（已弃用该源）");
            }
            last = raceLast ?? last;
            if (attempt < _options.MaxSourceAttempts - 1)
            {
                var delay = backoff(attempt);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            }
        }

        // 重试耗尽：检查网络并报告（用户要求"重试 3 次后检查网络并报告"）。
        // 注意：此处不清理中间产物——Task 层还有自动重试（ScheduleAutoRetry），清理放在
        // 「真正终态失败」（自动重试也耗尽）时，否则 Task 重试的换源续传材料（.parts）被提前清掉
        var reachable = await _networkChecker(hosts.ToList(), ct);
        LogWrapper.Warn($"[下载] 失败 {ShortUrl(url)} 重试{_options.MaxSourceAttempts}轮耗尽: {last?.Message ?? "未知"} 耗时{swDl.Elapsed.TotalSeconds:0.0}s");
        if (!reachable)
            throw new InvalidOperationException(
                $"网络不可达：{string.Join("、", hosts)} 均无法连接，请检查网络/代理/防火墙（已重试 {_options.MaxSourceAttempts} 轮）");
        throw last ?? new InvalidOperationException($"下载失败: {url}");
    }

    /// <summary>8-19 下载日志：URL 截断（签名 URL/镜像前缀超长，日志可读性）</summary>
    private static string ShortUrl(string url)
    {
        if (url.Length <= 100) return url;
        return url[..60] + "…" + url[^30..];
    }

    /// <summary>
    /// 竞速单个候选源（AL32）：下载到独立 race 目标（隔离 .tmp/.parts），
    /// 校验通过返回成功；竞速输（被取消）或失败返回失败标记——取消不抛（赢家已定）。
    /// </summary>
    private async Task<(bool Ok, Exception? Error)> RaceOneAsync(
        int index, string url, string destPath, string? expectedSha1, long? expectedSize,
        DownloadProgressHandler? progress, Action<long, long>? livePush, CancellationToken raceCt, CancellationToken ct,
        ThrottleState throttle)
    {
        // 8-13 片集按 URL 哈希命名：同 URL 跨轮复用已完成片（判死换路后下轮续传，不归零）；
        // 候选顺序轮间变化（Resolve 重排）不影响——键与 URL 绑定而非下标
        var raceDest = $"{destPath}.race{RaceKey(url)}";
        try
        {
            await DownloadFromSourceAsync(url, raceDest, expectedSha1, expectedSize, progress, livePush, raceCt, throttle);
            return (true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 竞速输（raceCts 已取消）或源自身超时——静默，赢家已经定了
            return (false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException
            or IOException or UnauthorizedAccessException)
        {
            // 8-22 全栈排查：IOException（文件锁/共享冲突）此前逃出整轮竞速循环——
            // 挂死源残留 .race*.parts 文件锁着时，下一轮同 key 复用开文件直接崩。归入源失败换源兜底
            return (false, ex);
        }
    }

    /// <summary>清理某源的竞速残留（.race{key} 本体 + .tmp + .parts 目录）</summary>
    private static void CleanupRaceFiles(string destPath, string raceKey)
    {
        var raceDest = $"{destPath}.race{raceKey}";
        try { File.Delete(raceDest + ".tmp"); } catch { }
        try { Directory.Delete(raceDest + ".parts", true); } catch { }
        try { File.Delete(raceDest); } catch { }
    }

    /// <summary>
    /// 8-14 成功路径竞速残留清扫：赢家出现后同步删全部 .race* 残留（本体/.tmp/.parts），
    /// 跳过 skipKeys（仍在后台写的 stragglers）；删不动（watchdog 摘除源锁着文件）静默失败。
    /// 不动 destPath 本身（赢家刚 rename 落盘）——只动 .race* 前缀与 destPath+".tmp" 旧残留。
    /// </summary>
    private static void CleanupRaceSweep(string destPath, IEnumerable<string> skipKeys)
    {
        var skip = new HashSet<string>(skipKeys, StringComparer.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(destPath);
        var name = Path.GetFileName(destPath);
        if (dir is null || name.Length == 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, name + ".race*"))
                if (!skip.Contains(RaceKeyOfPath(f))) { try { File.Delete(f); } catch { } }
            foreach (var d in Directory.EnumerateDirectories(dir, name + ".race*.parts"))
                if (!skip.Contains(RaceKeyOfPath(d))) { try { Directory.Delete(d, true); } catch { } }
            foreach (var f in Directory.EnumerateFiles(dir, name + ".race*.tmp"))
                if (!skip.Contains(RaceKeyOfPath(f))) { try { File.Delete(f); } catch { } }
            File.Delete(destPath + ".tmp"); // 直接路径旧残留（成功路径 destPath 已是成品，.tmp 必为垃圾）
        }
        catch { }
    }

    /// <summary>从 .race{KEY}[.parts|.tmp] 文件名提取 8 位键（解析失败返回空串，不会误跳）</summary>
    private static string RaceKeyOfPath(string fullPath)
    {
        var fn = Path.GetFileName(fullPath);
        var i = fn.IndexOf(".race", StringComparison.Ordinal);
        return i >= 0 && fn.Length >= i + 5 + 8 ? fn.Substring(i + 5, 8) : "";
    }

    /// <summary>8-13 竞速片集键：URL 的 SHA1 前 8 位 hex——同 URL 跨轮复用（键与 URL 绑定，候选顺序无关）</summary>
    internal static string RaceKey(string url)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..8];

    /// <summary>8-13 竞速淘汰评估（纯函数，按调用方数组顺序返回位置）：预计剩余时间最小者胜。
    /// 已下完（bytes ≥ total，合并中）的源直接保留（eta=0 语义——弃它 = 弃已下完文件）；
    /// 其余按 eta = (total - bytes) × window / delta——增量 ≤0 跳过（卡死）；全部无增量 → 回退总量领先。
    /// 数组非空由调用方保证。</summary>
    internal static int PickRaceLeader(long[] bytes, long[] lastBytes, long total, double windowSec)
    {
        if (total > 0)
            for (var i = 0; i < bytes.Length; i++)
                if (bytes[i] >= total) return i; // 已下完（合并中）→ 直接保留
        var lead = -1;
        var bestEta = double.MaxValue;
        for (var i = 0; i < bytes.Length; i++)
        {
            var delta = bytes[i] - lastBytes[i];
            if (delta <= 0) continue;
            var eta = total > 0 ? (double)(total - bytes[i]) * windowSec / delta : double.MaxValue;
            if (eta < bestEta) { bestEta = eta; lead = i; }
        }
        if (lead >= 0) return lead;
        lead = 0;
        for (var i = 1; i < bytes.Length; i++)
            if (bytes[i] > bytes[lead]) lead = i;
        return lead;
    }

    /// <summary>
    /// 陪跑监督器（批次 41）：收敛阶段（pending==1）按节拍采样唯一幸存源速度，追踪窗口次高值（峰值）
    /// 与连续下降计数；触发/顶替决策纯函数化（ShouldTriggerPace/ShouldTakeover）供测试直测。
    /// 只在竞速主循环单线程上被调用，无需锁。
    /// </summary>
    internal sealed class PaceTracker
    {
        private readonly DownloadOptions _opts;
        private readonly double[] _ring;    // 速度环（字节/ms）
        private int _ringPos;
        private int _ringCount;
        private long _lastBytes = -1;
        private long _lastPushTick;
        private double _lastSpeed;
        private int _declineSamples;
        private long _triggeredAtMs;        // 上次触发 tick（0 = 无冷却）

        public PaceTracker(DownloadOptions opts)
        {
            _opts = opts;
            _ring = new double[Math.Max(2, opts.PacePeakWindowSamples)];
        }

        /// <summary>采样领先源（仅收敛阶段每节拍调用一次）：读循环逐读推拍 (bytes, pushTick)——
        /// 速度按推拍差算（= 精确逐读速率）。250ms 上报节流与采样节拍错位会产生交替伪波动，
        /// 阶梯降速下错位交替不断清零下降计数、永远测不出——推拍绕开节流量化（8-14 采样重做）。
        /// 时间加权 EMA（τ=1s）：慢源 dt≈秒级 → 接近逐读原速；快源 dt≈毫秒 → 平滑逐读抖动。
        /// 下降计数连续不增语义：平台拍 EMA 相等保留计数，回升清零。首推只记基线。</summary>
        public void Sample(long bytes, long pushTick)
        {
            if (pushTick == _lastPushTick) return;               // 无新读完成：跳过
            if (_lastPushTick == 0) { _lastPushTick = pushTick; _lastBytes = bytes; return; }
            var dt = pushTick - _lastPushTick;
            _lastPushTick = pushTick;
            if (dt <= 0) return;
            var speed = (bytes - _lastBytes) / (double)dt;
            _lastBytes = bytes;
            _ring[_ringPos] = speed;
            _ringPos = (_ringPos + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            var alpha = 1.0 - Math.Exp(-dt / 1000.0);
            var ema = alpha * speed + (1.0 - alpha) * _lastSpeed;
            if (ema < _lastSpeed) _declineSamples++;
            else if (ema > _lastSpeed) _declineSamples = 0;      // 平台（相等）保留——连续不增
            _lastSpeed = ema;
        }

        /// <summary>最近一次推拍 tick（PaceTick 判「断流」用：无新推拍 = 源已断流）</summary>
        public long LastPushTick => _lastPushTick;

        /// <summary>
        /// 断流拍（8-15）：源无新读（推拍不更新）时按零速度采样——EMA 向 0 收敛、下降计数累积。
        /// 此前空拍直接跳过 → 断流时下降计数永不累积 → 陪跑永不触发，只等 watchdog 30s 摘除
        /// （真机 13:04 Nexus-Player 卡死现场：无「陪跑开赛」，30s 后才摘除重赛）。
        /// 健康源逐读推拍每秒必有新推拍（8KB 缓冲读间隔毫秒级），无新推拍 = 真断流，不会误判。
        /// 峰值保留（断流不重置）；EMA 按采样节拍收敛，5 拍 ≈ 5s 触发陪跑（vs watchdog 30s）。
        /// </summary>
        public void SampleStall()
        {
            if (_lastPushTick == 0) return; // 尚未有任何读（慢启动/无进度），不算断流
            var dt = _opts.PaceProbeIntervalMs;
            var alpha = 1.0 - Math.Exp(-dt / 1000.0);
            var ema = (1.0 - alpha) * _lastSpeed;
            if (ema < _lastSpeed) _declineSamples++;
            else if (ema > _lastSpeed) _declineSamples = 0;
            _lastSpeed = ema;
        }

        /// <summary>窗口峰值速度（字节/ms；窗口 = PacePeakWindowSamples 拍）。
        /// 取次高值而非最高：开局瞬时突发（ramp-up 过冲/单次快读）会顶高阈值线，
        /// 正常回落都够得着「<峰值×比例」→ 假触发；次高值只认持续 ≥2 拍的能力，突发拍被忽略。</summary>
        public double PeakSpeed
        {
            get
            {
                var top = 0.0;
                var second = 0.0;
                for (var i = 0; i < _ringCount; i++)
                {
                    var v = _ring[i];
                    if (v >= top) { second = top; top = v; }
                    else if (v > second) second = v;
                }
                return _ringCount >= 2 ? second : top;
            }
        }

        public double LastSpeed => _lastSpeed;
        public int DeclineSamples => _declineSamples;
        public int StableLeadTicks { get; private set; }

        /// <summary>顶替稳定计数：陪跑源领先一拍 +1，落后归零</summary>
        public void NoteStableLead() => StableLeadTicks++;
        public void ResetStableLead() => StableLeadTicks = 0;

        /// <summary>触发判定（收敛阶段、bytes&lt;total 时调用）：冷却 + 纯函数条件</summary>
        public bool ShouldTrigger(long total, long leadBytes, long now)
        {
            if (!_opts.PaceEnabled) return false;
            if (_triggeredAtMs != 0 && now - _triggeredAtMs < _opts.PaceCooldownMs) return false;
            return ShouldTriggerPace(_opts, _lastSpeed, PeakSpeed, _declineSamples, total, total - leadBytes);
        }

        /// <summary>标记已触发：起冷却 + 清采样（陪跑入局后重新积累）</summary>
        public void MarkTriggered(long now)
        {
            _triggeredAtMs = now;
            ResetSampling();
        }

        public void ResetSampling()
        {
            _lastBytes = -1;
            _lastPushTick = 0;
            _lastSpeed = 0;
            _declineSamples = 0;
            _ringPos = 0;
            _ringCount = 0;
            StableLeadTicks = 0;
        }

        /// <summary>纯函数：连续下降样本达标 + 当前速度 &lt; 窗口次高值×比例 + 大文件/剩余量守卫</summary>
        internal static bool ShouldTriggerPace(DownloadOptions opts, double curSpeed, double peakSpeed,
            int declineSamples, long total, long remainBytes)
            => declineSamples >= opts.PaceDeclineSamples
               && peakSpeed > 0
               && curSpeed < peakSpeed * opts.PaceDeclineRatio
               && total >= opts.PaceMinTotalBytes
               && remainBytes >= opts.PaceMinRemainBytes;

        /// <summary>纯函数：陪跑源字节反超 + 稳定领先样本达标 + 旧赢家未进入收尾（bytes&lt;total 合并/校验中不顶替）</summary>
        internal static bool ShouldTakeover(DownloadOptions opts, long paceBytes, long winnerBytes,
            int stableLeadTicks, long total)
            => paceBytes > winnerBytes
               && stableLeadTicks >= opts.PaceStableLeadSamples
               && winnerBytes < total;
    }

    /// <summary>
    /// 8-18 清理目标的全部中间产物：.tmp、.parts 目录、.race* 系列（本体/.tmp/.parts）。
    /// 永不动 destPath 本身——幂等检查（File.Exists(destPath)）依赖 destPath 存在的语义；
    /// 写入全走中间产物 + 原子 rename，destPath 不可能半截。终态失败时调用（不留垃圾文件）。
    /// </summary>
    internal static void CleanupResiduals(string destPath)
    {
        try { File.Delete(destPath + ".tmp"); } catch { }
        try { Directory.Delete(destPath + ".parts", true); } catch { }
        var dir = Path.GetDirectoryName(destPath);
        var name = Path.GetFileName(destPath);
        if (dir is null || name.Length == 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, name + ".race*")) { try { File.Delete(f); } catch { } }
            foreach (var d in Directory.EnumerateDirectories(dir, name + ".race*.parts")) { try { Directory.Delete(d, true); } catch { } }
            foreach (var f in Directory.EnumerateFiles(dir, name + ".race*.tmp")) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    /// <summary>单个候选源：定长走分片，否则单连接；前后计时记入源质量统计。
    /// allowSlowDeath：慢速判死是否启用——单候选（无镜像）时禁用：判死本意是「换源」，
    /// 单候选判死只会把「能下但慢」的源杀掉直接报错（8-20 实测：Ubuntu 官方源 6GB ISO 国内
    /// <100KB/s 被判死 → 「下载错误」；慢速大文件应继续硬啃而非放弃）</summary>
    private async Task DownloadFromSourceAsync(
        string url, string destPath, string? expectedSha1, long? expectedSize,
        DownloadProgressHandler? progress, Action<long, long>? livePush, CancellationToken ct, ThrottleState throttle,
        bool allowSlowDeath = true)
    {
        // 黑科技 A：ghapi 占位 URL → GitHub API 换签名直链（null = 换链失败，快速失败不影响竞速）
        if (url.StartsWith(GitHubApiDirect.Scheme))
        {
            var signed = await GitHubApiDirect.GetSignedUrlAsync(url, ct);
            if (signed is null)
                throw new HttpRequestException("GitHub API 换链失败", null, System.Net.HttpStatusCode.BadGateway);
            url = signed;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var totalSize = expectedSize ?? await GetContentLengthAsync(url, ct);
        try
        {
            if (totalSize >= ChunkThreshold)
                await DownloadChunkedAsync(url, destPath, totalSize, expectedSha1, progress, livePush, ct, throttle, allowSlowDeath);
            else
                await DownloadSingleAsync(url, destPath, expectedSha1, totalSize, progress, livePush, ct, throttle, allowSlowDeath);
            sw.Stop();
            _sourceStats.RecordSuccess(url, totalSize, sw.ElapsedMilliseconds);
        }
        catch
        {
            _sourceStats.RecordFailure(url);
            throw;
        }
    }

    /// <summary>单连接下载（断点续传 + 416 防御 + 校验失败抛 InvalidDataException 由外层换源）。
    /// AL29 H1：写入一律走 destPath+".tmp"，校验通过后原子 rename——崩溃/断电残留只可能是 .tmp，
    /// 不会出现「File.Exists 通过但内容半截」的 destPath。
    /// allowSlowDeath：慢速判死开关（单候选禁用——见 DownloadFromSourceAsync 注释）</summary>
    private async Task DownloadSingleAsync(
        string url, string destPath, string? expectedSha1, long? expectedSize,
        DownloadProgressHandler? progress, Action<long, long>? livePush, CancellationToken ct, ThrottleState throttle,
        bool allowSlowDeath = true)
    {
        var tmp = destPath + ".tmp";
        var from = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;

        // 416 防御：残留文件长度已 >= 目标总长（内容错误）→ 删除重下
        if (from > 0 && expectedSize is { } size && from >= size)
        {
            File.Delete(tmp);
            from = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from > 0) request.Headers.Range = new RangeHeaderValue(from, null);

        using var response = await SendWith416RetryAsync(request, destPath, ct);
        response.EnsureSuccessStatusCode();

        // AL8：进度 total 用真实目标大小（expectedSize 优先）——源返回 1B 垃圾（WAF 拦截页等）
        // 时不再显示 "1 B" 误导；校验仍由 sha1/size 兜底，无效响应自动换源
        var total = expectedSize ?? response.Content.Headers.ContentLength ?? 0;
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        {
            // BUGS#3 单连接半边（8-19 修复，与分片路径 1388 对齐）：服务器忽略 Range 回 200 全量时
            // append 会把完整 body 拼在半截 .tmp 后 → 错位文件；206 才追加，200 重写从头
            var isPartial = response.StatusCode == HttpStatusCode.PartialContent; // 206
            using var dst = new FileStream(tmp, from > 0 && isPartial ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None);
            var buffer = new byte[_options.BufferSize];
            long read = 0;
            // AL61 心率监测：持续低速（默认 30s < 100KB/s）→ 判源死抛异常 → 外层换路。
            // BUGS#1 修复：限速时阈值随限速下调（限速 50KB/s 时实测速度恒 50KB/s < 默认 100KB/s → 必判死）
            var slowDetector = new SlowSourceDetector(SlowThresholdForLimit(), _options.SlowSamples, _options.SlowProbeMs);
            // AL66 读心跳：读挂起（静默断流）时 AL61 检测跑不到（挂在数据循环体内）——心跳兜底判死
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(TimeSpan.FromMilliseconds(_options.ReadStallTimeoutMs));
            // REVIEW-卡进度：单连接进度节流（250ms）——旧代码每 64KB 块上报一次，
            // 高速下载（60MB/s ≈ 1000 次/秒）把 Avalonia UI Post 队列打爆 → 进度显示滞后
            // （真机 8-12 用户洞察「数据跟不上下载速度」——PCL 慢所以永远跟得上）
            var reportSw = Stopwatch.StartNew();
            var lastReportMs = 0L;
            int n;
            while ((n = await ReadWithStallAsync(src, buffer, stallCts, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                await ThrottleStreamAsync(n, ct, throttle, _limitPerStream);
                read += n;
                livePush?.Invoke(read, Environment.TickCount64); // 逐读推拍（陪跑采样——绕开 250ms 上报节流量化）
                // 8-22 补：剩余不足一片不判死（同分片路径——弃尾清零净亏，等流读完；total 未知时保持原判死行为）
                // 8-20：单候选（allowSlowDeath=false）不判死——慢速大文件继续硬啃
                if (allowSlowDeath && (total <= 0 || total - read >= ChunkSizeFor(total)) && slowDetector.ShouldAbort(read, ct))
                    throw new SlowSourceException(_options.SlowSpeedBps, slowDetector.LastSpeed);
                if (reportSw.ElapsedMilliseconds - lastReportMs >= 250)
                {
                    lastReportMs = reportSw.ElapsedMilliseconds;
                    progress?.Invoke(new DownloadProgress("", Path.GetFileName(destPath), read, total,
                        total > 0 ? Math.Min(read * 100.0 / total, 99) : 0));
                }
            }
            // 收尾强制报一次（节流窗口内的最后字节不丢）
            progress?.Invoke(new DownloadProgress("", Path.GetFileName(destPath), read, total,
                total > 0 ? Math.Min(read * 100.0 / total, 99) : 0));
            await dst.FlushAsync(ct);
        }

        // 校验：SHA1 优先，无 SHA1 时校验大小——校验对象是 tmp，通过后才替换真名
        var ok = expectedSha1 is null
            ? expectedSize is null || new FileInfo(tmp).Length == expectedSize
            : await Sha1MatchesAsync(tmp, expectedSha1, ct);
        if (!ok)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"下载校验失败（SHA1/大小不匹配）: {url}");
        }
        // AL29 H1：同目录 tmp → 原子替换（同卷 rename），旧 destPath 在文件完整前不被触碰
        File.Move(tmp, destPath, true);
    }

    /// <summary>发送请求（AL64 响应头超时：半开连接不卡死）；416（Range 起点不可满足）时删除文件从零重下一次</summary>
    private async Task<HttpResponseMessage> SendWith416RetryAsync(HttpRequestMessage request, string destPath, CancellationToken ct)
    {
        try
        {
            return await SendWithHeaderTimeoutAsync(request, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(destPath + ".tmp"); // AL29 H1：416 只删中间产物，destPath 未验证前不动
            var retry = new HttpRequestMessage(HttpMethod.Get, request.RequestUri!);
            return await SendWithHeaderTimeoutAsync(retry, ct);
        }
    }

    /// <summary>
    /// AL64 响应头超时：TCP 半开连接上 SendAsync(ResponseHeadersRead) 永不返回 → 子任务卡死
    /// → 组任务 WhenAll 挂 10 小时「下载中」（真机 08-11：26.2+Fabric 148.2/148.5MB 满速卡死）。
    /// 响应头 ResponseHeaderTimeoutMs 内拿不到 → 转 HttpRequestException（可重试/换路）；
    /// body 下载不受限（大文件慢网继续）。用户取消原样上抛（when 不匹配）。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithHeaderTimeoutAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_options.ResponseHeaderTimeoutMs));
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"等待响应头超时（>{_options.ResponseHeaderTimeoutMs / 1000}s）——源可能已死，自动换路", null);
        }
    }

    /// <summary>
    /// body 读心跳（AL66）：stallCts 的 CancelAfter 由调用方在每轮数据后重置——数据持续到达永不触发；
    /// ReadAsync 挂起（TCP 半开静默）→ 心跳超时 → 抛可重试错误（外层换路/重试）。
    /// 用户取消原样上抛（when 不匹配）。
    /// </summary>
    private async Task<int> ReadWithStallAsync(Stream src, Memory<byte> buffer,
        CancellationTokenSource stallCts, CancellationToken ct)
    {
        try
        {
            var n = await src.ReadAsync(buffer, stallCts.Token);
            stallCts.CancelAfter(TimeSpan.FromMilliseconds(_options.ReadStallTimeoutMs)); // 数据到达 → 心跳重置
            return n;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"下载卡死：>{_options.ReadStallTimeoutMs / 1000}s 无数据——源静默断流，自动换路", null);
        }
    }

    /// <summary>多连接 Range 分片下载：分片并行（单片重试 1 次）→ 合并 → SHA1 校验；整体失败回退单连接</summary>
    /// <summary>多连接 Range 分片下载：固定片大小 + 并发调度（并发 = gate 槽位；升片 = 提高并发不清进度）
    /// → 合并 → 总长/SHA1 校验；整体失败回退单连接</summary>
    private async Task DownloadChunkedAsync(
        string url, string destPath, long totalSize, string? expectedSha1,
        DownloadProgressHandler? progress, Action<long, long>? livePush, CancellationToken ct, ThrottleState throttle,
        bool allowSlowDeath = true)
    {
        try
        {
            var partDir = destPath + ".parts";
            Directory.CreateDirectory(partDir);

            var maxChunks = Math.Max(1, _options.ChunkCount);
            // 8-19 片大小自适应（入口一次定、永不变化）：边界固定 → 已完成片跨 attempt/换源/并发变化
            // 全部复用（换源续进度核心）；大文件自动大片（少请求、RTT 惩罚小）——旧实现（totalSize/chunkCount
            // 片=并发）片数一变边界全变，旧 .part 全废——升片/重试必然从零重下。
            var chunkSize = ChunkSizeFor(totalSize);
            var totalChunks = Math.Max(1, (int)Math.Ceiling(totalSize / (double)chunkSize));
            var currentConcurrency = Math.Clamp(await ProbeAndDecideConcurrencyAsync(url, totalSize, partDir, ct, throttle), 1, maxChunks);
            // 并发 = gate 槽位数（初始探测值，上限 maxChunks）；升片 = Release 腾出更多槽位，排队片自动进入
            using var gate = new SemaphoreSlim(currentConcurrency, maxChunks);
            var lastUpgradeAt = DateTime.MinValue;

            // AL61 分片总吞吐监测：cp.Bytes 每采样间隔测速，持续低速（默认 30s < 100KB/s）→ 判死换路；
            // 8-16 渐进限速 → 升片（只提高并发，不清 .parts 不重切——已下字节保留）；8-17 并发到顶仍慢 → 立即判死
            var slowDetector = new SlowSourceDetector(SlowThresholdForLimit(), _options.SlowSamples, _options.SlowProbeMs);
            using var slowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var slowAborted = 0;
            var speedRing = new double[3];
            var speedIdx = 0;
            var prevBytes = 0L;
            var cp = new ChunkProgress();
            var slowWatch = Task.Run(async () =>
            {
                try
                {
                    while (!slowCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(_options.SlowProbeMs), slowCts.Token);
                        var bytes = cp.Bytes;
                        speedRing[speedIdx % 3] = (bytes - prevBytes) / (TimeSpan.FromMilliseconds(_options.SlowProbeMs).TotalSeconds);
                        prevBytes = bytes;
                        speedIdx++;
                        // 8-22 补：AL61 持续低速判死同样受剩余守卫约束（同 679 行末尾判死）——
                        // 剩余不足一片时不判死（弃尾清零净亏，等最后一片下完）
                        if (allowSlowDeath && totalSize - bytes >= chunkSize && slowDetector.ShouldAbort(bytes, slowCts.Token))
                        {
                            Volatile.Write(ref slowAborted, 1);
                            slowCts.Cancel();
                            break;
                        }
                        var avg = (speedRing[0] + speedRing[1] + speedRing[2]) / 3;
                        // 升片判定：3 采样均速 < 阈值、剩余够下、并发有余量、距上次升片 ≥10s
                        if (speedIdx >= 3 && ShouldUpgradeChunks(
                                avg, bytes, totalSize, currentConcurrency, maxChunks,
                                (DateTime.UtcNow - lastUpgradeAt).TotalSeconds))
                        {
                            lastUpgradeAt = DateTime.UtcNow;
                            var target = Math.Min(maxChunks, currentConcurrency * 2);
                            gate.Release(target - currentConcurrency);
                            currentConcurrency = target;
                            LogWrapper.Info($"[下载] 升片 {ShortUrl(url)} 并发 {target / 2}→{target} 均速{avg / 1024:0}KB/s");
                        }
                        // 8-17 并发到顶仍慢 → 立即判死换路（镜像竞速淘汰后不会回来——外层重新 Resolve 让镜像重新参与）；
                        // 8-19 末尾（剩余 <8MB）低速也判死：升片被剩余守卫挡、并发 4（快源保底）未到顶——
                        // 该死区慢源（GitHub 连接级累积限速：末尾每连接传输量大被 throttle 到几十 KB）会拖到尾；
                        // 换路 = 新连接重新累积（前几 MB 快），收益远大于 Resolve+探测开销
                        // 8-22 补：剩余不足一片（<chunkSize）不判死——只剩最后一片在下，判死 = 弃 99.6% 清零重下
                        // （真机 8-12 PowerToys 271MB 最后 1MB 判死换路净亏）；等最后一片下完（至多几十秒）
                        if (speedIdx >= 3 && avg < SlowThresholdForLimit()
                            && (currentConcurrency >= maxChunks || totalSize - bytes < MinUpgradeRemainBytes)
                            && totalSize - bytes >= chunkSize)
                        {
                            Volatile.Write(ref slowAborted, 1);
                            slowCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            // 固定边界分片：已完成段复用入账（长度匹配即有效）、部分片入账后片内续传；未完成片排队等 gate
            var tasks = new List<Task>();
            for (var i = 0; i < totalChunks; i++)
            {
                var start = (long)i * chunkSize;
                var end = Math.Min(start + chunkSize - 1, totalSize - 1);
                var partPath = Path.Combine(partDir, $"{i}.part");
                var expectedLen = end - start + 1;

                // 已完成段直接复用（边界固定 → 跨 attempt/换源无缝续传）
                if (File.Exists(partPath) && new FileInfo(partPath).Length == expectedLen)
                {
                    Interlocked.Add(ref cp.Bytes, expectedLen);
                    continue;
                }

                // AL67 部分片（中断残留）：已下字节先入账（进度从断点续走不归零），
                // DownloadChunkAsync 内部从 have 处续传——片内重试不会再入账（重试不经过本循环）
                if (File.Exists(partPath) && new FileInfo(partPath).Length is > 0 and var have)
                    Interlocked.Add(ref cp.Bytes, have);

                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(slowCts.Token);
                    try
                    {
                        await DownloadChunkAsync(url, partPath, start, end, slowCts.Token, throttle, cp, Path.GetFileName(destPath), totalSize, progress, livePush);
                        // 片完成即时上报（force：允许同值重复报，见 ReportOnce 注释）
                        ReportOnce(cp, Path.GetFileName(destPath), totalSize, progress, force: true);
                    }
                    finally { gate.Release(); }
                }, slowCts.Token));
            }
            try
            {
                await Task.WhenAll(tasks);
                // 分片全成功 → 立即停监测，不等自然判死（慢速阈值=0 时 slowWatch 永不退出——真机靠
                // 「速度归零判死」碰巧退出，阈值关闭/短任务时 await slowWatch 永挂）
                slowCts.Cancel();
                await slowWatch;
            }
            catch
            {
                if (Volatile.Read(ref slowAborted) == 1)
                {
                    LogWrapper.Warn($"[下载] 判死换路 {ShortUrl(url)} 均速{slowDetector.LastSpeed / 1024:0}KB/s 剩余{(totalSize - cp.Bytes) / 1024 / 1024}MB");
                    throw new SlowSourceException(_options.SlowSpeedBps, slowDetector.LastSpeed); // 源死：直接换路
                }
                throw;
            }
            finally
            {
                slowCts.Cancel();
            }
            // 全片完成后补报最终值（片回调已覆盖时 Reported 护栏自动跳过，不重复）

            // 8-19 合并阶段提示：大文件合并（顺序读 64 片 + 写 166MB）要几秒，期间无下载活动——
            // 速度显示几十 KB 会被误认为「末尾限速卡死」；上报 Stage 让用户看到收尾而不是死寂
            progress?.Invoke(new DownloadProgress("正在合并文件…", Path.GetFileName(destPath),
                totalSize, totalSize, 99));

            // 合并写 tmp（AL29 H1：完整校验通过前不落真名）
            var tmp = destPath + ".tmp";
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (var i = 0; i < totalChunks; i++)
                {
                    var partPath = Path.Combine(partDir, $"{i}.part");
                    // AL67 片长度校验：服务器忽略 Range 返回 200 全量时片超长——拒绝换路（否则错位文件落盘）
                    var partLen = new FileInfo(partPath).Length;
                    var expectLen = i == totalChunks - 1 ? totalSize - (long)i * chunkSize : chunkSize;
                    if (partLen != expectLen)
                        throw new InvalidDataException($"分片 {i} 长度异常（{partLen} != {expectLen}）: {url}");
                    await using var part = File.OpenRead(partPath);
                    await part.CopyToAsync(dst, ct);
                }
                // 8-18 总长度校验：无 sha1（第三方下载）时字节数一致性的最后兜底——防分片计算/源大小漂移
                if (dst.Length != totalSize)
                    throw new InvalidDataException($"合并后总长度不符（{dst.Length} != {totalSize}）: {url}");
            }
            Directory.Delete(partDir, true);

            // SHA1 终校验失败 → 抛异常，外层换源重试（tmp 由 catch 清理）
            if (expectedSha1 is not null && !await Sha1MatchesAsync(tmp, expectedSha1, ct))
            {
                File.Delete(tmp);
                throw new InvalidDataException($"分片下载校验失败: {url}");
            }
            File.Move(tmp, destPath, true); // AL29 H1：校验通过后原子替换
        }
        catch (SlowSourceException) { throw; } // AL61 源死：不回退单连接（还要再等 30s 才判死）——直接换路
        catch (OperationCanceledException) { throw; } // 8-13 暂停/取消：保留 .parts（断点续传材料）——Resume 复用不清零
        catch
        {
            // 分片阶段失败：清理残留，回退单连接（弱网/镜像内容差异自愈）。
            // AL29 H1：只清中间产物（.parts/.tmp），destPath 已有旧文件保持不动——新文件未验证不覆盖
            try { Directory.Delete(destPath + ".parts", true); } catch { }
            try { File.Delete(destPath + ".tmp"); } catch { }
            await DownloadSingleAsync(url, destPath, expectedSha1, totalSize, progress, livePush, ct, throttle);
        }
    }


    /// <summary>
    /// 8-18 固定分片下限（1MB，PCL 同款）：边界永不变化 → 已完成片跨 attempt/换源/并发变化全部复用
    /// （换源续进度核心）。8-18 深夜用户实测：256KB 片对 GitHub 高延迟链路是灾难——每片一次 HTTP RTT
    /// （~100ms），吞吐崩到 1.5-2.5MB/s；改 1MB 后 RTT 惩罚降 4 倍。8-19 起片大小自适应
    /// （ChunkSizeFor）——本常量是下限（小文件恒 1MB，行为不变）。
    /// </summary>
    private const long FixedChunkSize = 1024 * 1024;

    /// <summary>8-19 片大小目标片数（大文件片 = totalSize/64——8 并发 × 8 波 = 0.8s RTT 上界）</summary>
    private const int TargetChunkCount = 64;

    /// <summary>8-19 片大小上限（4MB）：零字节失败重下粒度 + 服务器忽略 Range 检测前浪费的代价上限</summary>
    private const long MaxChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// 8-19 片大小自适应（纯函数）：小文件（&lt;64MB）恒 1MB（Modrinth 小库文件吃并发）；
    /// 大文件片 = totalSize/64（166MB → 2.6MB → 64 片，RTT 惩罚降 2.6 倍 vs 1MB）；
    /// 上限 4MB（1GB → 256 片，8 并发 32 波 = 3.2s）。片大小入口一次定、永不变化——
    /// 与并发解耦（探测/升片只动并发）→ 边界固定 → 换源续传复用语义保留。
    /// </summary>
    internal static long ChunkSizeFor(long totalSize)
        => Math.Clamp(totalSize / TargetChunkCount, FixedChunkSize, MaxChunkSize);

    /// <summary>ramp-up 探测段大小（1MB——快源 0.4s 内拉完提前决策，慢源 2s 窗口截断采样）</summary>
    private const long ProbeBytes = 1024 * 1024;

    /// <summary>ramp-up 探测窗口：超过此时间未拉完探测段 → 按已得字节决策</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// GitHub CDN 域（objects.githubusercontent.com / codeload.github.com / github.com）的探测档位：
    /// 8-15 用户实测小文件快、大文件掉几百 KB——GitHub CDN 对长连接渐进式限速（前几 MB 全速后被 throttle），
    /// 默认 1MB/2s 探测测不出 → 误判高速给 1 片 → 后续掉速。加大窗口让限速暴露 → 分片数决策正确。
    /// </summary>
    private const long ProbeBytesGitHub = 4 * 1024 * 1024;
    private static readonly TimeSpan ProbeWindowGitHub = TimeSpan.FromSeconds(5);

    /// <summary>是否 GitHub CDN 域（渐进式限速特征——需要更大探测窗口）</summary>
    public static bool IsGitHubCdn(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host == "github.com" || host.EndsWith(".github.com") || host == "objects.githubusercontent.com";
        }
        catch { return false; }
    }

    /// <summary>
    /// 单连接速度 ≥ 此值 → 快源。8-19 起大文件（&gt;8MB）快源不再降单连接：RTT 惩罚（每片一次往返
    /// ~100ms）对吞吐检测不可见（恒 &gt;10MB/s），慢速检测/升片永不触发——必须在探测时刻摊薄，
    /// 保底 4 并发（每连接单请求 ≤2.6-4MB 在 GitHub「前几 MB 节流」窗口内，不新增节流暴露）。
    /// 小文件（≤8MB）保持 1 连接：≤8 波 × 100ms = 0.8s RTT 可忽略，且限并发源不受影响。
    /// </summary>
    private const double FastSingleBps = 800 * 1024;

    /// <summary>单连接速度 < 此值 → 满片（按连接限速源需要分片）</summary>
    private const double SlowSingleBps = 200 * 1024;

    /// <summary>8-16 动态升片阈值：分片总吞吐持续低于此值 → 升片（渐进限速源掉速信号；高于判死阈值）</summary>
    private const double UpgradeSpeedBps = 300 * 1024;

    /// <summary>
    /// 8-16 动态升片判定（纯函数）：均速低于阈值（渐进限速掉速）、剩余 ≥ 8MB（剩余太少升片收益
    /// 不划算——8-17 用户实测 OBS 后期 80%+ 才掉速，「完成 <80%」会挡住后期升片，改按剩余量判断）、
    /// 并发未到上限、距上次升片 ≥10s（防抖动循环）。8-18 参数改并发语义（固定片后总片数恒 ≥ 上限，
    /// 旧「片数 < max」永不触发）。
    /// </summary>
    public static bool ShouldUpgradeChunks(double avgBps, long bytes, long totalSize,
        int currentConcurrency, int maxConcurrency, double secondsSinceUpgrade)
        => avgBps < UpgradeSpeedBps && totalSize - bytes >= MinUpgradeRemainBytes
           && currentConcurrency < maxConcurrency && secondsSinceUpgrade >= 10;

    /// <summary>升片最小剩余量（低于此值不再升片——重下损失大于收益）</summary>
    public const long MinUpgradeRemainBytes = 8 * 1024 * 1024;

    /// <summary>
    /// AL60 探测并发决策：拉文件头 1MB（或 2s 窗口）测单连接速度 → 返回并发建议（固定片后并发数 ≠ 片数；
    /// 8-18 片边界固定 1MB，探测只决定同时下几片）。探测段写 probe.part（探测后删除）。
    /// </summary>
    internal async Task<int> ProbeAndDecideConcurrencyAsync(string url, long totalSize, string partDir, CancellationToken ct,
        ThrottleState throttle)
    {
        var maxChunks = Math.Max(1, _options.ChunkCount);
        if (totalSize < ProbeBytes) return maxChunks; // 小文件（<1MB）保持旧满片行为——按连接限速源（Modrinth 单连几十 KB/s）
                                                      // 的 MC 小库文件需要分片；探测对它们无意义（探测段≈整个文件）
        // 8-22 域特征快速路径（看请求对象直接决策，免探测）：渐进限速 CDN（Modrinth/GitHub）+ ≤8MB
        // 决策恒为 4 并发（每连接传输量摊在 CDN「前几 MB 快窗口」内）——探测 1MB 对这类文件是纯浪费
        // （白下 1MB + 白等窗口；Fabric API 1.6MB 探测占 60% 流量）。>8MB 大文件仍走探测
        // （快/慢源分档对大文件有决策价值——满并发 vs 保底 4 差 4 个连接）。
        // 后续可加「按域经验记忆」（同域判死/成功记录复用决策）——静态特征先行，探测兜底仍在
        if (totalSize <= 8 * 1024 * 1024 && IsProgressiveThrottleCdn(url))
            return Math.Min(4, maxChunks);

        // 8-15 GitHub CDN 档位：更大探测窗口让渐进式限速暴露（默认档会误判高速给 1 片 → 大文件掉速）
        var probeLimit = IsGitHubCdn(url) ? ProbeBytesGitHub : ProbeBytes;
        var probeWindow = IsGitHubCdn(url) ? ProbeWindowGitHub : ProbeWindow;

        var probeEnd = Math.Min(probeLimit - 1, totalSize - 1);
        var probePart = Path.Combine(partDir, "probe.part");
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probeTask = Task.Run(() => DownloadChunkAsync(url, probePart, 0, probeEnd, probeCts.Token, throttle)); // 8-22 探测段也共享节流——限速时不再瞬间全速拉 1MB
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var probeDone = await Task.WhenAny(probeTask, Task.Delay(probeWindow, ct));
        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        long probeBytes;
        if (probeDone == probeTask)
        {
            try { await probeTask; probeBytes = probeEnd + 1; }
            catch { return 1; } // 探测失败 → 单连接直拉（分片阶段 catch 还有回退兜底）
        }
        else
        {
            probeBytes = File.Exists(probePart) ? new FileInfo(probePart).Length : 0;
            probeCts.Cancel();
            try { await probeTask; } catch { }
        }
        try { File.Delete(probePart); } catch { }

        var speed = probeBytes / elapsed;
        // 8-19 快源大文件保底 4 并发（RTT 摊薄；小文件 ≤8MB 保持 1——限并发源不受影响）；
        // 8-13 GitHub CDN 大文件直接满并发：连接级累积限速按「每连接传输量」——满并发把 271MB
        // 摊到 8 连接（每连接 34MB），每连接尽量留在「前几 MB 快」窗口内
        // 8-22 Fabric API 治本：渐进限速 CDN（Modrinth/GitHub）小文件快源也保底 4——CDN 开局全速
        // （探测 1MB 显示快）后按连接累积量掉到几十 KB/s，1 连接 1.6MB 文件全程磨；
        // 升片守卫（剩余 ≥8MB）对小文件永不触发，探测阶段必须决策正确
        return speed >= FastSingleBps
            ? totalSize <= 8 * 1024 * 1024
                ? IsProgressiveThrottleCdn(url) ? Math.Min(4, maxChunks) : 1
              : IsGitHubCdn(url) ? maxChunks : Math.Min(4, maxChunks)
            : speed >= SlowSingleBps ? Math.Min(4, maxChunks)
            : maxChunks;
    }

    /// <summary>渐进限速 CDN（按连接累积传输量掉速——前几 MB 快、之后被 throttle）：GitHub CDN 之外，
    /// Modrinth 文件 CDN 同特征（8-22 真机 Fabric API：开局爆速后掉到几十 KB/s）。此类源需要分片
    /// 把每连接传输量摊在「快窗口」内，小文件也不例外。</summary>
    public static bool IsProgressiveThrottleCdn(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return IsGitHubCdn(url) || host == "cdn.modrinth.com" || host == "api.modrinth.com";
        }
        catch { return false; }
    }

    private async Task DownloadChunkAsync(string url, string partPath, long start, long end, CancellationToken ct,
        ThrottleState? throttle = null, ChunkProgress? cp = null, string? destName = null, long totalSize = 0,
        DownloadProgressHandler? progress = null, Action<long, long>? livePush = null, int attempt = 0)
    {
        try
        {
            // AL67 片断点续传：残留部分片（中断/重试）从已下长度续拉，不整片重下——
            // Modrinth 实测每次末尾断流，37.4/39MB 判死后只需补 1.6MB
            long have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            var expectedLen = end - start + 1;
            if (have >= expectedLen)
            {
                if (have == expectedLen) return; // 完整段复用（重试入口兜底）
                File.Delete(partPath);           // 超长残留（内容错误）→ 删除重下
                have = 0;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start + have, end); // 从断点续传
            using var response = await SendWithHeaderTimeoutAsync(request, ct); // AL64 响应头超时
            var isPartial = response.StatusCode == HttpStatusCode.PartialContent; // 206 才追加；200（服务器忽略 Range）重写防错位
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(partPath,
                have > 0 && isPartial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[_options.BufferSize];
            // AL66 读心跳：探测段也走本函数（探测读挂起时 slowWatch 尚未启动——心跳兜底）
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(TimeSpan.FromMilliseconds(_options.ReadStallTimeoutMs));
            int n;
            while ((n = await ReadWithStallAsync(src, buffer, stallCts, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                if (throttle is not null)
                    await ThrottleStreamAsync(n, ct, throttle, _limitPerStream);
                if (cp is not null && progress is not null)
                    ReportChunkProgress(cp, n, destName!, totalSize, progress, livePush);
            }
        }
        catch (OperationCanceledException) { throw; } // 取消不重试（探测取消/用户取消原样上抛——否则重试请求悬挂）
        catch when (attempt < 1)
        {
            // 单片瞬时失败重试 1 次
            await DownloadChunkAsync(url, partPath, start, end, ct, throttle, cp, destName, totalSize, progress, livePush, attempt + 1);
        }
    }

    /// <summary>分片进度节流上报：Interlocked 累加字节，CompareExchange 抢占 250ms 窗口（见 ChunkProgress 注释）</summary>
    private static void ReportChunkProgress(ChunkProgress cp, int n, string destName, long totalSize,
        DownloadProgressHandler progress, Action<long, long>? livePush)
    {
        Interlocked.Add(ref cp.Bytes, n);
        livePush?.Invoke(cp.Bytes, Environment.TickCount64); // 逐读推拍（陪跑采样——绕开节流量化）
        var now = cp.Sw.ElapsedMilliseconds;
        var last = Interlocked.Read(ref cp.LastReportMs);
        if (now - last >= ChunkProgress.WindowMs
            && Interlocked.CompareExchange(ref cp.LastReportMs, now, last) == last)
        {
            ReportOnce(cp, destName, totalSize, progress);
        }
    }

    /// <summary>
    /// 串行上报护栏：锁内读 Bytes + 锁内 Invoke（锁串行化 → 读到的值序列必然不降，杜绝
    /// 「读旧快照 → 锁外晚 Invoke」的倒序回退）。force=false 时按 cp.Reported 去重（节流/最终
    /// 上报报最新值即可）；force=true 时允许同值重复报（片完成回调——片并行同刻完成时
    /// 若不重复报会被合并成 1 次，实时粒度丢失）。锁内 Invoke：用户回调仅更新 UI 进度，
    /// 不重入下载（若回调同步触发同 cp 下载会死锁——约定如此）。
    /// </summary>
    private static void ReportOnce(ChunkProgress cp, string destName, long totalSize,
        DownloadProgressHandler progress, bool force = false)
    {
        lock (cp)
        {
            var done = Volatile.Read(ref cp.Bytes);
            if (!force && done <= cp.Reported) return;
            cp.Reported = done;
            progress(new DownloadProgress("", destName, done, totalSize,
                totalSize > 0 ? Math.Min(done * 100.0 / totalSize, 99) : 0));
        }
    }

    /// <summary>HEAD 取长度：试全部候选源，全失败返回 0（走单连接按响应长度下载）。
    /// 每源限时 8s——HEAD 卡住（CDN 丢包/线路抖动）不拖长分片前的等待，超时直接换下一源。</summary>
    private async Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        foreach (var src in _resolver.Resolve(url))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8)); // 无超时时 HttpClient 默认等响应头 100s = 停顿
                using var request = new HttpRequestMessage(HttpMethod.Head, src);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                response.EnsureSuccessStatusCode();
                return response.Content.Headers.ContentLength ?? 0;
            }
            catch (Exception)
            {
                ThirdPartyDlSourceResolver.MarkFailed(src); // 8-18 失败记忆：HEAD 8s 超时浪费点，下轮排末位
            }
        }
        return 0;
    }

    // AL70：internal——VersionDownloadPipeline 复用（asset index 已存在 SHA1 匹配时跳过下载）
    internal static async Task<bool> Sha1MatchesAsync(string path, string expected, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await SHA1.HashDataAsync(fs, ct);
            return Convert.ToHexStringLower(hash) == expected;
        }
        catch (Exception) { return false; }
    }

    // ---------- 版本编排 ----------

    /// <summary>
    /// 编排完整版本下载。传 ctx（组任务）走阶段全并行管线（VersionDownloadPipeline，文件级子任务）；
    /// 否则走旧展平路径（阶段串行 + 加权整体百分比，兼容旧调用与测试）。
    /// </summary>
    public Task DownloadVersionAsync(
        VersionJson version, DownloadGroupContext? ctx = null,
        DownloadProgressHandler? progress = null, CancellationToken ct = default)
    {
        if (ctx is not null)
            return new VersionDownloadPipeline(this, _options, _gameDirectory).RunAsync(version, ctx, ct);
        return RunLegacyAsync(version, progress, ct);
    }

    /// <summary>旧展平路径：client → libraries → index → assets → logging（阶段串行）</summary>
    public async Task RunLegacyAsync(
        VersionJson version, DownloadProgressHandler? progress = null, CancellationToken ct = default)
    {
        // 加载器版本：解析 inheritsFrom 链（父版本必须已安装；client jar 沿链继承后落子版本目录）
        if (version.InheritsFrom is not null)
        {
            version = VersionJsonMerger.ResolveChain(version, LoadParentJson);
            if (version.InheritsFrom is { } unresolved)
                throw new FileNotFoundException(
                    $"依赖的父版本 {unresolved} 未安装（请先在版本页安装原版 {unresolved}）");
        }

        var versionDir = Path.Combine(_gameDirectory, "versions", version.Id);
        var librariesDir = Path.Combine(_gameDirectory, "libraries");
        var assetsDir = Path.Combine(_gameDirectory, "assets");

        // 预估总字节（整体百分比分母）
        var librariesBytes = 0L;
        foreach (var lib in version.Libraries ?? [])
        {
            librariesBytes += lib.Downloads?.Artifact?.Size ?? 0;
            if (lib.Downloads?.Classifiers is { } classifiers)
                librariesBytes += classifiers.Values.Sum(c => c.Size ?? 0);
        }
        var assetsBytes = version.AssetIndex?.TotalSize ?? 0;
        var estimated = (version.Downloads?.Client?.Size ?? 0) + librariesBytes
                        + (version.AssetIndex?.Size ?? 0) + assetsBytes
                        + (version.Logging?.Client?.File?.Size ?? 0);

        // 文件级进度包装：阶段 + 文件名 + 整体百分比（跨文件累计；并发报告为近似值，可接受）
        var accumulated = 0L;
        DownloadProgressHandler? Wrap(string stage, string? fileName)
        {
            if (progress is null) return null;
            long fileDone = 0;
            return p =>
            {
                if (p.FileBytesDone > fileDone) fileDone = p.FileBytesDone;
                var overall = estimated > 0 ? Math.Min((accumulated + fileDone) * 100.0 / estimated, 99) : p.OverallPercent;
                progress(new DownloadProgress(stage, fileName, p.FileBytesDone, p.FileTotalBytes, overall));
            };
        }

        // 1. client jar
        if (version.Downloads?.Client is { } client)
        {
            await DownloadFileAsync(client.Url, Path.Combine(versionDir, $"{version.Id}.jar"),
                client.Sha1, client.Size, Wrap("下载客户端", $"{version.Id}.jar"), ct);
            accumulated += client.Size ?? 0;
        }

        // 2. libraries（文件级并行，逐文件报告）
        using var semaphore = new SemaphoreSlim(_options.LibraryConcurrency);
        var libraryTasks = new List<Task>();
        var libTotal = 0;
        foreach (var lib in version.Libraries ?? [])
        {
            if (lib.Downloads?.Artifact is not null) libTotal++;
            else if (lib.Url is not null) libTotal++; // AL10.1：Fabric/Forge 的 url 形式库（顶层 url 无 downloads.artifact）
            if (lib.Natives is not null) libTotal++;
        }
        var libIndex = 0;
        foreach (var lib in version.Libraries ?? [])
        {
            var artifact = lib.Downloads?.Artifact;
            // AL30：url 空 artifact（forge client classifier 继承引用）无下载目标，跳过（同 pipeline/VerifyFiles 规则）
            if (artifact is not null && !string.IsNullOrEmpty(artifact.Url))
            {
                var path = Path.Combine(librariesDir, MavenPath.FullPath(lib.Name));
                libraryTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var n = Interlocked.Increment(ref libIndex);
                        await DownloadFileAsync(artifact.Url, path, artifact.Sha1, artifact.Size,
                            Wrap($"下载库文件 {n}/{libTotal}", MavenPath.FileName(lib.Name)), ct);
                    }
                    finally { semaphore.Release(); }
                }, ct));
            }

            // AL10.1：Fabric/Forge 库无 downloads.artifact，顶层 url + Maven 坐标拼下载地址（如 maven.fabricmc.net）
            if (artifact is null && lib.Url is { } repoUrl)
            {
                var path = Path.Combine(librariesDir, MavenPath.FullPath(lib.Name));
                var dlUrl = repoUrl.TrimEnd('/') + "/" + MavenPath.FullPath(lib.Name).Replace('\\', '/');
                libraryTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var n = Interlocked.Increment(ref libIndex);
                        await DownloadFileAsync(dlUrl, path, lib.Sha1, lib.Size,
                            Wrap($"下载库文件 {n}/{libTotal}", MavenPath.FileName(lib.Name)), ct);
                    }
                    finally { semaphore.Release(); }
                }, ct));
            }

            if (lib.Natives is { } natives && natives.TryGetValue("windows", out var classifierKey)
                && lib.Downloads?.Classifiers?.TryGetValue(classifierKey, out var nativeFile) == true)
            {
                var nativeName = MavenPath.FileName(lib.Name + ":" + classifierKey);
                var nativePath = Path.Combine(librariesDir, MavenPath.DirectoryPath(lib.Name), nativeName);
                libraryTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var n = Interlocked.Increment(ref libIndex);
                        await DownloadFileAsync(nativeFile.Url, nativePath, nativeFile.Sha1, nativeFile.Size,
                            Wrap($"下载库文件 {n}/{libTotal}", nativeName), ct);
                    }
                    finally { semaphore.Release(); }
                }, ct));
            }
        }
        await Task.WhenAll(libraryTasks);
        accumulated += librariesBytes;

        // 3. assets index
        if (version.AssetIndex is { } assetIndex)
        {
            var indexPath = Path.Combine(assetsDir, "indexes", $"{assetIndex.Id}.json");
            await DownloadFileAsync(assetIndex.Url, indexPath, assetIndex.Sha1, assetIndex.Size,
                Wrap("下载资源索引", $"{assetIndex.Id}.json"), ct);
            accumulated += assetIndex.Size ?? 0;

            // 4. assets 差量（文件级并行，按完成数报进度）
            if (File.Exists(indexPath))
            {
                var index = JsonSerializer.Deserialize<AssetsIndex>(
                    await File.ReadAllTextAsync(indexPath, ct));
                if (index is not null)
                {
                var objectsDir = Path.Combine(assetsDir, "objects");
                using var assetSemaphore = new SemaphoreSlim(_options.AssetConcurrency);
                var assetTasks = new List<Task>();
                var totalAssets = index.Objects.Count;
                var doneAssets = 0;
                long doneAssetsBytes = 0; // REVIEW-速度：累计资产字节（上报用真实字节——旧代码把文件序号当 FileBytesDone → 速度=文件数/秒却显示 MB/s 虚高）
                var totalAssetsBytes = index.Objects.Values.Sum(o => o.Size);
                foreach (var (_, obj) in index.Objects)   // key 是文件路径，hash 在 value
                {
                    var h = obj.Hash;
                    var objPath = Path.Combine(objectsDir, h[..2], h);
                    if (File.Exists(objPath) && new FileInfo(objPath).Length == obj.Size)
                    {
                        Interlocked.Increment(ref doneAssets);
                        Interlocked.Add(ref doneAssetsBytes, obj.Size);
                        continue;
                    }
                    var url = $"https://resources.download.minecraft.net/{h[..2]}/{h}";
                    assetTasks.Add(Task.Run(async () =>
                    {
                        await assetSemaphore.WaitAsync(ct);
                        try
                        {
                            await DownloadFileAsync(url, objPath, h, obj.Size,
                                Wrap($"下载资源 {Volatile.Read(ref doneAssets)}/{totalAssets}", h), ct);
                            var n = Interlocked.Increment(ref doneAssets);
                            var doneB = Interlocked.Add(ref doneAssetsBytes, obj.Size);
                            if (progress is not null)
                                progress(new DownloadProgress($"下载资源 {n}/{totalAssets}", h, doneB, totalAssetsBytes,
                                    estimated > 0
                                        ? Math.Min((accumulated + doneB) * 100.0 / estimated, 99)
                                        : 0));
                        }
                        finally { assetSemaphore.Release(); }
                    }, ct));
                }
                await Task.WhenAll(assetTasks);
                accumulated += assetsBytes;
                }
            }
        }

        // 5. logging 配置
        if (version.Logging?.Client?.File is { } logFile)
        {
            var fileName = Path.GetFileName(new Uri(logFile.Url).LocalPath);
            var logPath = Path.Combine(assetsDir, "log_configs", fileName);
            await DownloadFileAsync(logFile.Url, logPath, logFile.Sha1, logFile.Size,
                Wrap("日志配置", fileName), ct);
            accumulated += logFile.Size ?? 0;
        }
    }

    /// <summary>读磁盘上的父版本 JSON（inheritsFrom 链用）</summary>
    private VersionJson? LoadParentJson(string id)
    {
        var path = Path.Combine(_gameDirectory, "versions", id, $"{id}.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path)); }
        catch (Exception) { return null; }
    }

    private sealed record AssetsIndex(
        [property: System.Text.Json.Serialization.JsonPropertyName("objects")]
        Dictionary<string, AssetObject> Objects);

    private sealed record AssetObject(
        [property: System.Text.Json.Serialization.JsonPropertyName("hash")] string Hash,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size);
}
