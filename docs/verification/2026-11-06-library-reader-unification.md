# 词库两栏阅读与统一风格验收

日期：2026-11-06。基线：`5d58cb2b389be8b78c20cd50e67cba0748341844`，分支 `feature/windows-learning`。

## 按用户范围调整

活动词库只显示左侧文件列表和右侧 Markdown 阅读，保留搜索、导入、刷新、路径及前进后退。顶部四个导航、主题按钮、设置与底部状态栏始终沿用客户端其他页面的同一套界面。文件列表可调宽、收起和恢复；展开、选中与切文使用短促过渡，并遵循系统动画设置，不缩放正文。

移除主窗口的词库专属黑色调色板注入、隐藏导航和工作区页面路由。新 `LibraryBrowser` 只使用客户端现有动态主题资源；原版 `workspace-ui.json` 即使记录深色或收起状态也不会被读取。多标签、源码编辑、图谱和关联侧栏没有活动词库 UI 入口。旧底层服务、独立测试、Markdown 与历史元数据保留，未迁移或清除用户内容。

`NoteReader` 的标题与悬停预览同样使用客户端 Ink／Accent／Sheet／Line，切换暖灰、墨白、玫红时保持当前文档、文字选择和字号。内部链接仍可直接跳转，Ctrl+悬停可预览；查看简卡或全文不影响漫游计次。

## 验证结果

```powershell
pwsh -NoProfile -File scripts/publish.ps1 -OutputDirectory 'dist/兔词-统一风格版'
git diff --check
```

- **734 项单元测试通过，0 失败／跳过。** Release 构建 0 警告、0 错误；锁定还原和 Windows x64 自包含发布通过。
- 完整真实 WPF 控件冒烟 PASS，绑定错误 0。覆盖文件和目录同树、展开／折叠、选中、正文搜索、导入及重复不覆盖、真实刷新、阅读切换、链接返回位置、收起恢复及 920×700 最小窗口。
- 通过顶部实际主题按钮分别切换三种客户端主题，检查共享导航和底栏始终可见、正文 Ink 与客户端一致、当前 Markdown 不被替换；历史黑色偏好不参与新视图。
- 新状态回归先验证失败再修复：文件夹选中在刷新后保持；被动恢复队列不会抢走主动导航；缺失目标静默预载来源时树／正文同步且不额外导航。
- 原有收录、加密设置、纠错、错误响应、取消、API 请求计数、配置／Markdown 原文保护、漫游防重复计次及本机 WebView2 阅读检查继续通过。

已查看最终 `.artifacts/smoke/library-reader-warm.png`、`library-reader-mono.png`、`library-reader-pink.png`、`library-reader-minimum.png` 和 `library-reader-collapsed.png`：两栏清晰，导航一致，导入／刷新和较小窗口正文可用，动效结束后透明度恢复为 1。图像为当前机器 96 DPI 渲染，不代表全部显示器缩放配置。测试使用隔离目录、假 Key 和本机服务，未读写用户真实词库或 API 配置。

## 发布文件

`dist/兔词-统一风格版/兔词.exe`：142,272,100 字节。

SHA-256：`849FB96D73438B90DEF106125546ABD56DDAC8DE3C3FA4ED5C100F3CF7E66685`。

附带并嵌入第三方声明。iOS、server、同步协议与漫游权重没有改动。

## 参考来源澄清

Obsidian 官方 [obsidian-releases 仓库说明](https://github.com/obsidianmd/obsidian-releases#about-this-repo) 明确本体不是开源软件，仓库不含本体源码。之前的界面参照来自官方文档和公开界面资料，使用本项目 WPF 实现；本次按用户明确范围收敛为文件列表和 Markdown 展示。
