# Windows 应用内学习实现计划

**Goal:** 在现有 Windows 桌面应用内完成收录、目录/搜索、Markdown 阅读、关联词条简卡跳转、权重漫游和来源文章阅读。
**Architecture:** WPF 界面复用 Markdig 预览；Core 新增安全词库目录与设备本地学习状态；沿用手机分支的同步组件与 v1 协议。
**Spec:** `docs/superpowers/specs/2026-11-05-windows-learning.md`
**Baseline:** `05d0e649068916630ceab74fa230f79f76fccb0b`。工作分支 `feature/windows-learning`，不修改手机分支。

## 文件与任务

- [x] Core 本地词库与学习服务：`src/VocabCollector.Core/Library/`，`tests/VocabCollector.Tests/Library*Tests.cs`、`Roaming*Tests.cs`。
  - 安全扫描全部 Markdown，解析标题、音标、释义、摘录、入库时间、稳定 ID；解析内部链接与同名候选，安全导入；元数据失败必须提示且不破坏笔记。
  - 逐项验证手机权重，曝光防重和原子复习历史；先写并运行失败用例，再实现。
- [x] 阅读器和简卡：`src/VocabCollector.Desktop/Learning/NoteReader*`、`Preview/MarkdownPreview.cs`、阅读器测试。
  - 保留文本选择；单击/双击互斥；候选与缺失对话框；阅读历史返回栈及滚动恢复；更新内容时保留合理位置。
- [x] 应用内网页阅读：`src/VocabCollector.Desktop/Reading/ArticleWindow*`，WebView2 依赖与锁文件。
  - HTTP(S) 导航、加载/失败/重试、返回前进、独立用户数据目录；应用窗口关闭后释放资源；测试非法 scheme 不进入浏览器。
- [x] Windows 集成：App、MainWindow、MainViewModel、SettingsWindow、新 `LearningPage`。
  - 默认本地词库、既有路径兼容、目录/搜索/导入、词条和漫游页、收录后刷新、重复词读取、内部链接路由、同步刷新、状态和最小窗口布局。
- [x] 验证/交付：扩充 WPF 学习冒烟，运行完整验证与同步 round-trip；查看截图和真实控件；更新 README、验收文档；打包交付。

## 公共接口约定

Core 命名空间 `VocabCollector.Core.Library`：

- `LibraryNote(string Id, string RelativePath, string AbsolutePath, string Markdown, DateTimeOffset AddedAt)`，派生 `Title/Folder/IsWord/Phonetics/Meaning/Excerpt`。
- `LibrarySnapshot(IReadOnlyList<LibraryNote> Notes, IReadOnlyList<string> Warnings)`。
- `LibraryCatalog(VaultOptions options)`；`Task<LibrarySnapshot> ScanAsync(CancellationToken = default)`；`IReadOnlyList<LibraryNote> Resolve(string target, LibraryNote? source, IReadOnlyList<LibraryNote> notes)`；`Task<IReadOnlyList<LibraryNote>> ImportAsync(IReadOnlyList<string> files, CancellationToken = default)`。
- `ReviewRecord` 提供本地当日次数与 `Weight(added, now, timeZone)`。
- `RoamingExposure(Guid Id, LibraryNote Note, bool RememberedToday, double Weight)`。
- `RoamingSession(string stateFile, TimeProvider? clock = null, Func<double>? random = null)`；`Current`；`Task<RoamingExposure?> NextAsync(IReadOnlyList<LibraryNote> notes, Guid? finishingExposure = null, bool remembered = false, CancellationToken = default)`。无 finishing 时已有当前卡保持不变；同一旧 exposure 不重复完成。服务保存成功才推进状态。

阅读器 `VocabCollector.Desktop.Learning.NoteReader : UserControl`：

- `Configure(LibraryCatalog, IReadOnlyList<LibraryNote>)`、`OpenNote(LibraryNote, bool remember = true, double? initialOffset = null)`、`RefreshNotes(IReadOnlyList<LibraryNote>)`、`GoBack()`、`CanGoBack`、`Note`。
- `HandleLink(PreviewLinkEventArgs args, LibraryNote? source, FrameworkElement? placementTarget = null)`，供捕获页与阅读页共用。
- `NavigationRequested`（显式打开或返回，包括同 ID）、`NoteChanged`（LibraryNote）、`MessageChanged`（string）、`WebLinkRequested`（string）事件。
- `PreviewLinkEventArgs` 保留原构造并增加默认 false 的 `IsDoubleClick`。现有程序化 Hyperlink 点击仍可触发单击；真实鼠标单击延迟到双击判定后，双击直接打开一次。

网页阅读 `VocabCollector.Desktop.Reading.ArticleWindow(string url)`，静态 `IsAllowed(string)` 验证 URL。主窗口可继续注入原测试回调，不对测试机发真实网络请求。

## 执行记录

- 已 fetch 手机最新实现并与需求比对；使用托管独立工作树，避免改变原项目 checkout。
- 学习进度继续仅本机保存，这是手机现有范围；本次不扩展云端协议。
- 仅改变用户提出的 Windows 学习流程，iOS/server 文件保持原样。

- 最终验证：657 项单元测试通过，Release 0 警告／0 错误，真实 WPF＋本机 WebView2 冒烟、同步 round-trip 和自包含发布全部通过。详情见 docs/verification/2026-11-05-windows-learning.md。
