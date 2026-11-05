# 兔词 iPhone

在 iPhone 上收录生词、阅读每日文章，并离线浏览自己的 Markdown 词库。已配对的 Windows 和 iPhone 通过独立同步服务交换笔记。支持 iOS 17 及以上版本。

## 在 Mac 上构建

安装完整 Xcode 16 或更新版本，首次打开后安装 iOS 组件。选用与本机 macOS 和测试 iPhone 系统匹配的 Xcode。双击 `Tuci.xcodeproj`，选择 Tuci 方案和 iPhone 模拟器，点击运行。工程使用同目录的本地 Swift Package。

在仓库根目录也可以运行：

```sh
open ios/Tuci.xcodeproj
xcodebuild -project ios/Tuci.xcodeproj -scheme Tuci -configuration Debug \
  -destination 'generic/platform=iOS Simulator' CODE_SIGNING_ALLOWED=NO build
ios/scripts/test-core.sh
# 在已安装 iPhone 模拟器的 Xcode 环境验证手势和导航
ios/scripts/simulator-smoke.sh
```

若已安装 Xcode 而终端仍指向 Command Line Tools，可在该终端设置 `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer` 并导出，再运行上述命令。

## 安装到自己的 iPhone

1. 用数据线连接手机，在手机上信任这台 Mac，并按 Xcode 提示开启开发者模式。
2. 在 Xcode 的 Settings → Accounts 登录自己的 Apple 账号。在 Tuci target 的 Signing & Capabilities 中选择对应的 Team，启用自动签名。如果默认 Bundle Identifier 已被占用，在该 target 改用自己唯一的标识。
3. 选择已连接的 iPhone，点击 Run。安装在朋友的手机上时也需要适用的设备签名或测试分发方式。

## 开始学习

- 在“设置”填写 AI 接口地址、模型名和 API Key，保存后到“收录”输入单词，可附带原句。用户主动生成词卡时才调用 AI。设置中手动填写的 HTTP 地址可用于局域网模型，请根据地址提示确认其网络环境。凭证存储于系统钥匙串。
- 在“阅读”点击文章卡片打开原文。每日推荐读取公开订阅源；离线可重看上次缓存的标题和摘要。
- 在“词库”按目录或关键字找到笔记。单击正文中的关联词打开简卡，双击打开完整笔记。如果 Windows 词库在 Obsidian 下使用自定义目录，可在设置中填写其相对目录，以匹配历史笔记中的完整链接。
- 进入“漫游复习”，直接翻页或点“今天已记住”。已记住的词仍有机会抽到。漫游统计保存在手机上。
- 墨白、暖灰、玫红三套主题在设置里切换，字号跟随系统。

## 配对与同步

1. 管理员为每位用户生成单独的激活码。两端填写相同的 HTTPS 服务基址，保留管理员提供的路径前缀。
2. 如果已经有 Windows 词库，建议先在 Windows 版点击“词库同步”，选择现有**单词目录**并使用激活码绑定。首次同步后点击“生成配对码”。
3. 在 iPhone 设置的“词库同步”里填入地址和配对码，点击“连接我的词库”。反过来先绑定手机也可以。短期配对码仅限同一人的第二台设备，朋友使用自己的激活码。
4. 在任一端收录测试单词，另一端打开兔词后查看目录。设置中可立即同步，手机恢复前台也会自动同步。同路径不同内容的笔记会保留冲突副本。

服务端部署、激活码、备份和设备撤销见[运维说明](../server/README.md)。功能与验证边界见[开发验收记录](../docs/ios/verification.md)。
