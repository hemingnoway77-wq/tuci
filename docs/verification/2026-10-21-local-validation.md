# 首版交付验收记录

日期：2026-10-21（Asia/Shanghai）。平台：Windows x64，.NET 10/WPF。

## 结果

- 82 项自动行为测试通过，Release 编译 0 警告、0 错误。
- 真实 WPF 控件通过本机模拟 Chat Completions 服务验收：设置保存和重读、Key 加密、粘贴原词与原句、词性归档、历史链接、重复词、鉴权错误、坏 JSON、取消、旧文件保留。
- 默认窗口与最小窗口截图已检查；最小窗口原句可通过滚动完整访问，操作区没有覆盖输入。
- 独立 Python YAML 解析器读回生成笔记，词名和词性元数据正确。
- 自包含 exe 已实际启动，检测到“单词收集器”窗口，正常关闭；启动测试未改变用户已有设置。
- 发布后再次执行依赖锁校验和完整验证脚本通过；开发与 win-x64 发布使用各自锁文件。

最终发布文件：`dist/win-x64/VocabCollector.exe`。

- 大小：139,797,962 字节（约 133.3 MiB）。
- SHA-256：`28E43EFBFF81202C6F52EDE2E5DA8A2A83637210BF8B3BD4E657CF4814DB03F0`。
- 窗口截图、生成笔记及模拟验收结果：`.artifacts/smoke/`。

## 独立审查与修复

独立审查无 Critical、2 项 Important、无 Minor、无搁置判断。两项 Important 都先复现失败，再修复并运行完整验证：

1. **最小窗口遮挡输入**：输入区加入滚动容器。真实 WPF 回归检查可见区域不与生成按钮重叠，且原句在滚动后可完整访问。
2. **保存时关联目标已经删除**：在写锁内根据最新索引重新解析关联并渲染，提交前再次核验，返回实际提交的 Markdown。`TargetDeletedBeforeWriteLockIsNotLinkedInSavedContent` 从失败变为通过，保存内容与界面结果一致。

没有延期的小问题。真实服务商调用需要用户自行填写 API 地址、Key、模型名后验证；本次所有 HTTP 验收均使用隔离的本机模拟服务及假 Key。

## 实现时的判断及影响

1. 在新建的独立仓库 `feature/vocabulary-collector` 分支内实现，无需再创建额外 checkout：仓库内只有本工具内容。影响是后续维护应以该功能分支为源码基准，其他工作区项目不受影响。
2. DPAPI 直接使用 WPF 框架提供的程序集，去掉 SDK 指出的冗余 NuGet 依赖。影响是未来迁移到非 WPF 目标时，可能需要显式补充加密依赖。
3. 保存接口改为接收纯渲染回调并返回 `SavedNote`，让锁内最终链接及实际文件内容保持一致。影响是未来新增存储实现时，需要遵守更新后的内部保存合约。

## 重现方式

```powershell
pwsh -NoProfile -File scripts/verify.ps1
pwsh -NoProfile -File scripts/publish.ps1
```

脚本与源码均已保存，发布 exe 和模拟验收截图不纳入 Git。
