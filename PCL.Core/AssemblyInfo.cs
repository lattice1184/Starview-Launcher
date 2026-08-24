using System.Runtime.CompilerServices;

// 8-24 砍 WPF：移除 System.Windows.Markup 的 XmlnsDefinition/XmlnsPrefix 程序集属性（WPF XAML 命名空间映射，
// 启动器纯 Avalonia 不用；它们引用 System.Xaml/PresentationFramework 强制拉入 WPF 运行时）

[assembly: DisableRuntimeMarshalling]
[assembly: InternalsVisibleTo("PCL.Core.Test")]
