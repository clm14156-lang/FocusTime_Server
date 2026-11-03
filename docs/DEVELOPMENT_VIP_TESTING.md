# 开发环境 VIP 测试

本地测试账号 `vip-test@example.test` 通过真实注册、邮箱验证码和 Identity 密码哈希建立。密码不写入源码，保存在客户端项目 `.codex-build/local-login-server/vip-test-account.dpapi` 中，绑定创建它的 Windows 用户。

正式会员授权表和离线权益凭证尚未实现。为验证客户端已有 VIP 功能，增加独立的服务器开发测试配置：

```json
{
  "DevelopmentMemberships": {
    "已注册账号的用户UUID": "lifetime"
  }
}
```

配置文件为 API 内容目录下的 `.artifacts/test-memberships.json`。当前本地启动脚本将内容目录设为客户端项目的 `.codex-build/local-login-server/api`，所以该文件位于此目录内。

只有 Development 环境、邮箱已经验证、且 UUID 与本地配置精确匹配的用户获得 `lifetime` 测试身份。同邮箱的其他账号 UUID 不生效，未配置的用户返回 `normal`。Production 和 Staging 不加载该测试配置，解析服务也会拒绝应用测试授权。注册和登录请求不能自行指定会员级别。

`POST /api/v1/auth/login`、`POST /api/v1/auth/refresh` 和 `GET /api/v1/auth/me` 的用户资料新增兼容的 `membership` 字符串字段。客户端从服务器读取此字段，联网恢复登录时查询当前状态；离线缓存不授予 VIP。删除指定 UUID 的配置即可撤销本地测试身份，客户端在下一次联网验证后更新。

本地账号和配置在服务器重启后保留。此测试机制不替代正式会员模块、有效期管理、支付或签名离线授权。第六阶段仍需按开发计划实施。

验证：后端开发环境隔离及账号相关测试 28 项通过；客户端会员解析及登录相关测试 21 项通过；真实 API 登录、查询资料与刷新会话均返回 `membership: lifetime`。
