# iPhone 实施计划

以已确认的 [设计](design.md) 为验收依据。iOS 17+ 使用 SwiftUI 与 Foundation，Windows 接入现有 WPF 应用，服务端采用 ASP.NET Core 和 SQLite。

## 共用协议 v1

JSON 属性使用 camelCase，标识符为字符串。不在客户端指定词库 ID。除兑换和健康检查外都使用 Authorization Bearer 设备令牌。

- POST /v1/redeem 请求 {code,deviceName}，返回 {token,vaultId}。在服务端命令行生成首个激活码。已绑定设备可 POST /v1/pair 取得 {code}。
- GET /v1/changes?after=0 返回 {changes:[{seq,noteId,version,path,markdown,deleted,regions}],cursor,hasMore}。游标仅在整页安全落地后提交。
- POST /v1/operations 请求 {operationId,noteId,baseVersion,path,markdown,deleted,regions}。返回 {noteId,version,path,conflict}。创建的 baseVersion 为 0。服务器对同路径同内容合并，冲突时保留独立副本。客户端用后续拉取获取权威状态。无内容的删除使用空字符串。每人独立词库。请求重试复用 operationId。可选 regions 为四类受管段落的 SHA-256 校验值字典；无登记的普通笔记保持原样。

## 分工与检查

- [x] server/ 提供事务写入、隔离、激活与配对、令牌撤销、部署与备份。用两用户和多设备覆盖并发冲突、重试、非法路径与离线修改。
- [x] src/ 让 Windows 用户绑定服务与现有词库，程序自动扫描、上传、应用更改并报告状态。验证冲突、移动、外部修改、断网和目录访问失败。
- [x] ios/ 提供 Swift Package 可测试核心、Xcode 应用、收录与每日阅读、本地词库、笔记手势、漫游和同步。验证旧数据、重复词、权重、首次双向合并、重试与跨用户切换。
- [x] 使用真实 Windows 和 Swift 同步引擎跨进程联调，验证结果见[验收记录](verification.md)。
- [ ] 在完整 Xcode 和 iPhone 上完成签名、安装、手势和视觉验收；在 Windows 实机运行 WPF 界面测试。
