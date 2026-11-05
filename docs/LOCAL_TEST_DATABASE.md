# 本机测试数据库与账号查询

更新：2026-11-03。本文描述当前 Windows 客户端实际连接的持久测试实例；它使用 55432 端口，不是后端配置示例中的 5432。

## 当前连接

| 项目 | 值 |
| --- | --- |
| 主机 | `127.0.0.1` |
| 端口 | `55432` |
| 数据库 | `postgres` |
| 数据库用户 | `postgres` |
| 本机测试口令 | `123456` |
| API | `http://localhost:5080` |
| 局域网 PostgreSQL | `192.168.1.5:55432` |
| 局域网 HTTPS API | `https://192.168.1.5:5081/` |

按用户要求，该测试实例的数据库口令已更新为 `123456`，并同步保存到现有 DPAPI 凭据文件。后续已开启 `WLAN` 当前网段 `192.168.1.0/24` 的局域网访问，完整配置及 B 电脑操作见 [局域网联调](LAN_TESTING.md)。这里的口令用于 PostgreSQL 连接，客户端账号仍使用各自注册时设置的密码。

后端根目录已忽略的 `.env` 同时设置了 `POSTGRES_PASSWORD=123456`，供以后使用 Docker Compose 时读取；当前已运行的客户端测试实例仍由原启动脚本及 DPAPI 凭据管理。自动化集成测试继续使用各自随机口令，不修改为固定口令。

第六阶段后端已备份升级：升级时有 4 个账号、7 个会话，原开发测试 VIP 已转为正式数据库会员授权，均保持不变。当前会员查询与发放方法见 [会员说明](MEMBERSHIP.md)；下面“口令更新时”的 2 账号计数是更早的历史结果。

## 查询账号

在 PowerShell 中执行：

```powershell
Set-Location E:\FocusTime_Server
& .\.tools\pgsql\bin\psql.exe -h 127.0.0.1 -p 55432 -U postgres -d postgres -W
```

输入 `123456`，连接后执行：

```sql
SELECT "Id", "Email", "EmailConfirmed",
       "CreatedAtUtc" AT TIME ZONE 'Asia/Shanghai' AS "RegisteredAtShanghai"
FROM identity.users
ORDER BY "CreatedAtUtc" DESC;

SELECT COUNT(*) AS "RegisteredAccounts" FROM identity.users;
```

使用 `\q` 退出。账号表不是客户端本地 SQLite 表，删除本地客户端文件不会清除该服务器的注册账号。

## 启动、停止与数据

沿用客户端已有脚本：

```powershell
Set-Location C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01
.\scripts\Start-LocalLoginServer.ps1
# 需要停止时执行：
.\scripts\Stop-LocalLoginServer.ps1
```

数据目录为客户端下的 `.codex-build/local-login-server/postgres/`；口令保存在同级 `database-password.dpapi`，使用当前 Windows 用户的 DPAPI 保护。更新口令时保留了原加密凭据的备份。启动脚本会读取更新后的凭据；本次没有修改客户端代码、注册账号、专注记录或认证签名密钥。

## 口令更新时的验证

- 使用新口令建立数据库连接成功。
- 错误口令连接被拒绝；持久化 DPAPI 凭据与新口令一致。
- 沿用原脚本重启原数据库及 API 后，就绪检查返回 HTTP 200。
- 重复运行原启动脚本复用现有 API，未另建实例；55432 与 5080 均只监听回环地址。
- 更新前后均有 2 个注册账号，云端专注记录均为 0 条。
- 本次临时新建于后端 `.artifacts/local-development/postgres/` 的空实例已经停止，当前客户端仍连接上述原实例。

验证摘要保存在 Git 忽略的 `.artifacts/local-password-validation.json`，不包含密码散列或登录令牌。

上述仅本机监听的检查为口令更新时的历史结果；当前新增局域网监听的验证及范围以 [局域网联调](LAN_TESTING.md) 为准。
