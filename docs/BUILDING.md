# 构建与验证

## 桌面端与 CLI

在 Windows x64 安装 .NET 8 SDK、Windows App Runtime 1.7、Visual Studio 2022 C++ Build Tools
和 CMake。建议使用 PowerShell 7。WinUI 的 NuGet 依赖会在构建时还原。
仓库的 `global.json` 将 SDK 固定到 .NET 8 系列，避免已安装的更高版本 SDK
使构建期 Appx 任务引用与 .NET 8 不兼容；允许自动使用最新的 8.0 功能带和补丁。

```powershell
./desktop/build.ps1
./host/build.ps1
dotnet run --project desktop/tests/LookHarness -- --client-test
dotnet run --project desktop/tests/LookHarness -- --knock-test
```

客户端输出至 `dist/SKPDB`。`desktop/tests/README.md` 列出其它离线套件及实际页面测试要求。
测试真实设备必须显式提供其地址；离线测试不需要手机和 Root 密钥。

## Release / Canary

两端构建脚本均接受 `-BuildChannel Release`（默认）或 `-BuildChannel Canary`。
通道独立于 MSBuild 的 Debug/Release 优化配置，也独立于实验触点保护开关。

```powershell
./desktop/package-portable.ps1 -BuildChannel Canary
./skpdb_probe/build.ps1 -BuildChannel Canary

./desktop/package-portable.ps1 -BuildChannel Release
./skpdb_probe/build.ps1 -BuildChannel Release
```

版本采用 `版本号[-canary]+12 位提交号`，例如 `0.12.43-canary+0123456789ab`。
桌面标题、设置、画布与模块版本信息、手机调试层显示同一来源的版本号与提交号。
构建脚本默认读取当前 Git 提交；源码导出包携带 `SOURCE-COMMIT.txt`。
从其它来源构建须用 `-GitCommit <完整 40 位提交号>` 显式提供来源，不能伪造提交号。
Canary 版本号带 `-canary`，桌面标题、画布、模块 WebUI 和手机叠加层显示 Canary 版。
Release 不显示这些标识，不发起调试层心跳；模块原生入口拒绝启用调试层，
且其 Java 渲染器编译时移除调试面板和 Canary 图层的创建代码。普通手机光标保留。
Canary 客户端连接 Release 模块时根据模块返回的 `debug_overlay=0` 跳过调试层，
正常启动操控；旧模块未提供此字段时保留原有 Canary 行为。

直接构建桌面工程可传 `-p:BuildChannel=Canary`，原生 ndk-build 对应
`SKPDB_CANARY=1`（默认 `0`）。直接运行 ndk-build 前必须使用相同通道生成光标 DEX；
建议使用顶层模块构建脚本，以保证 C++ 与 Java 通道一致。

## GitHub 自动构建

推送到 `main` 自动运行完整离线回归，构建桌面绿色包与 Android arm64 模块，
并创建名为 `Canary <12 位提交号>` 的 GitHub 预发行版，标签为 `canary-<12 位提交号>`。
正式版对应 `Release <提交号>` / `release-<提交号>`。附件也包含版本号与提交号，
附带 ZIP、SHA-256 与模块依赖许可声明，不覆盖正式发行版。
Actions 的 `Build and publish` 也支持手动选择 `Release` 构建发行包。
只有 `main` 分支构建成功后发布，失败时不会生成不完整版本。

CI 从上游 `abcz316/SKRoot-linuxKernelRoot` 固定提交
`7ad782c8bce0410381e9dae803206c4b76d6f56d` 获取 SDK，并核对静态库 SHA-256。
SDK 只用于临时构建，不包含在发布附件中，无需上传 SDK 或手机密钥到仓库。
工作流使用仓库内置 `GITHUB_TOKEN`；仅发布任务需要 `contents: write`。
构建产物另保留为 Actions artifacts 14 天。

## 更新检查

桌面启动后自动检查，也可在设置中手动检查；有更新时弹窗，点击“前往更新”打开对应提交的发布页。
Canary 只读取 `updates` 发布附件的 `canary.json`，Release 只读取 `release.json`。
同一基础版本的不同提交通过 GitHub 提交祖先关系判断，不按哈希字符串排序，也不会提示降级。
网络失败不影响连接与操控；手动检查会显示失败提示。请求不携带设备地址或 Root 密钥。

模块用 SDK 的 `SKROOT_MODULE_UPDATE_JSON` 注册相同渠道的更新源，
由 SKRoot 管理器执行检查、提示和下载，不重复实现原生网络更新器。
清单的 `version`、`zipUrl`、`changelog` 与上游 SDK 更新示例一致；
其中更新说明由 CI 生成发布附件，不再维护仓库 Changelog。
CI 只在完整构建和发布成功后更新当前渠道清单，另一个渠道保持不变。

## Android 模块

需要 Android NDK r29、Android SDK Platform 35 / Build Tools 35.0.0、JDK 21，
以及有权使用的 SKRoot SDK（包含 `include/` 与 `lib/libkernel_module_kit_static.a`）。
SDK、工具链和第三方静态库不随源码分发。当前构建脚本面向 Windows 主机。

```powershell
$env:ANDROID_NDK_ROOT = 'C:/Android/Sdk/ndk/29.0.14206865'
$env:ANDROID_HOME = 'C:/Android/Sdk'
$env:JAVA_HOME = 'C:/Java/jdk-21'
$env:SKROOT_SDK_ROOT = 'C:/Dependencies/SKRootSDK'
./skpdb_probe/build.ps1
```

也可以用 `-Ndk`、`-Sdk`、`-Jdk` 显式传入路径。脚本生成 WebUI、光标 DEX、
传感器和触点保护载荷头文件，再调用 ndk-build，输出 `skpdb_probe/skpdb_dev_network.zip`。
没有 SDK 时无法完成真实模块链接，不提供替代的伪造 SDK。

默认构建不启用实验触点保护。研究复测可显式添加 `-ExperimentalTouchGuard`，依赖内核 BTF、
物理触屏/任务结构布局与软件注入标记；不支持的内核会明确报错。此路径尚未完成真机复测。
不会改用虚拟触屏，也不会停止厂商触摸服务。传感器 Direct Channel 仍不受支持。
