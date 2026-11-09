# 账号与跨端同步

Windows 1.3.0 与 `/tuci/` 手机 PWA 均需登录后使用，同一账号对应一份独立云端词库。Windows 自动创建账号私有的本机 Markdown 缓存，保留已有账号绑定目录；手机通过 IndexedDB 保留自己的离线副本和待发送队列。未登录、退出或在线会话失效时不开放主功能。已有未过期会话在网络不可达时可使用该账号缓存。用户可以在 iPhone Safari 分享菜单中添加到主屏幕；Safari 与主屏幕入口可能需要分别登录和配置 API。

## 账号

- `POST /v1/account/register`、`POST /v1/account/login` 请求 `{username,password,deviceName,browser}`。用户名 2–40 个中文、字母、数字或 `._-`，NFKC 和大小写归一化判重；密码 8–128 字符。
- Windows 得到 `{token,vaultId,userId,username,deviceId}`，只持久化当前 Windows 用户 DPAPI 加密后的 token。密码不持久化。
- 浏览器使用 `browser:true`，响应只有用户资料；令牌放入 `__Secure-tuci-session`，`Secure/HttpOnly/SameSite=Strict`，路径 `/tuci-sync/`，90 天。前端不读取该令牌。接口不开放跨域访问，并检查修改请求的 Origin 和 Sec-Fetch-Site。
- `GET /v1/account/me` 返回当前资料。`POST logout` 撤销当前设备；`POST password {currentPassword,newPassword}` 改密并撤销其他设备与未兑换配对码。
- 密码使用随机盐、600,000 轮 PBKDF2-HMAC-SHA256。登录按来源限流，同时按账号记录 15 分钟内的连续失败；会话令牌仅以 SHA-256 摘要入库。
- 注册携带显式旧 Bearer 时，可以认领尚无账号的旧词库。浏览器共享 cookie 不会被用作隐式认领依据。

## 隔离

所有笔记与状态查询均从认证设备取得 vaultId，不能从请求体指定目标用户。浏览器以 `userId` 分 IndexedDB、游标、outbox 和本地学习空间，退出会锁住界面，文件不混入下一账号。Windows 保留目录绑定标记，拒绝把另一账号关联到原账号目录。

浏览器同步请求必须携带 `X-Tuci-Vault` 表示界面当前词库；服务器仅将该值与已认证 vault 比较，不用它选择词库。这样即使另一个标签页切换共享 cookie，旧标签页也不能把原账号 outbox 写入新账号。客户端还通过跨标签消息锁屏，并保留未发送内容。

API 地址、模型名和 API Key 按各设备的账号 profile 在本地保存，Windows 使用当前系统用户 DPAPI 加密，切换账号先清空旧 Key；状态 DTO 只接受明确列出的学习字段。AI 请求直接发给用户所填服务商，浏览器服务商需要允许 CORS。清除网站数据会清除本机离线副本和 API 设置；已成功同步的数据仍在账号内。

## 笔记与学习身份

笔记继续使用 `/v1/operations`、`/v1/changes`，保持既有版本检查、冲突副本、删除保护与幂等重试。Windows catalog 的本地学习 ID 通过同步后的 `cloudNoteId/path` 映射到云 ID，并保留别名；远端改名和首次同内容去重不能让历史学习记录失去归属。

状态使用独立的 `POST /v1/state/operations` 与 `GET /v1/state/changes?after=n`。操作为 `{operationId,key,value,deleted}`，回执 `{key,seq}`，分页为 `{changes:[{seq,key,value,deleted}],cursor,hasMore}`，每页最多 128 项，单项 JSON 最多 16 KiB。状态游标与 Markdown 游标独立。

| key | value | 合并方式 |
| --- | --- | --- |
| `event/<exposureId>/shown` | noteId, at, day, weight, weightLevel | 首次写入固定 |
| `event/<exposureId>/completed` | noteId, at, day, remembered | 一个曝光只完成一次 |
| `legacy/<sourceDeviceId>/<noteId>/<day>` | noteId, day, remembered, skipped, shown, lastShown | 旧本地历史一次性基线 |
| `roaming/current` | noteId, exposureId, at, weight, weightLevel | 当前漫游位置 |
| `preference/theme` | name: pink / mono / warmNeutral | 最后提交值 |
| `reading/<SHA256 URL>` | url, progress, anchor, title? | 文章位置，URL 为 HTTPS |
| `note-reading/<noteId>` | noteId, progress, anchor, title? | 笔记位置 |

复习事件与旧历史不能覆盖或删除；不同设备独立完成的曝光累积，同一曝光重试只计一次。状态引用的云笔记必须已存在（含已删除历史），因此先同步 Markdown，再发送状态。客户端在本地落盘后再发送，并保留原 operationId 直到拉取与确认共同落盘。RoamingSession 在同一内部 gate 中导出和合并，不由后台线程直接覆写运行中的 JSON。

日期使用事件发生时本机的 `yyyy-MM-dd`，时间为带时区 ISO 8601；统计按记录日期汇总。阅读位置以段落锚点和 0–1 进度交换，像素偏移只用于本机。主题和位置属可覆盖偏好，同时离线修改同一位置时以服务端接收顺序为准。

## 运维与验证

当前数据仍使用兔词原有 SQLite，未复用 PostgreSQL 中其他业务数据库。部署前的备份、程序目录和 Nginx 配置保存在 `/root/tuci-account-backups/<时间>`。数据库升级后回退旧程序前需要核对 schema；不可直接用旧快照覆盖上线后新增记录。

后端测试 `server/tests/integration.py` 与 `server/tests/accounts.py` 启动真实进程和临时数据库，验证旧数据认领、两账号隔离、cookie/CSRF/多标签、重复复习、分页、改密、撤销、限流和在线备份恢复。Windows 与网页另有状态合并和离线队列测试；实际 iPhone Safari/主屏幕仍需要真机验证系统存储与交互行为。
