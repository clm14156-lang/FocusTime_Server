# FocusTime_Server 后端开发前期分析

- 整理日期：2026-11-02
- 文档基线：2026-11-02 开发前的源码阅读与方案整理；该基线描述保留为历史分析。
- 当前实施进度见 [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)，第一阶段验证见 [STAGE1_VALIDATION.md](STAGE1_VALIDATION.md)。
- 分阶段实施计划：[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)
- 本文性质：区分当前代码事实、推荐方案和待确定的业务策略，不表示相关能力已经实现。

## 1. 项目背景与范围

两个项目独立维护，通过 HTTPS API 通信：

| 项目 | 职责 |
| --- | --- |
| windowBlocked01 | Windows 客户端，C# / WPF，保留现有专注、屏蔽、本地记录和统计能力 |
| FocusTime_Server | 云端后端，负责账号、设备会话、专注记录同步和会员资格 |

后端第一版需要实现：

1. 邮箱注册、登录、退出、登录状态保持、密码找回与重置。
2. 同账号多设备登录、设备与会话查询、远程退出指定设备。
3. 本地优先的专注记录上传、下载、增量同步、断网重试和去重。
4. 服务器保存会员状态、有效期和权益，客户端验证强制模式及自动屏蔽权限，并支持有限离线使用。

范围约束：

- 复用现有客户端的数据模型和业务流程，不重构已经正常运行的功能。
- 第一版只同步专注记录，不同步完整目标、任务、屏蔽列表、自动规则、偏好设置和缓存。
- 暂不开发支付功能，保留未来支付来源与会员授权之间的关联字段。
- 后端采用简单的模块化单体。
- 本次检查只阅读源码，没有启动应用、打开或迁移真实业务数据库、运行构建或测试。
- 本次新增内容仅为分析和计划文档，不包含业务代码、项目初始化或数据库创建。

## 2. 检查位置与证据范围

| 项目 | 本次检查的本地目录 |
| --- | --- |
| windowBlocked01 | C:/Users/Administrator/Documents/Codex/2026-09-16/xia/windowBlocked01 |
| FocusTime_Server | E:/FocusTime_Server |

下文客户端文件路径均相对于 windowBlocked01 根目录。结论基于检查时的磁盘源码；界面行为和运行环境尚未通过启动应用验证。

主要证据文件：

| 文件 | 分析内容 |
| --- | --- |
| src/FocusApp.Desktop/ViewModels/AuthModalViewModel.cs | 测试账号登录、注册字段、找回密码界面状态 |
| src/FocusApp.Desktop/Views/AuthModal.xaml | 注册和登录界面、命令绑定 |
| src/FocusApp.Desktop/ViewModels/AccountSyncModalViewModel.cs | 设备退出、修改密码、注销账号演示逻辑 |
| src/FocusApp.Desktop/ViewModels/MainWindowViewModel.cs | 账号状态、会员状态、普通专注保存和自动调度计时器 |
| src/FocusApp.Desktop/ViewModels/MembershipCenterViewModel.cs | 会员展示与续费反馈 |
| src/FocusApp.Desktop/Resources/Strings.xaml | 静态同步时间、设备、邮箱及会员到期文案 |
| src/FocusApp.Core/LocalDataModels.cs | 实际持久化模型 |
| src/FocusApp.Core/FocusSessionRecord.cs | 专注完成后的界面及统计模型 |
| src/FocusApp.Core/ForcedModeAccessPolicy.cs | 强制模式权限策略 |
| src/FocusApp.Core/AutomaticBlockingScheduler.cs | 自动规则的内存调度逻辑 |
| src/FocusApp.Desktop/ViewModels/HomePageViewModel.cs | 手动与自动专注启动入口 |
| src/FocusApp.Service/ServiceStateCoordinator.cs | 本地命令处理、强制专注生命周期、记录保存与删除 |
| src/FocusApp.Service/NamedPipeServiceWorker.cs | Windows 客户端身份解析和 IPC 接入 |
| src/FocusApp.Service/UserStateCoordinatorProvider.cs | 按 Windows 用户创建状态协调器和存储 |
| src/FocusApp.Infrastructure/Persistence/LocalDataPathProvider.cs | 本地数据库路径和用户隔离方式 |
| src/FocusApp.Infrastructure/Persistence/SqliteDatabaseInitializer.cs | 表结构、迁移、WAL、备份 |
| src/FocusApp.Infrastructure/Persistence/SqliteLocalDataStore.cs | 数据读写、事务和级联删除行为 |
| src/FocusApp.Desktop/ViewModels/StatisticsOverviewViewModel.cs | 记录展示、目标名称回退、目标删除 |

## 3. 客户端现有架构与本地存储

### 3.1 项目结构

| 模块 | 当前实现与职责 |
| --- | --- |
| FocusApp.Desktop | .NET 8、WPF、MVVM；主界面、专注窗口、目标任务、统计、设置、账号和会员界面 |
| FocusApp.Agent | 当前 Windows 用户会话内的托盘、通知、启动项、系统代理操作 |
| FocusApp.Service | Windows 本地服务；接收 Named Pipe 命令，保存数据，执行强制专注及屏蔽 |
| FocusApp.Core | 专注状态机、自动规则计算、统计规则、纯业务模型 |
| FocusApp.Infrastructure | SQLite、本地网站代理、程序检测与终止等外部能力 |
| FocusApp.Contracts | Desktop、Agent、Service 之间的本地 IPC 协议和 DTO |
| tests/FocusApp.Tests | Core、Desktop、Infrastructure、Service、Agent 相关测试 |

FocusApp.Service 是客户端内部的 Windows 本地服务，不是拟开发的云端 FocusTime_Server。后端方案不替换它。

Docs/ARCHITECTURE.md 描述了设计目标，但部分云同步和会员校验仍停留在规划中，实施时应以实际代码为准。

### 3.2 存储方式

业务数据库路径：%ProgramData%/FocusApp/Data/<Windows SID 的 SHA256 哈希前24位>/focusapp.db。

- Named Pipe 服务端解析连接用户的 Windows SID。
- UserStateCoordinatorProvider 按该身份创建本地状态协调器和 SQLite 存储。
- 当前数据库按 Windows 用户隔离，尚未按云端账号隔离。
- 使用 Microsoft.Data.Sqlite、手写 SQL 和迁移，没有使用 EF Core 管理本地业务数据。
- 迁移代码目标版本为 14；本次没有读取真实数据库的实际版本。
- 已有写入事务、WAL、忙等待重试、迁移前备份和数据损坏错误处理。

另外存在图标缓存数据库，以及悬浮窗位置、代理恢复日志等本地文件。这些数据保留本地，不纳入第一版同步。

### 3.3 主要本地表

| 表 | 作用 |
| --- | --- |
| focus_sessions | 准备中、进行中、已完成的专注会话 |
| focus_session_tasks | 专注完成任务的名称、排序、完成时间和详情快照 |
| focus_session_website_rules | 本次专注的网站规则快照 |
| focus_session_application_rules | 本次专注的程序规则快照 |
| targets / tasks / subtasks | 目标、任务、子任务 |
| website_rules / application_rules / automatic_rules | 本地屏蔽规则 |
| app_settings | 本地设置 |
| duration_presets | 专注时长预设 |
| monthly_focus_targets | 月专注目标 |

## 4. 账号、设备、会员和网络代码的当前状态

### 4.1 账号界面已有，认证仍为演示

| 功能 | 当前代码事实 |
| --- | --- |
| 登录 | AuthModalViewModel.Login 使用硬编码账号：123/123 为普通用户，456/456 为年费会员，789/789 为永久会员 |
| 注册 | 邮箱、验证码、密码、确认密码界面已有；注册操作和发送验证码尚未接入真实后端 |
| 找回密码 | 分步界面已存在；验证码仅做格式判断并切换步骤，没有真实邮件校验和密码重置 |
| 登录状态 | MainWindowViewModel 保存内存状态；展示邮箱由测试账号拼接为 focusapp.local 地址 |
| 退出登录 | 清理界面账号及会员状态，没有服务器会话撤销 |
| 修改密码与注销账号 | 主要为界面状态流转，尚未执行真实服务器操作 |

适配时继续使用已有弹窗和布局，将命令及数据来源替换为真实服务，并增加等待、错误和重试状态。

### 4.2 设备与同步界面为静态演示

- AccountSyncModalViewModel 使用 IsMacBookProLoggedIn 表示演示设备状态。
- 远程退出只修改该布尔值，没有撤销任何真实凭证。
- 当前设备、MacBook Pro、最后同步时间和脱敏邮箱均存在静态文案。
- 没有发现真实设备列表、业务同步 Worker、持久化同步队列或增量游标。
- 同步界面还列出网站屏蔽列表、自动规则和偏好设置，与第一版仅同步专注记录的范围不一致，后续需要校正文案。

### 4.3 会员展示和权限检查不完整

- MembershipType 已有 Normal、Annual、Lifetime。
- 会员中心的到期日期和剩余天数仍是静态文案；升级和续费主要是反馈状态。
- 日历详情、目标投入详情和角色选择已有部分会员界面判断，可继续复用入口。
- ForcedModeAccessPolicy.CanUseForcedMode 当前直接返回 true，EvaluateStart 仅依据是否请求强制模式决定结果。
- ServiceStateCoordinator.StartForcedFocusAsync 校验时长和会话条件，但未校验云端账号或会员权益。
- 现有 ForcedModeAccessPolicyTests 也体现了当前放行行为。后续接入会员时需要更新相应预期，并回归正常专注行为。

### 4.4 HTTP 能力已有，但尚未用于业务后端

IconService 和 WebsiteMetadataService 使用 HttpClient 获取网站图标和元数据。未发现面向账号、设备、会员、云同步的业务 API Client、Bearer 认证、访问令牌或刷新令牌实现。

Named Pipe 的 Windows 身份验证保护本地用户作用域，不能代替云端账号认证。

## 5. 专注记录生命周期与兼容基础

### 5.1 应复用的模型

| 模型 | 用途与限制 |
| --- | --- |
| FocusSessionRecord | 专注完成后的界面和统计模型；包含时长、起止时间、完成方式、目标和任务 ID；自身没有记录 ID |
| LocalFocusSession | 持久化模型；具有稳定的 Guid SessionId、状态、UTC 时间、任务快照和规则快照；适合作为同步映射基础 |
| LocalFocusSessionTaskSnapshot | 保存任务 ID、名称、排序、完成时间及详情快照，不依赖任务一直存在 |

### 5.2 创建、保存、读取和删除

| 环节 | 代码链路 |
| --- | --- |
| 普通专注开始 | MainWindowViewModel 生成 SessionId，通过 IPC 让本地服务保存进行中的会话 |
| 普通专注完成 | FocusSession_CompletionRecorded 组装 LocalFocusSessionDto，调用 RecordCompletedFocusAsync |
| 强制专注 | ServiceStateCoordinator 创建 SessionId、保存准备状态，推进生命周期并保存完成结果，支持恢复 |
| 保存 | SqliteLocalDataStore.SaveFocusSessionAsync 按 SessionId 插入或更新，在事务内保存任务和规则快照 |
| 读取 | 本地服务加载 SQLite 并发布状态，Desktop 统计模块投影完成记录 |
| 单条删除 | 已有 DeleteFocusRecord IPC 和 DeleteFocusSessionAsync；本次未发现 Desktop 界面对单条删除 API 的直接调用 |
| 删除目标 | 删除目标关联的专注记录及任务，清理目标引用；当前是硬删除 |

### 5.3 同步前需要解决的具体问题

1. 专注有 5 秒准备阶段，PreparationStartedAtUtc 和 FocusStartedAtUtc 含义不同；时长统计继续使用 ActualSeconds。
2. 同步键必须使用持久化 SessionId，不能根据时间、目标名称或界面记录重新生成 ID。
3. 普通专注完成时仅在本地服务已连接的条件下保存，部分 IPC 异常被吞掉；需补充可恢复的待保存状态和错误反馈。
4. 删除目标时必须在同一事务内登记相关历史记录的删除操作，防止云端下载复活记录。
5. 云端导入不能直接复用会标记本地任务完成的普通完成命令，需要专门的批量历史导入入口。
6. 异机记录可利用目标名称和任务快照展示；当前目标管理列表仍来自本机 targets，记录同步不等同于完整目标任务恢复。

## 6. 强制模式与自动屏蔽的接入位置

强制模式链路：HomePageViewModel -> DesktopFocusSessionBridge -> 本地 ServiceStateCoordinator -> AccessControlExecutionHost。

自动规则触发链路：MainWindowViewModel 一秒计时器 -> FocusStateCoordinator -> HomePageViewModel -> AutomaticBlockingScheduler -> 开始专注。

屏蔽执行由本地服务和 Infrastructure 完成，包括 LocalWebsiteProxy、UserProxyManager、ApplicationControlMonitor。

推荐两层权限检查：

| 层级 | 接入位置与职责 |
| --- | --- |
| Desktop | 强制开关、自动屏蔽开关、规则触发和开始专注入口；展示权限结果和登录/会员引导 |
| 本地 Service | 强制专注启动、自动规则启用和执行入口；验证服务器签发的权益，拒绝绕过界面的 IPC 请求 |

普通手动专注与自动规则触发应显式区分来源。当前普通专注也使用屏蔽执行器，不能把所有 ActivateAccessControl 调用统一限制为会员。

此次会员接入沿用现有自动规则计算。将自动调度迁移到本地服务、实现界面关闭后继续触发，是可单独讨论的客户端需求，不混入本轮后端开发。

## 7. FocusTime_Server 当前状态

截至前期分析检查时（第一阶段开发前）：

- 项目目录只有 Git 元数据，尚无解决方案、项目文件、API、数据库配置或业务代码。
- origin 已指向 https://github.com/clm14156-lang/FocusTime_Server.git。
- main 尚无提交。
- 本次新增的是本文和开发计划，不代表已经初始化后端项目。

## 8. 推荐整体架构和技术选型

### 8.1 通信与职责

Desktop / Agent <-> Named Pipe <-> Windows 本地 Service <-> HTTPS API <-> FocusTime_Server <-> PostgreSQL。

| 客户端负责 | 云端必须负责 |
| --- | --- |
| 倒计时、悬浮窗、通知、网站和程序屏蔽 | 账号、邮箱和密码验证 |
| 本地记录持久化和断网使用 | 用户、设备及会话归属和撤销 |
| 待同步队列、后台重试、状态展示 | 幂等写入、增量查询、冲突和删除状态 |
| 验证离线权益凭证并执行本地限制 | 计算会员资格、有效期及权益，签发授权 |

后端单进程按 Identity、Sessions、FocusSync、Membership 四个模块组织，共用数据库和事务。初期一个 API 服务、一个 PostgreSQL，后台任务采用同进程 Hosted Service。

云端定义独立 API DTO 和 OpenAPI 契约，与客户端已有模型进行映射。FocusApp.Contracts 继续负责本地 IPC，不将它直接当作云端协议，也不让 Linux 后端引用 Windows 实现项目。

### 8.2 技术建议

| 项目 | 推荐方案 |
| --- | --- |
| 框架 | ASP.NET Core 10 / .NET 10 LTS |
| 数据访问 | EF Core 10 + Npgsql 10 |
| 数据库 | PostgreSQL 18，保持维护补丁 |
| 用户体系 | ASP.NET Core Identity，复用密码哈希、锁定、安全戳等能力 |
| API 认证 | 可撤销数据库会话；短效不透明 Bearer Access Token + 轮换 Refresh Token |
| 邮件 | 独立邮件接口，连接 SMTP 或邮件服务商 |
| 部署 | 受支持的 Ubuntu LTS，Nginx + Kestrel + systemd |
| 运维 | HTTPS、健康检查、结构化日志、备份及恢复验证 |

截至 2026-11-02，.NET 8 将于 2026-11-10 结束支持，新后端选择 .NET 10 LTS。客户端接口适配可先保留现有目标框架，框架升级另行安排回归。

数据库会话便于远程退出后在下一次请求拒绝访问。Identity 负责账号安全，但默认 Identity API 不直接提供本项目完整的设备会话和撤销机制；内置令牌也不能直接等同于 JWT。

令牌有效期初始可配置为 Access Token 15 分钟、Refresh Token 30 天，最终参数属于实施时需确认的安全策略。

## 9. 推荐云端数据库设计

以下是逻辑设计，字段名和表名可在实施时统一；Identity 标准表由框架映射管理，不建立重复用户体系。

| 表 | 主要字段、约束与关联 |
| --- | --- |
| users | id、email、唯一 normalized_email、password_hash、email_confirmed、security_stamp、锁定状态、created_at；映射 Identity 用户 |
| email_challenges | id、用户/邮箱、purpose、验证码带密钥摘要、expires_at、失败次数、consumed_at；注册和密码重置用途隔离 |
| devices | id、user_id、installation_id、名称、系统、客户端版本、首次和最后活动时间；唯一 (user_id, installation_id) |
| device_sessions | id、user_id、device_id、Access Token 摘要及到期时间、创建/活动时间、撤销时间和原因 |
| refresh_tokens | id、session_id、令牌摘要、令牌族、到期时间、使用时间、替代令牌 ID、撤销时间 |
| focus_records | user_id、record_id、source_device_id、专注事实字段、server_revision、deleted_at；唯一 (user_id, record_id) |
| focus_record_tasks | user_id、record_id、原 task_id、名称快照、排序、完成时间、详情 JSON；关联用户内的专注记录 |
| user_sync_states | user_id、当前已提交同步版本；事务内按用户分配递增版本 |
| sync_operations | user_id、operation_id、请求摘要、处理结果和版本；唯一 (user_id, operation_id) |
| membership_grants | id、user_id、等级、plan_code、开始/结束时间、状态、source、source_reference、操作人、权益版本 |

关联约束：

- 用户关联多个设备、设备会话、专注记录和会员授权。
- 会话关联刷新令牌族，撤销设备时撤销它的全部有效会话。
- 退出设备不删除来源设备的历史记录。
- focus_record_tasks 关联记录，不关联云端真实任务表；第一版没有完整目标任务同步。
- 目标、任务、自动规则的原 ID 作为来源元数据，不建立对应业务实体外键。
- 会员来源单号预留支付渠道关联；当前采用受保护的人工发放入口。

### 9.1 专注记录兼容字段

| 客户端字段 | 云端处理 |
| --- | --- |
| SessionId | 保留为 record_id，不重新生成 |
| ConfiguredSeconds / ActualSeconds | 整数秒 |
| PreparationStartedAtUtc / FocusStartedAtUtc | 分别保存准备时间与实际专注开始时间 |
| PlannedEndAtUtc / CompletedAtUtc | UTC 时间，HTTP 使用带时区的 ISO 8601 |
| CompletionKind / IsForcedMode | 保留历史事实，不代表当前会员有效 |
| TargetId / TargetNameSnapshot | 原始标识和名称快照 |
| CompletedTasks | 任务 ID、名称、排序、完成时间、详情快照 |
| BlockingEnabled / AutomaticRuleId / AutomaticOccurrenceStartedAtUtc | 可保留历史来源元数据，不据此重建或执行规则 |

第一版只接受已完成记录。运行中会话、网站地址、程序路径和完整屏蔽配置留在本地。

普通用户可由没有有效会员授权表示。年费、永久会员兼容现有展示枚举；预留方案代码，暂不建设订单、支付和退款表。

### 9.2 客户端必要的附加元数据

通过增量迁移增加记录归属、同步队列和游标，具体可以采用附加表：

- 记录归属/同步状态：SessionId、所属云端账号、来源、本地和服务器版本、同步状态。
- 待同步队列：operationId、账号、记录 ID、操作类型、载荷、重试状态和错误。
- 拉取状态：账号、安装标识、已提交游标。

硬删除业务记录时，删除操作和必要归属信息仍需保存在队列或墓碑中，不能随业务行级联删除。

## 10. API 设计

统一前缀：/api/v1。以下路径均相对于此前缀。

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| POST | /auth/email-codes | 请求注册邮件验证码 |
| POST | /auth/register | 校验验证码并创建账号 |
| POST | /auth/login | 邮箱密码登录，登记设备，返回令牌及账号摘要 |
| POST | /auth/refresh | 轮换刷新令牌 |
| POST | /auth/logout | 撤销当前会话 |
| POST | /auth/logout-all | 撤销当前账号全部会话 |
| GET | /me | 当前用户资料 |
| POST | /auth/password-reset/request | 请求找回密码邮件 |
| POST | /auth/password-reset/verify | 验证邮件验证码，签发一次性重置凭证 |
| POST | /auth/password-reset/complete | 消费凭证、修改密码并撤销旧会话 |
| GET | /me/devices | 设备列表、会话摘要及当前设备标记 |
| DELETE | /me/devices/{deviceId}/sessions | 退出指定设备的全部会话 |
| DELETE | /me/sessions/{sessionId} | 撤销指定会话 |
| POST | /sync/focus-records/push | 批量提交新增和删除操作 |
| GET | /sync/focus-records/changes?cursor=...&limit=... | 拉取增量记录和删除标记 |
| GET | /focus-records?from=...&to=...&cursor=... | 分页查询历史记录 |
| GET | /me/membership | 会员状态、有效期及权益 |
| POST | /me/entitlement-lease | 签发绑定账号、会话和设备的离线权益凭证 |

共同约束：

- 用户身份来自认证上下文，不信任请求体中的 userId。
- 来源设备由当前会话确定，不能通过修改设备 ID 冒充其他设备。
- 设备名称和安装标识不是身份凭证。
- 返回稳定错误码和 traceId，区分 401、403、409、429 等情况。
- 同步限制请求体和批次，初始可配置每批最多 200 条。
- 邮件验证码由服务器控制用途、有效期、尝试次数、重发间隔和一次性消费。
- 找回密码接口避免暴露账号是否存在，验证码成功后仅签发短期一次性重置凭证。
- 普通客户端没有修改会员资格的权限。
- 修改密码、注销账号等已有演示界面，在真实能力接入前不能继续展示为服务器操作成功。

## 11. 同步协议与冲突策略

### 11.1 本地优先与上传

1. 已完成记录和待同步操作在同一个 SQLite 事务中落盘。
2. 每条操作使用固定 operationId，并保留已有 SessionId。
3. 后台按账号提交有限批次；网络请求不阻塞倒计时、结束专注和本地保存。
4. 服务器在事务中校验归属、去重并保存，提交后返回逐条结果。
5. 客户端按已确认的操作移除队列项；服务端成功但响应丢失时，仍使用原 operationId 重试。
6. 队列项与本地版本绑定，确认旧操作不能误清除同记录后来新增的待同步操作。

### 11.2 增量下载

- 使用服务器版本游标，不依赖客户端墙上时间排序。
- 同步版本在每个用户的事务内串行分配，保证游标边界对应已提交变更。
- 分页拉取使用稳定边界，避免并发写入造成遗漏。
- 下载记录、删除标记和游标在同一本地事务中提交，失败则保留旧游标以便重试。
- 批量导入后统一发布状态，避免每条记录都全量刷新界面。
- 导入历史不修改本地真实任务状态，不触发新的完成提醒或音效。

### 11.3 冲突与删除

- 第一版以完成记录新增和删除为主，暂不引入通用的历史编辑合并。
- 同 ID、同内容重复提交视为成功。
- 同 ID、不同内容返回冲突，保留本地数据与错误，禁止静默覆盖。
- 删除保留墓碑，迟到的旧新增不能复活已删记录。
- 删除目标时，对该目标关联且属于当前账号的已同步记录逐条生成删除操作；不将“删除目标”解释为跨设备执行目标管理。
- 未同步的新建后删除，应安全合并或保留删除意图，保证不会在请求乱序时复活。
- 首版保留墓碑。后续清理墓碑必须增加游标过期和重新全量同步规则。
- 重试采用指数退避和抖动；永久校验错误、冲突、认证失效分别处理，不能无限重试所有错误。

### 11.4 账号隔离

- 当前 SQLite 按 Windows 用户隔离，必须新增云端账号归属维度。
- 旧本地记录先视为未归属记录，首次明确导入时绑定账号，切换账号不能重新归属并上传。
- 下载数据按账号隔离；退出后保留本地文件并隐藏账号缓存，普通离线专注仍可创建本地记录。
- 队列、游标、下载缓存和冲突状态均按账号分区，账号切换时暂停旧账号任务并切换上下文。
- 云端全量恢复与本地待上传数据合并时，不能清空尚未确认的本地队列。

## 12. 会员验证与离线策略

推荐业务基线：登录即可同步专注记录；强制模式和自动屏蔽需要有效会员。当前把多设备同步列为 VIP 权益的旧界面文案需要同步调整。其他现有功能的会员规则保持原有范围，不在本轮扩大收费限制。

### 12.1 在线验证

- 云端根据会员授权记录和服务器时间计算有效权益。
- 客户端查询实际状态，不接受客户端直接上传的 isVip 作为资格。
- 本地服务验证授权后才允许启动受限功能；界面判断用于展示和引导。
- 会员过期不应阻止本地保存、上传和下载用户已有专注记录。
- 会员发放、续期和撤销保留来源及操作记录，为后续支付接入留出接口。

### 12.2 离线权益凭证

推荐使用服务器私钥签名、本地服务通过公钥验证的凭证，包含用户、会话、设备、权益、签发时间、到期时间、权益版本和签名密钥 ID。

- 初始离线窗口建议为可配置的 72 小时，属于待确定的产品策略。
- 凭证到期时间不超过会员实际有效期；永久会员也定期续签离线凭证。
- 在线获知会话撤销或权限失效后，清理离线授权缓存，拒绝新的受限操作。
- 已开始的专注建议按启动时授权完成，受原计划结束时间约束，不能延长或新开会话。
- 保存可信服务器时间，运行中结合单调时钟；明显时间回拨或不可信恢复时要求联网验证。
- 签名密钥支持轮换，客户端不保存服务器私钥或可伪造权益的共享密钥。

完全离线时无法立即接收远程撤销，其生效延迟受离线窗口限制。本地管理员仍能修改程序、时间或文件，纯本地功能无法承诺绝对防破解。

## 13. 客户端适配清单

| 文件或模块 | 最小必要调整 |
| --- | --- |
| AuthModalViewModel.cs / AuthModal.xaml | 接入真实异步账号命令、验证码、等待和错误状态 |
| MainWindowViewModel.cs | 订阅真实账号与权益状态，补齐普通记录保存失败处理 |
| AccountSyncModalViewModel.cs / 对应 XAML | 设备集合、真实退出、同步时间、队列和错误展示 |
| MembershipCenterViewModel.cs / 对应 XAML | 使用真实有效期、剩余时间和权益 |
| ForcedModeAccessPolicy.cs | 接受已验证权益结果，替换无条件放行 |
| SettingsPageViewModel.cs / HomePageViewModel.cs | 统一强制模式和自动规则入口判断，区分手动与自动来源 |
| ServiceStateCoordinator.cs | 权威权限校验、账号上下文、同步协调和专用历史导入 |
| UserStateCoordinatorProvider.cs | 在 Windows 用户作用域内管理云端账号上下文 |
| ILocalDataStore.cs / SqliteLocalDataStore.cs / 迁移类 | 增量增加归属、队列、游标和删除登记，复用原有事务 |
| FocusApp.Contracts / DesktopServiceConnection.cs | 扩展本地账号、权限、同步命令和状态；保持 IPC 版本兼容 |
| Infrastructure 新增云端模块 | API Client、认证处理、安全令牌存储、同步 Worker、签名权益验证 |
| StatisticsOverviewViewModel.cs | 按账号可见范围展示导入记录，保持名称快照回退 |
| 现有测试项目 | 扩展权限、持久化、IPC、导入副作用和回归测试 |

安全存储必须考虑 Service 和 Desktop 的 Windows 身份不同：由实际持有令牌的进程使用对应 DPAPI 身份保护，并限制文件 ACL，不能假设用户进程的 CurrentUser 密文可被服务直接解密。

## 14. 主要风险与处理方向

| 风险 | 处理方向 |
| --- | --- |
| 重复上传、响应丢失 | 固定 SessionId、operationId、唯一约束、事务和幂等结果 |
| 数据尚未本地落盘就结束 | 修补普通专注的本地保存失败反馈与恢复，不把内存成功当作持久化成功 |
| 删除后被重新下载 | 删除墓碑和事务内队列，覆盖目标级硬删除 |
| 增量漏读 | 服务器提交版本、稳定分页边界、记录和游标原子落盘 |
| 账号之间串数据 | 每条记录归属、队列和游标按账号隔离，首次历史导入明确绑定 |
| 刷新令牌泄漏与重放 | 服务端只存摘要、轮换及重放检测、客户端串行刷新、明确丢失响应的恢复方式 |
| 设备 ID 冒充认证 | 安装 ID 仅用于展示与登记，云端操作依赖有效会话 |
| 绕过界面使用会员功能 | 本地服务验证服务器签名权益，云端拒绝伪造资格 |
| 离线撤销和时钟回拨 | 有限离线窗口、可信时间记录、联网失效处理；说明管理员绕过边界 |
| 导入记录导致任务状态变化 | 专用批量历史导入，不调用带本地任务完成副作用的命令 |
| 客户端自身代理或 VPN 影响 API | 覆盖代理、断网和 API 不可达测试；重试不阻塞专注和结束 |
| Linux 或数据库故障 | 健康检查、自动重启、备份恢复验证、持久化密钥和受限账户 |

## 15. 实施前需要固化的业务参数

以下已有推荐值，后续可按产品决策调整，不妨碍先开发基础能力：

1. 普通登录用户的同步权限：建议开放基础专注记录同步。
2. 离线会员窗口：建议 72 小时可配置。
3. 会员到期或远程退出时，已开始专注的处理：建议完成原计划，禁止延长和新开。
4. 旧本地记录首次导入、账号切换及退出后的可见性：采用明确归属和账号隔离。
5. 令牌期限、验证码期限、限流、同步批次和墓碑保留策略。

服务器、域名和邮件服务商通过配置及接口接入；缺少具体供应商信息不需要重新设计客户端业务。

## 16. 技术资料

以下官方资料在前期分析时用于核对技术支持和实现边界：

- [.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [PostgreSQL 版本支持政策](https://www.postgresql.org/support/versioning/)
- [Npgsql EF Core 10 文档](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [ASP.NET Core Identity API](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0)
- [ASP.NET Core Linux / Nginx 部署](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0)
- [OWASP 会话管理](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html)
- [RFC 9700：刷新令牌安全实践](https://www.rfc-editor.org/rfc/rfc9700.html)
- [Windows DPAPI / ProtectedData](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata)
