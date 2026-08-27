using System.Runtime.CompilerServices;

// 8-27 生态实例构造可测性：Launcher.Core.Tests 引 Launcher.App（已 ProjectReference），
// 允许测试访问 internal BuildInstanceVM 等内部成员（模组版本解析回归防护）。
[assembly: InternalsVisibleTo("Launcher.Core.Tests")]
