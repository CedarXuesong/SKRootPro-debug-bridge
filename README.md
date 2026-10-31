# SKPDB

Android SKRoot 调试桥与 Windows 桌面客户端。提供经过认证的设备连接、文件与终端、
内存工具、真实触屏键鼠映射、手机光标与调试叠加层，以及模块 WebUI。

**源码公开、附限制许可。不得 Fork，不得以 SKPDB 或 SKPDB 改进版名义二次分发。**
完整范围、例外与权利边界以 [LICENSE](LICENSE) 为准；这不是 OSI 定义的开源许可。
GitHub 公开仓库的站内 Fork 权利另见 [发布说明](docs/PUBLISHING.md)。

| 目录 | 内容 |
| --- | --- |
| `desktop/` | WinUI 3 客户端、采样引擎和离线回归 |
| `skpdb_probe/` | Android arm64 模块、WebUI、协议和原生测试 |
| `host/` | Windows 命令行客户端与协议测试 |
| `docs/` | 构建、连接诊断、能力边界和发布流程 |
| `tools/` | 源码发布检查与不包含本地数据的导出工具 |

构建与测试见 [BUILDING](docs/BUILDING.md)，连接与断触排查见
[TROUBLESHOOTING](docs/TROUBLESHOOTING.md)，版本与构建记录见仓库 Releases 和提交历史。
外部依赖说明见 [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)。

模块依赖独立获取的 SKRoot SDK；该 SDK 及其参考工程不包含在本仓库中。
桌面端需要 Windows 10 1809 或更高版本的 x64 系统，完整视觉体验面向 Windows 11。
模块的触摸注入使用已有的物理触屏，不创建虚拟触屏或新增输入设备。
这不构成“无法被检测”的保证，普通应用仍会收到正常的触摸事件。
传感器 Direct Channel 注入尚未实现。厂商提前释放触点的修复仍在验证，实验保护默认关闭，
见 [当前验证状态](docs/VALIDATION.md)。不同厂商、内核与游戏的兼容性需要分别验证。

Root 密钥只用于认证，不应写入仓库、反馈包、截图或命令行参数。
源码包不包含用户布局、实际连接地址、原始诊断日志、本机工具链、缓存或历史构建包。
