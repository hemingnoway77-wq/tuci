# 书页界面与黑白主题验收

> 本记录保留当时的图标和字号验收结果；后续透明黑色图标、小字清晰度调整及最新发布见[图标与字体清晰度验收](2026-11-04-icon-and-legibility.md)。

日期：2026-11-04
范围：语义化主题与三套配色、主窗口页面导航、每日阅读页内化、设置页两栏布局、Markdown 预览排版。

## 验证方式

```powershell
pwsh -NoProfile -File scripts/verify.ps1
```

环境：Windows x64、.NET SDK 10.0.203。结果：单元测试 **571/571 通过**，Release 构建 **0 警告 0 错误**，WPF 冒烟检查 **PASS**。

## 主题

- **墨白**：除错误色外，所有主题刷子的 RGB 通道极差 ≤ 6，确认为无彩色；页面与卡片、正文与强调均保持阅读对比。
- **暖灰 / 玫红**：正文对比度 ≥ 10:1，次要文字 ≥ 4.5:1，按钮文字 ≥ 4.5:1（单元测试计算相对亮度）。
- 主题选择继续写入 `%LOCALAPPDATA%\VocabCollector\appearance.json`；旧的 `pink`、`warmNeutral` 值可读，新增 `mono`；缺失、损坏、未知值回退暖灰。
- 写入失败时保留当前视觉效果并提示；不改写 `settings.json` 或 API Key。
- 切换主题不重建窗口、不清空输入、不重载已显示笔记或文章、不调用模型；已打开的设置页同步换色。
- 窗口标题栏（caption）在 Windows 11 上通过 DWM 设置为当前主题的 `Paper` / `Ink` / `Line` 色；冒烟检查确认 DWM 接受这三个颜色。Windows 10 保持系统默认标题栏。
- exe、窗口、任务栏和界面左上角均使用用户提供的黑底白色线描兔子；`app.ico` 由 `app-icon-source.png` 生成 16–256 像素各尺寸，`create-icon.ps1` 的尺寸校验通过。

## 界面细节

- 输入框占位符在获得焦点时隐藏，启动时不再自动聚焦，因此光标不会与示例字重叠。
- 主题选择为三枚色点（墨白/暖灰/玫红），悬停显示名称，选中项带细环。
- 元信息字号提升到 10px、提示 11.5px、字段标签 12px、状态栏 12px；阅读摘要 13px。
- 词卡位置与打开按钮合并为一行，默认窗口内可完整看到两个例句；阅读页三篇文章收进一屏。

## 页面

- 顶部「收录 / 每日阅读」为两个页内页面；每日阅读是主窗口内的 `ReadingPage`，不再创建独立窗口。
- 首次进入按需加载；离开页面取消进行中的请求；返回时复用已加载文章；「刷新文章」强制获取；最小尺寸下刷新按钮与最后一篇文章仍可到达。
- 每日阅读全程零模型请求，不读取或写入词库。
- 设置页改为「连接 AI / 安放词卡」两栏，900×700 窗口内完整显示，窄窗口可滚动。

## 截图

| 文件 | 内容 |
| --- | --- |
| `.artifacts/smoke/main-empty.png` | 暖灰首页空状态 |
| `.artifacts/smoke/main.png` | 暖灰首页与已生成词卡 |
| `.artifacts/smoke/main-scrolled.png` | 词卡下半部分（语法、原句、我的笔记） |
| `.artifacts/smoke/daily-reading.png` | 页内每日阅读（头条 + 两栏文章 / 日期侧栏） |
| `.artifacts/smoke/theme-mono-main.png` | 墨白主题 |
| `.artifacts/smoke/theme-warm-main.png` | 暖灰主题 |
| `.artifacts/smoke/theme-pink-main.png` | 玫红主题 |
| `.artifacts/smoke/theme-warm-reading.png` | 暖灰主题下的阅读页 |
| `.artifacts/smoke/theme-pink-reading.png` | 玫红主题下的阅读页 |
| `.artifacts/smoke/settings.png` | 设置页两栏布局 |
| `.artifacts/smoke/theme-warm-settings.png` / `theme-mono-settings.png` | 设置页随主题即时换色 |
| `.artifacts/smoke/main-minimum.png` | 最小窗口 920×660 |
| `.artifacts/smoke/daily-reading-minimum.png` | 最小窗口下的阅读页 |

## 发布

`scripts/publish.ps1` 在完整验证通过后发布自包含单文件版本：

- `dist/win-x64/兔词.exe`：140,730,307 字节，SHA-256 `32E5A1DE31553853A210634033AFFB320D30DC0DD2A96127F6187326982FC39F`
- `dist/兔词-书页版-win-x64.zip`：61,320,788 字节（含 exe、第三方声明和 release.json），SHA-256 `6C037F24E175F4293074D99869EA3635C448E36F053ADC1CAEE77F14379F86A8`

界面细节修订：主题名改为三枚色点（悬停显示名称）、占位符在获得焦点时隐藏、移除启动自动聚焦、整体上调小字号、词卡位置与打开按钮改为同一行。本版还换上了用户设计的单色兔子图标（exe、窗口、任务栏、标题栏和界面品牌标记），并让 Windows 11 标题栏使用当前主题的纸色。

## 已知限制

- 版面使用 Windows 自带的 Sitka / Georgia 与 Noto Serif SC；缺少衬线字体时回退到微软雅黑，布局不变、字形会变。
- 阅读页文章列表在窄窗口内滚动；滚动条沿用全应用的闲置隐藏行为。
- 主题切换只改变配色，不改变内容与文件；标题栏配色只在 Windows 11 生效，Windows 10 保持系统默认标题栏。
