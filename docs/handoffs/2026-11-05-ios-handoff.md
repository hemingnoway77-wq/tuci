# 兔词 iPhone 版：Mac mini 端需求交接

交接日期：2026-11-05（Asia/Shanghai）。当前阶段：讨论手机端方向与安装发布方式，功能细节尚未确定。

## 给接手者的第一句话

用户已经有一台 Mac mini，想把「兔词」扩展到 iPhone。请先了解现有项目，再和用户讨论手机功能范围与技术选择；这份交接不是已批准的完整 iOS 开发规格，不应直接据此开始大规模实现。

## 已确认的用户意图

- 程序名称：兔词，原有用途是收集遇见的英文单词。
- 希望开发 iPhone 版本，手机上的功能会比当前版本更多，但用户明确说「功能细节我们稍后再讨论」。
- 用户对手机端的原话：「自带卡片单词库，不需要切换到 Obsidian 上进行查看」。已确认的含义是：单词卡片可以在兔词应用内部直接查看。
- 用户有 Mac mini，可用于后续准备开发环境；芯片、macOS 版本、Xcode 安装情况和可用 iPhone 型号尚未核实。
- 本轮用户请求是交接需求，方便在 Mac 端继续。

## 尚未确定，不能作为既定需求

- 「自带单词库」是否意味着预置一套单词内容，还是主要展示用户自己收集的单词。不要擅自选择其中一种含义。
- 卡片字段、浏览交互、搜索、背词或复习机制、发音、离线能力等具体功能。
- 是否继续提供 Obsidian 导入、导出或联动。用户要求不依赖切换到 Obsidian 查看，不等于要求删除全部 Obsidian 对接能力。
- 是否需要账号、服务端、跨设备同步，以及与 Windows 版如何共享数据。
- AI 配置方式、API 费用承担方式、是否沿用用户自配兼容接口。
- iOS 技术栈：Swift/SwiftUI、C# 移动端方案或其他路线都未选定。
- Apple 开发者会员是否已经开通、是否愿意付费、首阶段采用哪种安装渠道。
- 是否公开上架、是否给朋友测试、上架地区、收费或商业化方式。

这些是待讨论问题，不是待实现功能清单。下一轮选择影响最大的少数问题逐步讨论，不一次抛出全部问题。

## 仓库与当前基线

- 仓库：https://github.com/hemingnoway77-wq/tuci
- 主分支：`main`。
- 交接前的代码基线：[`8499971`](https://github.com/hemingnoway77-wq/tuci/commit/8499971933db2ca6f9027e075c3af66fe6c702da)，包含此前 22 条提交历史。
- 当前已实现的是 C# / .NET 10 / WPF 的 Windows 桌面版本，不能把现有 WPF 界面直接打包为 iPhone 应用。
- 现有能力、笔记格式、API 行为与 Windows 构建方式请直接阅读 [README](../../README.md)，不在交接里复制整份说明。
- 原有界面与品牌更新记录：[书页界面验收](../verification/2026-11-04-literary-redesign.md)、[图标与字体验收](../verification/2026-11-04-icon-and-legibility.md)。

## 接手时重点阅读的代码

| 位置 | 用途与迁移边界 |
| --- | --- |
| [src/VocabCollector.Core](../../src/VocabCollector.Core) | `net10.0` 核心库。可评估复用业务规则或将其作为行为参考，不代表整个库已验证适用于 iOS。 |
| [Models.cs](../../src/VocabCollector.Core/Models.cs)、[CaptureContracts.cs](../../src/VocabCollector.Core/CaptureContracts.cs) | 当前单词分析数据、收录结果和服务接口。`VaultOptions`、笔记位置与存储接口仍耦合桌面 Markdown / Obsidian 工作流，需要对照手机端内置词库重新设计。 |
| [Ai](../../src/VocabCollector.Core/Ai) | 兼容接口调用、返回格式校验、拼写处理与安全诊断。保留已有校验含义，不把模型结果直接当作可信存储内容。 |
| [Storage](../../src/VocabCollector.Core/Storage)、[Markdown](../../src/VocabCollector.Core/Markdown) | 当前文件词库、去重、关联与 Markdown 输出，可作为现有数据兼容参考；不应直接等同于手机端数据库方案。 |
| [src/VocabCollector.Desktop](../../src/VocabCollector.Desktop) | Windows 专用界面和系统集成，不是可直接迁移的 iOS UI。 |
| [WindowsSettingsStore.cs](../../src/VocabCollector.Desktop/WindowsSettingsStore.cs)、[WindowsNavigator.cs](../../src/VocabCollector.Desktop/WindowsNavigator.cs) | Windows DPAPI 加密与桌面跳转；手机端需要平台适配。iOS 凭据存储可评估 Keychain，尚未实施。 |
| [Assets](../../src/VocabCollector.Desktop/Assets) | 品牌资源。用户最终确认的母版为 `app-icon-source.png`：黑色兔耳放大镜、背景与镜圈内部真实透明。iOS 出包图标规格应在 Mac 端按当前苹果规范准备。 |

保留 Windows 版现有功能。未与用户确认前，不为适配 iOS 而大范围重写或删除当前桌面实现。

## 已讨论的安装与发布路径

已经向用户说明：个人开发者可以让他人使用应用，也可以审核上架 App Store，不必先成立公司。

| 路径 | 已核实的规则 | 用户决策状态 |
| --- | --- | --- |
| 免费 Apple 账号 + Xcode 真机安装 | Personal Team 签名描述文件 7 天到期，需要重新构建安装。 | 尚未选定。 |
| Apple Developer Program + TestFlight | 付费会员可邀请测试；每个构建最多测试 90 天，外部测试需相应审核。 | 尚未选定。 |
| Apple Developer Program + App Store | 个人可注册，会员目前为 99 美元/年或当地价格；正式发布需提交审核。面向中国大陆时还需核对适用的 APP 备案等地区要求。 | 尚未决定公开上架、地区或付费会员。 |

此前建议的节奏是「先真机自用 → 需要时邀请测试 → 成熟后考虑上架」，这是建议，不是用户已经确认的发布计划。无需为了本轮交接注册账号、购买会员或提交审核。

苹果官方参考（准备实际操作时再次核实）：

- [免费账号与 Personal Team 限制](https://developer.apple.com/help/account/basics/about-your-developer-account)
- [个人开发者注册与费用](https://developer.apple.com/help/account/membership/program-enrollment)
- [TestFlight](https://developer.apple.com/help/app-store-connect/test-a-beta-version/testflight-overview/)
- [Xcode 系统要求](https://developer.apple.com/xcode/system-requirements/)
- [App Store 审核流程](https://developer.apple.com/help/app-store-connect/manage-submissions-to-app-review/overview-of-submitting-for-review)
- [大陆区相关信息要求](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information)

## 验证状态与 Mac 端注意事项

- 代码基线在 Windows 上已执行 `scripts/verify.ps1`：571 项单元测试通过、Release 构建 0 警告 0 错误、真实 WPF 冒烟检查 PASS。详细证据见现有验收文档。
- 当前整个测试项目与 WPF 冒烟项目都面向 `net10.0-windows` 并依赖桌面项目；不能在 Mac 上把原有整套验证命令照搬执行，也不能声称这些测试已经覆盖 iOS。
- 当前仓库不含真实 API Key、个人配置、用户词库或 Windows 发布 exe；`dist`、`bin`、`obj`、`.artifacts` 被忽略。
- 用户更偏好直接推进已经授权的工作，避免反复确认同一件事。涉及新功能或技术选型时清楚区分事实、建议与待确认项。

## Mac mini 端下一步

1. 获取仓库并先阅读本交接与 README：

   ```sh
   git clone https://github.com/hemingnoway77-wq/tuci.git
   cd tuci
   ```

   已有克隆时先查看分支和未提交修改，再同步 `main`，不要覆盖 Mac 端已有工作。

2. 核实 Mac mini 芯片、macOS、Xcode、可用 iPhone 和 Apple 账号会员状态，判断现有环境能否真机测试。
3. 先和用户澄清「应用内卡片式单词库」的具体使用过程与「自带」的含义，再逐步确认首版范围。
4. 根据确认后的范围讨论技术方案与可复用部分，用户确认后再编写实现计划和开发。

建议下一会话使用的技能（若 Mac 端可用）：`superpowers:brainstorming` 用于需求讨论；需求确认后再使用 `writing-plans`。真机验证阶段可考虑 `ios-qa`，但先检查其前提，不把它当作已安装或已授权执行的步骤。

## 可直接发送给 Mac 端助手的开场提示

> 请先阅读仓库 `docs/handoffs/2026-11-05-ios-handoff.md` 和 README。我想为「兔词」开发 iPhone 版本，已有 Mac mini。手机端要能在应用内直接查看卡片式单词库，不依赖切到 Obsidian；更多功能还没有确定。请先了解项目与开发环境，再和我逐步讨论需求、首版范围和技术方案，暂时不要直接开始全面开发。
