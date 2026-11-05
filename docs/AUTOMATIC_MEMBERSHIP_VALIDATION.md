# 账号登录后自动验证会员

日期：2026-11-04。

## 改动

- 客户端 Service 将当前服务器的公开会员验证公钥作为程序集资源发布，首次使用无需管理员安装会员公钥。
- 登录后继续自动获取服务器签发的账号权益凭证，普通账号、VIP、撤销和退出仍由服务器和本地签名验证流程决定。
- 保留管理员信任覆盖，已配置覆盖目录时完整采用覆盖列表；空列表或配置错误不恢复内置信任。
- 用户提示不再要求手动安装会员公钥。
- 源码已应用到本机 `windowBlocked01`，补丁为 `integrations/windowBlocked01/automatic-membership-trust.patch`。新增测试受客户端本地 `.git/info/exclude` 的 `/tests/` 规则忽略，测试源文件仍随补丁完整交付；没有修改该本地排除规则。

## 验证

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 会员权限、服务入口与新增默认信任测试 | 38/38，无跳过 | `.artifacts/automatic-membership/results/membership.trx` |
| 真实 API、两个设备的会员发放、撤销和退出 | 1/1，无跳过 | `.artifacts/account-client-11eb5a095e5a45a4946758c9ce65599c/results/client-live.trx` |
| Desktop、Service、Agent Release 发布 | 全部成功 | `.artifacts/FocusTime-Client-AccountVip/` |
| 发布程序集与验证构建匹配、ZIP 必要文件和私钥/用户数据排除检查 | 通过 | `.artifacts/automatic-membership/validation-summary.json` |
| Windows PowerShell 5.1 启动脚本语法 | 通过 | `.artifacts/FocusTime-Client-AccountVip/Start-FocusApp.ps1` |

新增测试覆盖干净机器无需配置文件、localhost 和 LAN 两个范围、错误服务器/公钥拒绝、无账号凭证不产生权益、伪造签名拒绝、管理员覆盖/禁用及不安全配置不回退。

## 交付与限制

将 `.artifacts/FocusTime-Client-AccountVip.zip` 复制到已完成局域网 HTTPS 配置的电脑，解压后退出旧版 Desktop、Agent 和 Service，再运行 `Start-FocusApp.cmd`。更新包会沿用已保存的 API 地址；会员公钥不需要额外下载安装。需要 Windows 与 .NET 8 Desktop Runtime。

当前内置公钥只覆盖 `http://localhost:5080/`、`https://192.168.1.5:5081/` 和当前服务器公钥。正式部署或密钥轮换应更新发布配置，详见 [MEMBERSHIP.md](MEMBERSHIP.md)。局域网 HTTPS 证书仍沿用原有连接配置，首次连接全新电脑不在本次会员流程改动范围内。

没有重启当前客户端、Agent、Service 或生产测试服务器，没有更改真实账号和会员授权，尚未在另一台物理电脑启动本次更新包。真实 API 自动测试使用临时数据库与临时账号。
