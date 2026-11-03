# 第一阶段验证记录

更新日期：2026-11-03。

## 验收结论

第一阶段基础代码和 Windows 本机验证完成：Release 构建为 0 警告、0 错误，18 项测试全部通过，未跳过数据库测试。发布产物已通过真实 Kestrel HTTP 和命令行重复迁移检查。

Linux / Docker 实际运行尚未验证。本机没有 Docker 或可用 WSL，已配置 Linux CI 和容器运行流程；工作流尚未运行，不能把本机通过视为完整跨平台验收完成。

## 本机环境

| 项目 | 验证版本/环境 |
| --- | --- |
| 操作系统 | Windows x64 |
| .NET SDK | 10.0.403，项目忽略目录内的隔离工具 |
| .NET / ASP.NET Core 运行时 | 10.0.14 |
| EF Core / dotnet-ef | 10.0.14 |
| Npgsql EF Core Provider | 10.0.3 |
| PostgreSQL | 18.6，临时独立实例，回环地址及随机端口 |

依赖来自官方发行源和 NuGet。大包下载中断时使用忽略目录内的分段缓存，仍由 NuGet 正常校验和安装；没有将代理、缓存源或签名验证例外写入项目配置。

## 已实际执行的验证

| 验证项 | 实际结果 |
| --- | --- |
| Release 构建 | 成功，0 warnings / 0 errors |
| HTTP 管线与 PostgreSQL 测试 | Passed 18 / Failed 0 / Skipped 0 |
| 空库迁移 | 成功，生成 Identity 基础表及迁移历史 |
| 重复迁移 | 成功，已有用户数据保持不变 |
| 数据库约束 | normalized email 唯一约束生效 |
| 设计时与运行时模型 | 与最新迁移一致，无待生成模型变更 |
| 幂等 SQL 脚本 | 已成功生成 |
| 发布构建 | 成功，产物位于忽略的 .artifacts/publish |
| 发布产物 --migrate | 连续两次成功退出，第二次无需应用新迁移 |
| 真实 HTTP | 存活 200、就绪 200、系统信息 200、未知路由 404 |
| 生产 OpenAPI | 未公开，返回 404 |
| 数据库停止后 | 就绪 503，存活仍为 200 |
| 临时进程清理 | 测试及冒烟检查创建的 API、PostgreSQL 进程均已停止 |

测试覆盖：400 参数及 JSON 校验、404/405、415、413、429 和 Retry-After、异常响应隐藏细节、跟踪 ID、开发/生产 OpenAPI 边界，以及真实 PostgreSQL 迁移和就绪状态。

首轮测试捕获的 record 校验元数据位置问题已修复，校验标记位于 MVC 使用的构造参数上，修复后完整重跑 18 项通过。

## 验证命令

先按 README 配置 .NET 10 环境。以下命令已实际执行，其中集成测试运行器会自动准备和停止临时数据库：

```powershell
dotnet build FocusTime_Server.slnx -c Release --no-restore --disable-build-servers -m:1
./scripts/Test-Postgres.ps1
dotnet ef migrations has-pending-model-changes --project src/FocusTime.Api --configuration Release --no-build
dotnet ef migrations script --idempotent --project src/FocusTime.Api --configuration Release --no-build --output .artifacts/migrations.sql
dotnet publish src/FocusTime.Api -c Release --no-restore -o .artifacts/publish /p:UseAppHost=false
```

原始测试证据保存在忽略目录：

- .artifacts/postgres-4b146f44157f45d1ad057d5144688414/results/foundation.trx
- .artifacts/host-smoke-337b71cda0834602856b0502221cd2b7/result.json
- .artifacts/host-smoke-337b71cda0834602856b0502221cd2b7/404.json
- .artifacts/host-smoke-337b71cda0834602856b0502221cd2b7/api.stdout.log

## 尚待执行

- [ ] GitHub Actions 的 Linux、PostgreSQL 18 测试。
- [ ] Linux Docker 镜像构建、两次命令行迁移和 HTTP 冒烟检查。
- [ ] 本机 Docker Compose 实际运行（若采用此开发方式）。

上述流程已在 .github/workflows/ci.yml、Dockerfile 和 compose.yaml 中准备。后续执行后将结果补充到本记录。

### 2026-11-03：Linux CI 验收准备

- 已选择通过关联仓库 clm14156-lang/FocusTime_Server 的 GitHub Actions 执行实际 Linux 验收。
- 工作流支持 push、pull_request 和手动触发，记录环境、被验证提交、测试结果、迁移 SQL 和容器运行证据。
- 新增 scripts/Assert-TestResults.ps1，要求至少 18 项测试及 3 项 PostgreSQL 测试全部通过，禁止把跳过数据库测试视为验收通过。
- 新增 scripts/verify-linux-container.sh，使用独立 Docker 网络和临时 PostgreSQL，验证两次迁移、真实 HTTP、生产 OpenAPI 关闭、数据库停机及恢复。
- 已通过 actionlint 1.7.12 工作流静态检查、Git Bash 语法检查，以及测试结果校验器的实际通过/失败结果检查。静态检查在 Windows 执行，不代表 Linux 验收通过。
- 已只读确认工作流引用的 GitHub Actions 版本标签存在。
- 当前本机没有可用 Docker/WSL，没有 GitHub 凭据；GitHub 插件尚未完成安装和账号授权，因此尚未推送或触发工作流，也没有 Linux CI 运行编号。

完成 GitHub 安装及授权后，继续执行：推送当前项目、运行工作流、检查原始测试和容器结果、修复失败项，最后补充运行链接、提交 SHA 和验收结论。

本阶段没有修改 windowBlocked01；账号、设备、同步和会员业务 API 仍按后续阶段实现。
