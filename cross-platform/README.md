# ModularDiff 跨平台版

这个子目录提供独立的 Avalonia 11.3.22 桌面宿主，目标为 .NET 10。原 Windows WPF GUI 不变。核心、契约、CLI 和插件通过项目引用及运行时加载复用父目录代码，因此复制源码时需要保留整个 ModularDiff 目录。

## 已接入的功能

- 文件和目录比较，Myers / Patience 算法选择，中文及空格路径。
- 左右对齐差异行、差异块导航、选中差异块双向复制；在两侧的行文本里查找（⌘F 打开查找条，F3 / ⇧F3 下一处 / 上一处，可区分大小写，匹配处高亮）。
- 独立的左右编辑页签；每侧保存、撤销、重做。保留编码，保存时检查外部修改冲突，关闭或更换比较前处理未保存修改。
- 目录比较视图：扫描是渐进的（先列出根目录，随后按增量生长）；可按名称（子串 / 通配符 / 正则）和状态筛选；左右两侧各自选择，文件名不同的两个文件也能配对打开；路径栏回车即比较。
- 目录同步：执行前逐项预览，每个文件的复制方向可以改、可以跳过；覆盖前备份、持久化记录、历史恢复预览和冲突检查。
- 报告导出：文本 / JSON / HTML，格式全部由 `IResultFormatter` 插件提供。导出的是当前比较——目录页是整个目录比较（扫描结束后），差异 / 编辑页是当前这一对文件。报告原子写盘，并且不会写进被比较的文件或目录。
- 命名会话与最近比较，与 WPF 版共用同一存储。
- 手动对齐与隔离、不重要差异规则、按文件类型的右键工具、换行与格式化（都由插件提供）。
- 十六进制视图（左侧可就地编辑）与图片并排视图（差异像素叠层）。
- 三路合并窗口，以及供 git mergetool 使用的 `--git-merge` 退出码协议。
- 语法着色、明暗主题。
- 多语言：全部界面文字来自 `../windows/ModularDiff.Gui/languages/lang-*.json`，与 WPF 版共用同一批语言包；插件给出的名称和操作说明也在宿主处翻译。
- 紧凑工具栏，操作说明通过悬停提示显示。核心操作依然委托给插件。
- CLI 复用原项目，随发布目录一同生成。

界面测试覆盖以上各项（`ModularDiff.Desktop.Tests`，无头渲染，不访问用户的真实数据目录）：

```bash
dotnet run --project ModularDiff.Desktop.Tests -c Release -- <插件目录>
```

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
- `../shared/ModularDiff.Core/ComparisonSession.cs`：与 UI 无关的编辑会话，调用现有插件（WPF 版也用它）。
- `ModularDiff.Desktop.Tests`：Headless 界面测试——比较与渲染、就地编辑、保存冲突、目录视图、同步与恢复、报告导出、多语言。测试把持久化位置重定向到私有目录（`MODULARDIFF_DATA_DIR`），不读写用户的真实设置。
- `../shared/ModularDiff.Core`、`../shared/ModularDiff.Contracts`、`../shared/plugins`：共享实现，不依赖 WPF。

## 当前范围

这是可运行的跨平台版本，与 WPF 版的能力还有这些差别：

- 差异对齐与编辑分为两个页签；一次比较一个窗口（macOS 上由单实例多窗口代替 WPF 的会话标签页）。
- 目录是按相对路径排序的平铺列表，不是可折叠的目录树。
- 差异视图能查找，不能替换。
- 差异视图里没有查找 / 替换，也没有自动换行开关（过长的行在视图宽度处截断）。
- 没有把文件拖进窗口；快捷键目前只有 Enter（比较）、⌘G（转到行号）、Esc（取消对齐），以及 ⌘1–9 / ⇧⌘1–9 的书签。

Windows 资源管理器集成和注册表设置只在 WPF 版里；macOS 的右键菜单见仓库根目录的 `enable-macos-finder-menu.command`。

macOS 和 Linux 的系统文件选择器、文件权限、符号链接、大小写敏感/不敏感卷、字体和系统菜单可能因系统版本而异；跨目标编译成功不代表已在所有平台实机测试。

Avalonia 参考：https://docs.avaloniaui.net/docs/get-started/
