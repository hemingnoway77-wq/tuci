# 单词收集器

一个简单的 Windows 桌面工具：粘贴陌生英文单词，调用自己配置的 AI，生成固定格式的 Markdown，存入 Obsidian 仓库。

## 开始使用

1. 双击 `dist/win-x64/VocabCollector.exe`。发布版自带运行环境，无需另装 .NET、Python 或 Node.js。
2. 点击右上角“设置”，填写 API 地址、API Key、模型名。
3. 选择 **Obsidian 仓库根目录**，填写仓库内的单词子目录，默认 `英语单词`。
4. 按需开启“按主要词性分文件夹”，保存设置。
5. 粘贴一个单词；可附上遇见它时的原句。点击“解析并保存”。
6. 完成后可点击“在 Obsidian 中打开”，也可打开文件夹。笔记展示区支持选择和复制文本。

可执行文件可以单独复制到别处运行。设置位于 `%LOCALAPPDATA%\VocabCollector\settings.json`，API Key 使用当前 Windows 用户的 DPAPI 加密，不以明文保存。把此配置文件复制到其他电脑或其他 Windows 账户后，需要重新填写 Key。

## API 配置

使用 OpenAI 兼容的 **Chat Completions** 接口，示例：

```text
API 地址：https://your-provider.example/v1
API Key：你的服务商提供的 Key
模型名：服务商实际提供的模型名称
```

程序会追加 `/chat/completions`；也可填写已经含此后缀的完整地址。不会猜测或追加 `/v1`。普通服务须用 HTTPS，本机 loopback 服务可用 HTTP。模型需要能按提示返回 JSON；不要求专有结构化输出功能。

解析最长等待 90 秒，可主动取消；失败后不自动重试。用户点击解析会调用其配置的服务商，具体费用由该服务商和模型决定。

## 笔记与词条关联

笔记包含：音标、词性与中文释义、2 个双语例句、常见搭配、遇见时的原句、已收录关联词和“我的笔记”区。固定模板示例见[设计规格第 5 节](docs/superpowers/specs/2026-10-21-english-vocab-tool-design.md)。

默认路径示例：

```text
你的仓库/
  英语单词/
    名词/resilience.md
    形容词/resilient.md
```

- 一个输入词一份笔记，多种词性写在同一文件内；原句帮助确定归档的主要词性。
- 查重不区分大小写，覆盖单词目录中的普通子目录。重复词只提供已有路径，不调用 AI，不覆盖手写内容。
- 保留输入词形，例如 `running` 和 `run` 分别保存，`saw` 不会自动合并到 `see`。
- Windows 保留设备名 `con/aux/prn/nul` 使用 `_con.md` 等安全文件名，程序仍按原单词查重和关联。
- 历史词条以 **单词目录中的 `.md` 文件名** 识别，首版不解析别名或任意笔记全文。
- AI 给出同根词、近义词、反义词、易混词候选，程序仅链接实际存在且唯一匹配的词条。没有历史匹配时显示“暂无已收录的关联词”，不保证找到所有语义关系。
- 内部链接示例：`[[英语单词/名词/resilience|resilience]]`，可供 Obsidian 显示反向链接及图谱。程序不改写旧笔记。
- 单词输出子目录避开 `# ^ [ ] |` 等 Wikilink 保留字符；旧子目录包含这些字符时仍查重，但跳过关联。仓库根目录可以包含 Windows 允许的特殊字符，打开时会编码。
- 为防止写到仓库外，不跟随目录链接、junction 或文件 symlink。请选择普通本地文件夹；首次使用请确认路径。

正式 Markdown 使用 UTF-8 与 LF，通过临时文件和原子移动提交；存在的文件不会覆盖。`.vocabcollector.write.lock` 是程序用于协调并发保存的空锁文件，不是笔记。

## 验证与构建

开发需要 Windows x64 和 .NET 10 SDK。仓库已提交 NuGet 锁文件。

```powershell
pwsh -NoProfile -File scripts/verify.ps1
pwsh -NoProfile -File scripts/publish.ps1
```

没有 `pwsh` 时可使用 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1`。脚本不修改系统执行策略。

自动验证包括核心行为测试及真实 WPF 控件的模拟接口验收：设置保存与重读、Key 加密、输入与归档、已有词关联、重复词、API 401、坏 JSON、取消、旧文件保留以及窗口截图。测试只使用隔离目录、本机模拟服务与假 Key，不读取用户真实词库。

截图、样例 Markdown 和验收结果位于 `.artifacts/smoke/`；发布产物与大小、SHA-256 信息位于 `dist/win-x64/`。两者不提交 Git。

**真实服务商尚未联调**：需要在程序设置中填入自己的 API 配置后验证。程序未申请代码签名证书；Windows 首次运行可能显示来源提示。
