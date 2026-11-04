# 兔词项目位置与名称验证

日期：2026-11-04。

## 项目位置

- 新项目根目录：`C:\Private Files\Projects\TUCI`。
- 从 `C:\Private Files\develop\Codex\工作台\english-vocab-tool` 复制全部 127 个已跟踪文件及独立的 `.git` 历史，基线为 `b8267ef`。
- 未复制历史发布包、构建缓存、临时验收文件；在新目录重新构建发布。原目录保留作备份，未修改其代码。

## 名称与兼容

- 主窗口标题、页面名称、设置窗口和每日阅读窗口统一使用“兔词”。
- 发布文件为 `dist/win-x64/兔词.exe`，Windows 文件属性中的产品名称、文件描述均为“兔词”。
- 显式保留内部命名空间和 NuGet 项目标识；锁定依赖还原通过。
- 设置、主题、日志和阅读缓存继续使用 `%LOCALAPPDATA%\VocabCollector`；不迁移或改写用户配置、Key 或 Obsidian 词库。
- 发布脚本保存为带 BOM 的 UTF-8；Windows PowerShell 5.1 实际解析通过，两个中文可执行文件名均正确识别。

## 验证结果

- 在新目录运行 `pwsh -NoProfile -File scripts/publish.ps1`：564 项测试通过，0 失败；Release 编译 0 警告、0 错误。
- 真实 WPF 控件及本机模拟接口验收通过，绑定错误为 0；截图中的主界面已显示“兔词”。验收使用隔离词库与假 Key。
- 127 个源项目已跟踪文件在新目录全部存在；源项目工作区保持干净。
- 自包含 Windows x64 单文件发布成功：140,607,427 字节。
- SHA-256：`35594BCA7D16762CFBD454CB17DCD2AC2806CBBF654541E9125746678727955E`。
- `git diff --check` 通过。

截图和测试结果保存在新目录的 `.artifacts/smoke/`，发布清单见 `dist/win-x64/release.json`。
