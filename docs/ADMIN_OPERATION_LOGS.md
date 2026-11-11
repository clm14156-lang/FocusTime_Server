# 管理后台操作日志

`GET /api/v1/admin/audit-logs` 与 `GET /api/v1/admin/audit-logs/{source:guid}` 复用管理员会话和 Administrator 白名单，返回 no-store。支持 query（管理员、对象、内容、备注的字面量包含搜索）、category=all|users|orders|plans|settings、status=all|success|failure、start/end（北京时间日期，含首尾自然日）、page、pageSize（最多 100）。默认最近七天，按操作时间及来源 ID 倒序稳定分页。

## 记录来源

- `admin.operation_logs`：套餐变更及失败的管理请求。套餐价格、时长、销售与推荐状态、版本只记录实际提交的前后值；日志与套餐更新在同一事务提交，日志写入失败则套餐更新也回滚。
- `membership.admin_adjustments`：只展示 applied 的会员调整回执，使用已有 before/after 快照；成功预览不作为业务操作记录，重复 apply 仍只有一条成功记录。
- `membership.audit`：既有人工管理工具的会员授权记录；排除支付自动发放和 admin-adjustment 子审计，避免重复展示。
- `membership.order_events`：只展示 admin: 操作人发起的权益重试结果，排除网站创建订单和模拟支付等自动事件。
- `identity.admin_security_audit`：既有管理员改密成功和失败记录，不读取密码、哈希或令牌。

全局失败过滤器仅处理已通过管理员身份白名单的套餐更新、会员调整（含校验失败）和订单重试。业务事务退出后使用新的 DbContext 保存失败原因代码；不保存原始请求、请求体、密码、Cookie、Authorization、异常内部详情。错误请求未完成的字段变更不记为成功。

历史审计未保存的 IP 和字段级变更明细保持缺失。新的套餐与失败请求记录从服务器连接读取 IP，不信任客户端自带的 X-Forwarded-For。来源名称按实际入口标注。没有通用系统配置编辑、用户禁用等真实写入接口时，不补造这些操作。

## 部署与验证

应用增量迁移 `20261106171601_AdminOperationLogs`，只创建 admin.operation_logs 和索引，不更新用户、套餐、订单或会员权益数据。应用前按现有运维流程备份数据库，再部署新后端和前端。

本地 PostgreSQL 隔离数据库验证真实套餐修改及失败持久化、日志写入失败时事务回滚、会员调整幂等与去重、自动事件排除、401/403、日期边界、查询分页及敏感字段排除。测试及产物留在本地，不提交或推送。
