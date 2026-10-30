# 构建与验证

## 桌面端与 CLI

在 Windows x64 安装 .NET 8 SDK、Windows App Runtime 1.7、Visual Studio 2022 C++ Build Tools
和 CMake。建议使用 PowerShell 7。WinUI 的 NuGet 依赖会在构建时还原。

```powershell
./desktop/build.ps1
./host/build.ps1
dotnet run --project desktop/tests/LookHarness -- --client-test
dotnet run --project desktop/tests/LookHarness -- --knock-test
```

客户端输出至 `dist/SKPDB`。`desktop/tests/README.md` 列出其它离线套件及实际页面测试要求。
测试真实设备必须显式提供其地址；离线测试不需要手机和 Root 密钥。

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
