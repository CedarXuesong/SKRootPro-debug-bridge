# SKPDB 游戏外设与内核光标实现计划

> 最终实施记录（2026-10-25）：游戏外设已随客户端 0.12.4 发布；光标改用
> `SurfaceControl + eSkipScreenshot(0x40) + CPU Canvas`，由模块启动独立
> `app_process`，不再走内核 DRM 原子提交。文档中阶段 5 的“纯 C 内核 plane”
> 是原计划，因本机花屏/重启未落地；最终验收以物理屏幕可见、screencap/录屏
> 不可见、鼠标跟随、右键拖动隐藏并在松开后恢复为准。

## 0. 范围

本计划覆盖两个相互依赖、但可以独立验收的模块：

1. **游戏外设**：WinUI 3 客户端页面、键盘/鼠标映射、移动轮盘、视角拖动、动作按钮、按前台应用切换配置。
2. **内核光标叠加层**：纯 C CPU 绘制光标、通过 DRM overlay plane 显示、普通 screencap/录屏不可见。

执行原则：

- 先做不依赖内核改动的游戏外设，再单独推进内核光标。
- 内核光标是高危路径，独立开关、独立测试、失败可回退。
- 任何阶段都不在未验证的情况下直接升级日常设备。
- 所有功能默认关闭，用户显式开启。
- 不承诺“反作弊完全检测不到”；“无痕”只按截图/录屏不可见验收。

## 1. 基线冻结

在开始改代码前完成：

- 记录当前客户端版本：`0.11.2`。
- 记录设备模块版本：`0.11.1`。
- 保留当前可用的：
  - `dist\SKPDB\SKPDB.exe`
  - `skpdb_probe\skpdb_dev_network.zip`
  - 安装工具 `libskpdb_install.so`、`skpdb_loader`
- 用 git 标签或备份目录冻结当前状态。
- 确认设备恢复路径：
  - U净模块列表能删除 SKPDB 模块。
  - `su -c id` 可用。
  - 设备可以重启。
- 新功能使用 feature flag：
  - `GamepadEnabled`
  - `KernelCursorEnabled`

## 2. 总体阶段

```text
阶段 0  基线冻结
阶段 1  触摸控制底层
阶段 2  游戏外设最小可用页面
阶段 3  按前台应用配置
阶段 4  可视化布局与完整输入
阶段 5  内核光标独立验证
阶段 6  光标与外设整合
阶段 7  稳定性、性能、恢复
阶段 8  发布
```

其中阶段 5 可以与阶段 2～4 并行，但不能阻塞游戏外设发布。

---

## 阶段 1：触摸控制底层

### 目标

让客户端可以可靠地执行持续按压、移动、抬起，而不只是 tap/swipe。

### 代码改动

`desktop/SkpdbDesktop/SkpdbClient.cs`：

```csharp
public Task<string> TouchDownAsync(int x, int y, int slot, CancellationToken ct = default);
public Task<string> TouchMoveAsync(int x, int y, int slot, CancellationToken ct = default);
public Task<string> TouchUpAsync(int slot, CancellationToken ct = default);
```

继续复用 25 字节请求：

```text
[I][action][flags][slot u16][x u32][y u32][x2 u32][y2 u32][duration u32]
```

`desktop/SkpdbDesktop/Gamepad/TouchDeviceInfo.cs`：

- 解析 `touch=ready`
- `screen=1280x2772`
- `slots=0..9`
- `abs_x/abs_y`
- 失败时给出原始错误

### 测试

- 单元测试：请求字节布局、slot 编码、坐标边界。
- mock bridge 增加 touch 记录：
  - 收到 Down/Move/Up 顺序
  - slot 是否泄漏
  - 坐标是否归一化正确
- 真机：
  - touch info 正常
  - Down → 移动 → Up 能在 `getevent` 看到完整序列
  - 两个 slot 并行不串线

### 验收门槛 A

- Down/Move/Up 在 mock 与真机都通过。
- 停止时能释放所有测试 slot。
- 失败不崩溃，错误可见。

---

## 阶段 2：游戏外设最小可用页面

### 目标

侧边栏出现“游戏外设”，固定默认布局可以完成移动、视角、开火。

### UI

`desktop/SkpdbDesktop/Pages/GamepadPage.xaml`：

- 顶部：连接状态、当前前台应用、开始/停止操控
- 中间：按屏幕比例的布局画布
- 右侧：固定属性面板，先不做拖拽编辑
- 底部：RTT、事件率、活动 slot

`MainWindow.xaml` 增加导航项，`MainWindow.xaml.cs` 增加 `"gamepad" => typeof(GamepadPage)`。

### 固定默认布局

```text
移动轮盘：center (0.18, 0.76)，radius 0.11，slot 0
视角区：  anchor (0.82, 0.52)，slot 1
开火：    (0.84, 0.82)，slot 2，MouseLeft
跳跃：    (0.90, 0.72)，slot 3，Space
```

### 输入

第一版使用页面焦点采集：

- `KeyDown/KeyUp` 进入 `GamepadEngine`
- `PointerMoved` 累计视角位移
- 输入框编辑时暂停
- `Esc` 停止并释放

### 引擎

`desktop/SkpdbDesktop/Gamepad/GamepadEngine.cs`：

- 60Hz tick
- `KeyboardState` / `MouseDelta`
- 左轮盘状态机
- 视角拖动状态机
- 按钮 hold
- 单飞行请求
- Move 合并
- `ReleaseAll`

### 验收门槛 B

- WASD 四方向和斜向移动正确。
- 松开后角色停止，无残留手指。
- 鼠标拖动视角连续，停止后不反向。
- 左键开火按住/松开正确。
- 停止、失焦、断线、切页全部释放。
- 连续移动 10 分钟无卡死。

---

## 阶段 3：按前台应用配置

### 目标

每个 Android 前台包名保存独立布局，前台切换自动加载。

### 数据

`GamepadProfile.cs`：

- 归一化坐标
- slot 约束
- 按键绑定
- 校验和默认值

`GamepadProfileStore.cs`：

```text
%LOCALAPPDATA%\SKPDB\gamepad\profiles.json
```

- schema 版本
- 备份损坏文件
- 读取失败回退默认
- 不保存 Root 密钥

### 前台检测

`ForegroundWatcher.cs`：

```text
dumpsys activity activities | grep -m1 mResumedActivity
```

- 750ms 轮询
- 包名稳定 500ms 才切换
- 只在页面可见且已连接时轮询
- 过滤系统桌面、设置、锁屏、系统 UI
- 页面离开停止轮询

### 切换流程

```text
前台变化
  → ReleaseAll
  → 加载 package 配置
  → 无配置则用 __default__
  → 页面状态回到 Armed
  → 首次按键后恢复 Capturing
```

### UI

- 左侧配置列表
- 新建、复制、删除、重命名
- 导入/导出 JSON
- 自动切换开关
- 手动锁定当前配置

### 验收门槛 C

- 两个不同包名各自布局独立。
- 切换前台时旧 slot 全部释放。
- 重启客户端后配置保留。
- 损坏 JSON 不影响启动。
- 未配置应用回退默认，不自动按键。

---

## 阶段 4：可视化布局与完整输入

### 目标

用户可以在客户端页面调整布局，完整支持多指、连发、切换和 Raw Input。

### 画布

- 屏幕比例预览
- 拖拽轮盘、视角区、按钮
- 边缘拖拽改变半径
- 方向键微调
- 选中高亮
- 实时显示触摸点
- 坐标归一化保存

### 属性面板

- 轮盘：半径、死区、八方向吸附、冲刺按钮
- 视角：锚点、活动范围、灵敏度、Y 反转、平滑、回中策略
- 按钮：绑定、hold/toggle/rapid、连发间隔
- slot 冲突检查

### Raw Input

`RawMouseInput.cs`：

- 注册 `RIDEV_INPUTSINK`
- 处理 `WM_INPUT`
- 相对位移累计
- 捕获时隐藏/裁剪指针
- 停止时恢复
- 失焦自动停止

### 调度

- 60Hz 单飞行
- Move 只保留最新点
- Down/Up 不丢弃
- RTT > 30ms 降到 30Hz
- 高延迟提示

### 验收门槛 D

- 布局拖拽后配置保存并正确注入。
- 轮盘 + 视角 + 两个按钮同时多指。
- 连发/切换模式正确。
- Raw Input 到屏幕边缘仍能继续转视角。
- 20 次配置切换无 slot 泄漏。
- 60Hz 连续 10 分钟无堆积。

---

## 阶段 5：内核光标独立验证

> 更新：直连内核 DRM atomic 提交已在本机验证为不可用（花屏/重启），已改为
> `SurfaceControl` + `eSkipScreenshot` + CPU Canvas 的独立 app_process 辅助进程。
> 详见 `KERNEL_CURSOR_DESIGN.md` 顶部“实施结论”。

### 前置事实

设备实测：

- 无 `/dev/fb0`
- 10 个 DRM plane 全部为 OVERLAY
- 没有 CURSOR plane
- 活动 FB 为 UBWC
- root 直接 `drmModeSetPlane` 被拒绝

因此采用：

```text
线性 64x64 ABGR dumb buffer
  + 空闲 overlay plane
  + 内核 DRM atomic 提交
```

### 5.1 用户态线性 FB

`cursor_fb_test.c`：

- `DRM_IOCTL_MODE_CREATE_DUMB`
- `MAP_DUMB`
- `mmap`
- `ADDFB2`，格式 `ABGR8888`，modifier `LINEAR`
- 写入一个红色方块，验证 FB ID
- 退出时释放

不设置 plane，不影响显示。

验收：

- 创建成功
- FB 格式/尺寸正确
- 重复创建/销毁无泄漏

### 5.2 纯 C 光标渲染器

`cursor_render.c`：

- 32×32 或 64×64 箭头
- 预乘 alpha
- `cursor_draw(pixels, stride, x, y)`
- 热点坐标固定
- 单元测试输出 PPM/PNG，人工检查边缘和透明度

### 5.3 内核单 plane 提交

`cursor_plane.c`：

- 找到 `drm_device`
- 枚举 `mode_config.plane_list`
- 选一个 `possible_crtcs` 匹配活动 CRTC 的空闲 plane
- `drm_framebuffer_lookup(fb_id)`
- `drm_atomic_state_alloc`
- `drm_atomic_get_plane_state`
- 设置 `crtc/fb/src/crtc_*`
- `drm_atomic_commit`
- 关闭时 `fb=NULL` 或整 plane disable

硬性约束：

- `drm_atomic_commit` 可能睡眠，不能放 kprobe handler。
- 使用 workqueue/kthread 或已知可睡眠的内核执行上下文。
- 不能和 HWC 同时使用同一个 plane。
- 失败必须回滚，不能留下残影。

测试顺序：

1. 只提交一个红色方块，固定位置。
2. 关闭 plane，确认恢复。
3. 位置移动。
4. 显示光标形状。
5. 截图对比。

### 5.4 截图不可见验收

- 屏幕上有红色方块/光标。
- `adb shell screencap` 结果中**没有**该方块。
- MediaProjection 录屏中**没有**该方块。
- 外接相机能看到，记录为已知限制。
- 如果 screencap 仍包含，说明 plane 进入了 SF 合成，方案失败，停止。

### 5.5 控制通道

新增 opcode：

```text
[C][action][x u16][y u16][visible u8]
```

- `action=0` 设置位置/可见
- `action=1` 销毁
- 位置 60Hz 限频
- 模块退出/断线自动隐藏

### 5.6 安全与恢复

- 模块启动后延迟 30 秒再尝试创建 cursor plane。
- 看门狗：10 秒无心跳则关闭 plane。
- 只在用户显式开启时提交。
- 保留旧模块 zip，可随时删除/回滚。
- 先在备用设备/可恢复环境验证。
- 出现黑屏/bootloop 立即删除模块并重启。

### 验收门槛 E

- 光标可见、位置可移动。
- screencap/录屏不可见。
- 退出/断线/锁屏/旋转后无残影。
- 失败不导致显示异常。
- 通过至少 30 分钟稳定性测试。

---

## 阶段 6：光标与外设整合

### 行为

- 游戏外设页空闲：光标 visible=1，跟随 PC 鼠标。
- 按下视角键/进入视角拖动：visible=0。
- 松开视角回到空闲：visible=1，光标出现在当前虚拟位置。
- 页面停止/失焦/断线：隐藏光标并释放触摸。
- 未启用内核光标：不发送任何光标命令。

### 坐标

- PC 鼠标位置映射到手机屏幕归一化坐标。
- 光标位置与视角拖动位置分离：
  - `cursorPosition` 跟随鼠标
  - `lookRemainder` 只服务视角
- 切换配置时重置光标位置到屏幕中心或配置锚点。

### 设置

每份 profile 增加：

```json
"cursor": {
  "enabled": true,
  "size": 48,
  "offsetX": 0,
  "offsetY": 0,
  "hideDuringLook": true
}
```

### 验收门槛 F

- 不转视角时鼠标移动，屏幕光标平滑跟随。
- 转视角时光标消失，视角正常。
- 停止转视角光标恢复，不跳变。
- 切换应用配置光标不残留。
- 截图不可见同时满足。

---

## 阶段 7：稳定性、性能与恢复

### 压力测试

- 60Hz 移动 30 分钟
- 连续开火/移动/视角 10 分钟
- 20 次前台应用切换
- 20 次开始/停止
- 断线、重连、锁屏、唤醒、旋转
- 模块崩溃/被杀后的光标清理
- HWC 占用目标 overlay plane 的冲突场景

### 指标

- RTT 均值/P95
- 发送事件率
- 丢弃 Move 数
- slot 泄漏数
- 光标提交失败数
- 模块内存占用

### 恢复

- 连接断开：停止引擎、隐藏光标、释放触摸
- 模块重启：重新初始化，不沿用旧 FB
- 显示异常：自动禁用光标，保留触摸
- 配置文件损坏：备份并使用默认

---

## 阶段 8：发布

### 版本

- 桌面客户端：`0.12.0`
- 模块：`0.12.0`（新增光标 opcode；游戏外设本身兼容 0.11.1）
- 文档：
  - `desktop/GAMEPAD_DESIGN.md`
  - `desktop/KERNEL_CURSOR_DESIGN.md`
  - `desktop/PERIPHERAL_IMPLEMENTATION_PLAN.md`

### 发布检查

- 客户端构建 0 警告
- 文件页下载/上传回归
- 终端/内存/触摸回归
- 游戏外设 6 个页面导航正常
- 模块安装/卸载流程可回退
- 默认关闭内核光标
- 首次使用有明确风险提示

---

## 3. 任务清单

| 阶段 | 任务 | 产出 | 验收 |
|---|---|---|---|
| 1 | Touch Down/Move/Up | SkpdbClient | mock + 真机 |
| 1 | 触摸信息结构化 | TouchDeviceInfo | 解析正确 |
| 2 | 页面与导航 | GamepadPage | 可进入 |
| 2 | 轮盘/视角/按钮 | GamepadEngine | 训练场可用 |
| 2 | 全量释放 | ReleaseAll | 无残留 |
| 3 | 配置文件 | profiles.json | 重启保留 |
| 3 | 前台检测 | ForegroundWatcher | 自动切换 |
| 4 | 布局编辑 | 画布/属性面板 | 拖拽保存 |
| 4 | Raw Input | RawMouseInput | 边缘无限转视角 |
| 4 | 多指/连发 | 调度器 | 并行正确 |
| 5 | 线性 FB | cursor_fb_test | 创建成功 |
| 5 | 纯 C 渲染 | cursor_render | 图像正确 |
| 5 | 内核 plane | cursor_plane | 屏幕可见 |
| 5 | 截图测试 | 对比报告 | 截图不可见 |
| 6 | 坐标控制 | C opcode | 光标跟随 |
| 6 | 视角隐藏 | 状态机 | 无残影 |
| 7 | 压力/恢复 | 测试报告 | 30 分钟稳定 |
| 8 | 发布 | 0.12.0 | 全回归 |

## 4. 优先级与依赖

必须按顺序：

```text
Touch Down/Move/Up
  → GamepadEngine
  → GamepadPage
  → ProfileStore
  → ForegroundWatcher
  → LayoutEditor
  → RawInput
  → Cursor FB/Render
  → Kernel Plane
  → Cursor Integration
```

可以并行：

- 前台检测与配置 UI 可以并行。
- 光标用户态 FB/渲染器可以在游戏外设阶段并行开发。
- 内核 plane 测试必须独立，不阻塞游戏外设发布。

## 5. 风险最高的三项

1. **DRM overlay plane 与 HWC 冲突**  
   目标 plane 可能被 SurfaceFlinger 占用，导致提交失败或显示异常。

2. **`drm_atomic_commit` 执行上下文**  
   不能在 kprobe/atomic 上下文调用；必须安排在可睡眠的内核线程/工作队列。

3. **“截图不可见”不成立**  
   如果光标被 SF 当成普通 layer 合成，screencap 会看到。阶段 5.4 是硬性停止门槛，不通过就停止该方案。

## 6. 建议的最小发布切片

如果时间有限，先发布：

- 游戏外设页
- 轮盘、视角、开火/跳跃
- 默认布局
- 页面焦点输入
- 全量释放
- 版本 0.12.0，光标开关默认关闭

内核光标作为第二个独立版本，通过截图不可见测试后再开启。
