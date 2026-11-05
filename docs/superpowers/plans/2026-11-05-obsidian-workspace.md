# Obsidian 式词库工作区实现计划

> 使用 superpowers:subagent-driven-development 分工实现并统一审查验证。用户明确要求直接执行。

**Goal:** 将 Windows 词库改为 Obsidian 对应的文件、标签、编辑与关联阅读工作区。
**Architecture:** Core 新增安全文件操作和 Markdown 关联索引；NoteWorkspace 负责多标签阅读／编辑；LearningPage 负责树、搜索与侧栏并继续管理漫游。MainWindow 对词库采用完整工作区布局。
**Tech Stack:** C# / .NET 10 / WPF / 现有 Markdig，无新增编辑器依赖。
**Spec:** docs/superpowers/specs/2026-11-05-obsidian-workspace-design.md

## Global Constraints

- 继续使用真实 Markdown、保留手写内容与现有同步协议。
- 删除可恢复；修改前备份；外部修改与本地编辑冲突必须保留两者。
- 所有写入必须验证词库范围、拒绝路径链接和同名覆盖。
- 词库链接改为单击直接导航、Ctrl+单击新标签、Ctrl+悬停预览；其他旧独立阅读器测试可继续用默认旧模式。

## Review Focus

- 外部编辑发生在自动保存之间：旧版本不能覆盖新内容。
- 有未保存草稿时切页／关闭标签／退出：保存失败不能丢草稿。
- 文件树刷新不能重置展开与选择，不能打断编辑／悬停预览。
- 重命名、移动、回收再恢复后保持笔记身份与学习进度。
- 小窗口侧栏折叠、键盘查找、标签溢出仍可操作。

## 公共接口

Core `LibraryCatalog` 增加：

- `Task<LibraryNote> CreateNoteAsync(string relativePath, string markdown, CancellationToken=default)`。
- `Task CreateFolderAsync(string relativePath, CancellationToken=default)`。
- `Task<LibraryNote> SaveNoteAsync(LibraryNote expected, string markdown, CancellationToken=default)`；期望内容或身份冲突抛 `LibraryConflictException : IOException`，不覆盖外部文件。
- `Task<LibraryNote> MoveNoteAsync(LibraryNote expected, string relativePath, CancellationToken=default)`。
- `Task<LibraryTrashItem> TrashNoteAsync(LibraryNote expected, CancellationToken=default)`；`Task<IReadOnlyList<LibraryTrashItem>> GetTrashAsync(CancellationToken=default)`；`Task<LibraryNote> RestoreNoteAsync(LibraryTrashItem item,CancellationToken=default)`。
- `LibraryTrashItem(string Id,string RelativePath,DateTimeOffset DeletedAt)`；所有磁盘位置由 Catalog 内部计算，不接受任意绝对恢复路径。
- `LibraryGraph(LibraryCatalog,IReadOnlyList<LibraryNote>)`，`Outgoing(LibraryNote)`、`Backlinks(LibraryNote)` 返回 `IReadOnlyList<LibraryNote>`，去重且路径语义与 Catalog.Resolve 一致。
- `LibraryHeading(int Level,string Text,int Index)`；`LibraryGraph.Headings(string markdown)` 返回标题列表（Index 为标题顺序索引，用于阅读器定位）。

Desktop `NoteWorkspace : UserControl, IDisposable`：

- `Configure(LibraryCatalog,IReadOnlyList<LibraryNote>)`、`RefreshNotes(notes)`、`OpenNote(LibraryNote,bool newTab=false,double? offset=null)`、`HandleLink(PreviewLinkEventArgs,LibraryNote? source,FrameworkElement? target=null)`。
- `NoteReader ActiveReader`、`LibraryNote? Note`、`bool HasUnsavedChanges`。
- `NewTab()`、`CloseActiveTabAsync()`、`ReopenClosedTab()`、`NextTab(int delta)`、`GoBack()`、`GoForward()`、`Find()`、`ToggleEditing()`、`Task<bool> SaveAllAsync()`。
- 事件 `NoteChanged` (LibraryNote)、`NavigationRequested`、`MessageChanged` (string)、`WebLinkRequested` (string)、`FilesChanged`；后者触发宿主 Reload。
- `NoteReader` 保持旧默认模式，新增直接链接模式、Ctrl 点击新标签事件、Ctrl 悬停简卡；增加前进与标题定位接口 `NavigateHeading(int index)`。
- 所有新控件使用资源 NotebookBackground/Surface/Raised/Text/Muted/Border/Accent/Hover/Selection；Root 提供工作区资源，也映射原渲染器 Ink/Accent 等颜色。

## Tasks

- [x] Core：LibraryCatalog.Operations.cs 与 LibraryGraph.cs，先写冲突、备份、创建、防覆盖、越界、移动身份、回收恢复与图关系的红灯测试，再实现并筛选验证。
- [x] NoteWorkspace／NoteReader／MarkdownPreview：多标签与每标签位置、源码编辑与安全自动保存、前后导航／标题跳转、直接链接和悬停预览；先红绿验证关键状态，不改 LearningPage/MainWindow。
- [x] Root：NotebookTheme、LibraryWorkspace 词库外壳／树／搜索／右栏／快捷对话框；LearningPage 接入；MainWindow 完整词库布局与退出保存。保留漫游算法和同步。
- [x] 真实 WPF 验证：更新旧简卡预期、新标签／编辑冲突／树操作／右栏／快捷键／动效终态与最小窗口。查看实际截图、独立审查、完整验证、打包并同步分支。

## 执行结果

726 项单元测试通过，完整 WPF 工作区／文章冒烟、同步往返及自包含发布通过。独立审查问题均已修复；最终截图已查看。详见 docs/verification/2026-11-05-obsidian-workspace.md。
