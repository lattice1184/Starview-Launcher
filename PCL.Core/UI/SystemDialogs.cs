using System;
using PCL.Core.App.Localization;
using PCL.Core.Logging;

namespace PCL.Core.UI;

/// <summary>
///     提供文件和文件夹对话框相关的实用方法。
/// </summary>
public static class SystemDialogs
{
    // 8-24 砍 WPF：原实现用 Microsoft.Win32.SaveFileDialog/OpenFileDialog/OpenFolderDialog（PresentationFramework ~15MB）。
    // 启动器纯 Avalonia（自研 StorageProvider 文件选择），本类零调用，改恒返回"取消"（空串/空数组）。
    // 保留签名与文档以维持 API 兼容，Files.ExportAsZipArchiveAsync 已带空路径兜底。

    /// <summary>
    ///     显示保存文件对话框，要求用户选择保存位置。
    /// </summary>
    /// <param name="title">对话框标题。为 <c>null</c> 时使用本地化的默认标题。</param>
    /// <param name="fileName">默认文件名。</param>
    /// <param name="fileFilter">文件格式过滤器，例如 "常用图片文件|*.png;*.jpg"。为 <c>null</c> 时使用本地化的全部文件筛选器。</param>
    /// <param name="initialDirectory">初始目录，默认为 <c>null</c>。</param>
    /// <returns>用户选择的完整文件路径，如果取消则返回空字符串。</returns>
    public static string SelectSaveFile(
        string? title,
        string fileName,
        string? fileFilter = null,
        string? initialDirectory = null)
    {
        LogWrapper.Info("Dialog", "保存文件对话框已摘除（砍 WPF），返回取消");
        return "";
    }

    /// <summary>
    ///     显示打开文件对话框，要求用户选择单个文件。
    /// </summary>
    /// <param name="fileFilter">文件格式过滤器，例如 <c>常用图片文件|*.png;*.jpg</c>。为 <c>null</c> 时使用本地化的全部文件筛选器。</param>
    /// <param name="title">对话框标题。为 <c>null</c> 时使用本地化的默认标题。</param>
    /// <param name="initialDirectory">初始目录，默认由系统决定。</param>
    /// <returns>用户选择的完整文件路径，如果取消则返回空字符串。</returns>
    public static string SelectFile(
        string? fileFilter = null,
        string? title = null,
        string? initialDirectory = null)
    {
        return "";
    }

    /// <summary>
    ///     显示打开文件对话框，要求用户选择文件。
    /// </summary>
    /// <param name="fileFilter">文件格式过滤器，例如 <c>常用图片文件|*.png;*.jpg</c>。为 <c>null</c> 时使用本地化的全部文件筛选器。</param>
    /// <param name="title">对话框标题。为 <c>null</c> 时使用本地化的默认标题。</param>
    /// <param name="initialDirectory">初始目录，默认由系统决定。</param>
    /// <param name="allowMultiSelect">是否允许选择多个文件，默认允许。</param>
    /// <returns>用户选择的文件路径数组，如果取消则返回空数组。</returns>
    public static string[] SelectFiles(
        string? fileFilter = null,
        string? title = null,
        string? initialDirectory = null,
        bool allowMultiSelect = true)
    {
        return [];
    }

    /// <summary>
    ///     显示文件夹选择对话框，要求用户选择一个文件夹。
    /// </summary>
    /// <param name="title">对话框标题。为 <c>null</c> 时使用本地化的默认标题。</param>
    /// <param name="initialDirectory">初始目录，默认为桌面。</param>
    /// <returns>用户选择的文件夹路径（以 \ 结尾），如果取消则返回空字符串。</returns>
    public static string SelectFolder(string? title = null, string? initialDirectory = null)
    {
        return "";
    }
}
