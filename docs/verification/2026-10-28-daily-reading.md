# 每日阅读验收记录

日期：2026-10-28。范围：独立文章入口，额外 AI token 为 0，不匹配本地词库。

## 自动验证

执行 `pwsh -NoProfile -File scripts/publish.ps1 -OutputDirectory 'dist/兔词-每日阅读版'`，包含锁定依赖还原、Release 全套单元测试、完整构建和真实 WPF 控件验收。

- 467 项测试全部通过，0 失败、0 跳过。
- Release 构建 0 警告、0 错误；Windows x64 自包含单文件发布成功。
- 阅读服务先观察到 16 项未实现的预期失败；补齐后 22 项通过。真实 ScienceDaily `EDT` 发布日期触发回归测试，9 项命名时区预期失败后修复；核心阅读测试最终 35 项通过。
- 阅读 VM 首轮观察到预期失败后实现；跨日激活测试覆盖本地 +08:00 的午夜边界与离线旧日期缓存，最终 VM 测试 10 项通过。

服务测试覆盖 RSS/Atom、纯文本摘要、相对链接、HTTP/HTTPS 协议及 credentials 校验、去重、DTD 禁止、含无 Content-Length 流的 2 MiB 上限、来源超时/部分失败、同日缓存零网络、跨日轮换、空结果/断网保留缓存、损坏或篡改缓存、原子写入失败、并发与取消。

## 真实窗口

扩展已有 WPF smoke，使用隔离模拟服务和配置目录，不读取用户实际词库或配置。确认：

- 未配置 API/Obsidian 时点击“每日阅读”即可打开并显示三张文章卡片。
- 重复点击复用窗口；原文按钮调用注入的 URL 打开器，保留文章来源。
- 刷新、关闭取消、再次打开均正常；这些操作的模型请求增量为 0。
- 最小主窗口 900×640 时新增入口不重叠；阅读窗口 800×600 时卡片可滚动，沿用隐藏滚动条和现有图标/粉色主题。
- 整理单词时阅读按钮仍可用；既有输入、Markdown 预览、拼写修正、关联、取消和错误处理 smoke 全部通过。
- 全程 WPF 绑定错误为 0。已检查正常与最小尺寸截图。

窗口跨日保持打开时，再次激活会按本地日期调用非强制加载；同日切回不重复请求，离线旧日期不会造成每次激活反复联网。不增加午夜后台计时器。

## 真实网络

独立无凭据 HttpClient 运行实际 DailyReadingService，而非仅检查 XML URL：

- 默认来源包括 ScienceDaily Society/Environmental Science、Cloudflare Blog 和 BBC Technology。
- 当次初次发出 4 个公开 HTTP 请求，即便部分来源不可用，仍返回 2 篇通用和 1 篇技术文章，包含英文标题、摘要、原文链接和可解析的发布日期。
- 同日再次读取，额外 HTTP 请求为 0，返回同一批文章。
- 随后模拟断网强制刷新，仍保留原来的 3 篇文章并显示离线提示。
- 不携带 Authorization 头，模型请求为 0；阅读代码没有调用 AI 或词库服务。

真实 ScienceDaily 发布日期使用 `EDT` 等 RFC822 缩写，现按明确时区偏移解析。来源文章的发布日期与本地推荐日期分开显示。没有缓存时仍需要联网；本工具不保证外部网站永远可访问，也不会把泛读文章标为考研真题。

独立只读复核发现的隔夜窗口刷新问题已修复；最终无未解决的 P1/P2。

## 交付

- `dist/兔词-每日阅读版/VocabCollector.exe`，140,575,219 bytes。
- exe SHA-256：`1E5049760344EA85F239AEAA78A4AD853AE044FA8D8314A8180683D50DA1594E`。
- `dist/兔词-每日阅读版.zip`，61,182,441 bytes。
- zip SHA-256：`F54CF8D2F82E76619480ABAA91E57DEFECDBDDD830D461D73CC2B4542BAB9687`。

压缩包仅含 exe、第三方许可证、release.json 和使用说明；不包含用户配置、API Key、阅读缓存或单词笔记。设置继续沿用当前用户 LocalAppData 路径，旧版 exe 和旧词库未被覆盖。

截图：`.artifacts/smoke/daily-reading.png`、`daily-reading-minimum.png`、`main-minimum-before-scroll.png`。真实请求记录：`.artifacts/reading-live/result.json`。上述机器产物不提交 Git。
