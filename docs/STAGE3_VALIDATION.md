# 第三阶段验证记录

日期：2026-11-03。设备与会话管理代码已实现，本机自动验证通过；真实两机界面操作和 Linux 运行仍待外部验收。

## 后端

最终 PostgreSQL 测试：**33/33 通过，0 失败、0 跳过**，其中 18 项使用真实 PostgreSQL。证据：`.artifacts/postgres-3d6708100f42478c83d9070e0d679712/results/foundation.trx`。

新增 8 项场景：

| 场景 | 验证 |
| --- | --- |
| 设备查询 | 仅本账号有效会话、当前设备/会话标记、确定排序、服务器活动时间、过期过滤、无令牌返回 |
| 单会话退出 | 同设备其他会话保留、重复退出幂等、当前会话可退出 |
| 整设备退出 | 该设备全部访问及刷新凭证失效，其他设备保留，历史行不删除 |
| 账号隔离 | 外账号与未知 ID 均 404，匿名管理请求 401 |
| 全部退出 | 当前及其他设备失效，另一个用户不受影响 |
| 并发与恢复 | 远程撤销和刷新并发不能复活会话，相同请求超出恢复窗口时拒绝 |
| 会话上限 | 同设备额外登录被拒绝，原会话继续有效，释放名额后可登录 |
| 旧库升级 | 从第二阶段迁移升级，旧会话保留，LastSeenAtUtc 回填，模型一致 |

原有注册登录、密码重置、限流、健康、错误响应及迁移测试继续通过。

## 客户端

系统 .NET 8.0.425，独立构建到 `.artifacts/client-stage3-final/`。本次相关测试 75 项：**66 通过，9 项为原有失败**，无跳过。

| 测试类 | 通过 | 失败 |
| --- | --- | --- |
| AuthModalViewModelTests | 10 | 0 |
| CloudAccountClientTests | 5 | 0 |
| CloudDeviceClientTests | 6 | 0 |
| AccountSyncModalViewModelTests | 15 | 0 |
| AccountSyncTypographyPresentationTests | 1 | 0 |
| FocusThemePersistenceTests | 2 | 0 |
| MainWindowViewModelTests | 27 | 9 |

设备测试包含受保护 HTTP 路径、远程退出保留本机凭证、当前会话退出后清理、服务器撤销通知、断网不假报成功、401 后刷新重试，以及清空账号后迟到响应不恢复旧列表。字体规范测试只调整源码定位方式以支持独立产物目录，没有放宽断言。

证据：`.artifacts/client-stage3-final-results/client-devices.trx`、`.artifacts/client-stage3-final.log`。失败名集合与第二阶段记录的修改前源码基线完全一致，没有新增失败；因此未声称客户端全部测试通过。原有失败内容见 [第二阶段记录](STAGE2_VALIDATION.md)。

客户端 Release 编译通过，0 警告 / 0 错误。证据 `.artifacts/client-stage3-release.log`，产物 `.artifacts/client-stage3-release/bin/FocusApp.Desktop/release/`。用户正在运行的旧 Desktop、Agent、Service 进程未停止或重启。

## 真实 API 联调

最终构建 **2/2 通过、无跳过**，包括第二阶段账号闭环与本阶段多设备场景。证据 `.artifacts/account-client-f5af291691404a1e8c5268029ce9d331/results/client-live.trx`；此前的一轮同样通过。

本阶段场景实际经过：

1. 真实 Kestrel、PostgreSQL、开发 `.eml` 邮件完成注册。
2. 使用两个独立 DPAPI 目录生成不同安装标识，并登录相同账号。
3. 现有设备管理 ViewModel 读取两台设备并标记调用方。
4. A 确认退出 B，B 的真实 `/auth/me` 与刷新被拒绝，本地凭证被清理并通知 AuthModal。
5. A 继续使用；B 重新登录仍归入原设备 ID。
6. A 确认退出全部设备，A 本机凭证清空、设备弹窗关闭，B 也无法恢复旧会话。

这里模拟的是两份独立客户端安装，并非两台真实物理机器。测试没有使用用户真实账号、实际邮箱或专注数据库。临时 API 与数据库由运行器停止。

## 交付检查

接口与规则见 [设备会话说明](DEVICE_SESSIONS.md)。新增迁移只补充活动时间，不删除数据。客户端补丁保存为 `integrations/windowBlocked01/stage3-devices.patch`，已对当前源码进行反向 `git apply --check`，没有执行回滚。

GitHub 测试门槛提升到 33 项总测试、18 项 PostgreSQL 场景且禁止跳过。当前没有声称 GitHub Actions 或 Linux 容器已经实际运行。

EF 检查没有待生成的模型变化，幂等 SQL 保存于 `.artifacts/stage3-migrations.sql`。后端发布到 `.artifacts/publish-stage3/`，未包含开发邮件或认证密钥。工作流通过 actionlint 静态检查。

## 待外部验收

- 两台真实 Windows 设备上的界面点选、显示布局及安装版重启恢复。
- 正式 SMTP 邮件送达、Linux CI / Docker 实际运行；此前的外部连接依赖仍然保留。
- 专注记录云端同步、会员与权限、部署和保留清理策略，按后续阶段实施。

撤销在服务器提交后对下一次请求生效；离线设备无法立刻收到通知，已经进入处理的请求可能完成。没有引入实时推送，也没有把有效会话等同于设备当前在线。
