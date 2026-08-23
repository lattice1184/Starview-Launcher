using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Launcher.App.Converters;

/// <summary>值等于参数（字符串比较；null 视作空串；参数 "ALL" 匹配 null）——chips 选中态 / 单选枚举双向绑定用</summary>
public sealed class ValueEqualsConverter : IValueConverter
{
    public static ValueEqualsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var v = value?.ToString() ?? "";
        var p = parameter?.ToString() ?? "";
        if (p == "ALL") p = "";
        return v == p;
    }

    /// <summary>8-23 单选枚举（RadioButton GroupName）用：勾选时把参数解析回枚举；取消勾选不改值</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not true || parameter is null) return BindingOperations.DoNothing;
        return targetType.IsEnum && Enum.TryParse(targetType, parameter.ToString(), out var parsed)
            ? parsed
            : parameter.ToString();
    }
}
