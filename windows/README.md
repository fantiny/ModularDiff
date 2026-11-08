# Windows 专属界面

`ModularDiff.Gui` 使用 WPF，目标为 `net10.0-windows`。本目录包含 WPF 控件、窗口和文件对话框，以及 ExplorerIntegration 中的注册表、资源管理器右键菜单集成。

比较、编辑、合并、同步和备份恢复的实现来自 `../shared/plugins`，项目只引用 `../shared/ModularDiff.Core` 及其传递引用的 Contracts，不引用 Avalonia 项目。

从仓库根目录运行 `./build.ps1` 或构建 `ModularDiff.sln`。需要自定义 SDK 时使用 `./build.ps1 -Dotnet <dotnet.exe路径>`。输出仍为根目录 `artifacts/gui`、`artifacts/cli`、`artifacts/plugins`，现有启动脚本不变。
