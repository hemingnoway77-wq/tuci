# 英语单词收集器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付一个 Windows exe，将粘贴的英语单词经用户配置的 AI 解析，保存为带有效历史词条链接的 Obsidian Markdown。

**Architecture:** WPF 负责输入与展示，独立 Core 类库负责解析调用、词库索引、Markdown 与保存流程。配置和 Windows 外部打开操作留在 Desktop；HTTP、存储路径、时间和配置目录可注入，从而使用临时仓库与模拟接口验收。

**Tech Stack:** C#、.NET 10、WPF、HttpClient、System.Text.Json、Windows DPAPI、xUnit。普通功能不依赖 OpenAI SDK、数据库或服务进程；DPAPI 包与测试包在首次建立项目时解析为兼容的稳定版本并提交锁文件。

**Spec:** [已确认设计规格](../specs/2026-10-21-english-vocab-tool-design.md)

## Global Constraints

- 目标平台：Windows x64 桌面，交付可双击运行的 `.exe`。
- 首版交付 Windows x64 的自包含单文件 exe、源码、构建脚本和中文使用说明。
- 单词长度限制为 64 个字符；原句最多 2,000 字符；不自动合并词形或纠正拼写。
- 内容目标是 1–3 个常见释义、2 个双语例句、2–4 个常见搭配。
- AI 产生最多 12 个候选；最终最多展示 8 个；同名文件有多个时跳过。
- 调用超时 90 秒；非流式；不做可能重复计费的自动重试；不强制 `response_format`。
- API Key 使用 Windows 当前用户的 DPAPI 加密；消息不包含 Key、仓库路径或历史笔记正文。
- 不覆盖或自动移动旧笔记；Markdown 使用 UTF-8、LF；只链接已存在且唯一的文件。
- 所有自动验收使用临时目录和假 Key，不访问用户真实仓库，不消耗真实 API 额度。

## Review Focus

1. 合法英文词 `con` 与 Windows 保留设备名冲突：可收录、可查重，不能创建设备路径。由 Task 1/2 的保留名映射测试固定。
2. 仓库或历史词条目录包含中文、空格、`#`、`[`、`]`、`|` 等字符：普通文字保留；输出子目录拒绝 Wikilink 保留字符，旧目录的危险关联跳过；仓库根目录在外部打开 URI 中正确编码。由 Task 2/5 测试固定。
3. 扫描后目标删除、变成目录链接，或两个进程同时保存同词：不生成悬空目标、不越界、不覆盖。由 Task 2/4 测试固定。
4. 服务商把 Key 回显在 401/429/500 响应正文中：用户界面及持久化错误记录不得泄露。由 Task 3 的脱敏测试固定。
5. 用户取消时服务端刚好返回，或写入目录刚好失去可写性：提交前观察到取消则不写正式文件，提交已成功则报告已保存；存储失败仍可复制完整结果，正式笔记不残缺。由 Task 2/4/5 测试固定。

## 文件与职责

```text
VocabCollector.slnx
Directory.Build.props                 # nullable、隐式 using、确定性编译与锁文件
.gitignore / .gitattributes             # 忽略构建产物；源码统一 LF
src/VocabCollector.Core/
  VocabCollector.Core.csproj
  Models.cs                            # 不可变配置与解析数据、词性和关系枚举
  CaptureContracts.cs                  # 流程接口、结果与进度
  WordInput.cs                         # 单词校验、身份和安全文件名映射
  Storage/VaultPaths.cs                 # 根目录边界、目录链接、词性目录映射
  Storage/VaultIndex.cs                 # 文件名索引、已有词条与关联解析
  Storage/VaultStore.cs                 # 扫描、跨进程保存锁与无覆盖原子提交
  Markdown/NoteRenderer.cs              # 固定模板与纯文本转义
  Ai/ChatEndpoint.cs                    # Base URL 校验与地址拼接
  Ai/AnalysisJson.cs                    # JSON 合约解析、限制与校验
  Ai/OpenAiCompatibleAnalyzer.cs        # HTTP 调用、取消和中文错误
  CaptureService.cs                    # 一次解析保存流程与部分结果保留
src/VocabCollector.Desktop/
  VocabCollector.Desktop.csproj
  App.xaml / App.xaml.cs               # 资源与依赖组合
  MainWindow.xaml / MainWindow.xaml.cs # 主窗口与对话框/打开动作
  SettingsWindow.xaml / .xaml.cs       # 设置草稿、校验与成功后关闭
  MainViewModel.cs / AsyncCommand.cs   # 可测的状态、绑定和命令
  WindowsSettingsStore.cs              # JSON 配置与 DPAPI Key
  WindowsNavigator.cs                  # Obsidian URI 与资源管理器
tests/VocabCollector.Tests/            # xUnit；引用 Core 与 Desktop
tests/VocabCollector.Desktop.Smoke/    # 独立 STA/WPF 验收程序，不增加产品测试入口
scripts/verify.ps1 / publish.ps1        # 验证与自包含打包
README.md
```

所有路径相对于项目根目录。测试临时数据和截图放 `.artifacts/`，发布文件放 `dist/win-x64/`，两者忽略提交；源码、测试、文档和锁文件提交。

## 共享合约

以下名称是任务之间的固定接口；实现过程中若确需改变，先同步所有消费者与测试。

- `ApiOptions(string BaseUrl, string ApiKey, string Model)`、`VaultOptions(string RootPath, string OutputSubfolder, bool ClassifyByPartOfSpeech)`、`AppSettings(ApiOptions Api, VaultOptions Vault)`：不可变 record。
- `CaptureRequest(string Word, string Context)`；`CaptureStage` 为 `Validating, CallingAi, Saving`。
- `NoteLocation(string Word, string AbsolutePath, string VaultRelativePath)`；相对路径保留 `.md`，渲染链接时去掉扩展名。`SavedNote(NoteLocation Note, string Markdown)` 返回实际提交内容。
- `PartOfSpeech` 对应规格的 11 种词性；`RelationKind` 为 `SameRoot, Synonym, Antonym, Confusable`。
- `Meaning(PartOfSpeech PartOfSpeech, string Definition)`、`BilingualText(string English, string Chinese)`、`RelatedCandidate(string Word, RelationKind Kind)`、`ResolvedRelation(RelationKind Kind, NoteLocation Note)`。
- `WordAnalysis(bool IsValidWord, string Error, string UkPhonetic, string UsPhonetic, PartOfSpeech PrimaryPartOfSpeech, IReadOnlyList<Meaning> Meanings, IReadOnlyList<BilingualText> Examples, IReadOnlyList<BilingualText> Collocations, IReadOnlyList<RelatedCandidate> Related)`。
- `CaptureStatus` 为 `Saved, AlreadyExists, InvalidInput, Failed, Cancelled`；`CaptureResult(CaptureStatus Status, string Word, string Markdown, IReadOnlyList<NoteLocation> Notes, string Message)`。
- `ICaptureService.CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct) -> Task<CaptureResult>`。
- `IWordAnalyzer.AnalyzeAsync(string word, string context, ApiOptions options, CancellationToken ct) -> Task<WordAnalysis>`。
- `IVocabularyStore.ScanAsync(VaultOptions options, CancellationToken ct) -> Task<VaultIndex>`；`SaveNewAsync(string word, PartOfSpeech primaryPos, Func<VaultIndex,string> render, VaultOptions options, CancellationToken ct) -> Task<SavedNote>`。最终审查后修订为锁内使用最新索引渲染，并在提交前复核关联，避免锁等待期间删除的目标留下悬空链接。
- `ISettingsStore.LoadAsync(CancellationToken ct) -> Task<SettingsLoadResult>`；`SaveAsync(AppSettings settings, CancellationToken ct) -> Task`。`SettingsLoadResult(AppSettings Settings, string Warning)` 在 Key 损坏时保留其他字段、Key 置空并返回中文说明。
- `IAppNavigator.OpenNote(NoteLocation note)`、`OpenFolder(NoteLocation note)`，仅 Desktop 实现；测试使用记录参数的替身。

### Task 1：可测试的工程与单词合约

**Files:** 创建解决方案、两个产品项目与 xUnit 项目、构建属性、忽略规则；创建 `Models.cs`、`CaptureContracts.cs`、`WordInput.cs`；测试 `WordInputTests.cs`。

**Interfaces:** 产出上述共享合约、`WordInput.Normalize(string raw) -> string`、`ToFileName(string normalized) -> string`、`FromFileStem(string stem) -> string`。校验失败使用 `ArgumentException` 的明确中文说明；下游不直接展示 .NET 原始异常。

- [x] 建立最小项目骨架：Core `net10.0`，Desktop WPF 与 Tests `net10.0-windows`；Tests 引用 Core/Desktop。启用 nullable 与 NuGet 锁文件，不编写业务实现。
- [x] 写 `WordInputTests`：`" Resilient " -> "resilient"`；`"can’t" -> "can't"`；连字符合法；空值、句子、`../word`、控制字符与 65 字符拒绝；`running` 与 `run`、`saw` 与 `see` 不同身份。
- [x] 写保留名回归：`ToFileName("con") == "_con.md"`，`FromFileStem("_con") == "con"`；`aux/prn/nul` 同样映射。普通文件名保持原词。只有明确保留名映射前缀，其余历史文件名不随意剥离 `_`。
- [x] `Normalize_PreservesInflections` 和 `FileName_ReservedNamesRoundTrip` 至少包含这些断言：

```csharp
Assert.Equal("resilient", WordInput.Normalize(" Resilient "));
Assert.NotEqual(WordInput.Normalize("saw"), WordInput.Normalize("see"));
Assert.Equal("_con.md", WordInput.ToFileName("con"));
Assert.Equal("con", WordInput.FromFileStem("_con"));
```

- [x] 运行 `dotnet test tests/VocabCollector.Tests --filter FullyQualifiedName~WordInputTests`，期望缺少类型/方法或行为断言失败，保留红灯证据。
- [x] 实现共享合约与输入方法，重跑相同命令，期望全部通过。
- [x] 提交：`feat: establish vocabulary contracts and safe word identities`。

### Task 2：词库、关联与固定 Markdown 安全保存

**Files:** 创建 `Storage/*.cs`、`Markdown/NoteRenderer.cs`；测试 `VaultPathsTests.cs`、`VaultIndexTests.cs`、`VaultStoreTests.cs`、`NoteRendererTests.cs`。

**Interfaces:** 消费 Task 1；产出 `VaultIndex(IReadOnlyList<NoteLocation> notes)`、`Find(string word) -> IReadOnlyList<NoteLocation>`、`ResolveRelated(string currentWord, IReadOnlyList<RelatedCandidate> candidates) -> IReadOnlyList<ResolvedRelation>`；`VaultStore : IVocabularyStore`；`NoteRenderer.Render(string word, string context, WordAnalysis analysis, IReadOnlyList<ResolvedRelation> relations, DateTimeOffset created) -> string`。

- [x] 先写路径测试：中文仓库可用；绝对子目录、驱动器相对路径 `C:words`、根相对路径 `\words`、`..`、兄弟目录前缀伪装、重解析目录越界均拒绝；根目录未存在或为文件给可读错误；词性目录映射覆盖全部 11 值。
- [x] 先写索引与关联测试：分类开关不影响跨目录查重；缺少输出目录视为空索引；同名歧义与自链接跳过；缺失候选不生成链接；最多 12 候选取最多 8 唯一目标；保留名单词按 Task 1 规则识别。
- [x] 先写渲染断言：固定标题、YAML 可解析、空音标写“未提供”、原句保留、空关联提示；`[[fake]]`、HTML、标题与围栏输入被当作文本；仓库相对路径用 `/`、无 `.md`。输出子目录的路径段除 Windows 非法字符外，还拒绝 `#^[]|`；历史目录含这些字符时保留查重但跳过链接，不猜测 Wikilink 编码。
- [x] 先写保存断言：开启/关闭分类路径正确；已存在内容逐字不变；两个独立 store 并发保存同词仅一个成功，包括主要词性不同的情况；取消发生在提交前不产生 `.md`；失败不留半份正式文件；链接目标在渲染前重新核验，删除或越界则排除。
- [x] `Find_DoesNotChooseBetweenSameNameNotes` 与 `ResolveRelated_DropsMissingTargets` 使用真实 NoteLocation 数据执行这些断言：

```csharp
Assert.Equal(2, index.Find("record").Count);
Assert.Empty(index.ResolveRelated("resilient", [new("missing", RelationKind.Synonym)]));
Assert.DoesNotContain("[[fake]]", renderedFromUntrustedText);
```

- [x] 运行 `dotnet test tests/VocabCollector.Tests --filter "FullyQualifiedName~Vault|FullyQualifiedName~NoteRenderer"`，期望缺少实现或断言失败。
- [x] 实现索引、渲染与保存。扫描不跟随目录重解析点；危险历史目录不造链接。每次保存获得词库目录内 `.vocabcollector.write.lock` 的独占文件句柄，在锁内重新检查重复词并创建同目录临时文件，最终 `File.Move(..., overwrite: false)`；锁等待支持取消，最多 3 秒并给中文忙碌提示。释放句柄，保留空锁文件，避免删除锁文件的竞争。
- [x] 定义 `DuplicateWordException(IReadOnlyList<NoteLocation> notes)` 供应用服务处理。写盘前、获得锁后、提交前都检查取消及目录边界；清理仅涉及本次已知临时文件。
- [x] 重跑本任务测试至通过，提交：`feat: persist linked Obsidian vocabulary notes without overwrites`。

### Task 3：可兼容、可取消的 AI 解析

**Files:** 创建 `Ai/*.cs`；测试 `ChatEndpointTests.cs`、`AnalysisJsonTests.cs`、`OpenAiCompatibleAnalyzerTests.cs` 与 `Fakes/RecordingHttpHandler.cs`。

**Interfaces:** 消费 Task 1；`OpenAiCompatibleAnalyzer(HttpClient client)` 实现 `IWordAnalyzer`；`ChatEndpoint.Resolve(string baseUrl) -> Uri`；`AnalysisJson.Parse(string json) -> WordAnalysis`。定义 `AnalysisException(string userMessage)`，消息只能来自代码中受控的中文文案。

- [x] 写地址测试：`https://host/prefix/v1/` 拼接一次 `chat/completions`，完整 endpoint 不重复追加；不猜测 `/v1`；拒绝 URL 内嵌用户名/密码、query、fragment 和非 loopback HTTP，接受本机 HTTP。
- [x] 写协议测试：Bearer 仅在头部；请求含用户配置 model、单词、原句和固定任务要求；`stream:false`；不发送目录、历史正文、强制结构化参数或额外模型参数；不自动重试。使用假 Key，确认 401/429/500 回显 Key 时错误中不出现 Key 或原始响应。
- [x] 写 JSON 正常及错误测试：普通 JSON、单层 `json` 围栏可用；无效单词返回解释；有效词要求 1–3 meanings、恰好 2 examples、2–4 collocations、0–12 related、合法枚举、主词性属于 meanings；拒绝缺失、类型错误、空内容、任意前后散文、未知关系或异常超长内容。音标为空允许。
- [x] 固定模型 JSON 字段：`is_valid_word,error,phonetics:{uk,us},primary_pos,meanings:[{part_of_speech,definition}],examples:[{english,chinese}],collocations:[{english,chinese}],related:[{word,relation}]`；枚举用小写规格词性和 `same_root/synonym/antonym/confusable`。服务响应读取 `choices[0].message.content`；非文本、截断或拒绝结果不可保存。
- [x] 写超时与取消测试：注入阻塞 handler；用户取消能立即结束；测试 client 将 Timeout 设为 50 毫秒以验证与主动取消不同的中文超时提示，生产 client 为 90 秒。响应最大 1 MiB，字段文本最多 2,000 字符。关闭自动重定向，错误不包含原始响应正文。
- [x] `Endpoint_AppendsOnlyTheCompletionSuffix` 与 `ApiError_DoesNotEchoKey` 包含这些断言；后者在 401、429、500 中分别运行：

```csharp
Assert.Equal("https://host/prefix/v1/chat/completions", ChatEndpoint.Resolve("https://host/prefix/v1/").AbsoluteUri);
Assert.DoesNotContain(canaryKey, error.Message);
Assert.Equal(1, handler.RequestCount);
```

- [x] 运行 `dotnet test tests/VocabCollector.Tests --filter "FullyQualifiedName~ChatEndpoint|FullyQualifiedName~AnalysisJson|FullyQualifiedName~OpenAiCompatible"`，期望缺少实现或断言失败。
- [x] 实现三个 AI 模块，重跑相同命令至全部通过，提交：`feat: analyze words through configurable OpenAI-compatible APIs`。

### Task 4：加密设置与完整解析保存流程

**Files:** 创建 `WindowsSettingsStore.cs`、`CaptureService.cs`；测试 `WindowsSettingsStoreTests.cs`、`CaptureServiceTests.cs`、`Fakes/FakeWordAnalyzer.cs`。

**Interfaces:** 消费 Task 1–3；`WindowsSettingsStore(string settingsDirectory)` 实现 `ISettingsStore`；`CaptureService(IWordAnalyzer analyzer, IVocabularyStore store, TimeProvider timeProvider)` 实现 `ICaptureService`。默认设置目录为 `%LOCALAPPDATA%/VocabCollector`，默认输出子目录 `英语单词`、分类开启；API 值默认空白。

- [x] 写设置测试：保存/重读一致；磁盘 JSON 无明文假 Key；密文损坏保留其他设置且给重填提示；文件损坏不崩溃、不悄悄覆盖；保存失败留旧有效配置。使用用户级 DPAPI 及独立临时目录。
- [x] 写完整流程测试：进度依次 Validating/CallingAi/Saving；输入无效或原句超过 2,000 字符不调用 AI；已有单词返回全部已有路径且 AI 调用次数为 0；无效词/模型错误不调用保存；成功包含真实路径、完整 Markdown 与正确时间。
- [x] 写竞态测试：AI 返回后出现同名笔记，返回 AlreadyExists 并保留旧文；用户取消后即使假 analyzer 返回也不提交；正式移动成功后到来的取消仍返回 Saved；写盘失败返回 Failed 但 Markdown 完整；生成时使用配置快照；关系解析使用调用结束后的新索引。
- [x] `Settings_DoesNotPersistPlaintextKey` 与 `StorageFailure_PreservesGeneratedMarkdown` 包含以下断言：

```csharp
Assert.DoesNotContain(canaryKey, await File.ReadAllTextAsync(settingsFile));
Assert.Equal(CaptureStatus.Failed, result.Status);
Assert.Contains("## 词性与释义", result.Markdown);
```

- [x] 运行 `dotnet test tests/VocabCollector.Tests --filter "FullyQualifiedName~WindowsSettingsStore|FullyQualifiedName~CaptureService"`，期望缺少实现或断言失败。
- [x] 实现两个服务。设置 JSON 用临时文件替换旧配置；加密失败不落盘。核心流程不阻塞 UI 线程，文件索引与同步目录检查安排在后台任务并传递取消。
- [x] 重跑本任务测试至通过，提交：`feat: orchestrate captures and persist encrypted user settings`。

### Task 5：简洁 WPF 窗口与可测状态

**Files:** 创建/修改 Desktop 的 App、两个窗口、`MainViewModel.cs`、`AsyncCommand.cs`、`WindowsNavigator.cs`；测试 `MainViewModelTests.cs`、`WindowsNavigatorTests.cs`。

**Interfaces:** `MainViewModel(ICaptureService service, AppSettings settings)`，公开绑定属性 `Word,Context,Markdown,Message,IsBusy,Notes,SelectedNote`、`GenerateCommand,CancelCommand`、`GenerateAsync() -> Task`、`ApplySettings(AppSettings settings)`；`MainWindow(MainViewModel viewModel,ISettingsStore settingsStore,IAppNavigator navigator)`；`SettingsWindow(AppSettings draft,ISettingsStore store)` 仅保存成功后设置 `SavedSettings` 并关闭；`WindowsNavigator.BuildOpenUri(NoteLocation note) -> Uri`。

- [x] 写 VM 测试：处理中禁用生成与设置、取消可用；成功/重复/失败/取消均恢复按钮；保留输入；开始新操作清除旧路径；存储失败仍展示 Markdown；服务异常转为通用中文提示且不泄露参数；IsBusy 时拒绝 ApplySettings。
- [x] 写 URI 测试：使用 `obsidian://open?path=` 编码完整文件路径，中文、空格、`#`、`&`、`?`、`%` 不串改参数；路径来自 NoteLocation 而不是拼接模型文本。打开文件夹使用 `ProcessStartInfo.ArgumentList`，不拼 shell 命令。生产在操作失败时显示可读提示。
- [x] `Generate_RestoresStateAndKeepsInputAfterFailure` 包含以下断言；另测已取消、重复、成功状态：

```csharp
Assert.False(vm.IsBusy);
Assert.Equal("resilient", vm.Word);
Assert.True(vm.GenerateCommand.CanExecute(null));
```

- [x] 运行 `dotnet test tests/VocabCollector.Tests --filter "FullyQualifiedName~MainViewModel|FullyQualifiedName~WindowsNavigator"`，期望缺少实现或断言失败。
- [x] 实现 VM/命令/导航，并完成以下两个窗口。
- [x] 主窗口采用系统字体、浅色背景、清晰的输入/结果两区；单词、可选原句、“解析并保存”、取消、设置、状态、只读 Markdown、已有路径选择与两个打开按钮。窗口允许缩放，长内容可滚动，键盘焦点顺序正常，不依赖外部字体或图片。
- [x] 设置窗口含规格所有字段；Key 用 PasswordBox；目录用 OpenFolderDialog；在保存时校验路径/API 值，保存失败保持窗口可编辑。启动加载配置，损坏时提示并允许修复。生成期间不能变更配置。
- [x] 运行本任务测试并 `dotnet build VocabCollector.slnx -c Release`，全部通过后提交：`feat: add the vocabulary capture desktop interface`。

### Task 6：真实窗口验收、打包与中文说明

**Files:** 创建 `tests/VocabCollector.Desktop.Smoke/`、`scripts/verify.ps1`、`scripts/publish.ps1`、`README.md`；修改 Desktop 发布属性。

**Interfaces:** Smoke 是独立测试 exe，STA 线程创建真实 WPF MainWindow/SettingsWindow，使用临时配置目录、临时词库、本机 loopback 模拟 HTTP 服务；不在产品增加特殊测试命令。脚本失败时返回非零退出码，成功打印输出目录。

- [x] 编写独立 smoke runner：通过真实窗口控件/绑定命令填写配置、保存、重载，输入单词与原句，调用本机模拟 Chat Completions 服务，验证分类路径、原句、已有词链接与 Markdown；测试重复词、401、坏 JSON、取消不留下新文件，且旧文件字节不变。
- [x] 窗口从真实 App 资源字典加载样式，记录 binding 错误；用 RenderTargetBitmap 输出主窗口和设置窗口 PNG 到 `.artifacts/smoke/`，不读取其他应用屏幕。运行 `dotnet run --project tests/VocabCollector.Desktop.Smoke -c Release`，期望退出码 0、明确 PASS、有效的保存文件与截图。
- [x] 查看两张截图，修正截断、按钮不可见、对齐或滚动问题；重跑受影响的 smoke 断言。不通过时不宣称 GUI 验收完成。
- [x] 编写 `verify.ps1`，顺序执行 restore（锁文件）、`dotnet test -c Release`、Release build、smoke，任一步失败立即停止；编写 `publish.ps1`，执行校验后发布 `win-x64`，设置 `SelfContained=true`、`PublishSingleFile=true`、`IncludeNativeLibrariesForSelfExtract=true`、`PublishTrimmed=false`，产物命名 `VocabCollector.exe`。
- [x] 运行 `pwsh -NoProfile -File scripts/publish.ps1`（无 pwsh 时使用 powershell），确认 `dist/win-x64/VocabCollector.exe` 存在；实际启动发布 exe 验证窗口句柄、标题与可正常关闭，不修改用户现有配置。后台测试启动用 `Start-Process -WindowStyle Hidden`；只关闭本次启动且已核实的进程。
- [x] README 写明配置步骤、Base URL 样例、输出模板、保留名文件前缀、历史词按文件名匹配、已有文件不覆盖、关联可能为空、配置存放位置、构建命令、模拟验证范围，以及真实 API 需用户填入配置后验证。记录最终 exe 大小和 SHA-256 到交付说明，不提交二进制到 Git。
- [x] 全部验证通过后提交源码、测试、脚本和说明：`build: verify and package the standalone Windows application`。交付本地 exe 链接、源码目录、简短配置说明与已验证/未验证的范围。

## 执行顺序与可并行部分

Task 1 固定共享合约后，Task 2 与 Task 3 可以独立实现；Task 4 依赖两者，Task 5 依赖 Task 4，Task 6 验收完整流程。每个任务先写必要的行为测试、确认失败、实现再确认通过；不为静态样式或纯粹重复实现的细节添加测试。

建议在本会话由主代理直接实现六个任务，完成后由新的独立代理审阅整个分支；期间可委派只读检查。程序规模小、接口联系紧密，此方式可减少逐任务交接，并保留最后的独立审查。用户也可选择每个任务都由新代理实现和单独审阅的方式。

## 计划自查记录

- 规格的界面、设置、输入、词性、模板、链接、HTTP、失败、存储、交付要求分别覆盖到 Task 1–6。
- 五项 Review Focus 均有归属任务及具体断言；保留名安全前缀是对 Windows 文件系统限制的实现细化，不改变词条身份。
- 共享类型、字段与接口统一列出；无未定义的业务组件。UI 外部打开由导航接口隔离，测试不唤起真实 Obsidian。
- 用户已确认本计划并选择直接实现；各任务复选框记录执行结果，最后进行独立分支审查。
