# 数据库会员套餐与官网价格

付费套餐的唯一数据源为 PostgreSQL `membership.plans`。复用现有管理员白名单/Cookie、Identity 会话、订单及会员授权服务。官网项目实际路径为 `E:\blockedWebsite`，管理后台为 `E:\focusServe_admin`。

## 接口

所有路径以 `/api/v1` 开头，响应禁止缓存。金额单位为整数分，币种为 `CNY`。

| 方法和路径 | 权限与行为 |
| --- | --- |
| `GET /membership-plans` | 匿名；返回 `{ plans, simulationAvailable }`，包含三档价格、天数、上架状态、推荐标记、版本和更新时间 |
| `GET /admin/membership-plans` | 现有管理员身份及权限白名单；返回三档套餐数组 |
| `PUT /admin/membership-plans/{id}` | 同上，另要求 `X-FocusTime-Admin: 1`；更新后返回已保存的套餐 |
| `POST /me/membership-orders` | 已登录；从数据库读取套餐，保存价格、名称、期限及版本快照 |

套餐 ID 固定为 `monthly`、`annual`、`lifetime`。管理员更新体：

```json
{
  "amountFen": 1875,
  "durationDays": 30,
  "available": true,
  "recommended": false,
  "expectedVersion": 1
}
```

`expectedVersion` 必须来自最近的管理员查询。保存通过账号锁、套餐行锁和版本校验串行处理；并发旧版本返回 `409 plan_version_conflict`，不会覆盖其他管理员的改动。价格范围为 1–100000000 分；有限期套餐为 1–36500 天，永久套餐天数必须为 `null`。保存时记录服务器时间和操作人 ID。

创建订单仍接受 `plan`、`method`、`requestId`，新增可选 `expectedPlanVersion`。官网始终携带它：版本变化返回 `409 plan_changed`，下架返回 `409 plan_unavailable`，均不创建订单。前端提交的金额不参与计价。相同用户和请求 ID 的重复请求仍返回原订单，包括调价、下架之后的重试。

## 数据库及历史兼容

迁移 `20261105143836_MembershipPlans`：

- 新增 `membership.plans`：价格、天数、上架及推荐标记、版本、更新时间、更新人；包含条款检查约束及操作人外键。
- 为 `membership.orders` 增加 `PlanName`、`DurationDays`、`DurationMonths`、`PlanVersion` 快照，增加套餐外键及期限约束。
- 首次迁移将原服务端价格写入数据库：月会员 1500 分/30 天，年会员 9800 分/365 天，永久会员 19800 分/永久；随后全部从数据库查询，不再通过代码常量决定成交价。
- 旧订单原有金额、状态和会员授权不变。旧有限期订单回填 1/12 个日历月快照，保留旧的月末算法；新订单按管理员配置的天数履约。

支付、开通及异常重试只使用订单快照，不读取当前套餐期限。因此调价、期限调整或下架不会改变历史订单及已开通权益。既有订单仍可在其有效期内按原条款完成模拟支付。

按现有迁移流程应用后重启 API：

```powershell
dotnet run --project src/FocusTime.Api -- --migrate
```

常规启动不会自动执行迁移。生产环境继续禁止创建模拟订单和模拟支付；公开价格仍可查询，官网购买入口显示暂未开放。真实支付后续扩展见 [支付说明](SIMULATED_PAYMENTS.md)。

## 前端行为

管理后台沿用套餐表和编辑/确认弹窗，加载、保存均使用管理员接口。成功后用服务器返回值刷新价格及时间；失败保持弹窗并显示错误。免费体验规则区域未在本次付费套餐工作中接入服务器。

官网沿用价格页面、三个切换项、购买入口和支付弹窗。共享 `membership-catalog.js` 目录数据，在页面加载、重新获得焦点、恢复可见及每 30 秒获取最新套餐；购买前再次刷新。接口失败清除可购买价格并提供重试，不以固定价格回退。下架套餐可查看但不能新建订单。支付弹窗展示服务器订单快照，调价不会替换待支付订单金额。

价格调整不需要重新部署前端。首次部署涉及模板/模块变更，官网 Node 服务需要重启一次；其内存登录会话会失效，重新登录即可，数据库订单和会员不受影响。

本机运行地址：[官网会员页](http://127.0.0.1:5175/membership/)、[后台套餐管理](http://127.0.0.1:5181/plans)。两者连接 `5082`；客户端 `5080` 实例共享数据库，已升级并验证目录一致。实际测试及边界见 [验证记录](MEMBERSHIP_PLANS_VALIDATION.md)。
