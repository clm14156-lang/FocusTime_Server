# 第四阶段验证记录

日期：2026-11-03。本阶段代码、临时数据库与真实 API 联调已实现；不包含第五阶段双向下载或云端删除。

## 后端

最终 **40/40 测试通过，无失败、无跳过**，含 25 项真实 PostgreSQL 场景。证据 `.artifacts/postgres-9a5e23592a954c9ba7563259670df86a/results/foundation.trx`；此前的一轮同样通过。

新增上传测试覆盖：上传与单条读回、任务/子任务快照、固定来源设备、相同操作或记录重复上传、同批重复及并发重试、部分成功、内容冲突不覆盖、账号隔离、UTC/任务顺序规范化、准备阶段提前结束、异常记录逐项拒绝、撤销会话不能上传、未开放删除明确拒绝。

数据库迁移新增三张 focus schema 表，沿用原 Identity 和 Sessions 结构。EF 模型检查无待生成变化，幂等 SQL 保存于 `.artifacts/stage4-migrations.sql`，后端发布于 `.artifacts/publish-stage4/`。

## 客户端与本地服务

最近结果 **137 项中 128 通过、9 项为既有失败，无跳过**。证据 `.artifacts/client-stage4-final-results/client-stage4.trx`。失败测试名集合与第三阶段和修改前源码基线相同，没有新增原有功能回归。

| 类别 | 通过 | 失败 |
| --- | --- | --- |
| SQLite 原有存储/迁移测试 | 45 | 0 |
| FocusOutboxTests | 8 | 0 |
| FocusSyncIpcTests | 4 | 0 |
| FocusUploadWorkerTests | 5 | 0 |
| 账号、设备、样式、主题等其他相关测试 | 39 | 0 |
| MainWindowViewModelTests | 27 | 9 |

SQLite 原有测试的当前版本断言更新为 v15，数据、回滚、备份和约束断言未放宽。

新增场景包括：记录和队列同事务、队列失败回滚、开始时归属固定、历史显式导入、API 地址隔离、稳定操作 ID 跨重启保留、退避落盘、迟到确认不能清除新操作或删除标记、超大记录本地保留、IPC 限制 Agent、普通完成暂存恢复、强制自然结束入队、损坏暂存隔离、导入确认在切换账号后取消、提交后丢失响应重试、不持有本地锁等待网络、拒绝错账号发送及不完整确认、本地写入失败不能显示上传完成。

完整客户端方案 Release 编译通过，0 警告 / 0 错误。产物 `.artifacts/client-stage4-release/bin/` 分别包含 Desktop、Service、Agent；没有重启或替换用户正在运行的旧进程。

## 真实联调

最终构建 **3/3 通过，无跳过**，包括第二阶段账号闭环、第三阶段设备管理和第四阶段上传闭环。证据 `.artifacts/account-client-ce0cac4d9e26488b993a7b9acde810cb/results/client-live.trx`；此前的一轮同样通过。

第四阶段使用真实组件：临时 SQLite → Named Pipe → ServiceStateCoordinator → 持久化队列 → CloudAccountClient → Kestrel → PostgreSQL；认证凭证使用临时 DPAPI 文件，验证码使用开发 `.eml`。

实际验证步骤：

1. 创建未归属历史，注册登录 A；历史未自动上传。
2. 通过真实 IPC 开始并完成普通专注，数据库重开后操作 ID 保持。
3. 服务端提交上传后主动丢弃 HTTP 响应，云端已可读取，本地仍待确认。
4. 重试同一操作，服务器重复成功，本地仅此时标记确认。
5. 通过本地服务开始强制专注、推进测试时钟到自然结束，记录自动入队并上传。
6. A 留下一条待上传记录后切到 B，B 无法读取或上传 A 的记录；B 新记录只进入 B。
7. 回到 A 恢复 A 队列，不能读取 B 记录；确认历史归属后历史才上传。

没有使用真实用户账号、生产数据库、实际屏蔽执行器或用户专注数据。测试访问控制宿主为空实现，不修改系统代理、防火墙或用户正在运行的专注。临时 API 与 PostgreSQL 由运行器停止。

## 复现

后端可运行 `scripts/Test-Postgres.ps1`。客户端先使用 .NET 8 将测试项目编译至 `.artifacts/client-stage4-final`，再从后端项目根目录运行：

```powershell
./scripts/Test-AccountClient.ps1 `
  -ClientProjectRoot 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01' `
  -ArtifactsDirectory '.artifacts/client-stage4-final' `
  -TestFilter 'FullyQualifiedName~CloudFocusLiveTests|FullyQualifiedName~CloudAccountLiveTests|FullyQualifiedName~CloudDeviceLiveTests' `
  -ExpectedPassed 3
```

运行器创建随机账号、数据库端口和独立数据目录，检查测试没有跳过。缺少正式 SMTP 不影响本地开发邮件联调，不代表正式邮件已送达。

## 外部验收与限制

- 人工操作现有界面、安装版 Desktop/Service 配套升级、真实两机和 Linux CI 仍待验收。
- 没有云端下载、双向增量或云端删除；删除元数据已保留，云端已有副本暂时仍存在。
- 本地磁盘不能写入时只能保留进程内副本并明确提示，关闭进程不能保证恢复；损坏文件保留供检查。
- 所有归属未登记时开始的记录保持未归属，需确认历史导入；不会凭后续登录自动猜测。
- 完整本地状态快照的既有 1 MiB IPC 上限和大量历史的分页优化需后续验证；上传批次自身已按数量与字节限制。
- 本地界面仍按 Windows 用户展示原有统计；云端归属、队列和请求隔离已经实现，账号切换的全部可见性在第五阶段完善。
