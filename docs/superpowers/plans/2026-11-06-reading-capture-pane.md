# 阅读点词修复与侧栏收录

用户本轮三个请求：修复无框模糊的左侧断层、改进关闭按钮、修复阅读翻译／收录鼠标操作并将收录放到可关闭的右侧预览。继续当前 Windows worktree，不变更统计及 Markdown 格式。

- [x] 排查真实 PointerDown 路由：Popup 子按钮逻辑路由到 owner，被当成外部点击而清空选词；仅放行 Popup 子树内事件，保留外点关闭／双击复制。
- [x] Blur 自身不裁切，ScrollViewer 两侧扩大 10 DIP 与正文内缩抵消，留出光晕且保持文字原位置。真实 RTB alpha 像素回归证明渐隐。
- [x] 共享 PaneCloseButton：细线 X、30 DIP 透明命中、主题轻量 hover／pressed，保留原生键盘与无障碍语义，替换漫游和新收录侧栏的关闭按钮。
- [x] 阅读右侧收录：MainViewModel.PrepareDraft 防止旧结果／忙碌输入被覆盖；点收录只打开并预填原句，在侧栏手动解析保存和阅读 Markdown，关闭／离页／切文取消侧栏请求，文章位置保留。
- [x] 回归与交付：Pointer 和侧栏状态单测、真实 CUA 鼠标点译文／收录／保存／X，隔离目录落盘核验，全部 Release/WPF 验证873/873与 Windows 发布包及交付哈希。

接口：ReadingPage.OpenCapturePreview(MainViewModel, ReadingWordSelection)、CloseCapturePreview、IsCapturePreviewOpen、CapturePreviewViewModel；WordCaptureRequested 保留供 MainWindow 接入。CaptureLinkRequested 复用现有本地笔记／网页路由。仅明确点击解析并保存才调用已有 AI 配置，不为点击收录自动请求模型。
