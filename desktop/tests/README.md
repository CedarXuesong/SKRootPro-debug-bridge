# 桌面端离线回归

所有下列测试使用本地协议端点和生产源文件，无需手机，也不安装或修改模块。

在仓库根目录配置构建缓存并编译一次：

```powershell
$env:TMP = "$PWD\.tools\tmp"
$env:TEMP = $env:TMP
$env:DOTNET_CLI_HOME = "$PWD\.tools\dotnet_home"
$env:NUGET_PACKAGES = "$PWD\.tools\nuget"
$env:APPDATA = "$PWD\.tools\appdata"
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
& .\.tools\dotnet\dotnet.exe build desktop\tests\LookHarness
foreach ($suite in @('--slots-test','--control-test','--input-test','--profile-test','--transport-test','--client-test','--reconfigure-test',
                     '--joystick-test','--preview-test','--self-test','--snapshot-test')) {
    & .\.tools\dotnet\dotnet.exe desktop\tests\LookHarness\bin\Debug\net8.0\LookHarness.dll $suite
    if ($LASTEXITCODE -ne 0) { throw "回归失败：$suite" }
}
```

`--input-test` 覆盖鼠标模式隔离、同时按键、跨模式按住、快速短按、观察叠加、失焦、
滑动时长、连发、10,000 次移动合并、取消后资源释放与重启。
还覆盖 10 槽满布局在四种旋转下的 32 次视角/鼠标切换、双指接力、
按住直接拖动时切换、迟到释放、动态分配后的视角/鼠标槽归还和观察模式切换。
`--slots-test` 检查 1/2/3/10 触点设备上的 100,000 次随机分配：边界、唯一占用、按住期间不换槽、
满载排队、按顺序补位、等待取消、同帧接力顺序和不确定 UP 后的强制清理。
还检查 30/50/100 控件序列化、高编号、旧布局兼容、四种旋转的有限容量画布输出，以及 24,000 帧
动态分配后的实际多指位移守恒（包含单触点硬件回退）。本地协议端点验证 100 个控件逐个输出、
10 个按住加 2 个等待、取消与补位、失焦、双指视角切鼠标、缩小设备容量和停止清理。
`--profile-test` 覆盖非法配置、模式绑定冲突、保存失败保留原文件、迁移与部分损坏恢复。
也检查设置文件被锁定时明确返回失败、保留原内容、无临时文件残留，以及非法字段恢复。
`--control-test` 覆盖四种旋转的 40,016 次坐标往返、逻辑分辨率缩放、Tap/Skill 的 40 ms 保持、
短按排队、Swipe/Scroll/Zoom 的时长/终点/方向、Hold/Toggle、不同间隔 Rapid、
全部非模式控件与画布事件一致、释放/重置和有界队列。
`--client-test` 使用真实随机 nonce/HMAC 的本地协议端点，覆盖认证停滞、重连竞争、
独立通道/排队请求的会话绑定、默认请求超时、进行中及重复 Dispose、畸形文件块、
失败下载保留原文件和成功替换。
同一套件还做 1.4 MB 二进制上传/下载比对、Unicode 路径、空文件覆盖和不完整写入拒绝。
其它套件覆盖原有轮盘随机轨迹、视角接力、前台/旋转切换、连接中断与内存快照。

`--self-test` 的视角模拟器累加所有手指的实际位移。包含 36 组、86,400 帧逐采样位移守恒检查：
四种旋转、40/160/600 ms 重叠、正常/小区域/屏幕边缘、快速反向、变速、空闲、1–100 ms 采样间隔。
同时检查触点方向、区域边界、无全抬空档、持有触点快照与释放；另测新指 0→全速的平滑交接，
以及滤波尾巴、小数余量和单指回退。`--preview-test` 验证画布预览使用同一生产采样器，
四种旋转共 12,000 帧的事件和坐标逐项相同。这些离线检查不代表游戏真机兼容性验证。
浏览器测试页也按多指累加；用 `node desktop/tests/look_lab/test_multitouch.cjs` 可离线验证其触摸处理函数。

## 实际 WinUI 页面

先构建客户端，再编译测试宿主。`PublishedClientDirectory` 可指向待测版本目录。

```powershell
& .\desktop\build.ps1
& .\.tools\dotnet\dotnet.exe build desktop\tests\DesktopInputHarness `
    -p:PublishedClientDirectory="$PWD\dist\SKPDB"
$ui = "$PWD\desktop\tests\DesktopInputHarness\bin\Debug\net8.0-windows10.0.19041.0\win-x64"
Copy-Item dist\SKPDB\*.xbf $ui -Force
New-Item -ItemType Directory -Force "$ui\Pages" | Out-Null
Copy-Item dist\SKPDB\Pages\*.xbf "$ui\Pages" -Force
& .\.tools\dotnet\dotnet.exe desktop\tests\LookHarness\bin\Debug\net8.0\LookHarness.dll --snapshot-ui-peer
```

端点只监听 loopback，输出动态 `port`。保持端点运行，在另一个终端执行：

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
$ui = "$PWD\desktop\tests\DesktopInputHarness\bin\Debug\net8.0-windows10.0.19041.0\win-x64"
& "$ui\DesktopInputHarness.exe" 127.0.0.1 --input-ui-test --port=PORT
# 独立运行快照页面回归：
& "$ui\DesktopInputHarness.exe" 127.0.0.1 --snapshot-ui-test --port=PORT
```

宿主使用固定本地测试密钥并将数据隔离到 `.scratch/snapshot-ui-时间戳`。
输入页面套件检查实际页面的路由、Esc、编辑器命中、活动通知、连续导航、文件列表竞态和关闭清理。
还覆盖主页连接重入与旧尝试隔离、终端重复 Enter/独立通道、内存查询结果乱序、
触摸操作互斥、空白数值拒绝，以及离开再返回后不回写旧结果。
编辑器检查开火/行走/准星的连发模式回显、连发间隔和滚动/缩放时长可编辑。
动态槽编辑器检查实际添加 30 个控件、复制到 100 个、唯一逻辑编号、保存重载、撤销重做，
以及满布局下添加额外轮盘/视角；逻辑编号输入上限为 65533，手机物理容量单独保留。
鼠标模式回归在 10 槽满布局与四种旋转下检查 24 次切换后立即点击，
故意保留旧的隐藏捕获状态或释放捕获，要求原生路由及时恢复且协议端点收到每次点击。
无法取得交互桌面的前台时，会明确输出 `SKIP native page input`；此时页面非交互部分仍运行，
`INPUT_UI_PASS` 不代表被跳过的鼠标实点/键盘交互已验证。
快照套件验证 WinUI 完整解码、画布背景、失败保留、旋转、切页与内存释放；手机快照不写入 PNG。
0.12.38 另验证标题测试版徽章、倾斜水印裁剪/命中穿透、真实侧栏按钮收缩并扩大画布，
以及光标关闭、视角模式、停止、旧模块拒绝、心跳故障后的页面清理。
测试宿主将桌面界面渲染证据保存到 `.scratch/beta-ui-时间戳.png`，它不是采集的手机快照。
`LookHarness --overlay-test` 验证 HUD 大端字段、非法状态、独立心跳、停止、旧模块响应、
单次连接故障重连、持续故障通知和主连接代次隔离。
模块的 Android 无关状态测试可用 JDK 编译 `HudState.java` 与 `skpdb_probe/tests/HudStateTest.java`，
运行 `HudStateTest` 检查模式/旋转、文本、边角位置、六秒租期与异常输入。它不验证 Android 合成层。
`DesktopInputHarness HOST --device-overlay-test` 通过 stdin 接收密钥，使用隔离数据目录，
在真实设备上验证生产外设页面启动、持续心跳和停止，分别开启/关闭光标；不注入测试手势。
它需要有效模块和触摸设备；不能视为手机游戏或横竖屏全部功能的实测。
`LookHarness --micro-test` 覆盖细微输入：0.1 倍灵敏度、零/高平滑、四种旋转、
正反方向和间隔输入，检查落指前与空闲抬指后的余量；另用正式引擎与本地 TCP 端点检查 40 次单计数输入。
WinUI 输入套件另检查编辑器 19 组数值/滑块联动、实时模型、延时保存、连续调整合并撤销与重做、
无变化不增加历史、重新填充字段不改模型、组件选择器、配置面板收缩和退出时保存。

也可在构建测试宿主并复制 XBF 后，用 Python 一次运行两组页面回归并保存带时间戳的日志：

```powershell
python desktop/tests/run-ui-tests.py --report .scratch/desktop-ui.json
```

可选的 `--isolate-overlays` 仅在测试宿主中启用 Microsoft-only 原生 DLL 签名策略，
用于隔离第三方覆盖层故障。正式客户端从 0.12.35 起不再启用此策略，
因为它也会阻止 NVIDIA 等厂商签名组件。开启此选项的测试结果不能证明普通启动兼容性。
0.12.34 的未隔离宿主曾记录本机 GamePP 覆盖层注入崩溃。
页面输入测试调用真实路由回调，
并检查前台/捕获状态；这仍不是人工键鼠操作或手机游戏实测。

正式程序启动检查持续观察错误对话框、确认唯一 SKPDB 主窗口响应且未被模态窗口禁用，
读取实际进程签名策略和加载模块，并检查正常关闭及本次启动后的崩溃/代码完整性事件：

```powershell
python desktop/tests/check-startup.py --client dist/SKPDB/SKPDB.exe --settle-seconds 45
```

报告保留错误对话框文本、模块路径及匹配事件；事件日志读取失败会判为未通过。
检查范围限于观察时段内的离线启动，不能代替长时间或真机测试。
输出目录被测试进程占用时，先停止测试进程再构建。

端点或宿主运行时不要重建其正在使用的输出目录；先停止测试进程，或使用另一配置/输出目录。

0.12.41 新增 `LookHarness --gyro-test`，检查双轴/四旋转/反转、采样时间增益、配置克隆及安装失败时保留触摸。
真实设备入口 `LookHarness HOST --gyro-device-test` 从 stdin 读取密钥，目标使用 com.android.shell / UID 2000，
验证安装 ACK、SKG2、健康检查、关闭和停止；不启动 DFM。它会安装并在 finally 中移除自己拥有的传感器 hook。
模块侧 `gyro_payload_test.cpp` 检查类型转换、四元数、租期、seqlock、UID 布局和序号范围；
`gyro_wrapper_test.cpp` 在独立进程中测试真实 AArch64 跳板与 PAC，不修改 system_server。
完整记录和 Direct Channel 未完成范围见 `skpdb_probe/REVIEW-0.12.13.md`。


0.12.42 新增 `LookHarness --knock-test`，模拟首轮签名 UDP 突发全部丢失后才开放 TCP，
验证新 nonce 重试、HMAC 与 PONG。所有离线套件可用 `python tools/run_regressions.py` 一次运行。
真实设备测试必须显式提供地址。WebUI 真机脚本现在可以安全导入，不会在测试发现阶段尝试连接手机。

物理触屏保护与槽位分配的独立测试：

```sh
g++ -std=c++17 -O2 skpdb_probe/tests/touch_guard_test.cpp skpdb_probe/src/input/touch_guard_payload.cpp -o touch_guard_test
./touch_guard_test
```

此测试可在 Linux/WSL 运行，不安装 hook，不向输入设备写事件。
`touch_event_probe.cpp` 是明确选择真实设备的低层诊断工具，`--clear` 会写释放事件，
`--grab` 仅用于限时原始输入测试（最多 45 秒），不属于正式模块的输入路径。
`skpdb_probe/tests/touch_receiver/` 是可选的空白触摸接收测试 Activity，不启动任何游戏。
