# Rider 开发与项目骨架

> 创建日期: 2026-06-08 09:52
> 最后更新: 2026-06-08 09:52
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
- `dotnet test .\AssetManager.sln --no-build`: 通过，1 个测试通过。

## 注意事项

- `AssetManager.Application` 这个命名空间会和 WPF 的 `Application` 类型同名；WPF `App.xaml.cs` 中应显式使用 `System.Windows.Application`。
- `AssetManager.Infrastructure.Windows` 目标框架必须是 `net10.0-windows`，否则后续无法干净承载 WPF/Windows 拖放、剪贴板和 Shell 相关类型。
- 当前项目只有骨架和模板默认代码；下一步实现应优先补 `Infrastructure.Windows` 的拖入、拖出和剪贴板 POC。

## 相关文档

- [素材管理工具产品需求实现规划](../../features/2026-06-04-素材管理工具产品需求规划/实现.md)
- [Windows 素材管理工具技术选型](../../features/2026-06-04-windows素材管理工具技术选型/设计-Windows素材管理工具技术选型.md)
- [插件系统与 UI 扩展选型调整](../../features/2026-06-04-插件系统与UI扩展选型调整/设计-插件系统与UI扩展选型调整.md)

---

## 修订记录

| 时间 | 作者 | 变更说明 |
|------|------|----------|
| 2026-06-08 09:52 | Adsicmes | 初始创建 |
