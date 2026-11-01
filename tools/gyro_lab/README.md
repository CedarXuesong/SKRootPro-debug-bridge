# 手机陀螺仪闭环

手机浏览器实际接收 `DeviceMotion.rotationRate`，网页换算成 rad/s 并回传电脑。
测试不会生成或回填传感器读数；没有手机事件时，网页闭环直接拒绝开始。
本页按实测的 Android Chromium 轴序读取 alpha/beta/gamma 为原生 X/Y/Z；
不是 DeviceOrientation 欧拉角的 beta/gamma/alpha 换序。其他浏览器内核需要另行验证。

## 范围

当前后端在经过 ABI 验证的 Qualcomm `sensors.qsh.so` HAL 统一出口处理数据，
位于 SensorService 向应用分发之前，不选择应用 UID。root / shell 是两个验证接收器，
不是 hook 的目标。普通队列的 type 4/16/11/15 经过此出口。

**这是全局 vendor HAL 后端，不是 Linux 内核传感器驱动 hook。**
SSC/DSP Direct Channel 绕过该出口，目前未覆盖。不能据网页或原生预检宣称所有硬件通道可用。
未知 HAL、事件 ABI 或函数入口会拒绝安装，不退回单应用实现。

## 准备

需要 Python 3.10+、Android NDK、授权 SKRoot SDK、JDK 和 ADB。示例路径需按本机配置替换：

```powershell
./skpdb_probe/build.ps1 -Ndk C:/Android/ndk -Sdk C:/AuthorizedSDK -Jdk C:/Java -BuildChannel Canary
./tools/gyro_lab/build_probe.ps1 -Ndk C:/Android/ndk
./tools/gyro_lab/build_hal_test.ps1 -Ndk C:/Android/ndk -Sdk C:/AuthorizedSDK
./tools/gyro_lab/start.ps1 -DeviceHost 192.0.2.1 -Ndk C:/Android/ndk -Background
adb -s DEVICE_SERIAL reverse tcp:8767 tcp:8767
```

启动时密钥隐藏输入，只存在进程内存及诊断加载器的 stdin，不写入文件、URL或命令行。
本地服务仅绑定 `127.0.0.1`，具备页面 token、Host/Origin 校验和 CSP。

## 手机闭环

1. 保持 ADB 连接，在**手机浏览器**打开 `http://127.0.0.1:8767/phone`。
2. 手机页面显示真实 X/Y/Z 角速度、频率和采样数；需要时点“开始读取”。
3. 将手机平放，在电脑 `http://127.0.0.1:8767/` 选择“手机网页闭环”，开始测试。
4. 对比 X/Y 正反向、Z 正向、停止归零、租约过期和卸载恢复。

localhost 用于浏览器安全上下文，不要求放宽不安全来源设置。ADB 断开会中断端口反向映射。
电脑“原生全局预检”只验证两个独立接收器，结果会明确注明“网页未验证”。

验证使用临时诊断库，调用生产 `GyroInstall / GyroApplyPacket / GyroRemove`，
不安装为启动模块。测试有 120 秒上限，速率不超过 1 rad/s，
每次新命令只续约一次，停止发送后由 200 ms 租约归零。
续约使用独立连接，避免采样日志下载阻塞控制流。
归零判定避开 400 ms 过渡窗口，不声称精确测量了 200 ms 边界。
退出前回读 HAL 入口并检查进程身份；恢复未确认时不会显示通过。
原始结果及构建产物位于已忽略的 `.scratch/gyro-lab/`。

“停止并恢复”取消测试；电源按钮停止测试、恢复后关闭服务。
不要强杀诊断进程；需要恢复时保留设备连接。

也可通过 ADB 运行独立控制器，默认要求已打开手机网页：

```powershell
python tools/gyro_lab/hal_device_test.py --adb C:/Android/platform-tools/adb.exe --serial DEVICE_SERIAL
```

## 离线回归

```powershell
python tools/gyro_lab/test_server.py
python tools/gyro_lab/test_measure.py
```
