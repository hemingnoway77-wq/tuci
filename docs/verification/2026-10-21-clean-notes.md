# 干净笔记修复验收

日期：2026-10-21。

## 问题与修复

用户截图显示上一版的 `<!-- vocabcollector:links:v1 ... sha256=... -->` 和结束标记在 Markdown 编辑界面占据正文。根因是把程序的区域归属和人工编辑保护记录写进了用户笔记。

新模板只输出普通 Markdown 学习内容。`ManagedLinks` 使用可见章节定位和独立的区域状态，`LinkRefresher` 把持久记录放在单词目录的 `.vocabcollector/note-state.json`，性能缓存仍单独放 `link-index.json`。生产代码已删除生成旧标签的 `Wrap` 和依赖标签补链的旧接口。

旧版笔记的迁移只删除程序原有的成对标记行，保留正文、已有链接、原始换行和用户补充。去除注释前先登记原校验值，写入使用原子替换并留存原文备份。手动改过的区域仍不被接管；一个区域可补链时也不会重签另一个手改区域。不配对、重复模式或出现在代码／普通注释内的标签保守跳过。

## 验证

执行：

```powershell
pwsh -NoProfile -File scripts/verify.ps1
pwsh -NoProfile -File scripts/publish.ps1 -OutputDirectory dist/win-x64-clean
git diff --check
```

- 完整测试 **164/164 通过**。
- Release 构建 **0 警告、0 错误**。
- 真实 WPF 控件 smoke **PASS**：新输出及预览无程序标签，旧标记迁移保留正文和精确原文备份，未来词条补链正常，HTTP 请求次数不增加。
- 新的清洁输出／保存后警告测试先观察 2 项 RED，再实现 GREEN；区域解析及迁移 14 项、存储相关 22 项定向测试通过。
- 独立只读审查未发现可复现的 Critical／Important／Minor 问题；审查者另行运行隔离测试 **38/38 通过**。

实际用户文件检查：`C:/Private Files/Obsidian/English/英语单词/动词/introduced.md` 在迁移命令执行前已变为无标签正文，并已存在外置记录及备份。本次核验没有再次改写正文，确认其与备份经“只去程序标记”得到的结果完全一致，现有记录保留。当前正文 SHA-256：`D4613D8117C8BE9FC34CBD216E190E1763331DE8B7C60231E983A74998C6E7D7`。核验记录位于 `.artifacts/clean-note-migration/result.json`。

没有调用用户 AI API，没有关闭或覆盖正在运行的旧 exe，没有修改 API 设置。运行中的旧版路径为 `dist/win-x64-notes/VocabCollector.exe`，修复版放在独立目录，需关闭旧版后运行修复版。

## 产物

- `dist/win-x64-clean/VocabCollector.exe`
- Windows x64，自带运行时，单文件。
- 139,860,938 字节。
- SHA-256：`2E03E6BBF54856621558344E2C596BDD449C523E3867CAF4D1E7041C87C7B5BB`
- [干净模板预览](../examples/introduced.md)

## 保守处理边界

外置记录与笔记一起保留；记录丢失时不根据相同标题接管私人笔记。手动移动源笔记后，当前版本不自动重新绑定其管理归属；它作为链接目标仍可使用。元数据写入失败时，已提交笔记仍报告保存成功并明确提示自动补链记录失败，后续以保护正文为先。
