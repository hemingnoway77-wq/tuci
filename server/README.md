# 兔词同步服务

服务为每位用户保存一份私有词库，Windows 和 iPhone 分别上线时传送 Markdown 笔记。一个 ASP.NET Core 10 进程写入一个 SQLite 数据库，数据库包含设备令牌摘要、配对凭证摘要、笔记版本、冲突副本及重试回执。管理员可以读取数据库中的笔记正文，备份按私人词库资料管理。

## 本地开发

在仓库根目录运行下列命令。开发环境需要 .NET 10 SDK，集成测试还用到 Python 3。

```sh
dotnet restore server/Tuci.Sync.csproj --locked-mode
python3 server/tests/integration.py
export TUCI_DB="$PWD/.artifacts/sync-dev/tuci.sqlite3"
dotnet server/bin/Release/net10.0/Tuci.Sync.dll invite
dotnet server/bin/Release/net10.0/Tuci.Sync.dll --urls http://127.0.0.1:5080
```

测试会启动真实 HTTP 服务、使用临时数据库和模拟词库，覆盖多用户隔离、一次性配对、撤销、并发写入、分页、冲突、重复请求、备份和恢复。可通过环境变量 `DOTNET` 指定 SDK 可执行文件。

## 阿里云部署

将 `server/` 复制到已有 Docker 和 Compose 的 Linux 主机。以下部署示例适合一条专用域名和可供 Caddy 监听的 80、443 端口。用指向该主机的真实域名替换示例。安全组放行 80、443。管理 SSH 按主机原有策略访问。

```sh
cd server
export TUCI_HOST=sync.example.com
docker compose up -d --build
curl -fsS "https://$TUCI_HOST/health"
docker compose exec sync dotnet Tuci.Sync.dll invite
```

服务器启动后应得到 `{"ok":true}`。命令行与运维工具执行 Compose 命令时需要同一个 `TUCI_HOST` 值，可写入部署主机上 `server/.env`。数据库保存在 Compose 命名卷 `tuci_data`，服务端口仅在容器网络中使用。若已有 HTTPS 网关，请将 `sync` 服务接入该网关所在网络，通过网关把专用域名转发至 `sync:8080`，仅启动 `sync` 服务。数据库目录须使用持久本地磁盘，单实例运行。

## 建立与维护词库

1. 每位用户运行一次 `invite`，得到新的 `vaultId` 和有效期 24 小时的一次性激活码。管理员留存词库 ID，通过私密渠道把激活码交给对应用户。
2. 在任一设备的兔词设置填写 HTTPS 站点地址并兑换激活码。由已绑定设备生成十分钟有效的配对码，将另一台设备加入同一词库。每台设备只保留最近一次生成的待用配对码。
3. 管理员可列出某人的设备，撤销丢失设备，或为同一词库补发激活码。撤销设备会一并作废由它发出的未兑换配对码。

```sh
docker compose exec sync dotnet Tuci.Sync.dll devices VAULT_ID
docker compose exec sync dotnet Tuci.Sync.dll revoke DEVICE_ID
docker compose exec sync dotnet Tuci.Sync.dll invite VAULT_ID
```

## 备份与恢复

SQLite 在线备份会从运行中的数据库生成一致快照，同时包含 WAL 中已提交的内容。目标文件必须为新文件。为每次备份选择不同日期和时间，并把结果转移到独立存储。下例命令在 `server/` 运行。

```sh
umask 077
stamp=$(date -u +%Y%m%dT%H%M%SZ)
docker compose exec -T sync dotnet Tuci.Sync.dll backup "/data/$stamp.db"
docker compose cp "sync:/data/$stamp.db" "./$stamp.db"
sqlite3 "./$stamp.db" 'PRAGMA integrity_check;'
```

检查应返回 `ok`。移走并核验快照后，可移除卷中对应的临时备份文件。恢复到测试实例时，用新卷或新目录保存备份，设 `TUCI_DB` 指向恢复的数据库，再启动服务。快照保存着当时的设备授权，测试端口应限制在本机或隔离网络。

恢复正式实例时，先停止服务，将当前整个数据卷制作保留副本，然后用备份文件建立**新数据卷**，确保目标文件由容器的 `app` 用户可读写。将新卷挂载为 `/data`，将快照命名为 `tuci.sqlite3` 后再启动，使用测试设备检查笔记数与冲突记录。恢复旧快照可能让设备游标领先，此时服务返回 409。保存各端当前本地词库和待同步工作，通过重新关联和初始合并恢复差异。应以恢复前保存的设备撤销记录复核授权。

## API 约定

请阅读 [三端协议](../docs/ios/implementation.md)。`POST /v1/redeem` 返回设备 `token` 和该用户稳定的 `vaultId`。后续请求使用 `Authorization: Bearer TOKEN`。设备凭证的撤销立即对新请求生效。

- 路径相对于选定词库目录，统一使用正斜杠和 NFC。上限 1024 个 UTF-16 单元，每段最多 255 个 UTF-8 字节。仅接收 Markdown 文件，检查隐藏目录、Windows 保留名、非法字符和路径穿越。单条笔记正文最多 1 MiB UTF-8。
- 可选的 `regions` 是例句、词根、语义关系及词形区域的原始校验值字典，键为 `examples / roots / relations / forms`，值为 SHA-256 十六进制。原始记录为空时客户端应保留人工笔记的编辑保护。
- 每个词库的 `seq` 递增，返回页最多 64 条变更，合计正文最多 2 MiB。读取至 `hasMore=false`。空列表返回原游标。相同版本可能随冲突处理再次出现在变更流中。
- 同路径同内容的首次上传会返回已有 ID。冲突写入创建可辨识的独立副本，回复包含服务器分配的 ID、路径、版本以及 `conflict=true`。过时的删除保留更新后的原文。提交后读取增量，能取回两个版本。
- 操作 ID 在单个词库内唯一。相同请求重试返回最初的回执。有相同 ID 但内容不同的请求返回 409。正文与回执一起在 SQLite 事务内提交。
- 401 表示授权失效，400 表示请求内容无效，409 表示操作标识、游标或本地版本需要核对，429 表示尝试次数过多，稍后重试。通过共享代理的设备默认共享兑换请求限额。对于同机 nginx 和只监听回环地址的服务，可设置 TUCI_TRUST_LOOPBACK_PROXY=1，同时在专用 location 中用 proxy_set_header X-Forwarded-For $remote_addr; 覆写该请求头。服务只接受一跳、来自回环地址的转发信息，以实际客户端 IP 限制兑换尝试。
