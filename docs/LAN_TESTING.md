# 两台 Windows 电脑的局域网联调

更新：2026-11-03。本机 A 已启用局域网测试配置，保留原有账号、数据库、认证签名密钥和客户端本地功能。B 运行完整 Windows 客户端及其本地服务，通过 HTTPS 调用 A 的 API；数据库直连供查看数据使用。

## 当前地址

| 用途 | 地址或配置 |
| --- | --- |
| A 的 Wi-Fi IPv4 | `192.168.1.5`，网卡 `WLAN` |
| 允许访问的网段 | `192.168.1.0/24` |
| B 使用的后端 API | `https://192.168.1.5:5081/` |
| A 原有本机 API | `http://localhost:5080/` |
| PostgreSQL | `192.168.1.5:55432`；数据库/用户均为 `postgres`；测试口令 `123456` |
| 证书指纹 SHA-1 | `6D494A5BC5BB2B06F2C2F914F3CE6DB69E40370C` |
| 证书有效期截止 | `2027-11-03` |

这些是当前本机测试地址。A 的局域网 IP 改变后，需要在 A 重新执行启用脚本，再将新配置包交给 B。应让两台电脑接入可互访的同一网络；访客 Wi-Fi 或 AP 隔离可能阻止设备间通信。

## A 电脑

配置已实际应用，当前数据库及 API 正在运行。以后从客户端项目启动同一实例即可，启动脚本会读取持久化的 `lan-settings.json`：

```powershell
& 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01\scripts\Start-LocalLoginServer.ps1'
```

如需重新应用局域网配置，在管理员 PowerShell 中执行：

```powershell
Set-Location E:\FocusTime_Server
.\scripts\Enable-LanDevelopment.ps1 -InterfaceAlias WLAN
```

脚本会核对当前私有 IPv4 子网，创建或复用包含实际 IP 的 HTTPS 证书，配置 PostgreSQL SCRAM 认证和防火墙，重启原测试实例并生成 B 的配置包。首次信任证书时，Windows 可能显示“安全警告”，需由用户核对指纹并手动确认。

日常停止仍使用客户端原脚本，数据继续保留：

```powershell
& 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01\scripts\Stop-LocalLoginServer.ps1'
```

## B 电脑

1. 将 A 上的 `E:\FocusTime_Server\.artifacts\FocusTime-LAN-client.zip` 复制到 B 并解压。
2. 在解压目录打开 PowerShell，执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Configure-LanClient.ps1
```

3. 如果 Windows 显示证书安全确认，核对上表指纹后手动允许。脚本只安装本次生成证书的公钥，保留客户端正常的 HTTPS 证书校验。
4. 看到 `HTTPS certificate validation and API readiness passed.`，说明从 B 到 A 的 HTTPS 及数据库就绪检查通过。
5. 关闭旧客户端和启动器，再启动完整客户端。源码方式可在运行配置脚本的同一个 PowerShell 中运行客户端的 `scripts/Start-FocusApp.ps1`；安装版可从同一 PowerShell 启动其正常启动程序，让新进程继承设置。B 不需要启动后端或数据库。

脚本将 `FOCUSTIME_API_BASE_URL=https://192.168.1.5:5081/` 保存到当前 Windows 用户环境，并设置当前 PowerShell 环境。已运行进程或旧终端不会自动更新自己的环境；遇到地址未更新时，重开启动器或从上述 PowerShell 直接启动。

只检查已配置的连接、不改环境变量或证书：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Configure-LanClient.ps1 -VerifyOnly
```

当前开发邮件模式不会向真实邮箱发信。B 注册或找回密码时，验证码保存在 A 的客户端目录 `.codex-build/local-login-server/api/.artifacts/mail/`；可在 A 的客户端项目运行 `scripts/Get-LocalLoginCode.ps1 -Email 测试邮箱` 读取对应验证码。

## B 直连数据库查看账号

B 装有 PostgreSQL 客户端工具时执行：

```powershell
psql -h 192.168.1.5 -p 55432 -U postgres -d postgres -W
```

输入 `123456`，然后查询：

```sql
SELECT "Id", "Email", "EmailConfirmed", "CreatedAtUtc"
FROM identity.users ORDER BY "CreatedAtUtc" DESC;
```

Windows 客户端自身只需要 API 地址及 FocusTime 登录账号，不需要数据库管理员口令。这里开放的是当前私有网段的测试实例，数据库连接沿用 SCRAM 密码认证；数据库通信未配置 TLS。

## 实际生效范围

- API：`localhost:5080` 保持本机 HTTP；`192.168.1.5:5081` 提供 HTTPS。
- PostgreSQL：监听 `127.0.0.1,192.168.1.5`，端口 `55432`。
- `pg_hba.conf` 新增带标记的管理块，只允许 `192.168.1.0/24` 使用 `postgres` 用户访问 `postgres` 数据库，认证方式为 `scram-sha-256`。
- 防火墙规则 `FocusTime-LAN-HTTPS` 和 `FocusTime-LAN-PostgreSQL` 限定本机地址、对应程序、`WLAN` 网卡、对应端口及远端 `192.168.1.0/24`。保留 Windows 防火墙及原网络类别。
- A 的私钥留在当前 Windows 用户证书存储，设置为不可导出；B 的 ZIP 只有 `.cer` 公钥证书、JSON 地址、配置脚本及说明。
- 原账号和云端数据保留；客户端 C# 网络地址校验不变，远程连接仍要求 HTTPS。

客户端启动脚本的改动已导出到 `integrations/windowBlocked01/lan-startup.patch`；当前客户端目录已经应用，请勿重复应用。

## 恢复为仅本机访问

在 A 的管理员 PowerShell 执行：

```powershell
Set-Location E:\FocusTime_Server
.\scripts\Disable-LanDevelopment.ps1
```

该操作移除本次管理的 HBA 条目、防火墙规则及 LAN 启动设置，重启为原本的 localhost 模式，保留账号、专注记录和证书。若 B 以后恢复为本机测试服务器，需同时将 B 的 `FOCUSTIME_API_BASE_URL` 改回对应本机地址；已归属旧 API 地址的上传队列仍按原地址隔离。

## 已验证与待验证

已在 A 实际验证：正常证书校验的 LAN HTTPS、Windows PowerShell 5.1 运行配置包的只读检查、LAN IP 数据库登录、错误口令拒绝、HBA 无解析错误、防火墙范围、2 个原账号保留、公钥包不含私钥、关闭/重新启用及重复启动复用现有进程。

尚未远程操作 B。B 仍需执行配置包和实际登录/上传/下载操作，验证 Wi-Fi 路由、设备隔离及 B 自己的系统环境。第五阶段双向同步已经实现。

2026-11-04 起，新版客户端的 Service 已内置当前服务器的会员验证公钥，B 配套更新 Desktop 与本地 Service 后，登录即可自动验证账号会员，无需额外下载或安装会员公钥。更新包为 `.artifacts/FocusTime-Client-AccountVip.zip`，配置包仍为 `.artifacts/FocusTime-LAN-client.zip`。旧配置包保留 `entitlement-trust.json` 和 `Install-MembershipTrust.ps1`，仅供旧客户端或管理员自定义信任使用。两类公钥信任的用途及覆盖规则见 [MEMBERSHIP.md](MEMBERSHIP.md)。局域网 HTTPS 自签名证书仍由前述连接配置处理。

技术参考：[Kestrel 端点与证书配置](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)、[Windows 自签名证书](https://learn.microsoft.com/en-us/powershell/module/pki/new-selfsignedcertificate?view=windowsserver2025-ps)、[PostgreSQL HBA 认证](https://www.postgresql.org/docs/18/auth-pg-hba-conf.html)。
