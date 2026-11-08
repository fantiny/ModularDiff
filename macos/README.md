# ModularDiff · macOS 集成

本目录包含 macOS 平台的 Finder 集成与安装辅助,配合 `ModularDiff.CrossPlatform.sln` 构建出的桌面版使用。

## 目录结构

| 路径 | 说明 |
|---|---|
| `FinderSync/` | Finder 一级菜单扩展源码(Swift)。提供「选择左侧文件夹进行比较 / 与左侧比较 / 比较所选两个项目」菜单项 |
| `quick-actions/` | Automator 快速操作(两步式比较),复制到 `~/Library/Services/` 即生效 |
| `../enable-macos-finder-menu.command` | 扩展签名/公证/注册一键脚本 |

## .app 打包布局

```
ModularDiff.app/Contents/
├── MacOS/            # dotnet publish 产物(自包含 osx-arm64)
├── PlugIns/          # FinderSync 扩展 .appex(需公证后才会被 pluginkit 收录)
└── Resources/
    ├── plugins/      # 8 个插件包(应用按 ../plugins、../PlugIns、../Resources/plugins 顺序探测)
    └── AppIcon.icns
```

注意:APFS 默认大小写不敏感,`Contents/plugins` 与 `Contents/PlugIns` 会被视为同一目录,
插件因此放在 `Resources/plugins`。

## 编译扩展

```zsh
cd macos/FinderSync
swiftc -O -suppress-warnings main.swift FinderSync.swift \
  -framework FinderSync -framework Cocoa -o ModularDiffFinderSync
# 然后组装 <App>/Contents/PlugIns/ModularDiffFinderSync.appex/Contents/{MacOS,Info.plist}
```

## 注册限制(重要)

macOS 26/27 的 pluginkit 仅收录**已公证(Notarized)** 的第三方 FinderSync 扩展;
ad-hoc、自签、Apple Development 签名均会被静默拒绝(2026-11 验证)。
未公证环境下请使用 `quick-actions/` 的快速操作方案,功能等价、入口在子菜单。

## 两步式比较状态

「设为左侧」与 FinderSync 菜单共享状态文件:
`~/Library/Application Support/ModularDiff/pending-left`(纯文本,存左侧绝对路径,比较成功后自动清除)。
