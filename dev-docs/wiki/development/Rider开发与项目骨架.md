# Rider 开发与项目骨架

> 创建日期: 2026-06-08 09:52
> 最后更新: 2026-06-08 10:03
> 作者: Adsicmes

## 概述

本文档记录当前工作区的 .NET 10 / WPF 项目骨架、Rider 打开方式、项目分层和基础验证命令。当前骨架由 CLI 创建，目标是先支撑 Windows 文件交互 POC，再逐步落地素材库和插件系统。

## 开发环境

- SDK: .NET SDK 10.0.300
- IDE: JetBrains Rider
- 主程序框架: WPF, `net10.0-windows`
- 解决方案文件: `AssetManager.sln`
- SDK 锁定文件: `global.json`

在 Rider 中打开项目时，直接打开工作区根目录下的 `AssetManager.sln`。

## 项目结构

```text
asset-manager/
├── AssetManager.sln
├── global.json
├── src/
│   ├── AssetManager.Desktop/
│   ├── AssetManager.Application/
│   ├── AssetManager.Domain/
│   ├── AssetManager.Infrastructure.Storage/
│   ├── AssetManager.Infrastructure.Windows/
│   ├── AssetManager.Plugin.Abstractions/
│   ├── AssetManager.Plugin.Sdk/
│   ├── AssetManager.Plugin.Host/
│   └── AssetManager.Plugin.Worker/
└── tests/
    └── AssetManager.Tests/
```

## 分层说明

| 项目 | 目标框架 | 责任 |
|------|----------|------|
| `src/AssetManager.Desktop` | `net10.0-windows` | WPF 主程序、窗口、MVVM、后续 BlazorWebView 承载 |
| `src/AssetManager.Application` | `net10.0` | 素材库用例、导入、搜索、预览和插件编排 |
| `src/AssetManager.Domain` | `net10.0` | 素材、标签、素材库配置和领域模型 |
| `src/AssetManager.Infrastructure.Storage` | `net10.0` | SQLite、FTS5、迁移、缩略图缓存和库内管理状态 |
| `src/AssetManager.Infrastructure.Windows` | `net10.0-windows` | DragDrop、Clipboard、IFileOperation 和 Shell 集成 |
| `src/AssetManager.Plugin.Abstractions` | `net10.0` | 插件公共契约 |
| `src/AssetManager.Plugin.Sdk` | `net10.0` | 插件作者使用的 SDK、默认基类和构建器 |
| `src/AssetManager.Plugin.Host` | `net10.0` | 插件加载、UI slot registry、能力校验 |
| `src/AssetManager.Plugin.Worker` | `net10.0` | 进程外插件 worker 原型 |
| `tests/AssetManager.Tests` | `net10.0` | 单元测试和集成测试入口 |

## 项目引用方向

```text
Desktop
  -> Application
  -> Domain
  -> Infrastructure.Storage
  -> Infrastructure.Windows
  -> Plugin.Host

Application
  -> Domain
  -> Plugin.Abstractions

Infrastructure.Storage
  -> Application
  -> Domain

Infrastructure.Windows
  -> Application
  -> Domain

Plugin.Host
  -> Application
  -> Plugin.Abstractions
  -> Plugin.Sdk

Plugin.Sdk
  -> Plugin.Abstractions

Plugin.Worker
  -> Plugin.Abstractions
```

## Rider 使用方式

1. 打开 Rider。
2. 选择 `Open`。
3. 打开 `D:\UserFiles\Development\Projects\asset-manager\AssetManager.sln`。
4. 等待 Rider 完成 NuGet restore 和项目索引。
5. 运行配置选择 `AssetManager.Desktop`。

调试拖放、剪贴板和 QQ 投递时，Rider 建议普通权限启动，不要用管理员权限启动。Windows 的权限隔离可能导致普通权限的资源管理器或 QQ 无法和管理员权限的应用正常拖放。

## 验证命令

```powershell
dotnet --version
dotnet restore .\AssetManager.sln
dotnet build .\AssetManager.sln --no-restore
dotnet test .\AssetManager.sln --no-build
```

当前验证结果：

- `dotnet build .\AssetManager.sln --no-restore`: 通过，0 警告，0 错误。
- `dotnet test .\AssetManager.sln --no-build`: 通过，2 个测试通过。

## Windows 文件交互 POC

当前 POC 代码覆盖了资源管理器文件路径接入和标准文件拖放数据输出：

| 文件 | 说明 |
|------|------|
| `src/AssetManager.Domain/AssetTransferItem.cs` | 表示 POC 中拖入的文件或文件夹，包含路径、显示名和文件/目录类型 |
| `src/AssetManager.Infrastructure.Windows/WindowsFileTransferService.cs` | 封装 `DataFormats.FileDrop` 读取、文件拖放 `DataObject` 构造和剪贴板写入 |
| `src/AssetManager.Desktop/MainWindow.xaml` | POC 窗口布局，提供文件列表和 Copy/Clear 操作 |
| `src/AssetManager.Desktop/MainWindow.xaml.cs` | 处理拖入、拖出、复制到剪贴板和列表状态 |
| `tests/AssetManager.Tests/UnitTest1.cs` | 覆盖 `AssetTransferItem` 对文件和目录的基础识别 |

当前支持：

- 从资源管理器拖入文件或文件夹。
- 从 `DataObject` 读取 `DataFormats.FileDrop` 文件路径。
- 在窗口列表展示拖入项。
- 将选中项构造成标准文件拖放 `DataObject` 并拖出。
- 将选中项或全部项写入 Windows 文件剪贴板。

仍需桌面手动验证：

- 从列表拖出到资源管理器后，资源管理器能复制文件。
- 从列表拖出到 QQ 后，QQ 能接收文件。
- 点击 Copy 后，在资源管理器中粘贴能得到文件。

## 注意事项

- `AssetManager.Application` 这个命名空间会和 WPF 的 `Application` 类型同名；WPF `App.xaml.cs` 中应显式使用 `System.Windows.Application`。
- `AssetManager.Infrastructure.Windows` 目标框架必须是 `net10.0-windows`，否则后续无法干净承载 WPF/Windows 拖放、剪贴板和 Shell 相关类型。
- 当前 POC 只传递真实文件路径。后续虚拟素材或插件生成内容需要先物化到临时文件，再复用同一文件拖放通路。

## 相关文档

- [素材管理工具产品需求实现规划](../../features/2026-06-04-素材管理工具产品需求规划/实现.md)
- [Windows 素材管理工具技术选型](../../features/2026-06-04-windows素材管理工具技术选型/设计-Windows素材管理工具技术选型.md)
- [插件系统与 UI 扩展选型调整](../../features/2026-06-04-插件系统与UI扩展选型调整/设计-插件系统与UI扩展选型调整.md)

---

## 修订记录

| 时间 | 作者 | 变更说明 |
|------|------|----------|
| 2026-06-08 09:52 | Adsicmes | 初始创建 |
| 2026-06-08 10:03 | Adsicmes | 补充 Windows 文件交互 POC 当前实现和手动验证清单 |
