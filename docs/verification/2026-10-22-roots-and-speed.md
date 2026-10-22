# 2026-10-22 释义、词根与生成速度验收

## 最终行为

- 释义标签单独一行，每个义项为引用块中的独立一行，使用 Markdown 硬换行，保留截图的竖线和上下排列。
- 发音下面增加词根；最多 3 组，包含中文解释及可选变体。不确定时明确说明暂无可靠信息，原形、时态和前后缀不混作词根。
- 词根卡片位于配置的单词目录下 `词根/`，支持 `duc.md`、`-duc-.md` 等单词根文件名。`duc / duct` 整组在唯一匹配时链接至真实卡片路径；多卡歧义时保留文本。普通单词与词根索引分开。
- 后续新增、移动、删除、出现同名词根卡片，启动、保存新词或手动刷新都能在本地补链或撤链，不调用 AI。
- 新词根区域沿用外置校验记录；人工改过的段落跳过。兼容旧的两个区域记录，不自动接管手工补写的旧词根。Markdown 正文没有程序标签或校验码。
- 新模板只用于新生成的卡片，不覆盖或重新解析用户已有笔记。

## 请求与速度

固定系统提示从 1789 缩至 1198 字符，保留释义、音标、词形、例句、搭配、语义关系、语法和词根。优先简短解释，历史词库始终不发往模型。请求中的链接由本地生成。

默认使用流式 Chat Completions，首段正文到达后更新界面；忽略推理内容，不把半份 JSON 展示或保存为笔记。正常结束标志及完整结构校验通过后才能写入。兼容接口直接返回普通 JSON；仅明确拒绝流式参数的 HTTP 400/422 回退一次，鉴权、限流和其他错误不自动重试。取消和超时同时覆盖回退和流式读取。

SSE 按 [WHATWG 事件流规范](https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation) 处理 UTF-8、空行事件、多 data 行、不同换行符及 BOM。响应总大小限制 1 MiB；保留 API Key 隐藏处理。

当前配置使用 DeepSeek 官方 `deepseek-flash`。官方文档说明其[默认开启深度思考且强度为 high](https://api-docs.deepseek.com/guides/thinking_mode/)，[Chat Completions 参数](https://api-docs.deepseek.com/api/create-chat-completion/)支持关闭。本版仅对 `api.deepseek.com` 的已知 `deepseek-flash`、`deepseek-v4-pro` 请求关闭深度思考，以减少日常词卡整理的等待。其他主机、本地 HTTP 和未知模型不携带该专用参数。

定点测量仅发送 `introduced` 和原句 `The company introduced a new policy.`，使用用户当前配置，不写入用户词库、不打印凭据。每种方式一次测量，受网络、服务端排队和缓存影响，不能作为保证或统计结论。

| 方式 | 总耗时 | 首段正文 | 系统提示字符 | 请求字节 |
| --- | ---: | ---: | ---: | ---: |
| 原版，默认模型行为、非流式 | 12.640 秒 | 不适用 | 1789 | 6819 |
| 精简提示、流式，仍默认深度思考 | 17.734 秒 | 16.613 秒 | 1198 | 3978 |
| 最终版，精简提示、流式、关闭默认深度思考 | 2.661 秒 | 1.061 秒 | 1198 | 4009 |

三次均为单次请求、完整结构校验成功；第二次测量说明仅缩短提示词无法保证更快。最终样本总耗时相对原版减少约 79%，不表示所有词、网络条件或服务商都有相同比例收益。关闭深度思考也可能影响复杂词源等内容的质量，仍要求模型不确定时留空。

本地扫描排除 `.obsidian`、`.vocabcollector` 子树，包括内部备份；词根单独索引。移除 AI 完成后的冗余外层扫描，提交仍在写锁内重新扫描、复查重复词和链接目标。

合成扫描基准：Windows 10.0.26200、.NET 10.0.11、SDK 10.0.203，1 次预热后 7 次交替测量中位数；1000 份普通卡片 + 100 份词根卡片，原版 25.68ms、新版 23.30ms。再加入内部目录的 5000 个 `.md` 后，原版 123.37ms、新版 22.56ms。只说明排除内部文件的扫描收益，不代表真实用户词库或 AI 延迟。

## 验证记录

实现前观察到释义排版、词根索引、词根补链、严格 roots JSON 解析和流式请求的预期失败，再逐项实现通过。

首次完整运行 `scripts/verify.ps1`：233/233 测试通过，Release 构建 0 警告、0 错误，真实 WPF 模拟 HTTP 流式验收通过。覆盖竖排释义、词根显示、后加词根卡片补链、刷新 0 次 API 调用、旧注释迁移及备份、人工编辑保护、重复词、401、坏 JSON、取消、设置加密和最小窗口。

独立审查发现 `upstream` 被错误识别为 `stream`，导致泛模型错误回退；已用独立参数和相邻拒绝语句匹配修复，增加 upstream、泛模型失败不重试与明确 stream 拒绝的回归用例。修复及官方参数隔离经再次独立审查，无剩余可操作问题。

最终执行 `scripts/publish.ps1 -OutputDirectory dist/win-x64-roots`，包含完整验证和自包含发布：246/246 测试通过，Release 构建 0 警告、0 错误，真实 WPF 流式模拟接口验收通过。新产物保留在独立目录，避免覆盖正在运行的旧版本。

- 文件：`dist/win-x64-roots/VocabCollector.exe`
- 大小：139875786 字节
- SHA-256：`D19F5A8AAC2CD094EA8C024E93CCF1FA4E07D8769CD843A366A2187927650562`
- 产物详情：同目录 `release.json`
- 窗口截图和隔离示例：`.artifacts/smoke/`
- 定点测量：`.artifacts/ai-speed/baseline.json`、`optimized.json`、`optimized-fast.json`

示例 [introduced.md](../examples/introduced.md) 由实际渲染器在隔离目录生成；音标、词形、释义和词源参考 [Oxford introduce](https://www.oxfordlearnersdictionaries.com/us/definition/english/introduce)，词根变体参考 [Merriam-Webster duct](https://www.merriam-webster.com/dictionary/duct)，例句自拟。用户真实笔记未改写。
