# API 与同步基础契约

## 当前状态

已实现系统、账号、设备及专注记录双向增量同步。账号规则见 [账号说明](ACCOUNT_AUTHENTICATION.md)，设备规则见 [设备说明](DEVICE_SESSIONS.md)，同步规则见 [双向同步说明](FOCUS_SYNCHRONIZATION.md)。会员仍为后续契约。

业务契约分别位于 Modules/Identity/Contracts、Modules/Sessions/Contracts、Modules/FocusSync/Contracts、Modules/Membership/Contracts。后续实现接口时必须增加完整的嵌套字段、跨字段和业务验证，不能把 DTO 存在视为接口完成。

## 通用约定

- 业务前缀：/api/v1；JSON 属性 camelCase。
- 枚举使用 camelCase 字符串，不接受整数枚举值。
- ID 使用 UUID 字符串；专注 recordId 保留客户端 LocalFocusSession.SessionId。
- 时间使用 ISO 8601 的带时区时间，业务入口规范化为 UTC；区分准备、实际开始和完成时间。
- 时长统一整数秒，不能使用时间字符串替代；同步仅包含完成记录。
- 专注记录列表使用有限分页和不透明游标；同步批次最多 200 条，请求体最多 1 MiB。
- 设备管理返回完整有效设备/会话集合；新登录受账号 100 会话、每设备 10 会话的上限约束。旧版本已有会话不静默截断，详见设备说明。
- 用户和来源设备由已认证会话确定，请求不能指定其他用户作为资源所有者。
- 缺少认证为 401，权限不足为 403，单资源或请求级冲突为 409，限流为 429，并在适用时返回 Retry-After。

## 已实现接口

| 方法 | 路径 | 返回 |
| --- | --- | --- |
| GET | /api/v1/system/info | service、apiVersion |
| GET | /health/live | status、checks、traceId；存活为 200 |
| GET | /health/ready | 数据库可连接且迁移已完成为 200，否则 503；不暴露连接及异常详情 |
| GET | /openapi/v1.json | Development 环境下的已实现接口 OpenAPI；其他环境不提供 |
| POST | /api/v1/auth/registration-code | 邮箱注册验证码挑战 |
| POST | /api/v1/auth/register | 创建已确认邮箱的账号；201 |
| POST | /api/v1/auth/login | 邮箱密码登录与设备登记，返回访问及刷新令牌 |
| POST | /api/v1/auth/refresh | refreshToken + 固定 requestId，返回轮换凭证 |
| POST | /api/v1/auth/logout | refreshToken 撤销所属会话；幂等 204 |
| GET | /api/v1/auth/me | Bearer 访问令牌认证后的用户资料 |
| POST | /api/v1/auth/password-reset/code | 申请密码找回验证码 |
| POST | /api/v1/auth/password-reset/verify | 验证挑战后取得一次性重置凭证 |
| POST | /api/v1/auth/password-reset/complete | 完成密码重置并撤销全部会话；204 |
| GET | /api/v1/me/devices | 当前账号的有效设备及会话数组，当前设备标记 |
| DELETE | /api/v1/me/sessions/{sessionId} | 退出指定会话；204 |
| DELETE | /api/v1/me/devices/{deviceId}/sessions | 退出指定设备全部会话；204 |
| POST | /api/v1/auth/logout-all | 退出当前账号全部会话，包括调用方；204 |
| POST | /api/v1/sync/focus-records/push | 批量 upsert/delete，逐项 applied/duplicate/conflict/rejected；删除优先、墓碑不复活 |
| GET | /api/v1/sync/focus-records/changes?cursor=...&limit=50 | 增量变化、签名 nextCursor、hasMore；默认 50，limit 为 1–200，并按字节限制页面 |
| GET | /api/v1/focus-records?cursor=...&limit=50 | 按稳定提交边界分页的未删除历史，最后 nextCursor 为 null |
| GET | /api/v1/focus-records/{recordId} | 本账号单条上传记录及版本，仅用于查询/核验，不进行客户端导入 |

健康检查与业务错误响应分别采用健康状态对象和 Problem Details，不混用状态含义。

## 错误与跟踪

错误使用 application/problem+json，包含 status、title、instance、code 和 traceId；参数验证额外包含 errors。未处理异常返回通用错误信息，不回传异常文本和堆栈。

每个请求由服务器生成 X-Trace-Id，错误体使用同一 ID。客户端传入的同名头不会被直接信任。请求日志仅记录方法、路径、状态、耗时和跟踪标识，不记录请求体、查询字符串或 Authorization。

| HTTP | code |
| --- | --- |
| 400 | validation_failed |
| 401 | authentication_required |
| 403 | permission_denied |
| 404 | not_found |
| 405 | method_not_allowed |
| 409 | conflict |
| 413 | payload_too_large |
| 415 | unsupported_media_type |
| 429 | rate_limit_exceeded |
| 500 | internal_error |
| 503 | service_unavailable |

基础限流按直连来源 IP 分区，默认每 60 秒 120 次，不排队。健康检查和开发 OpenAPI 豁免。当前不信任任意 X-Forwarded-For；可信反向代理配置属于部署阶段。

## 后续业务接口

以下路径省略 /api/v1 前缀，均尚未实现。

| 阶段 | 方法和路径 | 请求/响应契约 |
| --- | --- | --- |
| 6 | GET /me/membership | MembershipResponse |
| 6 | POST /me/entitlement-lease | EntitlementLeaseResponse |

## 同步语义

第五阶段已实现以下同步语义。历史列表当前不提供 from/to 参数，客户端保留原有本地时间筛选。游标绑定用户、查询用途及分页上界，篡改或混用返回 invalid_sync_cursor；不可变变化日志保障并发删除不会跳过旧页面。

1. 客户端完成记录和待同步操作同事务保存，使用固定 operationId 和 recordId；未收到确认就保留原操作重试。
2. 每条上传操作包含操作 ID、记录 ID、类型、可选记录载荷和可选 baseRevision。upsert 必须有载荷，delete 不带完整载荷，UUID 不得为空。
3. 服务器按用户和 operationId 去重；相同操作 ID 的不同载荷必须拒绝。请求摘要采用规范化后的语义内容，不能把 JSON 属性顺序当成不同操作。
4. 同记录 ID、同内容为幂等成功；同 ID、不同内容返回冲突。第一版不进行任意历史字段的自动合并。
5. 可处理的批量请求返回 HTTP 200，并逐条返回 applied、duplicate、conflict 或 rejected，以及服务器版本或错误代码；部分成功不改变整体为 409。认证、请求格式等请求级错误使用相应 HTTP 错误；确认旧操作不能清除更新的队列项。
6. 删除形成墓碑，迟到上传不能复活；删除目标时原子登记它关联的当前账号记录删除操作。
7. 服务器版本在每个用户的事务中按提交顺序分配，不使用客户端时间决定覆盖关系。
8. 增量游标绑定用户和稳定分页边界；并发变化不能造成遗漏。完成一页后，记录与游标在客户端同事务提交。
9. 完成记录带目标与任务快照，不同步目标/任务实体和网站/程序规则；导入不标记本地任务完成，也不执行屏蔽。
10. 首版保留墓碑。后续清理前必须增加游标过期、全量恢复和本地未确认操作的合并协议。

## 会员语义

建议登录用户可同步记录，forced_mode 和 automatic_blocking 需要有效权益。会员过期不影响历史记录保存和同步。

离线凭证与云端 API 会话令牌用途分离；云端签名、本地服务使用公钥验证。72 小时离线窗口和已启动专注继续完成是建议策略，阶段 6 实施时固化。完全离线的远程撤销受离线窗口约束。
