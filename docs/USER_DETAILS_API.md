# 用户详情只读接口

所有接口沿用 `SessionAuthenticationHandler` 和 `Administrator` 权限白名单，并返回 `Cache-Control: no-store`。这些 GET 接口只读取数据，不执行支付、会员发放或会话撤销。启用管理员调整会员后，审计聚合也读取新的确认回执表，需先应用 `20261106145339_AdminMembershipAdjustments` 迁移；写入接口与权限说明见 [USER_MEMBERSHIP_ADJUSTMENTS.md](USER_MEMBERSHIP_ADJUSTMENTS.md)。

| 接口 | 用途 |
| --- | --- |
| `GET /api/v1/admin/users/{userId}` | 复用现有基本资料、账号状态、当前会员状态 |
| `GET /api/v1/admin/users/{userId}/account` | 新增安全信息和云端统计 |
| `GET /api/v1/admin/users/{userId}/membership` | 新增当前有效授权、来源及关联订单 |
| `GET /api/v1/admin/orders?userId={userId}&page=1&pageSize=10` | 复用现有用户订单查询与服务端分页 |
| `GET /api/v1/admin/orders/{orderId}` | 复用现有订单详情；前端额外核对返回的 UserId |
| `GET /api/v1/admin/users/{userId}/sessions` | 新增该用户已留存的真实会话 |
| `GET /api/v1/admin/users/{userId}/audit?page=1&pageSize=5` | 新增该用户审计记录的服务端分页 |

不存在的用户返回 404；未登录返回 401；未进入现有管理员白名单的账号返回 403。分页要求 page >= 1、1 <= pageSize <= 100。

## 统计口径及缺失字段

- 专注时长、记录数量只统计 `focus.records` 中该 UserId 的未删除记录，不累计修订历史或本地未同步记录。
- 完成任务是这些记录中已保存的 `completedTasks.taskId` 去重数量；不是当前客户端任务清单的完成数。
- 专注天数按北京时间的完成日期去重，仅计实际专注秒数大于 0 的记录。
- 屏蔽规则仅统计 `focus.website_rule_sets` 中该用户保存的网站规则，不代表客户端全部应用规则。
- 最近成功同步取该用户 `FocusSyncReceipts` 中 Applied/Duplicate 记录的最大接收时间；没有记录则为空。服务器未保存拉取成功时间及网站规则同步时间，不以最近登录时间替代。
- 当前目标、未完成任务及任务总数没有服务端实体，返回 null。登录 IP/地区、独立风控状态及密码重置完成时间也返回 null。前端展示「—」，不推算。
- 密码只返回是否设置，绝不返回哈希、SecurityStamp 或任何访问/刷新凭证。

## 会员、会话和审计

会员优先级和生效条件沿用现有服务：未撤销、已开始、未到期，永久优先，其次到期最晚。来源仅在存在关联已支付且已开通订单、或现有人工授权审计时标注；不明来源返回 null。

会话逐条读取，仅按真实撤销时间、有效期、SecurityStamp 和访问凭证有效期展示「已退出」「已过期」「待刷新」「会话有效」。不通过最近活动时间推测在线状态。系统字段沿用设备上报值，客户端版本单独标注，未知设备类型不推断。

审计只聚合已保存且能关联该目标 UserId 的记录：

1. 会员管理工具的人工 grant/revoke 审计；排除 `membership-order` 自动发放，以及由调整会员回执单独展示的 `admin-adjustment:` 内部授权记录。
2. 关联用户订单中 `admin:` 操作人留下的会员开通重试结果；排除自动支付事件。
3. 管理员自身改密审计，只在其 ActorUserId 就是当前查看用户、且操作明确为 `admin.password.change` 时展示。
4. `membership.admin_adjustments` 中已应用的发放/撤销回执，每次确认操作展示一条真实记录，包含原因与备注；待确认或过期方案不算成功操作。

现有管理员安全审计没有通用 TargetUserId，不将其他管理员作为操作人的日志归入目标用户。没有真实记录就返回空列表，不补造禁用、退出设备或重置邮件历史。日志中的操作人优先解析已有用户邮箱，否则保留原始记录值。

前端对每次请求建立 AbortController，并同时校验请求路径、重试版本和返回 UserId；订单集合与订单详情也核对归属。切换账号或页码时不保留前一账号/页的数据；错误提供明确提示与重试，空列表提供空状态。详情中的退出设备入口只读禁用。

## 验证

本地 PostgreSQL 集成测试使用 `focustime_test_*` 独立临时数据库，覆盖 401/403/404、分页校验、账号数据隔离、软删除排除、任务去重、北京时间日期、会员来源、会话状态、审计来源过滤、分页和敏感字段排除。测试文件按项目 AGENTS.md 留在本地，不提交或推送。
