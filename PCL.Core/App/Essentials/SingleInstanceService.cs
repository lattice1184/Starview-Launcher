using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PCL.Core.App.IoC;
using PCL.Core.IO;
using PCL.Core.Utils;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading, Priority = -2134567890)]
[LifecycleScope("single-instance", "单例", false)]
public sealed partial class SingleInstanceService
{
    private static FileStream? _lockStream;
    private static readonly string _LockFilePath = Path.Combine(Paths.SharedLocalData, "instance.lock");

    private static void _TryRpc(string processId, string content)
    {
        var pipeName = $"{RpcService.PipePrefix}@{processId}";
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(1000);
        using var sw = new StreamWriter(pipe, PipeComm.PipeEncoding);
        sw.WriteLine(content);
        sw.Write(PipeComm.PipeEndingChar);
        sw.Flush();
    }

    [LifecycleStart]
    private static void _Start()
    {
        try
        {
            var stream = File.Open(_LockFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            Context.Debug("未发现重复实例，正在向单例锁写入信息");
            using var sw = new StreamWriter(stream, Encoding.ASCII, 8, true);
            sw.Write(Basics.CurrentProcessId);
            sw.Flush();
            _lockStream = stream;
        }
        catch (Exception)
        {
            try
            {
                using var stream = File.Open(_LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var pid = reader.ReadToEnd();
                Context.Info($"发现重复实例 {pid}，尝试传递参数并拉起主窗口");
                try
                {
                    _TryRpc(pid, "REQ cli\n" + JsonSerializer.Serialize(StartupService.UnhandledCommands, JsonCompat.SerializerOptions));
                    _TryRpc(pid, "REQ activate");
                }
                catch (Exception ex)
                {
                    Context.Warn("RPC 通信失败", ex);
                    // 8-23 残留锁自愈：进程被强杀（_Stop 不跑）会留死 PID 锁，RPC 必然失败。
                    // 检查锁里 PID 是否真存活——已死说明是残留锁，清掉重试获取单例（不再无脑退出）。
                    if (!IsProcessAlive(pid))
                    {
                        Context.Info($"锁内进程 {pid} 已不存在（残留锁），清理后重新获取单例");
                        try { File.Delete(_LockFilePath); } catch { }
                        _Start();
                        return;
                    }
                }
            }
            catch (Exception ex) { Context.Error("读取单例锁出错", ex); }
            finally { Context.RequestExit(1); }
        }
    }

    /// <summary>8-23 判断锁内 PID 是否仍存活（残留死锁自愈用）</summary>
    private static bool IsProcessAlive(string pid)
    {
        if (!int.TryParse(pid, out var id) || id <= 0) return false;
        try { Process.GetProcessById(id); return true; }
        catch { return false; }
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (_lockStream is null) return;
        Context.Debug("正在删除单例锁");
        _lockStream.Dispose();
        File.Delete(_LockFilePath);
    }
}
