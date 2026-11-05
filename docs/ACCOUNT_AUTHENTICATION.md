# 第二阶段：账号认证与客户端接入

更新日期：2026-11-03。本文件记录第二阶段账号闭环；正式 SMTP 送达及人工界面点选尚待验收。设备列表与远程退出现已在第三阶段实现，见 [设备会话说明](DEVICE_SESSIONS.md)；专注同步、会员和支付仍按后续阶段开发。

## 运行配置

先应用全部数据库迁移，再按 README 启动 API。Windows 客户端默认请求 `http://localhost:5080/`；可通过进程环境变量 `FOCUSTIME_API_BASE_URL` 设置 API 根地址。远程地址必须使用 HTTPS，仅回环地址允许 HTTP。没有忽略 TLS 证书错误的逻辑。

Development 默认 `Email:Mode=Pickup`，邮件写入 API 内容目录的 `.artifacts/mail/*.eml`；这是本地开发邮件，不会投递真实邮箱。测试运行器使用独立邮件目录。开发 HMAC 密钥首次使用时随机生成，保存在 API 内容目录的 `.artifacts/auth-secret.key`，需要保留，禁止提交。

网站测试可使用后端的 `scripts/Get-LocalLoginCode.ps1`，按邮箱和用途读取，并核对数据库最新挑战与验证码摘要，避免误取其他实例或过期邮件。命令和本机配置见 [快速读取网站测试验证码](LOCAL_LOGIN_CODE.md)。

正式环境通过安全配置或环境变量提供以下内容：

| 配置 / 环境变量 | 内容 |
| --- | --- |
| `Authentication:Secret` / `Authentication__Secret` | 至少 32 个随机字节的 Base64 字符串，独立于数据库密码 |
| `Email:Host` / `Email__Host` | SMTP 服务器 |
| `Email:Port` / `Email__Port` | 默认 587，强制 STARTTLS；当前不支持隐式 TLS 的 465 端口 |
| `Email:From` / `Email__From` | 发件人地址 |
| `Email:Username` / `Email__Username` | SMTP 账号 |
| `Email:Password` / `Email__Password` | SMTP 授权码或密码 |

密钥可在受信任的终端中生成并直接保存到私有配置：`[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`。不要粘贴到代码、日志或聊天中。运行 `dotnet --migrate` 与健康检查无需认证密钥；非开发环境缺少密钥时，验证码申请和登录返回 503，不会使用公开默认密钥。Production 不允许 Pickup 代替真实邮件。

本机未提供正式 SMTP 凭证，因此没有声称完成真实邮件送达验证。正式部署前还需配置 HTTPS、持久化密钥、数据库最小权限和备份；这些属于部署阶段。

Compose 的 `api_development_data` 卷保存开发密钥和邮件。更换或删除开发密钥后，原验证码不可继续验证，尚未确认的刷新重试可能要求重新登录。开发目录从 Git、Docker 上下文和发布项中排除。

## 可调用 API

下表省略 `/api/v1/auth` 前缀。所有认证响应禁止缓存，错误沿用 Problem Details 和服务器生成的 traceId。

| 方法、路径 | 请求 | 成功结果 |
| --- | --- | --- |
| POST `/registration-code` | `email` | 200：`challengeId, expiresAtUtc, resendAfterSeconds` |
| POST `/register` | `email, challengeId, code, password` | 201：`id, email, emailConfirmed`，注册后显式登录 |
| POST `/login` | `email, password, device` | 200：`TokenResponse` |
| POST `/refresh` | `refreshToken, requestId` | 200：轮换后的 `TokenResponse` |
| POST `/logout` | `refreshToken` | 204；幂等撤销该凭证所属会话 |
| GET `/me` | `Authorization: Bearer <accessToken>` | 200：当前 `UserProfile` |
| POST `/password-reset/code` | `email` | 200：邮箱挑战，未知邮箱也返回同类结构 |
| POST `/password-reset/verify` | `challengeId, code` | 200：`resetTicket, expiresAtUtc` |
| POST `/password-reset/complete` | `resetTicket, newPassword` | 204；更新密码并撤销该用户全部会话 |

`device` 包含非空 UUID `installationId`、`name`（最多 128 字符）、`operatingSystem`（64）和 `clientVersion`（32）。安装 ID 只是设备标识，不能认证。客户端生成并持久化它；服务器按用户与安装 ID 的组合唯一登记设备，每次登录建立独立会话。

`TokenResponse` 包含 `tokenType=Bearer`、`accessToken`、`accessTokenExpiresAtUtc`、`refreshToken`、`refreshTokenExpiresAtUtc`、`sessionId`、`deviceId` 和 `user`。令牌是不透明的高熵凭证，不要求客户端解析 JWT。新增兼容的 `membership` 字符串字段，默认 `normal`。正式会员模块尚未实现；仅 Development 环境可通过服务器本地测试配置向指定账号发放 `lifetime` 测试身份，见 [开发 VIP 测试说明](DEVELOPMENT_VIP_TESTING.md)。

退出接口只接受持有的刷新凭证进行所属会话撤销，不允许指定其他会话或用户。即使访问令牌已过期，也能完成离线退出的后续撤销。不存在或已撤销的凭证仍返回 204。

## 安全与一致性规则

- Identity 管理密码哈希、安全戳和锁定；密码要求 8–128 字符，至少包含一个字母和一个数字。连续 5 次登录失败锁定 15 分钟，已确认邮箱才能登录。未知账号与密码错误均返回 `invalid_credentials`，找回密码不会直接暴露邮箱是否存在；这不等于完整的时序侧信道消除。
- 六位验证码按注册/重置用途隔离，HMAC 摘要绑定邮箱、用途与挑战 ID，10 分钟有效、最多尝试 5 次、成功只消费一次。按邮箱跨用途至少间隔 60 秒，每小时最多 5 次；重发使同用途旧挑战失效。验证码不会出现在 API 返回或应用日志中。
- SMTP 发送有 15 秒取消时限。注册发信失败回滚挑战并返回 503；找回密码在 SMTP 失败时仍返回通用挑战并记录不含邮箱的运维警告，避免通过错误差异泄露账号存在，用户可在冷却后重试。发送成功但数据库最终提交失败时，收到的邮件也可能无法使用，应重新申请。没有引入邮件队列。
- 数据库事务使用按规范化邮箱取得的 PostgreSQL advisory lock，串行化同一账号的注册、尝试计数、登录、刷新、退出和密码重置；唯一索引提供最终约束。支持多个 API 实例共享数据库。
- 访问令牌有效 15 分钟，会话和刷新令牌绝对有效 30 天，不随刷新无限延长。只存储令牌 SHA-256 摘要，每次认证检查服务端到期、撤销和用户安全戳。刷新后旧访问令牌立即失效。
- 客户端刷新前持久化非空 `requestId`。同一旧刷新令牌和相同请求 ID 在 60 秒内、且后继令牌未再次使用时，可取回完全一致的响应；服务器通过 HMAC 派生令牌，无需明文存储。不同请求 ID 重用旧令牌、超过恢复窗口重试或后继已使用时，撤销该会话并要求重新登录。客户端需要单次串行刷新。
- 重置凭证是高熵随机值，数据库仅存摘要，10 分钟有效、一次使用。密码更新、安全戳变化、全部会话撤销及其他重置凭证失效同事务完成。密码不符合策略时不消耗重置凭证。
- 认证入口额外按来源 IP 限制 30 次/分钟，并受全局限流约束。邮件频率和账号锁定在数据库中保存，429 提供对应 `Retry-After`。部署反向代理时仍需只信任明确的代理地址。

主要业务错误：`invalid_code`、`invalid_credentials`、`email_registered`、`password_policy_failed`、`invalid_reset_ticket`、`invalid_device`、`invalid_request_id`、`invalid_session`、`refresh_reused`、`email_rate_limited`、`account_locked`、`email_unavailable`、`authentication_unavailable`。格式错误仍为 `validation_failed`，访问令牌无效为 `authentication_required`。

## Windows 客户端改动

账号 HTTP 请求及凭证由 `FocusApp.Desktop` 进程持有。当前没有云端后台同步任务，未把认证扩展到本地服务或 Named Pipe；第 4 阶段如需服务后台同步，应单独设计进程间授权，不直接复制桌面进程的令牌文件。

| 文件或模块 | 本阶段改动 |
| --- | --- |
| `Services/Cloud/IAccountClient.cs` | 可替换的账号接口、API 状态模型 |
| `Services/Cloud/CloudAccountClient.cs` | HTTP、串行刷新、持久化请求 ID、恢复登录、撤销重试 |
| `Services/Cloud/WindowsCredentialStore.cs` | 当前 Windows 用户的 DPAPI 加密与原子文件替换 |
| `ViewModels/AsyncCommand.cs` | 异步命令、防止重复提交 |
| `ViewModels/AuthModalViewModel.cs` | 替换生产模拟账号，接入注册、登录、验证码和找回流程；忙碌状态、错误消息、密码清理 |
| `ViewModels/MainWindowViewModel.cs` | 显示真实邮箱、启动及每分钟恢复/验证会话、退出清理 |
| `Views/AuthModal.xaml`、`.xaml.cs` | 绑定注册与发码、协议勾选、状态提示、清理 PasswordBox |
| `Views/AccountPanel.xaml` | 登录状态的云端/离线提示 |

凭证保存在 `%LOCALAPPDATA%/FocusApp/Cloud/<API地址摘要>.dpapi`，绑定当前 Windows 用户和 API 地址；不存密码，不写入专注数据库，不经 IPC 传递。损坏文件按未登录处理；网络断开可以显示缓存账号及离线提示，但不会授予会员权益。服务器撤销在联网验证时才能发现。

退出先持久化清除活动令牌并保存待撤销凭证，联网后重试；待撤销令牌也受 DPAPI 保护。桌面进程关闭时不会继续后台重试，下一次打开时恢复。失效凭证只清除账号状态，不删除专注记录。

客户端已修改在独立 `windowBlocked01` 项目中。为了让这个后端仓库保留可审查的联调改动，另保存 `integrations/windowBlocked01/stage2-account.patch`。原目录已应用，**不要再次应用补丁**；在其他同版本客户端副本中可先用 `git apply --check` 检查。补丁也包含相关测试，旧演示账号只出现在测试替身中。

## 验证及后续边界

自动验证见 [STAGE2_VALIDATION.md](STAGE2_VALIDATION.md)。真实联调调用现有弹窗 ViewModel，并使用真实 Kestrel、PostgreSQL、HTTP、DPAPI 和开发邮件文件；没有把模拟 HTTP 测试当作真实联调，也没有声称已人工点选所有 WPF 页面。

设备列表与远程退出已在第三阶段接入云端。AccountSyncModal 中账号注销、修改邮箱等其余原型不属于已完成的云端能力；会话退出不会注销云端账号。

后续需要完善设备操作、凭证与挑战的过期清理、发送服务的总体配额监控，以及离线会员校验。绝对离线期间无法实时获知服务器撤销；当前仅缓存账号身份，专注数据仍由本地服务负责。

参考：[Identity 配置](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0)、[DPAPI CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)。
