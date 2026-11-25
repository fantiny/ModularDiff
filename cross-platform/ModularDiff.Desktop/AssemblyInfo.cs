using System.Runtime.CompilerServices;

// 测试需要直接验证少量不便从公开界面触达的内部机制(例如状态栏的"配方"失效规则)。
[assembly: InternalsVisibleTo("ModularDiff.Desktop.Tests")]
