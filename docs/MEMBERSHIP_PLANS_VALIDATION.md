# 会员套餐联调验证记录

验证日期：2026-11-05。本记录只描述实际运行的检查。

## 自动化检查

| 范围 | 结果 | 主要证据 |
| --- | --- | --- |
| 后端全部测试及真实 PostgreSQL 集成 | 87 通过，0 失败，0 跳过 | `.artifacts/payment-integration/postgres-8d77279155d34bbca10db421f202f978/results/payment.trx` |
| 官网全部脚本 | 20 个脚本全部通过 | 官网 `package.json` 的 `test`；含新增目录状态/错误/切换检查 |
| 官网最终静态构建 | 通过 | `E:\blockedWebsite\dist`，包含最后的旧模板兼容修复 |
| 后台 TypeScript 与 Vite 构建 | 通过 | `E:\focusServe_admin\dist` |
| 后端 Debug 编译及隔离 Release 发布 | 通过 | `.artifacts/membership-plans/api-publish` |

新增四项 PostgreSQL 用例覆盖：匿名和普通用户无管理员权限、管理员 Cookie/写入头、三档调价及有效期持久化、新订单服务器计价、下架拒绝、历史快照、已授权权益不变、并发版本冲突、旧报价拒绝、非法条款拒绝，以及旧订单在 1 月 31 日购买仍按日历月到 2 月 28 日。重复迁移保留数据且没有待生成模型变更。既有支付、开通失败及重复通知测试也在本次全量测试中执行。

## 本机真实数据库和 API

`5080` 与 `5082` 均连接当前本地 PostgreSQL。三档套餐分别临时调整价格及有限期天数，逐项验证：

| 套餐 | 临时价格/期限 | 验证结果 |
| --- | --- | --- |
| 月会员 | 16.25 元 / 35 天 | 两实例返回一致，新订单保存相同快照，下架后新订单被拒绝 |
| 年会员 | 99.25 元 / 370 天 | 同上 |
| 永久会员 | 199.25 元 / 永久 | 同上 |

同时验证官网 Origin 的 CORS、前端传入 1 分不影响服务器计价、幂等重试保留旧订单、已有订单金额和期限不变、已有会员到期时间不变。脚本结束恢复原套餐值，证据为 `.artifacts/membership-plans/live-plan-evidence.json`。

## 实际浏览器操作

1. 在原后台编辑弹窗将月价从 15 元保存为 **18.75 元**，列表显示服务器更新时间。
2. 官网重新获取目录，月付切换项和专业版卡片均显示 **18.75 元**。
3. 测试账号登录并打开原支付弹窗，真实新订单 `FT50923BFD196E4EE89D5D7F6F263F14D6` 保存 **1875 分、30 天**，弹窗显示 18.75 元。该订单未模拟支付。
4. 模拟另一管理员先更新套餐后，在旧弹窗保存，看到“套餐已被其他管理员更新，请关闭弹窗并刷新列表后重试”，数据库没有被旧版本覆盖。
5. 年会员下架后刷新官网，看到“已下架”和禁用的“套餐暂未上架”按钮。
6. 恢复测试前价格及上架状态，刷新后台和官网均显示月 15 元、年 98 元、永久 198 元，全部上架。上述 1875 分订单快照仍不变。

截图保存在 `.artifacts/membership-plans/`：`admin-saved-price.jpg`、`website-live-price.jpg`、`new-order-price-snapshot.jpg`、`admin-save-conflict.jpg`、`website-delisted.jpg`。浏览器联调曾发现官网 Node 缓存旧模板，已增加目录节点检查并重启该服务，之后动态目录及下单验证通过。

浏览器编辑验证只操作月会员；年会员、永久会员价格与有效期修改通过真实 HTTP API 和 PostgreSQL 测试验证。接口故障时清空价格及重试逻辑通过脚本测试；没有另外进行断网浏览器截图验收。本次没有真实支付扣款，也没有执行生产发布或重跑物理 Windows 客户端界面。

## 本机部署与保留数据

- 已应用 `20261105143836_MembershipPlans`。迁移前备份：`.artifacts/membership-plans/before-plans-20261105150424.dump`。
- 已升级并重启两个本地 API，原认证及会员签名密钥保留，记录见 `.artifacts/membership-plans/upgrade-result.json`。
- 已更新官网源码、静态构建及后台源码、构建，官网运行于 5175，后台预览运行于 5181。官网重启后需重新登录。
- 本次新增测试账号及待支付订单留在测试数据库中，供复查；未删除或改变已有用户、历史订单和授权。
- UI 测试恢复记录：`.artifacts/membership-plans/gui-plan-evidence.json`，`RestoredOriginalPricesAndStatus=true`。
