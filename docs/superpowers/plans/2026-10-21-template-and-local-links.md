# 初学者模板与本地历史补链实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 按用户提供的 introduced.md 版式生成适合中文初学者的笔记，并以本地计算维护已有和未来词条链接。

**Architecture:** AI 只解析当前单词和可选原句；本地确定实际路径、插入链接和刷新历史笔记。新笔记以带校验摘要的隐藏管理块标记例句及语义关系，只有未被人工修改的块参与补链；原子替换前保留备份。现有人工示例不迁移、不覆盖。

**Tech Stack:** .NET 10、WPF、现有 xUnit 和真实 WPF smoke runner，无新增运行依赖。

**Spec:** 用户提供的 `C:/Private Files/Obsidian/English/英语单词/动词/introduced.md` 为排版参考；内容和链接约束见本计划。延续用户此前“直接实现”的执行要求，本次不重复要求实施授权。

## Global Constraints

- 标题、主要层级、双语例句、近义词/反义词及句法模式沿用示例；保留 YAML、可选来源原句和独立“我的笔记”。
- 释义 1–3 个；例句恰好 2 个，每句最多 20 个英文词；搭配通常 3 条（兼容 2–4 条）；近义词最多 3、反义词最多 2；句法模式最多 3 条。
- 近反义词说明适用义项，禁止为了凑数量编造；不存在词条时仍显示学习内容，保持普通文字。
- 记录原形和词形说明但不合并输入词形；音标对应输入实际词形。缺少音标明确显示未提供。
- 链接仅完整单词、忽略大小写、不猜变形、同名歧义不链接、跳过当前词和常见功能词；例句最多 3 个不同目标。
- AI 请求不包含历史词库。新增链接和历史补链不得调用 AI。首次建立本地缓存，后续只重算受目标变化及文件变化影响的文件。
- 只处理工具校验有效的管理块；人工修改的块、未标记历史笔记和个人区不覆盖。
- 外部新增卡片在工具下次启动/刷新时处理；工具关闭期间不承诺即时改写。保存新卡片自动触发补链。
- 目录边界、symlink/junction、防重复、HTTP/HTTPS、DPAPI 和取消语义保持现有约束。

## Review Focus

- 修改管理块文字后补链不得撤销人工编辑；只修改“我的笔记”不应阻碍其他块补链。
- 增加同名词条、移动或删除目标后，不保留错误路径，不猜歧义目标。
- 用户同步/编辑与刷新重叠，提交前需重读校验，原子替换保留原文件备份。
- 新笔记已经提交后，补链失败或取消不得将保存结果报告成失败或未保存。
- 词库变大时 HTTP 请求体不包含旧笔记；本地刷新不增加请求次数。

### Task 1: AI 内容契约

**Files:** Models.cs、Ai/AnalysisJson.cs、Ai/OpenAiCompatibleAnalyzer.cs 及其测试和 TestData。

**Interfaces:** WordAnalysis 新增 Lemma、FormNote、Grammar init 属性；GrammarPattern(Pattern, Explanation)；RelatedCandidate 追加可选 Explanation。

- [x] 写解析缺省兼容、严格类型/长度、近反义数量和当前词形提示的失败测试并运行。
- [x] 实现有界 JSON 解析和只含当前词/原句的提示，运行对应测试。

### Task 2: 本地链接与历史补链

**Files:** 新增 Markdown/ManagedLinks.cs、Storage/LinkRefresher.cs、LinkMaintenance.cs；修改 VaultIndex.cs、VaultStore.cs；新增对应测试。

**Interfaces:** ManagedLinks.Wrap(mode, body)、Refresh(document, currentWord, index)；LinkRefresher.RefreshAsync(options,index,ct)；ILinkMaintenance.RefreshLinksAsync(options,ct)；LinkRefreshResult(Updated,Skipped,Failed)。

- [x] 写不存在→存在、唯一→歧义、手工编辑、备份和幂等失败测试并运行。
- [x] 实现实际路径链接、校验块、变更缓存、受影响文件更新和安全写入。
- [x] VaultStore 使用现有同一写锁协调保存和刷新，独立刷新不调用模型。

### Task 3: 新模板与保存衔接

**Files:** Markdown/NoteRenderer.cs、CaptureService.cs 和相关测试。

- [x] 写版式、语义说明、未收录词保留、例句已有链接和保存后历史补链失败测试并运行。
- [x] 渲染用户版式并包裹受管理块；保存后执行本地刷新，后续失败仅附加提示。
- [x] 运行完整核心测试检查查重、取消和保存内容与预览一致。

### Task 4: 桌面刷新入口与交付

**Files:** MainViewModel、MainWindow、App 及 UI/smoke 测试；README、验收记录。

- [x] 写手动刷新不调用 API、忙时不可重复操作、取消及启动刷新失败测试并运行。
- [x] 增加“刷新词条链接”，复用同一 VaultStore；配置有效目录后启动补链。
- [x] 真实 WPF smoke 验证本地 HTTP、界面入口、补链、无额外请求和最小尺寸。
- [x] 完整 verify、独立代码审查、修复后再发布至单独目录；验证 exe 可启动且不修改用户配置或真实笔记。
- [x] 记录用法、当前不自动猜词形及只迁移新管理块的限制，并提交改动。
