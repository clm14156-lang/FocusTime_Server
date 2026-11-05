# 第六阶段：会员与离线权益

实现日期：2026-11-03。后端、Windows 客户端及本地 Service 已接通；验收证据见 [STAGE6_VALIDATION.md](STAGE6_VALIDATION.md)。支付尚未实现。

## 授权来源

会员资格保存在 PostgreSQL 的 `membership.grants`、`membership.states`、`membership.audit` 中。普通账号没有有效授权，年费授权有起止日期，永久授权没有到期日期。有效永久授权优先于有效年费授权；多个年费授权选择最晚到期的一项。到期、未来生效、已撤销授权不会产生权益。

服务器返回 `forced_mode` 与 `automatic_blocking` 两项权益。现有主题、统计等界面沿用现有会员展示策略，本阶段没有重写这些功能。普通专注、专注历史保存与云端同步不要求会员。

账号资料中的 `membership` 字符串保留兼容性。正式客户端以本地 Service 验证后的签名凭证决定权限，缓存的账号资料和 UI 布尔值不能授予执行权限。旧 `.artifacts/test-memberships.json` 不再加载；本机原测试 VIP 已一次性导入真实授权并留下审计记录。

## 本机管理命令

发放、续期及撤销仅通过有数据库权限的服务器管理命令进行，没有公开的管理 HTTP 接口。运行前配置目标数据库的 `ConnectionStrings__Database`；本机测试库的连接及账号 UUID 查询见 [LOCAL_TEST_DATABASE.md](LOCAL_TEST_DATABASE.md)。下列命令使用项目的 .NET 10 环境，日期以 ISO 8601 UTC 输入。

```powershell
$userId = '填入已验证账号的UUID'
$operationId = [Guid]::NewGuid().ToString()
# 保存 operationId，重试同一操作时复用它和所有原始参数。
dotnet src/FocusTime.Api/bin/Release/net10.0/FocusTime.Api.dll `
  --membership-admin grant --user-id $userId --operation-id $operationId `
  --kind annual --starts-at '2026-11-03T00:00:00Z' --expires-at '2027-11-03T00:00:00Z' `
  --reason '人工发放测试年费' --actor 'local-admin'
```

永久授权使用 `--kind lifetime`，不传 `--expires-at`。续期创建新的 grant 和 operationId；已存在授权不会直接覆盖。撤销全部授权：

```powershell
dotnet src/FocusTime.Api/bin/Release/net10.0/FocusTime.Api.dll `
  --membership-admin revoke --user-id $userId --operation-id '填入本次操作的新UUID' `
  --reason '撤销测试会员' --actor 'local-admin'
```

只撤销一项时加 `--grant-id '授权UUID'`。同一用户、同一 operationId、相同参数只生效一次；不同参数复用 ID 会报错。每次新操作增加用户版本，记录操作者、原因、请求摘要和时间。授权表外键关联 Identity 用户，账号事务锁与登录、远程退出及同步共用，避免并发重复发放和撤销竞态。

查看当前有效授权：

```sql
SELECT u."Email", g."Id", g."Kind", g."StartsAtUtc", g."ExpiresAtUtc", s."Version"
FROM membership.grants g
JOIN identity.users u ON u."Id" = g."UserId"
JOIN membership.states s ON s."UserId" = g."UserId"
WHERE g."RevokedAtUtc" IS NULL AND g."StartsAtUtc" <= now()
  AND (g."ExpiresAtUtc" IS NULL OR g."ExpiresAtUtc" > now());
```

## 接口与签名

| 接口 | 用途 |
| --- | --- |
| `GET /api/v1/me/membership` | 认证后获取当前类型、起止日期、版本、权益和服务器时间 |
| `POST /api/v1/me/entitlement-lease` | 认证后签发绑定当前账号、设备和会话的离线凭证 |
| `GET /api/v1/entitlement-keys` | 公开 RSA 验证公钥；没有私钥或发放能力 |

租约请求为 `{apiScope, machineBinding, nonce}`，三个值均为 64 位小写十六进制字符串。`apiScope` 是客户端完整 BaseAddress.AbsoluteUri 的 UTF-8 SHA-256；尾部 `/` 必须与客户端设置一致。账号、设备、会话、安装 ID 由服务器从认证会话确定，不能在请求中指定其他用户。

响应为 `{token, expiresAtUtc, keyId}`。token 为 RS256 JWS，包含固定 issuer `FocusTime_Server`、audience `focusapp-service`、版本 1、用户/设备/会话/安装 ID、API 范围、本机绑定、一次性随机挑战、会员类型、权益、会员版本和签发/到期时间。每次签发在账号锁内重新核对访问凭据、安全戳、会话撤销与到期情况。

开发环境首次启动在 API 内容目录生成 `.artifacts/membership-signing-key.pem`（RSA 3072），重启继续复用。生产及 Staging 必须配置持久的 `Membership__SigningKeyPath` 或通过秘密管理提供 `Membership__SigningKeyPem`；没有有效私钥会启动失败。私钥不得进入客户端、Git、公开配置包或日志。备份时保留认证密钥和签名私钥，两者用途独立。

公钥 ID 是 SPKI 二进制的 SHA-256。轮换时先发布同时内置新旧公钥的客户端，或由管理员更新信任覆盖列表，再更换服务器私钥并重启；超过旧凭证窗口后删除旧公钥。管理员覆盖列表中的公钥删除立即影响下一次状态检查或受限操作。客户端不会因为 HTTP 返回一个新公钥就自动信任它。

## 登录后自动验证会员

2026-11-04 起，客户端 Service 随程序内置当前服务器的公开验证公钥，覆盖 `http://localhost:5080/` 和 `https://192.168.1.5:5081/`。用户登录后自动获取并验证账号会员凭证，不需要额外下载会员证书或运行公钥安装脚本。Desktop 与 Service 应一起更新，旧客户端仍需升级才能使用这个默认行为。内置的是验证公钥，不是会员资格；有效会员、普通账号、到期和撤销仍由服务器判断。

默认公钥文件为客户端源码 `src/FocusApp.Service/Config/MembershipTrust.json`，编译为 Service 程序集资源；运行时不会从安装目录加载可写 JSON，也不从网络自动接受任意新公钥。部署到其他服务器或更换 API 地址时，应在发布前更新公钥及其 API 范围。客户端改动见 `integrations/windowBlocked01/automatic-membership-trust.patch`。

局域网 HTTPS 自签名证书用于服务器连接，会员公钥用于验证权益凭证；它们用途不同。此前已通过局域网 HTTPS 配置的电脑更新客户端后，无需再安装会员公钥。

## 管理员信任覆盖（可选）

`%ProgramData%\FocusTime.ServerTrust` 目录不存在时直接使用程序内置信任。如果管理员创建了该目录，则其中 `entitlement-trust.json` 完整覆盖内置信任。目录及文件必须由 Administrators 或 SYSTEM 拥有，只允许这两者写入，普通用户只读；服务检查 ACL、所有者及重解析点。空公钥列表可禁用全部信任；目录存在但文件缺失、格式无效或权限不安全时均不回退内置公钥。管理员移除整个覆盖目录才恢复默认内置信任。IPC 没有“添加公钥”入口。

以下导出及安装命令仅用于自定义服务器、轮换公钥或维护旧客户端，正常用户无需执行。

在 A 电脑的 PowerShell 7.2+ 导出公开公钥：

```powershell
./scripts/Export-MembershipTrust.ps1 -ServerBaseUrl 'http://localhost:5080/' `
  -AllowedBaseUrls @('http://localhost:5080/', 'https://192.168.1.5:5081/') `
  -OutputPath '.artifacts/entitlement-trust.json'
```

通过可信渠道核对导出指纹，再在需要运行客户端的电脑上以管理员 Windows PowerShell 执行：

```powershell
./Install-MembershipTrust.ps1 -TrustFile ./entitlement-trust.json
```

安装脚本兼容 Windows PowerShell 5.1。公钥是公开数据，安装不导入 Windows 根证书。局域网 HTTPS 证书仍按 [LAN_TESTING.md](LAN_TESTING.md) 配置。本机已有 localhost 与 LAN 两个范围的管理员覆盖；`.artifacts/FocusTime-LAN-client.zip` 保留公钥及安装脚本以兼容旧客户端，该配置包本身不含应用二进制。

## 离线及运行策略

- 离线窗口最多 72 小时，并取会员到期时间和会话绝对到期时间的更早值。访问令牌的短期到期不直接截断离线租约。
- Desktop 每 60 秒尝试更新，与登录/账号切换事件配合；HTTP 不可达时保留仍有效的已验证凭证。正常联网刷新通常一分钟内取得撤销结果；完全离线设备最长仍可使用剩余离线窗口。
- 本地 Service 发起随机挑战，有效期两分钟且只能消费一次。签名绑定机器 MachineGuid 与实际目标 Windows 用户身份的哈希，复制到其他电脑、用户、账号、会话或 API 范围均不能使用。
- SQLite v17 的 `membership_cache` 使用运行 Service 身份的 Windows DPAPI CurrentUser 加密整个凭证、可信时间及版本状态，不包含访问/刷新令牌或密码。服务账户改变、缓存损坏或无法持久化时要求重新联网验证。
- 新证明用服务器签发时间校准；之后综合单调计时、墙钟经过时间和最大已观察时间，计入休眠，回拨或撤销前跳不能延长凭证。重启检测到超过五分钟的墙钟回退会持久化“需要联网”状态，再重启也不能消除。服务器恢复到较旧会员版本同样会被拒绝，需要管理员处理版本或客户端重新注册缓存。
- 退出及切换账号清除会员证明；普通用户的签名证明会立即清除原有权益。签名错误、未知公钥、错误算法、重复字段、超长租约和异常权益都不会产生授权。
- 已开始的强制专注按原计划完成，不因会员到期、退出或撤销而提前结束。开始新强制专注、启用自动功能、执行新的自动计划仍需有效权益；没有因刷新租约延长正在进行的专注的入口。

本地 Service 在 `StartForcedFocus`、自动来源的 `StartNormalFocus`、自动访问控制启动、启用自动规则和相关设置入口检查权限。普通手动专注与手动屏蔽继续复用原逻辑。普通自动专注新增可选规则来源字段传到 Service，不重写倒计时或屏蔽执行器。

## 边界与部署

这是正常客户端与本地服务之间的权限检查，不是防管理员修改程序的 DRM。拥有机器管理员权限、替换二进制、恢复整机快照或掌握服务器私钥的操作者不在防护范围内。离线期间无法即时得知远程撤销，缩短窗口会降低这一延迟并减少离线可用时间。

当前开放数据库端口和测试口令仅延续此前局域网测试配置；生产客户端只连接 HTTPS API，生产数据库及授权写权限应受限。付款、订单、退款和支付回调在后续需求中独立接入，并通过已有幂等发放逻辑产生授权和审计记录。
