# 陀螺仪增量注入方案（Xiaomi 25053RT47C / lsm6dsv / SSC）

> 2026-10-30：下文保留早期研究记录，不作为当前安装指南。0.12.13 已改为按 UID 的私有事件副本，
> 回放实际被替换指令，只使用 ELF 已验证页尾留白并记录归属；禁止普通指令签名复用。
> 全局 getpid 网关已删除，状态通过确认写入与 seqlock 发布；协议升级为 SKG2。
> 普通队列支持 type 4/16/11/15，Direct Channel 实际注入仍未实现。详见模块 REVIEW-0.12.13.md。

日期：2026-10-28。基于真机 `192.0.2.1`（Android 16 / SDK 37，内核 6.6.118，
`25053RT47C`，Qualcomm `sun`）实测。

## 结论

- 这台机器的陀螺仪**不在 Linux 内核里**：`lsm6dsv` 由传感器 DSP（Qualcomm SSC）驱动，
  AP 侧 `/sys/bus/iio/devices` 只有 PMIC ADC（`vadc`、`glink-adc`）。
  因此 `iio_push_to_buffers_*` 这类内核 IIO hook 对本机**没有任何作用**。
- 真正可行且隐蔽的落点是 **system_server 内的 `libsensorservice.so`**：
  在 `SensorDevice::poll`（全客户端）或 `SensorEventConnection::sendEvents`
  （按 UID 过滤，仅目标应用）返回给框架的 `sensors_event_t` 上叠加角度速率。
- Hook 本身由 SKRoot 内核通道安装（`access_process_vm` 写代码洞 + 4 字节 B 跳转），
  **不 ptrace、不改内核代码、不加载模块、不注册 kprobe**；对游戏进程完全不可见。
- “增量”实现为**速率偏移**：每个事件的 `data[0..2] += rate[0..2]`，
  速率由桌面鼠标/视角输入平滑生成；不乘 dt、不改时间戳、不改采样率。

## 实测数据通路

```
lsm6dsv (SPI/I2C)
  └─ SSC 固件（sensors PD，独立于 Linux 内核）
       └─ QMI/SMEM（sns_client protobuf）
            └─ /vendor/lib64/sensors.qsh.so         (QSH 子 HAL, 628 个导出符号)
                 └─ /vendor/lib64/sensors.dynamic_sensor_hal.so
                      └─ android.hardware.sensors-service.multihal  (AIDL, pid 1826)
                           └─ MQDescriptor 共享内存事件队列
                                └─ system_server / libsensorservice.so
                                     ├─ SensorDevice::poll            (0x6609c)
                                     └─ SensorEventConnection::sendEvents (0x401d0)
                                          └─ BitTube → 应用 SensorEventQueue
```

关键事实：

| 项 | 实测值 |
| --- | --- |
| 陀螺仪 | `lsm6dsv Gyroscope Non-wakeup`，type 4，handle `0x29`，1–479.85 Hz |
| 未校准陀螺 | type 16，handle `0xa1` |
| 融合 | `Game Rotation Vector`（QTI，type 15，handle `0x97`）、`rotation_vector`（小米，含磁力计） |
| 加速度 | `lsm6dsv Accelerometer`，type 1，handle `0x0b` |
| 内核 IIO | 只有 `iio:device0 vadc`、`iio:device1 glink-adc`，无陀螺 |
| HAL 进程 | `/vendor/bin/hw/android.hardware.sensors-service.multihal`（pid 1826） |
| 框架 | system_server（pid 4115）加载 `/system/lib64/libsensorservice.so` |

`libsensorservice.so` 的关键导出（基址 + 偏移，ASLR 后需运行时解析）：

- `SensorDevice::poll(sensors_event_t*, size_t)`：`+0x6609c`，入口 `paciasp`。
- `SensorEventConnection::sendEvents(const sensors_event_t*, size_t, sensors_event_t*, const wp<const SensorEventConnection>*)`：
  `+0x401d0`，入口 `paciasp`；事件步长 `0x68`（104 字节，即 `sensors_event_t`）。
- `libsensorservice.so` 的 RX 段：文件 `0x40000..0x84550`，映射
  `base+0x40000 .. base+0x85000`；`.plt` 之后 `0x84550..0x85000`（2736 字节）
  为**全零、可执行**，实测可读（`memread` 返回全 0），是现成代码洞。

## 注入点对比

| 落点 | 层级 | 覆盖范围 | 隐蔽性 | 兼容性 | 结论 |
| --- | --- | --- | --- | --- | --- |
| `iio_push_to_buffers_with_timestamp` | 内核 IIO | 仅 IIO 机型 | 高（1 条 B + 隐藏 trampoline，无 kprobe） | 本机 **不可用** | 作为其它机型后端 |
| `glink/smem` QMI 传输 | 内核 | 全部 SSC 客户端 | 低（改所有传感器报文） | 需解析 protobuf/分片，极脆 | 不采用 |
| `sensors.qsh.so` `gyroscope::handle_sns_client_event` | vendor HAL | 所有客户端 | 中（HAL 重启丢 hook，需重装） | 需改 protobuf 载荷 | 备选 |
| multihal 共享队列写入 | vendor HAL | 所有客户端 | 中 | 需定位队列写入点 | 备选 |
| **`SensorDevice::poll`** | system_server | 所有客户端（一致） | 高（用户态，应用不可见） | 稳定，`sensors_event_t` ABI 固定 | **推荐第一版** |
| **`SensorEventConnection::sendEvents`** | system_server | 按 UID 过滤 | 最高（其它进程看到原始值） | 需读连接 UID，复制事件数组 | **目标形态** |
| 游戏进程内 hook | app | 单应用 | 最低（可被自身完整性检查） | 依游戏 | 不采用 |

## 推荐架构

### 1. Hook 安装（内核辅助，无 ptrace）

1. 用 `kallsyms`/ELF 解析 `system_server` 中 `libsensorservice.so` 的加载基址
   （`/proc/<pid>/maps` + 文件偏移），得到 `poll`/`sendEvents` 的运行时地址。
2. 在 `base+0x84550` 代码洞写入 trampoline（`write_kernel_mem` 走 `access_process_vm`
   的 `FOLL_FORCE|FOLL_WRITE`，只读映射也可写；现有 `memwrite` 的可写映射检查需要
   增加一个“代码补丁”特权路径，跳过权限判断）。
3. 用 4 字节 `B` 覆盖函数入口第一条指令（`paciasp`）。

### 2. PAC 处理（必须）

`sendEvents`/`poll` 入口是 `paciasp`，被覆盖后原函数结尾的 `autiasp` 仍会校验 LR。
Trampoline 必须：

```
cave:
    paciasp                 // 复现被覆盖的指令，SP/LR 与调用点一致
    sub  sp, sp, #0x100
    stp  x0, x1, [sp, #0x00]
    ... 保存 x0..x8、x30（已签名的 LR）
    // 读取 state，过滤 type/sensor，叠加 3 个 float
    ...
    ldp  x0, x1, [sp, #0x00]
    ...
    add  sp, sp, #0x100
    b    target+4           // 回到原函数第二条指令
```

不要调用原函数入口（会再次命中 hook）；直接把 `target+4` 作为续执行点。
如果改为“先调原函数再改输出”，只能在 `poll` 的**返回后**修改（post-hook），
本机选择 pre-hook 改输入缓冲更简单。

### 3. 状态块（可写页）

`system_server` 的 `libsensorservice.so` RW 段（`base+0x90000..0x91000`）尾部有
约 0x300 字节零填充，可作为可写状态区（实测 `base+0x90800` 全零）。
结构建议 64 字节对齐：

```c
struct GyroState {          // little-endian, 单写者（模块）/多读者（hook）
    volatile uint32_t seq;  // 奇数=写入中，偶数=稳定
    uint32_t enabled;       // 0/1
    uint64_t updated_ns;    // CLOCK_MONOTONIC
    uint32_t uid;           // sendEvents 版：目标 UID；0=不过滤
    float rate[3];          // rad/s，设备坐标系
    uint32_t magic;         // 'SKGY'
};
```

模块用户态每 4–8 ms 用 `memwrite` 更新 `rate`（seqlock 写）；hook 侧读取：
若 `seq` 变化或 `updated_ns` 超过 200 ms，按 0 处理（自动回零，不依赖用户态存活）。

### 4. 增量算法（用户态算速率，hook 只做加法）

输入：桌面鼠标/视角增量 `dx, dy`（与现有 `LookTouchRelay` 同一路数据）。

```
目标偏航速率 = clamp(dx * sensitivity * curve(speed), -maxRate, +maxRate)   // deg/s
平滑：current += clamp(target - current, -slew * dt, +slew * dt)            // 低通 + 斜率限制
空闲：超过 60 ms 无输入，target 按 exp(-dt/80ms) 衰减到 0
映射：worldYaw 轴 = normalize(重力)（来自加速度计或缓存），rate_vec = current * axis
     或使用固定轴（X/Y/Z 可配，按游戏朝向标定）
```

推荐参数：`maxRate 60–120 °/s`、`slew 1200–2000 °/s²`、低通 `10–15 Hz`、
起停斜坡 `30–60 ms`、超时 `200 ms`。取值保证叠加后仍落在人类手部运动的
幅度/带宽内，且不改变原始噪声底（只是叠加一个低频分量）。

Hook 侧每个事件：

```
if (type == 4 /*GYROSCOPE*/ || type == 16 /*UNCALIBRATED*/)
    data[0..2] += rate[0..2];          // rad/s
if (type == 15 /*GAME_ROTATION_VECTOR*/ 且启用融合)
    用 rate * dt 生成小角度四元数，左乘到 data[0..3] 并归一化
```

`dt` 取事件时间戳差（`sensors_event_t.timestamp`，offset 16），限制在 1–20 ms。
原始陀螺建议只改 type 4；type 16 未校准值通常也被游戏读取，需一并处理。

### 5. 按 UID 过滤（`sendEvents` 版）

`SensorEventConnection` 的 UID 在对象内固定偏移，可在安装时用已知连接
（`dumpsys sensorservice` 的 pid/uid 与内存扫描）标定。命中目标 UID 时，
把 `x1` 指向状态区里的事件副本（`count * 104` 字节，上限 64 条），改完再调原函数；
其它 UID 直接透传。这样只有游戏的进程（含同 UID 的反作弊子进程）看到叠加值。

### 6. 失败保护

- 安装前写“armed”标记；开机后 30 s 内未清除则视为上次启动崩溃，自动不装 hook。
- Trampoline 第一版先做**空实现**（只 `paciasp` + 跳回），验证 24 h 稳定性后再加逻辑。
- 保留原函数前 4 字节，可随时恢复；卸载时先恢复入口再清代码洞。
- 禁止在 `poll` 内做任何可能睡眠/分配的操作；只读状态 + 浮点加法。

## 实测结果（2026-10-28，真机）

已在 `25053RT47C`（system_server pid 8269，`libsensorservice.so` 基址 `0x71ef16f000`）上跑通：

- Hook 点：`SensorEventConnection::sendEvents + 4`（保留入口 `paciasp` 作为 BTI 落点，
  只替换第二条 `sub sp, sp, #0x110`）。
- 返回：trampoline 末尾用**直接 `B`** 回 `sendEvents+8`；不能用 `br`，
  否则 BTI 会以 `SIGILL/ILL_ILLOPC` 在 +8 处杀进程。
- 去重：`(events 指针, 首事件 timestamp)` 相同视为同一批的第二个连接，直接跳过，
  避免同一批事件被多个连接重复叠加（实测不去重时 +1.0 变成 +2.0）。
- 状态块（48 字节，system_server 的 `libsensorservice.so` RW 段
  `base+0x90800`，全零可写）：

  ```c
  struct {
      uint32_t magic;       // 'SKGY' = 0x59474B53
      uint32_t enabled;
      uint64_t updated_ns;
      float    rate[3];     // rad/s，设备坐标系
      uint32_t hits;        // trampoline 调用计数
      uint64_t last_ptr;    // 去重：events 指针
      uint64_t last_ts0;    // 去重：首事件时间戳
  };
  ```

- 验证：`gyroread`（原生 ASensor 轮询，50 Hz）两次平均
  `1.00002 / 0.99999 rad/s`；把 `rate` 清零后回到 `0.00003`；
  system_server pid 稳定；水平仪前台运行时读数与稳定性不变
  （水平仪主要用加速度/重力，验证了只改陀螺）。
- `gyroread` 源码：`.scratch/gyro/gyroread.c`；trampoline：`.scratch/gyro/tramp8.s`。

### 踩过的坑

1. 直接覆盖函数入口（`paciasp`）会让间接调用命中非 BTI 落点 → `SIGILL`。
   必须保留入口，改第二条指令，或在入口放 `bti c`。
2. `movz/movk` 拼 64 位地址时按 4 位一组容易错位（`0x6d29b85800` 被拼成
   `0x6d29b8585800`）→ `SIGSEGV`。用脚本按 16 位分段生成。
3. 用 `br` 回原函数体内也会触发 BTI 检查，返回必须用直接 `B`。

### 当前限制

- 修改的是 `poll` 产出的共享 `sensors_event_t` 批次，因此**所有客户端**都会看到叠加值；
  按 UID 过滤需要改为在 `sendEvents` 内部复制到连接私有缓冲后再改。
- 目前只改 type 4（校准陀螺）；type 16（未校准）、type 15（Game Rotation Vector）未处理。
- 钩子装在 system_server 内存中，重启即消失；需要模块侧的 install/remove/status
  自动重新解析基址、校验代码洞并安装。

## 其它机型：内核 IIO 后端

对陀螺仪走 IIO 的机型（部分 MTK/三星/平板）：

- 用 SKRoot `install_kernel_function_before_hook` hook
  `iio_push_to_buffers_with_timestamp`（或 `..._with_ts_unaligned`），
  入口 `x0=iio_dev, x1=data, x2=timestamp`。
- 在 hook 内比较 `iio_dev->name`（BTF 解析偏移），缓存命中指针；只处理目标陀螺。
- 扫描布局：`iio_dev->channels` + `iio_chan_spec.scan_index/storagebits/shift/sign`，
  时间戳槽位由 `iio_dev->buffer->scan_timestamp` 决定；按 `storagebits/8` 累加偏移。
- 修改 `data` 后调用 `arm64_emit_call_original`，不再二次执行原函数
  （`arm64_before_hook_end(a, false)`）。
- 状态块用 `alloc_kernel_mem` 分配，模块用户态用 `write_kernel_mem` 更新，
  hook 内 seqlock 读取；单位换算 rad/s → 原始计数用 `_scale`（sysfs 读取一次）。

## 分阶段计划

| 阶段 | 内容 | 判定 |
| --- | --- | --- |
| P0 | 运行时解析基址/符号，`memread` 校验代码洞与状态区；`memwrite` 写 4 字节再读回 | 读回一致，进程无异常 |
| P1 | 空 trampoline（`paciasp` + 跳回），安装到 `poll` | 24 h 无重启/无崩溃，传感器正常 |
| P2 | 状态块 + 固定 rate，验证陀螺仪数值偏移；`dumpsys sensorservice` 正常 | 目标应用读数变化，其它应用不变（sendEvents 版） |
| P3 | 接入桌面鼠标速率、平滑/回零、轴映射 | 游戏内视角连续，无台阶感，停手即停 |
| P4 | 融合向量（type 15）一致性；按 UID 过滤；崩溃回滚演练 | 反作弊/多进程读数一致 |

## 风险

- system_server 是单点：hook 代码错误会导致系统 UI 重启甚至开机循环；P1 必须空实现验证。
- 系统 OTA 后 `libsensorservice.so` 偏移变化：运行时按符号解析，不写死偏移。
- 游戏若使用 `SensorDirectChannel`（直接内存映射）会绕过 `sendEvents`，需改 hook `poll` 或 direct 上报路径。
- 含磁力计的 `rotation_vector` 与叠加偏航不一致，可能被高端检测发现；优先只改陀螺与
  `game_rotation_vector`，或同步旋转融合输出。
- 叠加速率过大/过快会产生非人类频谱；参数上限与斜率限制是隐蔽性的关键。

## 复现命令（本机）

```powershell
$env:SKPDB_KEY = '<key>'
dist\skpdb.exe --host 192.0.2.1 run "pidof system_server"
dist\skpdb.exe --host 192.0.2.1 run "cat /proc/<pid>/maps | grep libsensorservice"
dist\skpdb.exe --host 192.0.2.1 memread <pid> <base+0x84550> 64 --mode kernel
dist\skpdb.exe --host 192.0.2.1 memread <pid> <base+0x401d0> 16 --mode kernel
```
