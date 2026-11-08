# ModularDiff 跨平台版

这个子目录提供独立的 Avalonia 11.3.22 桌面宿主，目标为 .NET 10。原 Windows WPF GUI 不变。核心、契约、CLI 和插件通过项目引用及运行时加载复用父目录代码，因此复制源码时需要保留整个 ModularDiff 目录。

## 已接入的功能

- 文件和目录比较，Myers / Patience 算法选择，中文及空格路径。
- 左右对齐差异行、差异块导航、选中差异块双向复制。
- 独立的左右编辑页签；每侧保存、撤销、重做。保留编码，保存时检查外部修改冲突，关闭或更换比较前处理未保存修改。
- 目录左右列，双击打开文件；多选同步，未选时批量同步全部差异。
- 同步前预览、覆盖前备份、持久化记录、历史恢复预览和冲突检查。
- 紧凑工具栏，操作说明通过悬停提示显示。核心操作依然委托给插件。
- CLI 复用原项目，随发布目录一同生成。

## 构建和运行

需要 .NET 10 SDK；首次构建需要联网下载 NuGet 包。自包含发布后，使用者无需安装 .NET。动态插件需要保留独立 DLL，因此关闭裁剪和单文件发布。

Windows PowerShell：

```powershell
cd cross-platform
./build.ps1 -Runtime win-x64
./artifacts/win-x64/app/ModularDiff.Desktop.exe
```

也可用 `-Dotnet <SDK可执行文件路径>` 指定 SDK。省略 Runtime 生成需要 .NET 10 运行时的开发版。

macOS / Linux：

```bash
cd cross-platform
bash build.sh osx-arm64     # Apple Silicon
# bash build.sh osx-x64     # Intel Mac
# bash build.sh linux-x64
open artifacts/osx-arm64/ModularDiff.app
```

macOS 脚本生成 `ModularDiff.app/Contents/MacOS` 和 `Contents/plugins`，附带 Info.plist。该包尚未签名、公证；正式分发应在 Mac 上完成 Developer ID 签名、公证和实机测试。不要使用 Windows EXE 在 Mac 上运行。

参数示例：

```text
ModularDiff.Desktop --left <文件或目录> --right <文件或目录> --auto-compare
ModularDiff.Desktop --plugins <插件目录>
ModularDiff.Cli compare <左路径> <右路径> --algorithm patience
```

默认插件位置是宿主目录上一级的 `plugins`。备份记录存储在 .NET `LocalApplicationData/ModularDiff/SyncHistory`，路径随操作系统变化。移动程序时应保留 app、cli、plugins 三个目录。

## 项目结构

- `ModularDiff.Desktop/Program.cs`：跨平台启动和主题。
- `ModularDiff.Desktop/MainWindow.cs`：Avalonia 界面与文件选择、交互流程。
- `ModularDiff.Desktop/ComparisonSession.cs`：与 UI 无关的编辑会话，调用现有插件。
- `ModularDiff.Desktop.Tests`：Headless 界面构建/比较/渲染及编辑、保存冲突、同步恢复测试。
- `../shared/ModularDiff.Core`、`../shared/ModularDiff.Contracts`、`../shared/plugins`：共享实现，不依赖 WPF。

## 当前范围

这是可运行的跨平台版本，还没有与 WPF 版完全等价：差异对齐与编辑分为两个页签；目录是左右平铺相对路径列表，没有折叠目录树、搜索筛选、字符级着色、同步预览逐项改方向、拖放、完整快捷键及平台右键菜单。Windows 注册表设置没有引入跨平台项目。

macOS 和 Linux 的系统文件选择器、文件权限、符号链接、大小写敏感/不敏感卷、字体和系统菜单可能因系统版本而异；跨目标编译成功不代表已在所有平台实机测试。

Avalonia 参考：https://docs.avaloniaui.net/docs/get-started/
