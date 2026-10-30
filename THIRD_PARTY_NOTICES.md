# 第三方依赖

本项目的限制许可仅覆盖相应作者有权许可的 SKPDB 作品，不覆盖下列独立依赖。
本源码包没有包含 SKRoot SDK、NDK、JDK、Windows SDK、NuGet 缓存或第三方静态库。

| 依赖 | 用途与许可边界 |
| --- | --- |
| SKRoot kernel module kit | 外部 SDK，按提供者授权单独获取；本项目不对其作开源或再分发授权 |
| AsmJit（由 SKRoot SDK 提供） | AArch64 代码生成；适用其 Zlib 许可，二进制发布应保留随 SDK 提供的版权/许可文本 |
| CivetWeb（由 SKRoot SDK 提供） | 模块 WebUI 服务；适用其 MIT 许可，二进制发布应保留其声明 |
| .NET 8 / Windows App SDK 1.7 | 桌面运行与 WinUI；按 NuGet 包和运行时所附许可获取/分发 |
| Android SDK / NDK / Bionic / libc++ | 编译和模块运行；适用相应工具链、系统及运行库许可 |
| zlib | Android 系统压缩库，通过动态链接使用，适用 zlib 许可 |

版本以构建脚本、项目锁定的 PackageReference 和实际 SDK 为准。
外部 SDK 可能包含其它依赖；发布二进制前须检查实际 SDK 的完整依赖及必要的声明。
AsmJit 与 CivetWeb 的许可全文位于 `docs/third-party/`。本文件是依赖索引，不提供超出原许可的授权；
外部 SDK 的其它许可义务仍须逐项检查。
