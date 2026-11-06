# 2026-11-06：阅读点击修复、侧栏收录与光晕

## 问题和修复

1. **模糊左缘像刀切。** HoldToReveal 自身 ClipToBounds 加上正文起点位于 ScrollContentPresenter 视口左缘，形成两层裁切。取消自身裁切；ScrollViewer 左右外扩 10 DIP，CardBody 相应内缩 10 DIP，原文字坐标／可用宽度不变，给半径 8 的光晕留下余量。RTB 像素测试确认原左缘外出现逐渐变淡的 alpha，非硬截断。
2. **关闭按钮。** 漫游及新收录侧栏共用 PaneCloseButton：12 DIP／1.2 DIP 细线 X，30 DIP 透明命中区，无常驻框与文字；主题 hover／pressed／焦点反馈，保留原生 Button、键盘与无障碍名称。
3. **翻译／收录鼠标点击无响应。** Popup 采用独立呈现窗口，但内部按钮 PointerDown 仍经逻辑树路由到主窗口。原 owner 监听无条件认为是外点，按钮执行前就清除了选词。真实路由回归日志确认 `ownerRouted=1; source=ReadingTranslateButton; popup=True` 随后按钮进入时 `popup=False`；收录同样，未出现 Deactivated。现识别 Popup.Child 子树内的输入并放行；外点依然关闭，且不吞正文选择事件。

## 收录面板

- 点「收录」在文章右侧打开，可编辑单词与本次原句。打开只预填，不请求 AI／不写文件；点击「解析并保存」才使用现有配置生成／保存，并原生预览 Markdown。
- 与既有收录流程共用 MainViewModel／CaptureService；PrepareDraft 在忙态拒绝覆写，并清空旧词的结果，避免旧词卡出现在新词下。
- 关闭、返回目录、换文章或离开阅读只取消该侧栏发起的未完成生成。文章文档／阅读位置保留，已保存笔记保留，关闭后可重新选择其他词。共享模型原有其他来源的任务不会被侧栏取消。
- 清理保存后提示中的旧 Obsidian 依赖文案；按钮状态、错误和取消在面板内显示，无额外教学段落。

## 验证

测试使用隔离目录、模拟服务或 fake analyzer，绝不读取生产配置、API Key 或用户笔记。

- Hold／ReadingPage 组合 23/23；PaneClose＋漫游宿主 8/8；Pointer＋ReadingPage 14/14；新增收录侧栏 12/12、与 ReadingPage／Pointer 组合 26/26。覆盖成功／已有笔记／失败／异常、忙态拒写、清旧结果、关闭／换文／离页取消与迟到回包。
- `--reading-only` 真实 WPF 通过：先经过 pointer route，再译文／侧栏预填、没有自动请求或保存、就地关闭和正文位置保留、三主题与最小窗布局。
- **真实鼠标 CUA 核验：** 在 `--reading-manual` 的独立 MainWindow 中，用截图坐标点击正文 resilient、翻译按钮、收录按钮、解析保存和 X。实际观察中文译文、右侧原句预填、词卡完整预览及独立关闭后全文仍在。首轮 fixture 因只给一条例句被正式校验拒绝；补齐 fixture 并先经相同 CaptureService 自检 Saved，再启动全新隔离目录重测，未更改生产校验。
- 首轮 `.artifacts/reading-manual/e054da0a907946398ca8196bbbef9362/session.json` 记录 translationCalls=1；最终 `.artifacts/reading-manual/095334ada3744ed6b4410774cb853d38/session.json` 记录 analyzerCalls=1、savedFiles 包含真实 isolated-vault/words/形容词/resilient.md。已核对文件包含文章原句与两条例句，两个验证窗口均正常关闭；用户原程序保持运行。
- 自动截图在 `.artifacts/pure-reading-smoke/pure-reading-capture-*.png`；新增 pointer 测试保留同一路由，不再仅用 UIA Invoke 代替鼠标链路。

## 最终交付

`scripts/publish.ps1 -OutputDirectory 'dist/兔词-阅读收录修复版'` 完整成功退出：锁定依赖还原、Release 单测 **873/873**、构建 **0 警告／0 错误**、全部真实 WPF／本机 HTTP WebView2 冒烟以及自包含 Windows x64 发布均通过。验证后未再修改应用代码。

- EXE：`C:\Private Files\Projects\TUCI\dist\兔词-阅读收录修复版\兔词.exe`，143,532,232 bytes。
- ZIP：`C:\Private Files\Projects\TUCI\dist\兔词-阅读收录修复版-win-x64.zip`，62,411,905 bytes，6 个文件。
- SHA256：`C1DC261B8851D63EEB306BF1EE9EFFB3746ED4F2FAC750E5C17CB489A11CFC81`；交付 EXE 与 ZIP 内 EXE 均和构建产物一致。
- 包内附有 `release.json` 和第三方声明。保留所有旧版目录与原有词库／设置，退出旧程序后运行新版。
