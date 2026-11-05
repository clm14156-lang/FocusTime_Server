# 官网模拟支付与真实订单管理

本次复用已有 Identity 登录、官网 HttpOnly 会话代理、后台管理员 Cookie/白名单、会员授权和签名权益租约。未接入真实微信、支付宝扣款。后续套餐管理已接入数据库，见 [会员套餐与价格](MEMBERSHIP_PLANS.md)。

已完成的检查及未测试边界见 [本次验收记录](PAYMENT_VALIDATION.md)。

## 支付与开通

官网进入支付弹窗时创建数据库订单，订单所有者来自已认证会话，不接受前端指定用户。套餐金额及期限从 `membership.plans` 读取并保存到订单快照；前端金额不会参与计价，下架套餐不能创建新订单。订单号为 `FT` 加订单 UUID，用户/请求 ID 唯一，订单有效期为 15 分钟。

开发测试的确认分两步：先在账号锁内持久化支付确认，再在账号锁内尝试发放会员。会员授权、会员版本、审计、订单开通状态和开通日志一起提交。发放失败回滚本次授权及版本变化，但保留支付，并保存失败状态和日志；无法完成发放的已支付订单全部计入异常，包括确认支付后、开通开始前发生中断的记录。

每个订单以订单 UUID 作为唯一授权 ID，并复用会员审计的操作 ID 幂等机制。重复确认和并发回调不会重复发放；管理员可重试已支付异常订单，成功前不会通过「标记处理」隐藏异常。有限期会员在已有有效期限上续加订单快照的天数，旧订单保留原日历月快照；Windows 原有协议将月会员表现为有期限的 `annual` 类别，完整套餐仍由 `plan=monthly` 标识。

官网保留现有演示二维码。仅在订单 `activationStatus=activated` 且会员有效时展示开通成功；已经拥有另一份有效会员的用户，本次订单开通失败也不会被误报成功。

## 接口

| 方法与路径 | 用途及权限 |
| --- | --- |
| `GET /api/v1/membership-plans` | 公开套餐、服务器价格及是否允许模拟支付 |
| `POST /api/v1/me/membership-orders` | 已登录用户创建待支付订单；请求 `plan`、`method`、`requestId` 及可选 `expectedPlanVersion` |
| `GET /api/v1/me/membership-orders` | 当前账号最近 50 笔真实订单 |
| `POST /api/v1/me/membership-orders/{id}/method` | 当前账号修改待支付订单方式 |
| `POST /api/v1/me/membership-orders/{id}/simulate-payment` | 当前账号模拟支付、开通或重试，返回订单与会员资格 |
| `GET /api/v1/admin/orders` | 管理员搜索、筛选、分页及真实统计 |
| `GET /api/v1/admin/orders/{id}` | 管理员读取用户、订单、关联授权及处理日志 |
| `GET /api/v1/admin/orders/export` | 按筛选条件导出数据，最多 10000 条，前端生成 CSV |
| `POST /api/v1/admin/orders/{id}/retry-activation` | 管理员重试已支付订单，要求 `X-FocusTime-Admin: 1` |
| `GET /api/v1/me/membership` | 当前账号的会员资格、套餐、版本和有效期 |
| `POST /api/v1/me/entitlement-lease` | 既有 Windows 客户端使用的签名会员权益证明 |

后台查询支持 `query`（订单号/邮箱）、`status=all|pending|paid|closed`、`plan=all|monthly|annual|lifetime`、`method=all|wechat|alipay`、`range=all|week|month|quarter`、`exceptionOnly`、`userId`、`page`、`pageSize`。每页最多 100 条，按创建时间、UUID 倒序稳定分页。时间范围按北京时间计算；已超时未支付订单展示为已关闭。统计遵循当前时间范围和用户范围，其他搜索与筛选只影响列表和导出。加载失败显示错误，不以演示数据或假零值回退。

官网继续通过 `/api/website-session/orders`、`/api/website-session/orders/{id}/method`、`/api/website-session/orders/{id}/simulate-payment` 代理订单请求，浏览器不持有后端令牌。

## 数据库迁移

新增 `20261105123719_OrderFulfillment`，在已有 `membership.orders` 上添加：

- `ActivationStatus`：`notStarted`、`failed`、`activated`。
- `MembershipGrantId`、`ActivatedAtUtc`：关联唯一授权及开通时间。
- `ActivationAttemptedAtUtc`、`ActivationErrorCode`：最近尝试和安全的失败代码。
- `PaymentProvider`、`PaymentReference`：支付来源和回执标识，联合唯一索引。

新增 `membership.order_events` 保存创建、修改方式、确认支付、开通成功/失败的时间和操作来源。新增状态/时间索引、授权外键和开通状态约束。历史已支付订单只有存在同订单 ID、用户和套餐的授权时才回填为已开通，其余进入异常；保留原订单、账号和会员数据。

## 环境与后续真实支付

创建模拟订单、修改方式及模拟确认仅允许 `Development` 或 `Testing`，`Production` 返回 404。没有公开的故障注入接口；失败测试在独立数据库中用临时数据库触发器验证真实写入失败及事务回滚。

后续真实支付需要单独实现支付平台订单、回调验签、金额/币种/订单绑定检查、唯一回执验证及退款流程。经过验证的服务端回调才能持久化真实支付确认，并复用 `MembershipOrderService.ActivateAsync` 发放会员。当前前端按钮不能在生产环境调用模拟确认或自行授予会员。

本机联调使用官网 `127.0.0.1:5175`、后台 `127.0.0.1:5181`，二者都连接 `127.0.0.1:5082`。当前 Windows 客户端使用的 `5080` 实例与 `5082` 共享账号和会员数据库，已实测两者均返回相同的购买权益及可验证的签名证明。两实例继续保留各自的开发签名密钥和客户端信任配置；套餐接入时已将两实例升级到支持数据库套餐及期限快照的版本。
