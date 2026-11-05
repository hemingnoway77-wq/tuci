# iPhone 与同步交付验收

2026-11-05，开发分支 `feature/ios`。本轮在 Apple Silicon Mac 上验证 Swift 核心、UIKit 界面编译和真实跨端协议。下表按实际运行环境区分结果。

| 范围 | 已执行的验证 |
| --- | --- |
| 词库与漫游 | Swift Testing 覆盖手写内容保护、旧 Windows 日期与路径、同名链接、断电式重试、离线队列、分页原子提交、今日记住比例、翻过、跨日和长期未出现的旧词。 |
| 收录 | 使用模拟 AI 服务检查响应校验、拼写修正、流式完成条件、超时、主动取消、认证失败与明确拒绝流式时的单次回退。真实用户的 AI 接口留待设备设置后联调。 |
| 阅读 | 测试订阅源选择、解析和离线回退。Mac 上实际取得三篇推荐，并从独立的磁盘缓存读回同一天的推荐。 |
| Windows | 桌面工程和 Windows 测试程序集交叉编译通过。真实 Windows 同步引擎与 Kestrel/SQLite 的 20 项检查通过，覆盖外部编辑、并发冲突、不可访问路径、离线删除、损坏状态和备份恢复。Windows GUI 仍需在 Windows 上运行。 |
| 服务端 | 锁定依赖恢复和 Release 构建通过。真实 HTTP 测试覆盖一次性配对、猜测限流、设备撤销、两用户隔离、非法路径、分页、并发写入、幂等重试、备份与恢复。 |
| 端到端 | Windows 引擎上传示例，Swift 客户端离线创建同路径的不同笔记。即使服务已接收操作而客户端丢失回执，重新启动和重试后，双方仍各有原文及一份冲突副本。 |
| iPhone 界面 | 在本机 Mac Catalyst 的 UIKit 和 SwiftUI SDK 上对应用源码进行编译检查。需要用完整 Xcode 构建 iOS 目标，并在 iPhone 验收单击简卡、双击跳转、返回位置、辅助功能和大字号布局。 |

阿里云公网 HTTPS 的健康检查和证书校验已通过。使用隔离测试词库，在公网入口重复完成了 Windows 与 Swift 的双向同步、断线重试、冲突保留和跨用户隔离检查。运行中的 SQLite 备份通过完整性校验。

## 重现

在仓库根目录运行：

```sh
ios/scripts/test-core.sh
dotnet restore server/Tuci.Sync.csproj --locked-mode
python3 server/tests/integration.py
dotnet run --project tests/VocabCollector.Sync.RoundTrip -c Release -- server/bin/Release/net10.0/Tuci.Sync.dll
python3 server/tests/ios_roundtrip.py
```

最后一条命令使用临时 SQLite 和真实 Windows、Swift 引擎。Windows 图形界面验证沿用 `scripts/verify.ps1`。iPhone 安装和模拟器构建步骤见 [iOS 使用说明](../../ios/README.md)。

本轮运行机只有 Command Line Tools，没有 iPhoneOS SDK、iOS 模拟器与 Apple 设备签名。真机安装、触控、视觉与实际 AI 提供商测试应在交付签名包之前完成。云主机实际入口及激活资料单独交付给部署者。
