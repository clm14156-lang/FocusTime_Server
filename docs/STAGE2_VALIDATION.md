# 第二阶段验证记录

日期：2026-11-03。Windows 本机代码、账号闭环和真实 API 联调已实现；正式 SMTP、人工 WPF 点选和 Linux 验证仍待执行。本记录区分自动验证与尚未验收的部分。

## 实现范围

- 邮箱注册挑战、注册、登录、用户资料、退出、刷新令牌轮换。
- 用途隔离的验证码、发送间隔/配额、尝试上限、过期和原子消费。
- Identity 密码哈希、账号锁定、安全戳；一次性密码重置凭证及全部会话撤销。
- 最小设备登记与会话表、令牌摘要、同请求刷新响应丢失恢复、重放检测。
- 现有 WPF 弹窗命令绑定、真实邮箱显示、异步提示、密码清理、DPAPI 凭证恢复、断网退出重试。
- 新迁移 `20261102185629_AccountAuthentication`；未更改客户端专注数据库、倒计时或屏蔽实现。

## 后端自动测试

环境为隔离 .NET SDK 10.0.403 / runtime 10.0.14、PostgreSQL 18.6。Release 构建无警告/错误。

最终结果：25/25 通过，0 失败，0 跳过。证据为 `.artifacts/postgres-952e9bc0bf3b405b961e8aa33b337c70/results/foundation.trx`，包含最终密码策略、匿名健康检查和邮件服务故障处理。

在原有 18 项基础测试上新增 7 项真实 PostgreSQL 账号场景，覆盖：

| 场景 | 验证内容 |
| --- | --- |
| 注册、登录、退出 | 用户确认、真实资料接口、旧访问令牌失效、幂等退出、数据库不保存原始令牌 |
| 并发刷新与重放 | 三个相同请求 ID 得到一致响应，不同 ID 重放撤销该会话，另一个会话保持有效 |
| 邮箱挑战 | 冷却时间、用途隔离、五次错误耗尽、重发、并发消费仅创建一个用户 |
| 找回密码 | 不符合策略的密码不消耗凭证，一次性重置、旧会话全部失效、旧密码拒绝、新密码可登录 |
| 到期与锁定 | 访问令牌到期仍可刷新、会话绝对到期拒绝、设备安装 ID 稳定、重复邮箱、登录锁定 |
| 发送与过期 | 注册发信失败回滚、未知邮箱找回不发送邮件、找回密码发信故障仍返回通用结果、验证码过期拒绝 |
| 生产配置 | 缺少认证密钥时拒绝验证码和登录；健康接口仍正常 |

运行 `scripts/Test-Postgres.ps1` 会创建自己的随机密码、回环端口和数据目录；测试库使用随机前缀，结束后删除测试库并停止临时实例。未操作生产数据库。

## 客户端自动测试与已有失败

使用系统 .NET 8.0.425，在 `.artifacts/client-stage2-build` 独立构建，不关闭或覆盖当前正在运行的 FocusApp 进程。相关测试结果：

客户端 Release 独立构建同样通过，0 警告 / 0 错误，日志 `.artifacts/client-stage2-release.log`。新桌面产物位于 `.artifacts/client-stage2-release/bin/FocusApp.Desktop/release/`。当前运行的旧进程未重启，必须退出旧实例并启动新构建，账号改动才会生效。

| 测试类 | 通过 | 失败 |
| --- | --- | --- |
| AuthModalViewModelTests | 10 | 0 |
| CloudAccountClientTests（新增） | 5 | 0 |
| FocusThemePersistenceTests | 2 | 0 |
| MainWindowViewModelTests | 27 | 9 |
| 合计 | 44 | 9 |

新增测试验证刷新响应丢失后跨实例复用请求 ID、并发恢复只轮换一次、断网退出先持久化清理并在重启后撤销、失效凭证清理、真实 DPAPI 加密/损坏恢复/API 地址隔离、生产路径不接受旧演示账号以及网络错误提示。

客户端当前源码存在 9 项已有失败。已将修改前保存的源码在独立临时副本中重建，基线为 39 通过 / 9 失败；本次为 44 通过 / 同样 9 失败，失败测试名集合完全一致。旧输出目录二进制比当前源码更早，不能用它代替源码基线。

证据：

- `.artifacts/client-stage2-results/client-auth-regression.trx`
- `.artifacts/client-stage2-source-baseline-results/client-source-baseline.trx`
- `.artifacts/client-stage2-build.log`

已有失败分别是两项自定义时长、四种用户条件下的强制模式、普通用户权限入口、退出后的强制模式状态，以及自动规则管理跳转。本阶段保留这些已有业务逻辑，没有为了让账号测试变绿而修改它们；因此没有宣称客户端全套测试通过。

## 真实 HTTP 与现有 ViewModel 联调

`scripts/Test-AccountClient.ps1 -ClientProjectRoot <windowBlocked01目录>` 启动临时 PostgreSQL、迁移后的真实 Kestrel API，并运行客户端测试。**1/1 通过，无跳过**。

证据：`.artifacts/account-client-97e0348b59d24cc19f78a6d544faa2e1/results/client-live.trx`，同目录保存 API 与迁移日志。API 和临时 PostgreSQL 已停止。

测试实际经过：现有注册命令 → 读取本地 `.eml` 验证码 → 注册 → 邮箱密码登录 → DPAPI 保存 → 新客户端实例从磁盘恢复并请求真实 `/auth/me` → 退出 → 等待真实邮件冷却窗口 → 找回密码验证码 → 重置 → 新密码登录 → 退出。会员按普通账号处理。

这个测试验证的是现有弹窗 ViewModel 与实际后端、HTTP、数据库和 Windows 凭证存储，不是人工操作界面，也不是正式邮箱投递。未调用真实用户账号。

## 发布与交付检查

- EF 模型与已生成迁移一致，没有待生成的模型变化。
- 幂等迁移脚本已生成到 `.artifacts/stage2-migrations.sql`。
- 后端发布产物已生成到 `.artifacts/publish-stage2/`，检查没有开发邮件、认证密钥或 `appsettings.Local.json`。
- 开发邮件/密钥目录被 Git 忽略，并从 Docker 上下文和项目发布项中明确排除；Compose 开发数据卷保留开发密钥，Dockerfile 为非 root 用户准备可写目录。这些容器改动尚未经过实际 Linux 运行。
- 客户端联调补丁已保存，并通过对当前客户端的反向 `git apply --check` 验证；没有实际回滚或重复应用。
- GitHub 工作流通过 actionlint 静态检查，测试结果门槛提高到 25 项且禁止跳过；没有声称已运行远程 Actions。

## 未完成的外部验收

- 正式 SMTP 授权与真实收件箱送达；当前只有开发邮件目录和测试发件器验证。
- 人工点选 WPF 所有步骤、显示布局及目标机器上的安装版重启验证。
- Linux CI、Docker 镜像实际运行。此前用户选择 GitHub Actions，仍需完成 GitHub 连接授权；未把静态工作流检查当作已运行。
- 第三阶段设备列表/远程退出，以及后续同步和会员；AccountSyncModal 其余原型功能不属于本次完成范围。

## 已知边界

离线账号是缓存状态，不是已联网认证，更不是会员凭证；服务器撤销需要联网后才能发现。退出撤销的后台重试仅在桌面进程运行时执行，下次启动可继续。凭证保护依赖 Windows 用户账户及系统本身安全，无法阻止同一用户权限下的恶意进程读取其自身会话。

现有客户端使用单实例 Mutex；云端请求同时使用进程内串行门，刷新请求 ID 会在发送前落盘。将来把同步移到 Windows 服务进程时，需要另行设计凭证所有权和进程间授权。
