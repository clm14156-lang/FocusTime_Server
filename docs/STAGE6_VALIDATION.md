# 第六阶段验证记录

日期：2026-11-03。会员模块和 Windows 客户端接入已完成，本机功能验收通过。安装版、真实两台物理电脑操作和本次 Linux CI 尚待执行。

## 已实现

- 正式年费/永久授权、到期与撤销、版本和审计；本机管理命令幂等及并发保护，无公开发放接口。
- 当前会员、RS256 签名权益证明及公开公钥接口；持久密钥、生产配置验证、账号/设备/会话/安装/API/机器绑定。
- Desktop 刷新及离线恢复、本地 Service 强制模式与自动功能执行检查、SQLite v17 DPAPI 缓存、可信时间与回拨处理。
- 管理员保护的公钥信任文件、导出/安装脚本、更新的局域网配置包。
- 原有本机开发测试 VIP 转为数据库授权，旧测试身份覆盖逻辑移除。策略与命令见 [MEMBERSHIP.md](MEMBERSHIP.md)。

## 自动验证

| 检查 | 结果 | 本地证据 |
| --- | --- | --- |
| 后端完整 Release 测试 | 63/63，通过且无跳过；43 项使用真实 PostgreSQL | `.artifacts/postgres-518ee231c4884520ba5e7c0a2a914330/results/foundation.trx` |
| 客户端完整 Release 构建 | 0 警告、0 错误 | `.artifacts/client-stage6-build/bin` |
| 会员安全及服务入口 | 25/25，无跳过；覆盖签名、算法、重放、隔离、过期、时钟/休眠、注销、公钥撤销、缓存及普通专注 | `.artifacts/client-stage6-tests/membership-final.trx` |
| 会员/SQLite 迁移定向测试 | 69/69，无跳过；其后增加两项时钟/公钥安全测试并单独重跑会员集 | `.artifacts/client-stage6-tests/membership-storage.trx` |
| 既有账号、同步、SQLite、IPC、访问控制及新增会员回归 | 219 项中 210 通过，9 项与第五阶段原有失败逐项一致，无新增失败 | `.artifacts/client-stage6-regression/client-stage6-regression.trx` |
| 真实 API + 两个客户端 + 本地 Service | 最终产物复核 1/1，无跳过 | `.artifacts/account-client-3f902af9fc6149aea28dec61cfc4d353/results/client-live.trx` |

真实联调执行普通用户拒绝、管理命令发放及同操作重试、两个设备取得签名权益、跨设备证明拒绝、本地强制专注启动、撤销后禁止新操作、原专注结束时间保留和退出后清除权限。测试使用空操作访问控制执行器，没有改动系统代理或屏蔽真实程序。

旧 9 项失败涉及两个自定义时长断言、四个游客/会员均可强制启动的旧断言、普通账号权限、退出强制状态与自动规则跳转；详情保留在前阶段记录。这些失败未被删除、跳过或标记为通过。

后端测试额外覆盖年费到期、未来生效、续期选择、生命周期优先级、会话到期上限、用户隔离、会话撤销、授权并发幂等、数据库类型日期约束、私钥持久化、Production/Staging 必须配置密钥及公钥不可签名。

静态检查通过：EF 无待生成模型变更，幂等迁移 SQL 已导出；PowerShell 脚本解析、GitHub workflow 校验、Git Bash 语法检查、Git 空白检查和客户端补丁反向只读校验均通过。Linux 脚本语法检查不等于实际 Linux 容器验收。

## 本机测试实例升级

升级前建立 PostgreSQL 自定义格式备份与旧后端副本：`.artifacts/stage6-upgrade-dd8516b640b84bcfbf528385b5bb2ce7`。升级前后均为 4 个用户、7 个会话、0 条云端记录、0 条同步收据；认证密钥摘要保持不变。一项旧开发 VIP 授权导入正式表并记录原因及操作者。

升级后 `http://localhost:5080/health/ready` 和 `https://192.168.1.5:5081/health/ready` 均为 HTTP 200。运行信息见 `.artifacts/stage6-local-upgrade.json`。没有重启 Desktop、Agent 或本地 Service，没有结束已有专注。

已实际运行系统 Windows PowerShell 5.1 安装脚本，验证管理员所有者及只读用户 ACL；真实 Service 的信任解析器成功读取 localhost、LAN 两个范围的公钥。配置包 `.artifacts/FocusTime-LAN-client.zip` 仅包含公开证书、连接配置、公钥及安装脚本。

## 复现

在已配置 .NET 10 的后端目录：

```powershell
./scripts/Test-Postgres.ps1 -ArtifactsDirectory '.artifacts/backend-stage6'
./scripts/Assert-TestResults.ps1 -ResultsDirectory '填入上一步输出的 results 目录'
```

在 `windowBlocked01` 目录用 .NET 8 构建及验证：

```powershell
dotnet test tests/FocusApp.Tests/FocusApp.Tests.csproj -c Release `
  --artifacts-path 'E:\FocusTime_Server\.artifacts\client-stage6-build' `
  --filter 'FullyQualifiedName~MembershipAuthorityTests|FullyQualifiedName~MembershipServiceTests'
```

回到后端目录，使用上一步生成的 Release 测试产物：

```powershell
./scripts/Test-AccountClient.ps1 `
  -ClientProjectRoot 'C:\Users\Administrator\Documents\Codex\2026-09-16\xia\windowBlocked01' `
  -ArtifactsDirectory '.artifacts/client-stage6-build' `
  -ServerApiDll '.artifacts/backend-stage6/bin/FocusTime.Api/release/FocusTime.Api.dll' `
  -TestFilter 'FullyQualifiedName~MembershipLiveTests' -ExpectedPassed 1
```

运行器建立随机临时数据库、独立密钥/内容目录及开发邮件，结束后停止测试 API/数据库；不使用真实账号密码。测试客户端默认 Configuration 已统一为 Release，可显式传 `-Configuration Debug` 对应调试产物。

## 交付与待验收

客户端实际改动已应用到独立项目，后端仓库保存 `integrations/windowBlocked01/stage6-membership.patch` 以便审阅和迁移。补丁基于第五阶段之后的本机客户端源码，应用前应检查目标版本。

验证摘要为 `.artifacts/stage6-validation-summary.json`。本次没有提交或推送第六阶段改动。

- 新版 Desktop 与 Service 配套启动后人工确认会员中心日期、断网提示、强制模式和自动屏蔽行为；B 电脑需安装公钥并完成相同验证。
- 本轮 Linux 容器验证脚本已加入临时生产私钥挂载、公开公钥和重启指纹检查；本机无 Docker/WSL，未宣称实际运行通过。后续推送后由 GitHub Actions 执行。
- 正式邮件、生产 HTTPS、权限最小化、备份恢复及上线流程继续属于第七阶段；支付未实现。
