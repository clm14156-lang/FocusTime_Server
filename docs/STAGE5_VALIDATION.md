# 第五阶段验证记录

日期：2026-11-03。第五阶段代码、本机真实联调和现有开发后端升级完成。真实两台物理 Windows 电脑的界面验收、安装版及 Linux CI 仍待执行。

## 后端

**48/48 通过，0 失败，0 跳过**，其中 33 项真实 PostgreSQL 测试。证据：`.artifacts/postgres-ef921ce3d6954a26937877df77409efb/results/foundation.trx`。验收脚本最低要求已提高到 48 项总测试及 33 项数据库测试。

第五阶段新增覆盖：稳定变化页及历史快照、分页过程中并发删除、新变化进入下一轮、删除先于上传、旧收据重放不复活、游标篡改/跨账号/跨查询用途、撤销会话、按字节分页、并发版本连续、第四阶段记录回填和混合批次逐项确认。

EF 重新编译后检查无待生成模型变化；幂等 SQL 为 `.artifacts/stage5-migrations.sql`。后端发布到 `.artifacts/publish-stage5/`，已检查不包含开发邮件、DPAPI 凭据或认证密钥。工作流 actionlint 静态校验通过，不代表 Linux CI 已运行。

## 客户端回归与构建

与第四阶段相同的相关回归集合，加上第五阶段测试，共 **154 项：145 通过、9 项既有失败、0 跳过**。失败测试名集合与第四阶段逐项比较完全一致，没有新增失败。证据：`.artifacts/client-stage5-final-results/client-stage5.trx`。

| 类别 | 通过 | 失败 |
| --- | --- | --- |
| SQLite 原有存储及迁移 | 45 | 0 |
| 原上传队列、IPC 和上传工作器 | 17 | 0 |
| 新增下载存储、游标恢复、并发及隔离 | 17 | 0 |
| 账号、设备、主题和样式等原有相关项 | 39 | 0 |
| MainWindowViewModelTests | 27 | 9 |

上述同步/存储子集 79 项全部通过。额外验证请求关联、广播、重连、服务恢复及关闭行为，**38/38 通过**，证据 `.artifacts/client-stage5-ipc-results/client-stage5-ipc.trx`。不将这些选定测试宣称为整个客户端项目所有测试。

注入 SQLite 游标写入失败及乱序页面后，记录和游标均回滚。测试还覆盖：缓存与本地记录冲突保留、缺少目标时使用快照、导入不完成真实任务、账号切换立即清除旧可见历史、在途旧页面被丢弃、同 ID 不同账号/API 缓存隔离、删除目标保留其他账号历史、延后删除迁移保留 ID、未知确认不删除记录、旧确认不改变删除载荷、游标重置保留待同步操作、同版本内容改变与墓碑复活被拒绝。

真实 Named Pipe 验证了 200 条、重组后超过 1 MiB 的完整历史分页；各传输帧仍遵守原上限。Agent 和切换账号后的旧 token 无权读取历史页。

完整 `FocusApp.sln` Release 构建为 **0 警告、0 错误**。日志 `.artifacts/client-stage5-release.log`，Desktop、Service、Agent 配套产物位于 `.artifacts/client-stage5-release/bin/`。

## 真实 API 联调

最终共 **4/4 通过，无跳过**，为隔离限流和测试数据分两次启动独立 API/数据库：

- 原账号、设备、上传闭环 3/3：`.artifacts/account-client-1fa3b4b719bf4a668f33e6e15f098dc9/results/client-live.trx`。
- 新增双向闭环 1/1：`.artifacts/account-client-5d4be31cd21b4b478f1cbabaa6c86b5d/results/client-live.trx`。
- 新增双向闭环此前一轮同样通过：`.artifacts/account-client-3c50000275ca4847a5e2e198f4897c10/results/client-live.trx`。

新增场景使用两个独立 SQLite、Named Pipe、本地 ServiceStateCoordinator、DPAPI 凭据和安装 ID，连接真实 Kestrel/PostgreSQL。没有替换现有正在运行的客户端，也不使用真实屏蔽执行器。

执行步骤：

1. 两个设备实例登录同一个随机测试账号，安装标识互不相同。
2. A 通过 IPC 保存完成记录并上传，B 下载同一 ID、目标/任务/子任务快照；B 的真实未完成任务不改变。
3. B 删除记录，服务器提交后故意丢弃响应，同时阻断下载。本地删除保留，待删除操作未误确认。
4. 解除重试等待后重复相同删除操作，服务器返回重复成功；B 下载墓碑，A 随后下载并移除原记录。
5. 重放原 upsert 收据及新操作均不能使删除记录复活。
6. 删除目标传播到 A，其他目标记录保留，导入不会重建本机目标。
7. B 切到另一账号，以同一 recordId 保存不同内容；切回后恢复原账号快照和游标，不泄漏另一账号记录。
8. 新建后立即删除、尚未上传的记录在服务器形成墓碑，迟到首次上传返回 record_deleted。
9. 退出账号后隐藏其历史缓存。原账号、设备与强制记录上传联调继续通过。

测试只生成随机账号、临时数据库、临时 SQLite/DPAPI 文件及开发 `.eml` 邮件；临时 API 和 PostgreSQL 已由脚本停止。正式 SMTP 送达未验证。

## 当前测试服务升级

当前持久测试实例已升级至第五阶段，继续使用原 `postgres` 数据库和原开发认证密钥。升级前停止 API 写入并备份数据库及旧后端，再应用新迁移，对比升级前后用户、记录和收据数量，之后沿用原启动脚本重启。

- 升级前后均为 **2 个账号**；原云端记录及收据均为 0 条，未加入联调随机账号。
- 原认证密钥哈希保持一致；变更日志表已创建，当前历史回填数量为 0。
- `http://localhost:5080/health/ready` 和 `https://192.168.1.5:5081/health/ready` 均返回 200，OpenAPI 已包含新的 changes 接口。
- 数据库地址、口令及局域网防火墙范围保持原配置。
- 升级前备份：`.artifacts/stage5-upgrade-11ee5782c59742fd9cfc3f1fc3b58e50/`，包含 `postgres-before-stage5.dump` 和 `previous-api/`；该目录受 Git 忽略。
- 升级摘要：`.artifacts/stage5-local-upgrade.json`。
- 没有停止、替换或重启用户正在运行的 Desktop、Agent 或本地 Service。

客户端源码已更新，但使用新功能仍需配套运行新组件。先完成进行中的专注，再在客户端项目使用原 `Stop-FocusApp.ps1` 和 `Start-FocusApp.ps1 -Configuration Release` 流程。B 也需要新版完整客户端；之前的局域网配置包只负责证书与地址，不包含客户端程序升级。

客户端补丁 `integrations/windowBlocked01/stage5-bidirectional.patch` 已导出，包含 17 个修改文件及 6 个新增文件，并在实际客户端目录通过反向检查；未执行反向修改。

## 复现命令

在后端目录运行：

```powershell
./scripts/Test-Postgres.ps1 -ArtifactsDirectory '.artifacts/backend-stage5'
```

客户端使用 .NET 8，先在客户端目录编译，避免后端 global.json 选择 .NET 10：

```powershell
Push-Location 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01'
& 'C:\Program Files\dotnet\dotnet.exe' build tests/FocusApp.Tests/FocusApp.Tests.csproj `
  --artifacts-path 'E:\FocusTime_Server\.artifacts\client-stage5'
Pop-Location

./scripts/Test-AccountClient.ps1 `
  -ClientProjectRoot 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01' `
  -ArtifactsDirectory '.artifacts/client-stage5' `
  -ServerApiDll '.artifacts/backend-stage5/bin/FocusTime.Api/release/FocusTime.Api.dll' `
  -TestFilter 'FullyQualifiedName~CloudBidirectionalLiveTests' -ExpectedPassed 1
```

原有三项联调可使用同一命令，将过滤器改为 `FullyQualifiedName~CloudAccountLiveTests|FullyQualifiedName~CloudDeviceLiveTests|FullyQualifiedName~CloudFocusLiveTests`，ExpectedPassed 改为 3。运行器要求全部实际执行，跳过不算通过。

## 待外部验收及限制

- 两台物理 Windows 电脑的注册、下载、断网、重启和删除界面操作；安装版配套更新；Linux CI。
- 历史冲突保留双方内容并显示需处理，不自动合并任意字段。
- 本机目标/规则等非历史数据过大、单条异常超大本地历史以及更大规模历史的性能仍需验证；当前历史分页没有提升整个协议的帧上限。
- 变化日志和墓碑暂不清理；数据库恢复到旧版本引起的回退合并策略在部署阶段继续验证。
- 本地 SQLite 的隔离面向正常客户端，不防同一 Windows 用户直接读取数据库文件。
- 会员、离线会员权限和支付尚未实现；下一阶段为会员与权限。
