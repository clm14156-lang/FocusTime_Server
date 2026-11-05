# 开发环境 VIP 测试

第六阶段已替换早期开发配置覆盖。正式会员表、人工发放/续期/撤销、签名离线凭证和本地服务校验已实现，使用方法见 [MEMBERSHIP.md](MEMBERSHIP.md)，证据见 [STAGE6_VALIDATION.md](STAGE6_VALIDATION.md)。

原本机 `vip-test@example.test` 账号、密码哈希和会话均保留。此前 `.artifacts/test-memberships.json` 中的一项 lifetime 配置已经导入 `membership.grants`，并在审计表记录 `stage6-upgrade` 和导入原因。旧 JSON 不再参与会员判断，删除或修改它不会改变正式授权。

测试密码仍保存在客户端 `.codex-build/local-login-server/vip-test-account.dpapi`，绑定创建它的 Windows 用户，没有写入源码或 Git。今后使用管理命令调整会员；普通注册登录不能自行指定会员类型。新版 Desktop 与本地 Service 必须配套更新，且须安装受信任会员公钥。
