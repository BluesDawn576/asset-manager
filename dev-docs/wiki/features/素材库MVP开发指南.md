# 素材库 MVP 开发指南

> 创建日期: 2026-06-08 15:01
> 最后更新: 2026-06-08 17:57
> 作者: Adsicmes

## 概述

本文记录素材库 MVP 的代码结构、数据流和维护规则。该功能让 Windows 桌面端注册素材库位置、从已保存素材库列表切换，把导入素材复制到当前 UI 所在的库内文件夹，并把数据库、日志和临时管理文件集中放入素材库根目录下的 `.asset-manager`。

## 模块边界

Domain 层位于 `src/AssetManager.Domain/Library/`，只表达素材库业务概念，不依赖 WPF、SQLite 或文件复制实现：

- `LibraryLocation` 定义库根目录、`.asset-manager`、`asset-manager.db`、`logs`、`temp` 和 `thumbnails` 的固定路径。
- `LibraryRelativePath` 负责库内相对路径规范化，禁止根路径、`..` 穿越和指向 `.asset-manager`。
- `AssetTypeId` 表示素材类型标识，内置值包括 `image`、`video`、`audio`、`text` 和 `unknown`。它是字符串值对象，不再把类型系统封死成枚举。
- `AssetRecord` 表示素材记录，包含库内路径、源路径、类型标识、大小、时间、哈希、备注、状态和标签。

Application 层位于 `src/AssetManager.Application/Library/`，负责用例编排和端口定义：

- `LibraryApplicationService.OpenOrCreateAsync()` 创建或打开素材库，并初始化存储层。
- `LibraryApplicationService.ImportPathsAsync()` 把文件或文件夹复制到当前 UI 文件夹，然后写入数据库。
- `LibraryApplicationService.SearchAsync()` 按当前文件夹、搜索词和必选标签查询素材。
- `LibraryApplicationService.GetPreviewAsync()` 为 UI 返回预览所需的文件路径或文本内容。
- `LibraryApplicationService.SynchronizeAsync()` 扫描库内内容，处理重命名、移动、删除和手动新增文件。
- `KnownLibraryApplicationService.RegisterAndOpenAsync()` 把用户指定的位置注册为素材库，初始化该库，并把位置写入应用级注册表。
- `KnownLibraryApplicationService.OpenRegisteredAsync()` 只允许打开已经注册并持久化保存的素材库。
- `IAssetLibraryRepository`、`IAssetContentStore`、`IAssetActivityLog` 是 Infrastructure 层需要实现的端口。
- `IKnownLibraryStore` 是已知素材库注册表端口，用于持久化素材库位置和上次打开项。
- `IAssetTypeResolver` 是素材类型识别端口，`BuiltInAssetTypeResolver` 提供首批图片、视频、音频和文本识别规则。后续插件类型应扩展 resolver 链，而不是改 Domain。

Infrastructure.Storage 层位于 `src/AssetManager.Infrastructure.Storage/Library/`，实现 SQLite、文件系统和日志：

- `SqliteAssetLibraryRepository` 创建表、写入素材、维护标签和 FTS5 索引。
- `FileSystemAssetContentStore` 负责复制文件、复制文件夹、创建文本片段、计算 SHA-256、扫描库内文件和读取文本预览。它通过注入的 `IAssetTypeResolver` 给文件生成 `AssetTypeId`。
- `FileAssetActivityLog` 把操作日志写入 `.asset-manager/logs/activity.log`。
- `JsonKnownLibraryStore` 把已注册素材库列表写入 `%LOCALAPPDATA%\AssetManager\known-libraries.json`。

Desktop 层位于 `src/AssetManager.Desktop/`：

- `DesktopBootstrapper.cs` 是桌面端组合根，负责组装 `LibraryApplicationService`、SQLite/FileSystem 实现、内置类型 resolver 和预览 renderer。
- `MainWindow.xaml` 提供素材库注册、已保存素材库下拉切换、文件夹列表、导入、搜索、同步、素材列表和预览区；媒体预览区包含 Play/Pause/Stop 控制按钮。
- `MainWindow.xaml.cs` 把 WPF 事件连接到 `LibraryApplicationService`，但不再直接创建 SQLite、文件系统或已知素材库注册表实现。
- `Preview/IAssetPreviewRenderer.cs` 定义桌面端预览渲染扩展点。
- `Preview/BuiltInAssetPreviewRenderers.cs` 提供内置图片、媒体和文本 renderer 清单。
- `Preview/AssetPreviewPresenter.cs` 和 `Preview/PreviewSurface.cs` 把预览状态渲染到 WPF 控件，避免把类型 switch 留在 `MainWindow`。
- `TextInputDialog.xaml` 用于新建库内内容文件夹。
- `TextSnippetDialog.xaml` 用于新建文本片段。
- `Localization/LocalizationManager.cs` 管理 WPF 资源字典切换、当前 `CultureInfo` 和语言持久化。
- `Localization/Strings.zh-CN.xaml`、`Localization/Strings.en-US.xaml` 保存桌面端用户可见文案。
- `Localization/UiSettingsStore.cs` 把 UI 设置写入 `%LOCALAPPDATA%\AssetManager\ui-settings.json`，包含语言设置（`languageName`）和素材视图设置（`assetViewSettings`：`assetPreviewScale`、`isDetailsPanelExpanded`、`detailsPanelWidth`）。
- `AssetThumbnailLoadCoordinator.cs` 按顺序后台加载当前列表需要的图片缩略图，避免一次性打满磁盘和解码开销。

Windows 文件交互和缩略图缓存由 `src/AssetManager.Infrastructure.Windows/` 提供。`WindowsFileTransferService.cs` 负责素材列表拖出和剪贴板 FileDrop；`WindowsThumbnailCacheService.cs` 负责把图片缩略图缓存到 `.asset-manager/thumbnails/`，卡片列表优先显示缓存图而不是直接打开原图。

插件相关项目当前只落地了最小架构骨架，还没有动态加载、权限隔离或进程外执行：

- `src/AssetManager.Plugin.Abstractions/` 定义 `IAssetManagerPlugin`、`PluginManifest`、`PluginContribution`、`AssetTypeContribution`、`PreviewContribution` 和 `UiContribution`。
- `src/AssetManager.Plugin.Sdk/AssetManagerPluginBase.cs` 提供插件作者可继承的默认基类。
- `src/AssetManager.Plugin.Host/PluginRegistry.cs` 提供进程内插件注册和贡献聚合，拒绝重复插件 ID。

`AssetManager.Application` 不引用插件项目。后续完整插件 Host 应把插件贡献适配到 Application 的稳定端口，例如 `IAssetTypeResolver`，而不是让 Application 直接依赖插件加载机制。

## 界面国际化规则

当前桌面端支持 `zh-CN` 和 `en-US`。启动时 `App.OnStartup()` 会调用 `LocalizationManager.InitializeAsync()`，优先读取 `%LOCALAPPDATA%\AssetManager\ui-settings.json` 中保存的语言；如果没有保存值，则根据 `CultureInfo.CurrentUICulture` 判断，中文环境默认使用 `zh-CN`，其他环境默认使用 `en-US`。

`MainWindow.xaml`、`TextInputDialog.xaml` 和 `TextSnippetDialog.xaml` 使用 `{DynamicResource ...}` 绑定资源键。运行时切换语言时，`LocalizationManager.SetCultureAsync()` 会替换 `Application.Resources.MergedDictionaries` 中的 `Localization/Strings.*.xaml`，并保存新的 culture name。列表项、素材类型、状态、当前文件夹和素材库路径等运行时生成文本，需要在切换语言后刷新对应集合或重新计算绑定值。

新增用户可见文案时，必须同时在 `Strings.zh-CN.xaml` 和 `Strings.en-US.xaml` 增加同名 key。新增语言时，需要：

- 在 `LocalizationManager.SupportedLanguages` 中增加 `LanguageOption`。
- 新增 `Localization/Strings.<culture>.xaml`。
- 确保 `tests/AssetManager.Tests/UnitTest1.cs` 的资源键一致性和资源引用测试覆盖新字典。
- 检查所有代码侧状态提示、对话框标题和格式化消息都通过 `LocalizationManager.Get()` 或 `LocalizationManager.Format()` 取值。

## 素材库注册与切换规则

软件不能把任意选择的文件夹直接当作当前素材库打开。用户必须先通过 `Register Library` 指定一个位置；注册动作会初始化该位置下的 `.asset-manager`，并把素材库位置持久化保存到 `%LOCALAPPDATA%\AssetManager\known-libraries.json`。

后续切换素材库时，UI 只能从 `KnownLibraryBox` 下拉列表中选择已注册素材库。`KnownLibraryApplicationService.OpenRegisteredAsync()` 会拒绝未注册的 `Guid`，也会拒绝路径、`.asset-manager` 或 `asset-manager.db` 缺失的已注册素材库。

已知素材库注册表是应用级配置，不属于任何单个素材库，因此不能写进素材库根目录或 `.asset-manager`。这样多个素材库之间不会互相保存对方的位置，也方便以后扩展最近打开、收藏库、库分组等功能。

## 导入位置规则

素材库根目录只自动生成 `.asset-manager`。不会自动创建 `assets` 目录。

导入目标由 `MainWindow` 左侧文件夹列表当前选中的 `LibraryFolder.RelativePath` 决定。用户选择 `Library root` 时，文件复制到库根目录；用户选择 `images` 时，文件复制到 `<库根目录>/images/`。用户主动新建的文件夹和导入文件夹本身是素材内容，不属于管理文件污染。

导入文件夹时，`FileSystemAssetContentStore.CopyDirectoryAsync()` 会把源文件夹作为一个目录复制到当前 UI 文件夹下，并保留源文件夹内部结构。同名目录或文件冲突时使用 `name (1).ext` 或 `folder (1)` 的规则自动改名。

## 数据库结构

SQLite 数据库路径固定为 `.asset-manager/asset-manager.db`。当前版本由 `schema_migrations` 和 `library_settings` 记录。

核心表：

- `assets`：素材主表，记录 `id`、`display_name`、`library_relative_path`、`source_path`、`kind`、`extension`、`size_bytes`、`created_at`、`modified_at`、`imported_at`、`content_hash`、`notes`、`status`。
- `tags`：标签表，使用 `normalized_name` 做大小写无关唯一约束。
- `asset_tags`：素材与标签的多对多关系。
- `asset_search`：SQLite FTS5 虚拟表，覆盖文件名、库内路径、扩展名、类型、标签和备注。

`SqliteAssetLibraryRepository.OpenConnectionAsync()` 使用 `Pooling = false`。原因是 Windows 下测试和库目录操作需要确保连接释放后 `asset-manager.db` 不继续被连接池占用。

## 同步规则

`LibraryApplicationService.SynchronizeAsync()` 会扫描库根目录下除 `.asset-manager` 外的所有文件：

1. 如果数据库记录的 `library_relative_path` 仍存在，则刷新大小、时间、哈希和状态。
2. 如果原路径不存在，但扫描到相同 SHA-256 的文件，则认为素材被移动或重命名，并更新库内路径。
3. 如果找不到相同路径或相同哈希，则标记为 `Missing`。
4. 如果扫描到数据库中不存在的库内文件，则作为新素材补录。

这个策略不在素材旁写 sidecar 文件，因此不会污染库目录。代价是同步时需要读取文件计算 SHA-256。

## 预览规则

`BuiltInAssetTypeResolver` 按扩展名判断首批类型：

- 图片：`.bmp`、`.gif`、`.jpeg`、`.jpg`、`.png`、`.tif`、`.tiff`、`.webp`
- 视频：`.avi`、`.m4v`、`.mkv`、`.mov`、`.mp4`、`.mpeg`、`.mpg`、`.webm`、`.wmv`
- 音频：`.aac`、`.flac`、`.m4a`、`.mp3`、`.ogg`、`.wav`、`.wma`
- 文本：`.csv`、`.json`、`.log`、`.md`、`.rtf`、`.text`、`.txt`、`.xml`、`.yaml`、`.yml`

数据库 `assets.kind` 字段当前保存的是 `AssetTypeId.Value`。字段名仍叫 `kind` 是为了保持现有 schema，语义上应按“素材类型标识”理解。新增类型时不要新增数据库列，也不要恢复固定 enum；应让 resolver 返回新的 type id，例如 `font`、`archive`、`model-3d`。

WPF UI 使用 `Preview/IAssetPreviewRenderer.cs` 管线渲染预览。内置 renderer 目前包括：

- `ImageAssetPreviewRenderer`：使用 `Image` 预览 `image`。动画 GIF 通过 `AnimatedImageDetector.IsAnimatedGif()` 检测（仅支持 GIF，依据帧描述符计数而非版本头），命中后交给 XamlAnimatedGif 驱动帧合成。
- `MediaAssetPreviewRenderer`：使用 `MediaElement` 预览 `video` 和 `audio`。
- `TextAssetPreviewRenderer`：使用只读 `TextBox` 显示 `text` 的预览内容。

插件系统接入后，扩展预览能力应优先通过“类型 resolver + preview renderer”组合完成。插件可以贡献新的 type id 和对应 renderer；插件不应该直接改 `SqliteAssetLibraryRepository`，也不应该让 Domain 知道某个插件文件类型。

## 缩略图缓存规则

图片卡片缩略图是后台生成的显示缓存，不是素材源文件的一部分。当前规则如下：

- 缩略图只缓存到 `.asset-manager/thumbnails/`，不写回素材目录。
- 当前仅对 `image` 类型生成磁盘缓存；视频、音频和文本仍使用占位卡片。
- 缩略图文件名按素材 `content_hash` 组织，重复内容可复用同一缓存图。
- `MainWindow` 刷新素材列表后，会通过 `AssetThumbnailLoadCoordinator` 顺序请求缓存，避免一次性并发读取大量原图。
- 如果图片解码失败或格式当前无法生成缩略图，卡片继续显示类型占位，不影响素材记录和预览能力。

## 验证命令

完整构建：

```powershell
dotnet build .\AssetManager.sln
```

完整测试：

```powershell
dotnet test .\AssetManager.sln
```

当前测试文件 `tests/AssetManager.Tests/UnitTest1.cs` 覆盖以下关键点：

- 创建素材库后根目录只自动生成 `.asset-manager`。
- 导入文件会复制到当前 UI 文件夹，不创建 `assets`。
- 导入库内文件夹到自身或子文件夹时会被拒绝，避免递归复制污染素材库。
- 同名文件导入会自动改名。
- 文件名、标签、备注和必选标签搜索可命中。
- `AND` 等 FTS 操作符文本按普通搜索词处理，不会造成查询语法错误。
- 图片、视频、音频和文本片段可返回正确预览类型。
- 文件重命名后可通过 SHA-256 重新关联。
- 文件删除后会标记为 `Missing`。
- 已注册素材库位置会持久化保存，重新创建 `JsonKnownLibraryStore` 后仍可读取。
- 重复注册同一素材库路径不会产生重复记录。
- 打开已注册素材库会更新 active library，便于下次启动恢复。
- 已注册但路径失效的素材库会显示为不可用，并拒绝打开。
- `zh-CN` 和 `en-US` 资源字典必须保持完全相同的 key 集合，XAML/C# 引用的资源 key 必须存在。
- Domain 不能引用框架、外层项目或项目引用；Application 不能引用 Infrastructure、Desktop 或 Plugin 项目。
- 插件注册表可以聚合素材类型、预览和 UI 贡献，并拒绝重复插件 ID。

## 维护注意事项

- 不要在 Domain 或 Application 层引用 `Microsoft.Data.Sqlite`、WPF、Win32 或剪贴板 API。
- 不要在 Domain 中加入文件系统探测或展示文案。文件是否存在、拖拽剪贴板、路径归一化等外部行为应留在 Infrastructure 或 Desktop。
- 新增管理文件必须放进 `.asset-manager`，不能写在库根目录或素材旁。
- 已知素材库列表必须保存在应用级配置中，当前默认路径是 `%LOCALAPPDATA%\AssetManager\known-libraries.json`，不要写进任何素材库目录。
- UI 语言设置必须保存在应用级配置中，当前默认路径是 `%LOCALAPPDATA%\AssetManager\ui-settings.json`，不要写进素材库目录或 `.asset-manager`。`ui-settings.json` 同时保存素材视图设置（预览缩放比、详情面板展开状态和面板宽度），启动时由 `MainWindow` 加载，Ctrl+滚轮缩放、详情面板展开/收起和详情 Splitter 拖动结束时触发保存。
- 修改素材库注册或切换规则时，同步更新 `KnownLibraryRegistry_*` 测试。
- 修改导入位置规则时，同步更新 `ImportPaths_CopiesIntoCurrentFolderWithoutAssetsDirectory` 测试。
- 修改国际化资源或新增语言时，同步更新 `LocalizationResourceDictionaries_*` 测试。
- 修改 SQLite 表结构时，同步更新 `SqliteAssetLibraryRepository.InitializeAsync()` 和迁移记录。
- 新增素材类型时，优先扩展 `IAssetTypeResolver` 或后续插件 resolver，不要恢复 `AssetKind` enum 或在 Domain 写扩展名表。
- 新增预览能力时，优先增加 `IAssetPreviewRenderer` 实现并由 `DesktopBootstrapper` 或插件 Host 注入 renderer 列表，不要把新的 switch 分支写回 `MainWindow`。
- 修改项目引用关系时，同步运行架构边界测试，确认 Domain/Application 依赖方向没有倒灌。
- 修改拖出或剪贴板行为时，优先检查 `WindowsFileTransferService.CreateFileDropDataObject()` 和 `WindowsFileTransferService.CopyToClipboard()`。
- 修改缩略图卡片加载行为时，优先检查 `WindowsThumbnailCacheService`、`AssetThumbnailLoadCoordinator` 和 `AssetRow.SetThumbnailPath()`，避免重新把列表绑定回原图路径。

## 相关文档

- [素材库 MVP 实现规划](../../features/archive/2026-06-08-素材库MVP实现规划/实现.md)
- [素材管理工具产品需求规划](../../features/2026-06-04-素材管理工具产品需求规划/需求.md)
- [Rider 开发与项目骨架](../development/Rider开发与项目骨架.md)

---

## 修订记录

| 时间 | 作者 | 变更说明 |
|------|------|----------|
| 2026-06-08 15:01 | Adsicmes | 初始创建 |
| 2026-06-08 15:07 | Adsicmes | 补充导入到自身目录的递归保护验证说明 |
| 2026-06-08 15:09 | Adsicmes | 补充 FTS 操作符文本搜索验证说明 |
| 2026-06-08 15:26 | Adsicmes | 补充素材库注册表、持久化位置和下拉切换规则 |
| 2026-06-08 15:42 | Adsicmes | 补充 zh-CN/en-US 国际化接入、语言持久化和资源维护规则 |
| 2026-06-08 15:44 | Adsicmes | 补充国际化资源引用测试说明 |
| 2026-06-08 16:31 | Adsicmes | 补充架构修复后的 AssetTypeId、类型 resolver、预览 renderer 和边界测试规则 |
| 2026-06-08 16:31 | Adsicmes | 补充插件最小契约、SDK 基类和注册表说明 |
| 2026-06-08 17:57 | Adsicmes | 补充图片缩略图缓存目录、后台加载队列和卡片显示规则 |
| 2026-06-11 | Adsicmes | 补充 AssetViewSettings 持久化字段说明和 ui-settings.json 保存时机 |
| 2026-06-14 | Adsicmes | 媒体预览区新增 Play/Pause/Stop 控制按钮；AnimatedImageDetector 收窄为仅 GIF 检测（IsAnimatedGif），移除 APNG/WebP 分支 |
