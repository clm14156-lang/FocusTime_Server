# 第三阶段：设备与会话管理

更新日期：2026-11-03。复用第二阶段 Identity、设备登记、会话与刷新令牌，新增设备列表和远程退出。客户端仍是独立项目，通过 HTTP 通信。

## 可调用接口

所有接口需要有效 Bearer 访问令牌，响应禁止缓存。用户 ID 和当前会话 ID 来自认证结果，客户端不能指定资源所有者。

| 方法 | 路径 | 成功结果 |
| --- | --- | --- |
| GET | `/api/v1/me/devices` | 200，当前账号的有效设备及会话数组 |
| DELETE | `/api/v1/me/sessions/{sessionId}` | 204，退出指定会话 |
| DELETE | `/api/v1/me/devices/{deviceId}/sessions` | 204，退出该设备全部会话 |
| POST | `/api/v1/auth/logout-all` | 204，退出该账号全部会话，包括调用方 |

设备包含 `id, name, operatingSystem, clientVersion, firstSeenAtUtc, lastSeenAtUtc, isCurrentDevice, sessions`。会话包含 `id, createdAtUtc, lastSeenAtUtc, expiresAtUtc, isCurrentSession`。不返回原始令牌、令牌摘要、安全戳、其他用户资料或安装标识。

设备列表只包含尚未撤销、未超过绝对期限且安全戳有效的会话。访问令牌短期过期但仍能刷新的会话仍属于已登录状态。没有有效会话的设备退出列表，历史设备与会话行保留。

当前设备排在前面，其余按最近活动时间和 ID 确定排序。同一安装可以有多个会话：退出一个会话不影响同设备其他会话；退出设备覆盖其全部会话。已有的当前会话退出接口 `/api/v1/auth/logout` 保持可用。

对属于自己的已撤销设备/会话重复退出，仍返回 204；不存在或属于其他用户的 ID 统一返回 404。未登录、过期或已撤销的调用方返回 401，即使知道目标 ID 也不能操作。

本版新登录最多允许同账号 100 个有效会话、单设备 10 个，达到上限返回 409 / `session_limit_reached`。需要先退出已有会话，不会静默挤掉其他设备。设备列表返回完整集合而非分页，新的登录受上述上限约束；从旧版本升级时保留超过上限的已有会话，需通过列表主动退出，不会截断或删除它们。同步记录列表仍按其独立游标协议设计。

## 服务端实现

- `Modules/Sessions/DeviceSessionsController.cs`：认证及 HTTP 入口。
- `Modules/Sessions/DeviceSessionService.cs`：按账号查询、退出会话/设备/全部设备。
- `Modules/Sessions/AccountTransactions.cs`：与注册、登录、刷新、密码重置共用规范化邮箱的事务锁。
- `SessionAuthenticationHandler`：每次请求检查服务端撤销、到期和安全戳；普通有效请求的活动时间写入按一分钟节流。
- 登录和刷新令牌时即时记录活动时间，不把客户端时间作为服务器活动时间。

设备管理先获得账号事务锁，再重新检查调用方会话，防止请求完成初次认证后已被撤销仍能执行管理操作。远程退出和刷新采用同一锁：刷新先完成时，其新令牌也会被随后退出撤销；退出先完成时，刷新被拒绝，不能恢复旧会话。

撤销提交后，该设备下一次新 API 请求被拒绝；已经进入处理的请求可能完成。离线设备需要联网才能获知撤销，不宣称实时推送。后续使用密码重新登录可建立新会话，安装标识不变时仍归入原设备。

第三阶段没有物理删除用户、设备或专注记录。会话撤销只更新 Identity 会话表的撤销时间；客户端账号失效处理不调用专注记录删除或结束专注的方法。

## 数据库迁移

新增 `20261103031803_DeviceSessionActivity`，为 `identity.sessions` 添加非空 `LastSeenAtUtc`，旧行从 `CreatedAtUtc` 回填，不重建表或撤销原会话。继续使用 README 的 `--migrate` 流程，常规启动不自动迁移。

安装标识用于区分安装实例，不是硬件指纹或认证凭证。同一物理机器的不同 Windows 用户/安装可以显示为不同设备，相同设备名称不能作为退出目标的安全依据。

## 客户端接入

`MainWindowViewModel` 创建一个共享的 `CloudAccountClient`，由原登录弹窗和设备管理弹窗共同使用，避免各自刷新同一凭证。新增 `IDeviceSessionClient`，保持原账号接口兼容测试替身。

设备弹窗保留原布局和确认区域，改为绑定真实设备集合。支持刷新、展开会话、退出会话、退出设备和退出全部设备，确认内容显示实际选中的目标。请求期间阻止重复提交，断网或服务器错误不把目标假装移除，允许联网后重试。

访问令牌不足一分钟到期时先刷新；服务器返回 401 时允许一次强制刷新后重试，以处理本机时钟差异。刷新网络响应丢失时立即进行一次相同 `requestId` 的重试，请求 ID 已在发送前加密保存。60 秒有界恢复窗口及重放撤销规则沿用第二阶段；更长的断网可能需要重新登录。

远程撤销、刷新失效或退出当前会话后，清理加密的活动凭证，通知登录弹窗和主窗口恢复未登录状态，清空设备列表。退出/切换账号后迟到的旧设备列表会被丢弃。未退出的账号会话继续正常使用，本地专注记录和计时业务保持原有实现。

原有每分钟会话检查继续工作；用户主动访问云端时也立即处理失效。离线状态不能查看最新设备状态或确认远程退出；本地当前会话退出仍使用第二阶段的持久化撤销重试机制。

## 验证与使用

完整结果见 [第三阶段验证记录](STAGE3_VALIDATION.md)。Windows 后端测试：

```powershell
./scripts/Test-Postgres.ps1
```

在 `windowBlocked01` 根目录用 .NET 8 编译测试项目到独立产物目录后，可从本项目根目录执行真实账号与设备联调：

```powershell
./scripts/Test-AccountClient.ps1 `
  -ClientProjectRoot 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01' `
  -ArtifactsDirectory '.artifacts/client-stage3-final' `
  -TestFilter 'FullyQualifiedName~CloudAccountLiveTests|FullyQualifiedName~CloudDeviceLiveTests' `
  -ExpectedPassed 2
```

运行器只使用临时 PostgreSQL、随机测试账号、本地开发邮件和临时 DPAPI 存储。两个独立安装实例模拟两台设备，不等同于两台真实 Windows 机器的人工操作验收。

客户端改动已应用到独立项目，并保存为 `integrations/windowBlocked01/stage3-devices.patch`，以第二阶段后的源码为基线。当前目录不要重复应用；其他副本按第二阶段、第三阶段顺序检查补丁。

当前运行中的旧客户端未重启。新 Release 产物位于 `.artifacts/client-stage3-release/bin/FocusApp.Desktop/release/`，退出旧实例并启动新构建、配置 API 后才会显示新设备页。

真实两机验收时，两台客户端都需要指向同一个可达的 HTTPS API（通过 `FOCUSTIME_API_BASE_URL` 配置）。验证 A、B 同账号登录后能互相看到，A 退出 B 后 B 下一次联网请求失效、A 保持有效；再验证 B 重新登录和全部设备退出。单会话退出还需检查同设备其他会话保留。离线时不能把未确认的远程退出当作成功。

## 后续边界

真实两机界面点选、正式 SMTP 和 Linux CI 仍需外部验收。原账号安全页中账号注销等其余原型没有因此变成云端功能；专注同步、会员校验和支付也未在本阶段实现。过期挑战与历史会话的保留清理、反向代理配置及运维告警在后续安全与部署工作中完善。
