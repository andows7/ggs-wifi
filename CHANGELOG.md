# Changelog

版本采用 `MAJOR.MINOR.PATCH` 格式；Git 标签使用 `v` 前缀。

## [1.2.0] - 2026-11-22

### Added

- 首次 GitHub 发布，提供 Windows x64 便携 EXE、ZIP 及 SHA-256 校验文件。
- 透明校徽图标，内嵌到 EXE、程序窗口与系统托盘。
- GitHub Actions 构建检查、发布包脚本与使用文档。

### Included

- 临时登录与统一认证分别保存账号密码，临时登录失败后自动切换统一认证。
- 校园网当前接入参数获取、无重定向时的网关配置恢复。
- 断网监测、Windows 登录后自启、系统托盘及 Windows DPAPI 密码加密。
- 兼容标准 HTTP 检测页与 Apple 大写 HTML，避免已联网后重复认证。
- 验证码控件识别、脱敏运行日志、自动换行和可滚动布局。

[1.2.0]: https://github.com/andows7/ggs-wifi/releases/tag/v1.2.0
