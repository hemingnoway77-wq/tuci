# 双主题验收记录

日期：2026-10-28。默认暖灰主题，原粉色通过主窗口右上角开关启用。

## 实现与隔离

暖灰使用米白、炭色和棕灰层级，不含蓝色/绿色强调。64 组原色对应两套显式调色板；粉色保留原值。默认正文、辅助文字、主按钮文字的对比度分别至少 10:1、4.5:1、4.5:1。

主窗口、设置、每日阅读、笔记 FlowDocument、滚动条和界面矢量图标使用动态资源。保留当前文档对象、文本、输入和阅读文章，无须重建窗口或重新请求 AI。开关通过 Checked/Unchecked 支持鼠标、键盘和 TogglePattern，初始化已保存状态时不重复写盘。

偏好独立存放 `LocalAppData/VocabCollector/appearance.json`，仅含 `theme`，不改写 API 设置或 Obsidian 笔记。不存在、损坏、未知主题或不可读时回退暖灰；原子保存失败保留旧偏好，当前主题仍然可用并提示。

## 自动验证

完整执行：

```powershell
pwsh -NoProfile -File scripts/publish.ps1 -OutputDirectory 'dist/兔词-双主题版'
```

- 全套 486 项测试通过，0 失败、0 跳过。
- Release 构建 0 警告、0 错误；自包含 Windows x64 单文件发布成功。
- 新增主题/偏好 19 项测试先红后绿，覆盖两套颜色、资源键覆盖、对比度、动态控件、未知枚举、重启读取、损坏回退、未配置 API、独立配置、失败替换及取消。
- 原 8 项 Markdown 预览测试继续通过；既有阅读、拼写、关联、错误处理等测试全部保留。

## 实际 WPF 控件与截图

`ThemeChecks.cs` 加入原有 smoke，在隔离配置目录和本机模拟 API 上运行，未读取用户真实偏好、API Key 或词库。

- 未配置 API/Obsidian 时，通过真实 TogglePattern 切换粉色再切回暖灰；appearance.json 可重新读取，settings.json 没有被创建。
- 在已存在的笔记和阅读窗口上切换，确认窗口背景来自当前全局调色板、文档正文立即变色，且文档对象/文本、输入和文章列表不变。
- 已打开的设置页可即时更新，保留表单草稿；切回后确认加密 API 配置文件逐字不变。
- 主题相关操作模型请求增量为 0；单词整理期间开关仍然可用。
- 主窗口最小 900×640 时，开关与每日阅读、刷新、设置不重叠；沿用既有滚动和输入可达性检查。
- 全部 WPF 绑定错误为 0。已实际检查两套主窗口、每日阅读、设置和最小主窗口截图。

矢量图标使用非共享 DrawingImage 实例，使现有图像同步热切；exe 的品牌图标资产沿用旧版。独立只读代码复核无未解决的 P1/P2。

截图位于 `.artifacts/smoke/theme-neutral-main.png`、`theme-pink-main.png`、`theme-neutral-reading.png`、`theme-pink-reading.png`、`theme-neutral-settings.png`、`theme-pink-settings.png` 和 `main-minimum-before-scroll.png`。

## 交付

- `dist/兔词-双主题版/VocabCollector.exe`：140,591,603 bytes。
- exe SHA-256：`286586396EB6158CB1D795384B6C180C4ABFBC341101C95BF297F3678126501A`。
- `dist/兔词-双主题版.zip`：61,200,722 bytes。
- zip SHA-256：`C32CF7AE188200ADB30168E44A897E7AE457033227C8BAEF537D081D43C35AE6`。

压缩包仅含 exe、release.json、第三方许可证和使用说明，不包含个人偏好、API 配置、阅读缓存或词库。旧 exe 未被覆盖；关闭旧版后运行新版，API/词库配置继续使用原位置。
