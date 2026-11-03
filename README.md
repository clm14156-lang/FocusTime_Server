# FocusTime_Server

FocusTime Windows 客户端的独立云端后端，采用 ASP.NET Core 10、EF Core 和 PostgreSQL 模块化单体。

当前已实现基础能力、账号、设备管理及第五阶段专注记录双向增量同步：复用 Windows 客户端，支持持久化队列、归属确认、幂等重试、稳定分页下载、删除传播、账号历史隔离与本地保存恢复。会员及支付仍按后续阶段开发。

本机后端 48 项测试通过，完整客户端 Release 构建及账号/设备/上传/双向同步 4 项真实联调通过。154 项客户端相关测试中 145 项通过，保留 9 项原有失败；另 38 项本地通信及恢复回归通过。完整证据见第五阶段记录；正式 SMTP、安装版与真实两机点选、Linux CI 尚待执行。当前测试后端已升级，运行中的旧客户端需配套更新后体验下载和删除传播。

## 文档

- [后端分析](docs/BACKEND_ANALYSIS.md)
- [七阶段开发计划与进度](docs/DEVELOPMENT_PLAN.md)
- [API 和同步契约](docs/API_CONTRACT.md)
- [数据库迁移](docs/DATABASE.md)
- [第一阶段验证记录](docs/STAGE1_VALIDATION.md)
- [账号接口、配置与客户端接入](docs/ACCOUNT_AUTHENTICATION.md)
- [第二阶段验证记录](docs/STAGE2_VALIDATION.md)
- [设备与会话接口及客户端接入](docs/DEVICE_SESSIONS.md)
- [第三阶段验证记录](docs/STAGE3_VALIDATION.md)
- [专注记录上传、归属及本地恢复](docs/FOCUS_UPLOAD.md)
- [第四阶段验证记录](docs/STAGE4_VALIDATION.md)
- [双向增量同步与删除传播](docs/FOCUS_SYNCHRONIZATION.md)
- [第五阶段验证记录](docs/STAGE5_VALIDATION.md)
- [当前本机测试数据库与账号查询](docs/LOCAL_TEST_DATABASE.md)
- [两台 Windows 电脑的局域网连接](docs/LAN_TESTING.md)

## 目录

| 路径 | 内容 |
| --- | --- |
| src/FocusTime.Api/Common | 错误处理、请求跟踪、限流和系统接口 |
| src/FocusTime.Api/Modules | Identity、Sessions、FocusSync、Membership 的模块边界及契约 |
| src/FocusTime.Api/Infrastructure | PostgreSQL、迁移、健康检查 |
| tests/FocusTime.Api.Tests | HTTP 管线和真实 PostgreSQL 集成测试 |

## 本机运行

当前客户端已启动的本机测试实例使用 `127.0.0.1:55432`，数据库和用户均为 `postgres`，测试口令为 `123456`，API 地址为 `http://localhost:5080`。该实例由客户端现有的 `Start-LocalLoginServer.ps1` 管理，数据与加密启动凭据保持不变。连接及查询步骤见 [本机测试数据库](docs/LOCAL_TEST_DATABASE.md)。下面是单独部署后端时的默认配置示例。

当前也已启用同一 Wi-Fi 网段访问：API 为 `https://192.168.1.5:5081/`，PostgreSQL 为 `192.168.1.5:55432`，仅放行 `WLAN` 的 `192.168.1.0/24`。B 电脑配置包位于 `.artifacts/FocusTime-LAN-client.zip`，使用方法及关闭局域网访问见 [局域网连接](docs/LAN_TESTING.md)。

需要 .NET 10 SDK 和 PostgreSQL 18。若当前会话只有 .NET 8，可先安装 .NET 10；本次开发使用的隔离 SDK 位于已忽略的 .tools/dotnet，可在 PowerShell 中执行：

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

配置本地数据库连接，密码仅放环境变量或开发机 User Secrets，不写入受版本控制的配置：

```powershell
$env:ConnectionStrings__Database = 'Host=127.0.0.1;Port=5432;Database=focustime;Username=focustime;Password=<local-password>'
dotnet restore
dotnet tool restore
dotnet run --project src/FocusTime.Api -- --migrate
dotnet run --project src/FocusTime.Api
```

默认开发地址：http://localhost:5080。

第二阶段运行说明：Development 默认将验证码邮件保存为 `src/FocusTime.Api/.artifacts/mail/*.eml`，不实际发信；开发认证密钥首次使用时自动创建并保存在同目录的 `.artifacts/auth-secret.key`。两者均已被 Git 忽略。正式环境必须配置认证密钥和 STARTTLS SMTP，具体配置见 [账号说明](docs/ACCOUNT_AUTHENTICATION.md)。Windows 客户端默认连接此开发地址，可设置 `FOCUSTIME_API_BASE_URL` 指向自己的 HTTPS API。

| 地址 | 用途 |
| --- | --- |
| /api/v1/system/info | API 名称和版本 |
| /health/live | 进程存活，不依赖数据库 |
| /health/ready | 数据库可连接并且已完成所有迁移才返回 200，否则返回 503 |
| /openapi/v1.json | 仅 Development 环境提供已实现接口的 OpenAPI |

正常启动不会自动执行数据库迁移。未配置可用数据库时，存活和系统接口仍可响应，就绪检查返回 503。

运行发布后的 DLL 时，将工作目录设为发布目录，确保 appsettings.json 正确加载；Dockerfile 已配置对应 WORKDIR。

## 使用 Docker Compose

```powershell
Copy-Item .env.example .env
# 编辑 .env，为本地开发设置密码。
docker compose up -d db
docker compose run --build --rm api --migrate
docker compose up -d api
```

Compose 是本地开发配置，仅绑定回环地址，密码从忽略的 .env 读取。正式 HTTPS、反向代理信任和生产密钥运维在第七阶段完善。

## 测试

```powershell
dotnet test -c Release
```

不配置数据库时，PostgreSQL 集成测试显式显示为跳过。要执行全部验证，提供有 CREATEDB 权限的独立开发/测试实例：

```powershell
$env:TEST_DATABASE_CONNECTION = 'Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=<test-password>'
dotnet test -c Release
```

集成测试仅创建随机 focustime_test_ 前缀数据库，并在结束时删除这些测试数据库，不对应用库执行清空或删除。不要指向生产实例。

Windows 已有便携 PostgreSQL 时，也可使用隔离实例运行器：

```powershell
./scripts/Test-Postgres.ps1 -PostgresBinDirectory '<postgresql-bin-directory>'
```

运行器在忽略的 .artifacts 下初始化独立数据目录，使用随机密码和回环端口，执行测试后停止临时实例。测试产物保留用于检查。

GitHub Actions 已配置 Linux、PostgreSQL 18、重复迁移和 Linux 镜像 HTTP 冒烟验证。工作流文件存在不代表它已经运行，实际结果见验证记录。

工作流会校验数据库测试没有被跳过，并保存名为 linux-foundation-validation 的验证产物。在已有 Linux Docker 环境中，也可以单独执行容器验收：

```bash
bash scripts/verify-linux-container.sh
```

此脚本需要 Docker、curl、Python 3 和核心系统工具；使用独立临时容器及网络，验证结束后清理这些资源，结果保存在 .artifacts/linux-container。
