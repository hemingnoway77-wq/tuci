# 透明黑色图标与字体清晰度验收

日期：2026-11-04。工作目录：`C:\Private Files\Projects\TUCI`。

## 图标

- 使用用户最终确认的 `tuci-icon-black-transparent-v1.png`，按原始字节替换 `Assets/app-icon-source.png`，图形与透明区域不重新绘制。
- 源图为 1254×1254，SHA-256：`A7E876CC0CA96F8DA0C793094F91E1A48CCD6DD9831D32F228D1317BA4D672A3`。
- 沿用 `powershell.exe -NoProfile -STA -File scripts/create-icon.ps1`，生成 app.ico 的 16、24、32、48、64、128、256 像素帧和 256/512 像素 PNG。脚本运行成功，ICO 格式、各帧尺寸校验通过。
- 窗口样式、标题栏、任务栏、可执行文件和主界面标记共用这组资源。移除了主界面图片的矩形裁切。
- 生成资源独立逐像素检查通过：外围与镜圈内部 alpha=0；主体近黑色，采样 alpha=254，保留源图本身的数值，不伪称 alpha=255。
- 从最终发布 exe 提取的 32×32 图标再次验证：外围 alpha=0，镜圈内部 alpha=0，黑色兔耳 alpha=254。

## 字体排查与修改

修改前源码和截图显示：品牌副标题为 8 DIP；元信息为 10 DIP；提示、辅助按钮为 11.5 DIP；中文小标签和按钮使用 Noto Serif SC 衬线字体；等宽字体未显式指定中文回退。这些组合在小尺寸下保留的笔画空间不足，是本次清晰度调整的重点。现有 Display 格式化及布局取整已经开启，未发现界面用整体缩放变换放大小字的代码。

- 大标题和英文词条保留衬线风格。
- 小型中文标签、日期、路径、按钮和提示使用 Microsoft YaHei UI，并提供 Microsoft YaHei / Segoe UI 回退。
- 辅助文字至少 12 DIP，说明和普通按钮 13 DIP，输入与正文 14 DIP；不再使用原来的 8–11.5 DIP 小字。Markdown 分隔线的 FontSize=1 仅用于空白绘制，不是阅读文字。
- Markdown 正文使用 Segoe UI / Microsoft YaHei UI / Microsoft YaHei；等宽代码保留 Cascadia Mono / Consolas，并补充中文回退。
- 保留 UseLayoutRounding、SnapsToDevicePixels 和 TextFormattingMode=Display，显式设置 TextRenderingMode=ClearType。对应定义见 Microsoft 的 [TextFormattingMode](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformattingmode?view=windowsdesktop-10.0) 与 [TextRenderingMode](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textrenderingmode?view=windowsdesktop-10.0)。
- 字号增大后收紧设置输入框垂直内边距、阅读条目间距，放宽空状态提示的最大宽度，避免默认窗口的输入框或阅读入口被裁切。

## 验证

最终执行：

```powershell
pwsh -NoProfile -File scripts/publish.ps1
git diff --check
```

结果：571 项单元测试通过，无失败或跳过；Release 构建 0 警告、0 错误；真实 WPF 控件冒烟检查 PASS，绑定错误 0；自包含 Windows 发布成功。现有检查覆盖主题切换、最小窗口布局、阅读滚动、设置、词卡预览、链接、收录等流程，使用本地模拟服务和隔离词库。

已查看 `.artifacts/smoke/` 中的首页、词卡、设置、每日阅读与最小窗口截图。设置默认窗口内模型输入框完整显示，阅读页样例的三篇原文入口完整显示；最小窗口保持可滚动。修改前截图保留在 `.artifacts/icon-type-before/`。截图验证针对当前机器和 96 DPI 渲染，不代表所有显示器缩放比例的物理屏幕效果。

未修改用户词库、真实 API 配置或系统字体设置，保留本次前已有的未提交工作；未提交、未推送。

## 发布文件

- `dist/win-x64/兔词.exe` 与 `dist/兔词-书页版/兔词.exe` 已同步，压缩包内 exe 的 SHA-256 与两者一致。
- exe：140,595,139 字节，SHA-256 `5A706BB87894347FE7C6E79DAC99A5332D91AEE409BF05F0978EADD733C8273E`。
- `dist/兔词-书页版-win-x64.zip` 已更新，SHA-256 `F07DCB50CEB4CBACF576AD82AF86BCB8962DF7FC699712072A34D83A767DB221`。

## 后续：资源管理器显示旧图标

用户反馈文件列表仍为旧粉色图标，F5 无效。已在实际打开的 `dist/win-x64` 文件夹重现；同一路径的 exe 内嵌 32×32 图标和运行中窗口均为新黑色图标，独立 Shell 查询的 16×16 图标也无彩色像素。文件列表仍持有旧的系统图像列表缓存。

仅发送 UPDATEITEM / UPDATEDIR 通知不足以更新此显示。读取两个发布 exe 的系统图像列表索引后，按 exe 路径、图标序号 0 调用 SHUpdateImageW，再发送 SHChangeNotify 的 ASSOCCHANGED / FLUSH 及目录通知，实际 Explorer 文件列表已显示黑色兔耳放大镜。未重启 Explorer、未删除缓存文件、未重启应用或改写 exe；最终结果通过实际窗口截图确认。

API 依据：[SHUpdateImageW](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shupdateimagew)、[SHGetFileInfoW](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetfileinfow)、[SHChangeNotify](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify)。
