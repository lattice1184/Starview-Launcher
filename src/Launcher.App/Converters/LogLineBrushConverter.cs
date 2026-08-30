using Avalonia.Data.Converters;
using Avalonia.Media;
using Launcher.App.ViewModels;

namespace Launcher.App.Converters;

/// <summary>启动日志行类别 → 前景色：报错红 / 启动器事件(§)强调青 / 普通默认亮色。</summary>
public sealed class LogLineBrushConverter : IValueConverter
{
    public static LogLineBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is LogLineKind kind
            ? new SolidColorBrush(kind switch
            {
                LogLineKind.Warn => Color.Parse("#E8B84B"),   // 警告：琥珀
                LogLineKind.Error => Color.Parse("#E05A5A"),  // 报错：红
                LogLineKind.Fatal => Color.Parse("#C62828"),  // 致命：深红
                LogLineKind.Launcher => Color.Parse("#4FC3F7"), // 启动器事件(§)：强调青
                _ => Color.Parse("#E8ECF4"),                  // 普通：近白
            })
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
