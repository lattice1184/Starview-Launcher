using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL.Core.Logging;

[LifecycleService(LifecycleState.Loading, Priority = int.MaxValue)]
public class LogService : ILifecycleLogService
{
    private static LifecycleContext? _context;

    private static Logger? _logger;

    private static bool _wrapperRegistered;

    /// <summary>
    /// [Avalonia 补丁] 致命错误展示回调（宿主注入；默认输出到 stderr）
    /// </summary>
    public static Action<string>? FatalErrorReporter { get; set; }

    /// <summary>
    /// [Starview 8-30] 全局日志目录覆盖（宿主注入）。null = 默认 ExecutableDirectory/Lattice/Log。
    /// Linux 上可执行目录可能只读（日志写不进），且 Launcher 日志统一进 AppPaths.DataRoot/logs。
    /// 必须在 Lifecycle.OnLoading（LogService.StartAsync）之前设置。
    /// </summary>
    public static string? LogDirectoryOverride { get; set; }

    private LogService()
    {
        _context = Lifecycle.GetContext(this);
    }

    private static LifecycleContext Context => _context!;
    public static Logger Logger => _logger!;
    public string Identifier => "log";
    public string Name => "日志服务";
    public bool SupportAsync => false;

    public Task StartAsync()
    {
        Context.Trace("正在初始化 Logger 实例");
        var logDir = LogDirectoryOverride ?? Path.Combine(Basics.ExecutableDirectory, "Lattice", "Log");
        var config = new LoggerConfiguration(logDir);
        _logger = new Logger(config);
        Context.Trace("正在注册日志事件");
        LogWrapper.OnLog += _OnWrapperLog;
        _wrapperRegistered = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_wrapperRegistered)
            LogWrapper.OnLog -= _OnWrapperLog;
        if (_logger is not null)
            await _logger.DisposeAsync().ConfigureAwait(false);
    }

    public void OnLog(LifecycleLogItem item)
    {
        _LogAction(item.Level, item.ActionLevel, item.ComposeMessage(), item.Message, item.Exception);
    }

    private static void _LogAction(
        LogLevel level,
        ActionLevel actionLevel,
        string formatted,
        string plain,
        Exception? ex)
    {
        if (ex is not null)
            TelemetryService.ReportException(ex, plain, level);

        // log
#if !TRACE
    if (actionLevel != ActionLevel.TraceLog)
#endif
        Logger.Log(formatted);

        switch (actionLevel)
        {
            case <= ActionLevel.NormalLog:
                return;

            // hint
            case ActionLevel.Hint or ActionLevel.HintErr:
                HintWrapper.Show(
                    plain,
                    actionLevel == ActionLevel.Hint ? HintTheme.Info : HintTheme.Error);
                break;

            // message box
            case ActionLevel.MsgBox or ActionLevel.MsgBoxErr:
            {
                var theme = actionLevel == ActionLevel.MsgBoxErr
                    ? MsgBoxTheme.Error
                    : MsgBoxTheme.Info;

                var caption = ex is null
                    ? null
                    : Lang.Text("SystemDialog.Error.Unexpected.Title");

                var message = _ComposeUserError(plain, ex);

                if (actionLevel == ActionLevel.MsgBoxErr)
                    message = Lang.Text("SystemDialog.Error.Message.WithLogExportGuidance", message);

                MsgBoxWrapper.Show(message, caption, theme, false);
                break;
            }

            // fatal message box
            case ActionLevel.MsgBoxFatal:
            {
                var message = Lang.Text(
                    "SystemDialog.Fatal.Message.WithFeedbackGuidance",
                    _ComposeUserError(plain, ex));

                // [Avalonia 补丁] 致命错误对话框改为可注入回调
                var title = Lang.Text("SystemDialog.Fatal.Title");
                if (FatalErrorReporter is { } reporter) reporter($"{title}\n{message}");
                else Console.Error.WriteLine($"[FATAL] {title}\n{message}");

                break;
            }
        }
    }


    private static string _ComposeUserError(string plain, Exception? exception)
    {
        var summary = string.IsNullOrWhiteSpace(plain)
            ? Lang.Text("SystemDialog.Error.Unexpected.Message")
            : plain;

        return exception is null
            ? summary
            : ExceptionDetails.Compose(summary, exception);
    }

    private static void _OnWrapperLog(
        LogLevel level,
        string msg,
        string? module,
        Exception? ex)
    {
        var thread = Thread.CurrentThread.Name ?? $"#{Environment.CurrentManagedThreadId}";
        if (module is not null) module = $"[{module}] ";
        var result = $"[{DateTime.Now:HH:mm:ss.fff}] [{level.PrintName()}] [{thread}] {module}{msg}";
        _LogAction(
            level,
            level.DefaultActionLevel(),
            ex is null
                ? result
                : $"{result}\n{ex}",
            msg,
            ex);
    }
}