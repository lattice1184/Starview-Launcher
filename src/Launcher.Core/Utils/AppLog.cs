using Microsoft.Extensions.Logging;
using PCL.Core.Logging;

namespace Launcher.Core.Utils;

/// <summary>
/// 应用日志门面（8-18）：懒接 PCL.Core Logger（生命周期启动后可用），统一类别 "starview"。
/// 业务埋点一律走此（关键事件 1 行）；Logger 未就绪时返回 null，调用方用 ?. 忽略。
/// </summary>
public static class AppLog
{
    private static ILogger? _instance;

    public static ILogger? Instance
    {
        get
        {
            if (_instance is not null) return _instance;
            var logger = PCL.Core.Logging.LogService.Logger;
            return logger is null ? null : _instance = logger.CreateLogger("starview");
        }
    }
}
