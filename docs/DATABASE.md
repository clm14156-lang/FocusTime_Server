# 数据库与迁移

## 第一阶段范围

使用 EF Core 10 和 Npgsql 10。AppDbContext 继承 IdentityUserContext，以 Identity 标准用户能力作为后续账号模块的基础。

初始迁移包含 identity schema 的 users、user_claims、user_logins、user_tokens，以及 public schema 的 EF 迁移历史。普通注册使用邮箱，normalized email 有数据库唯一约束。后续账号及同步表见下述阶段迁移；会员表仍待第六阶段添加。

连接读取 ConnectionStrings:Database，可通过环境变量 ConnectionStrings__Database 覆盖。仓库默认连接不含密码，连接参数不得写入请求日志或健康检查响应。

## 第二阶段迁移

`20261102185629_AccountAuthentication` 在现有 Identity 基础上新增表，不删除或重建用户：

| 表（identity schema） | 字段与约束 |
| --- | --- |
| `email_challenges` | Id、规范化邮箱、Purpose、CodeHash、创建/到期时间、Attempts、Consumed、TicketHash、TicketExpiresAtUtc、TicketConsumed；邮箱/用途/时间索引，TicketHash 唯一 |
| `devices` | Id、UserId、InstallationId、名称/系统/版本、首次与最近登录时间；用户/安装 ID 唯一，UserId 外键 |
| `sessions` | Id、UserId、DeviceId、SecurityStamp、AccessHash、访问到期/创建/绝对到期/撤销时间；AccessHash 唯一，用户及设备外键 |
| `refresh_credentials` | Id、SessionId、TokenHash、UsedAtUtc、RequestId、SuccessorId；TokenHash 唯一，会话外键 |

验证码使用独立密钥的 HMAC，访问/刷新令牌和重置凭证只存 SHA-256 摘要。刷新旧行保留以检测重放，后继 ID 用于有界恢复；原始凭证不入库。记录保留和清理任务在设备安全与部署阶段完善。

账号相关写操作按规范化邮箱取得事务级 PostgreSQL advisory lock。迁移及业务测试同时覆盖空库安装、已有用户保留、重复执行和模型一致性。所有业务时间为 UTC；账号服务使用毫秒精度，避免数据库微秒精度造成重试响应差异。

## 第三阶段迁移与会话管理

`20261103031803_DeviceSessionActivity` 添加 `identity.sessions.LastSeenAtUtc`，旧行由 `CreatedAtUtc` 回填，不撤销原会话。登录和刷新时即时记录活动时间，普通认证请求按一分钟节流更新会话及设备活动。

设备列表仅显示有效会话；退出只设置 `RevokedAtUtc`，不删除设备、用户或其他业务记录。设备管理、刷新、登录和密码重置共用账号事务锁，并在锁内重新确认管理请求的调用方会话。

## 应用迁移

第四阶段迁移 `20261103042017_FocusRecordUpload` 添加 `focus.records`（用户/记录复合主键、每用户唯一版本、来源设备与 JSON 快照）、`focus.receipts`（用户/操作复合主键、规范化摘要和逐项结果）及 `focus.sync_states`（每用户提交版本）。上传、收据、版本在账号事务中一起提交；客户端 SQLite 的 v15 队列及迁移见 [上传说明](FOCUS_UPLOAD.md)。

第五阶段迁移 `20261103095323_FocusBidirectionalSync` 新增不可变 `focus.changes`：UserId、Revision、RecordId、SourceDeviceId、IsDeleted、PayloadJson、ReceivedAtUtc。主键为用户/版本，用户/记录/版本有索引；已有记录按原版本回填。新增和删除变化与收据、当前记录和每用户版本同事务提交。删除保留墓碑及变化历史，分页期间的删除不会使此前快照遗漏。

客户端 SQLite v16 新增按账号/API 绑定隔离的 `focus_sync_progress` 与 `focus_sync_remote`，整页记录、删除标记和游标同事务写入，并迁移第四阶段延后删除队列。客户端原生专注、目标和任务表保留，下载不更新真实任务状态；详细规则见 [双向同步](FOCUS_SYNCHRONIZATION.md)。

先创建专用的本地应用数据库及用户，再运行：

```powershell
dotnet run --project src/FocusTime.Api -- --migrate
```

该命令应用待执行的迁移并退出。重复执行应保持已有数据。常规 Web 启动不自动迁移数据库；数据库连接成功但仍有待执行迁移时，就绪检查仍为 503。

## 新增迁移

```powershell
dotnet tool restore
dotnet ef migrations add <MigrationName> --project src/FocusTime.Api --output-dir Infrastructure/Persistence/Migrations
dotnet ef migrations has-pending-model-changes --project src/FocusTime.Api
dotnet ef migrations script --idempotent --project src/FocusTime.Api --output .artifacts/migrations.sql
```

设计时工厂优先使用 ConnectionStrings__Database，未设置时使用不含密码的设计连接。生成迁移不需要连接数据库，实际应用迁移必须配置目标库。

生产迁移应在发布流程中使用有 DDL 权限的部署身份执行，并事先审阅脚本、备份和验证。应用运行身份原则上只保留后续业务所需的读写权限。生产自动化在第七阶段完善。

## PostgreSQL 集成验证

设置 TEST_DATABASE_CONNECTION 后，测试创建随机且有明确前缀的独立数据库，验证空库迁移、重复迁移保留数据、邮箱唯一约束和就绪状态变化。

未配置该环境变量时，数据库测试显式跳过，不能将仅通过 HTTP 测试视为数据库迁移已验证。
