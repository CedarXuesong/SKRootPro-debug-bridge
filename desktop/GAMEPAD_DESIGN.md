# SKPDB 游戏外设详细设计

## 1. 目标

在现有 WinUI 3 客户端中加入“游戏外设”页面，用键盘和鼠标驱动真实触摸屏的内核触摸注入：

- 左侧移动轮盘：WASD 控制角色移动，按下、持续拖动、松开完整对应真实触摸事件。
- 右侧视角滑动：鼠标移动映射为触摸拖动，支持灵敏度、反转、平滑和回中。
- 动作按钮：键盘/鼠标按键映射为屏幕按钮，支持按住、切换、连发。
- 按 Android 前台应用保存独立配置文件，前台应用变化时自动切换。
- 所有注入走现有真实触摸屏 `input_dev` 路径，不引入 uinput 虚拟设备。

不在本期范围：

- 屏幕画面采集/串流；页面只做控制，不显示手机画面。
- 复杂脚本、宏、条件循环、录制回放。
- 绕过反作弊或隐藏注入痕迹；注入的是正常触摸事件，仍可能被内核 hook、`getevent`、触摸统计或反作弊检测。

## 2. 现有基础

设备端已经具备以下能力（`skpdb_probe/src/input/touch.cpp`）：

- `kTouchInfo = 0`：查询触摸屏名称、`/dev/input/eventX`、屏幕尺寸、绝对轴范围、slot 数量。
- `kTouchDown = 1`：在指定 slot 按下。
- `kTouchMove = 2`：在指定 slot 移动。
- `kTouchUp = 3`：抬起指定 slot。
- `kTouchTap = 4`、`kTouchSwipe = 5`：便捷动作。
- 触摸请求固定 25 字节：`[I][action][flags][slot u16][x u32][y u32][x2 u32][y2 u32][duration u32]`。
- 坐标是屏幕像素，模块内部按 `abs_x/abs_y` 范围换算。
- 触摸事件之间由 `TouchMutex` 串行化，所有 slot 共用同一个真实 `input_dev`。

当前客户端只封装了 `TouchInfoAsync`、`TouchTapAsync`、`TouchSwipeAsync`，缺少持续按压需要的 `Down/Move/Up` 封装；这是游戏外设首先要补的底层能力。

## 3. 页面结构

侧边栏新增“游戏外设”，位置在“触摸”之后：

```text
主页
终端
文件
内存
触摸
游戏外设
设置
```

页面采用三栏布局，窗口较窄时自动折叠为单栏：

```text
┌──────────────────────────────────────────────────────────────────┐
│ 游戏外设    [已连接 192.0.2.1] [前台：com.xxx] [停止操控] │
├───────────────┬────────────────────────────────┬─────────────────┤
│ 配置文件       │ 手机屏幕布局预览                │ 选中元素属性     │
│ ├ 全局默认     │ ┌────────────────────────┐     │ 名称/类型        │
│ ├ com.xxx      │ │  [移动轮盘]      [视角] │     │ X/Y/半径        │
│ ├ com.yyy      │ │                        │     │ 死区/灵敏度      │
│ └ + 新建       │ │        [开火] [跳跃]    │     │ 按键/模式        │
│               │ └────────────────────────┘     │ 绑定触摸 slot    │
│ [自动切换]     │ 拖拽元素调整位置；滚轮缩放预览   │ [删除元素]       │
├───────────────┴────────────────────────────────┴─────────────────┤
│ 状态：已捕获 · RTT 8ms · 62 事件/秒 · 活动 slot 0,1,3           │
└──────────────────────────────────────────────────────────────────┘
```

顶部状态条：

- 连接状态、设备地址、模块版本。
- 当前前台包名/Activity，来源是轮询检测。
- “开始操控/停止操控”主按钮。
- 前台切换时显示“配置已切换：com.xxx”。

左侧配置列表：

- 每个前台包名一个配置。
- “全局默认”作为兜底。
- 支持复制、重命名、导入、导出、删除。
- 显示配置是否启用、最后使用时间。

中间画布：

- 按手机屏幕比例缩放，背景用纯色/网格，不放屏幕截图。
- 元素类型：`moveStick`、`lookArea`、`button`。
- 拖拽改变位置，边缘拖拽改变半径/尺寸。
- 选中元素高亮，键盘方向键微调。
- 显示触摸点预览：轮盘中心、当前手指位置、按钮热区。

右侧属性面板：

- 通用：名称、类型、X/Y（归一化 0..1）、slot。
- 轮盘：半径、死区、斜向归一化、八方向吸附、松开方式。
- 视角：锚点、最大活动范围、水平/垂直灵敏度、反转 Y、平滑、回中策略。
- 按钮：绑定键、触摸模式（按住/切换/连发）、连发间隔、按住时长。
- 只允许编辑当前选中元素；未选中时显示操作提示。

## 4. 数据模型

### 4.1 配置文件

保存在：

```text
%LOCALAPPDATA%\SKPDB\gamepad\profiles.json
```

结构：

```json
{
  "schema": 1,
  "profiles": [
    {
      "id": "com.example.game",
      "package": "com.example.game",
      "displayName": "示例游戏",
      "screen": { "width": 1280, "height": 2772 },
      "move": {
        "enabled": true,
        "centerX": 0.18,
        "centerY": 0.76,
        "radius": 0.11,
        "deadzone": 0.12,
        "slot": 0,
        "snap8": false,
        "sprintKey": "LeftShift",
        "sprintButton": "sprint"
      },
      "look": {
        "enabled": true,
        "anchorX": 0.82,
        "anchorY": 0.52,
        "maxRadiusX": 0.16,
        "maxRadiusY": 0.22,
        "sensitivity": 1.6,
        "invertY": false,
        "smoothing": 0.25,
        "slot": 1,
        "recenterMode": "onIdle",
        "recenterDelayMs": 80
      },
      "buttons": [
        {
          "id": "fire",
          "label": "开火",
          "x": 0.84,
          "y": 0.82,
          "radius": 0.06,
          "slot": 2,
          "binding": "MouseLeft",
          "mode": "hold",
          "tapIntervalMs": 80
        }
      ],
      "bindings": {
        "MoveUp": "W",
        "MoveDown": "S",
        "MoveLeft": "A",
        "MoveRight": "D",
        "Sprint": "LeftShift"
      },
      "autoSwitch": true
    }
  ],
  "lastPackage": "com.example.game"
}
```

坐标全部使用 **归一化 0..1**，运行时根据 `TouchInfo` 返回的屏幕尺寸换算。这样换设备或横竖屏切换不会把布局写死。`screen` 仅用于显示和校验，不作为绝对像素来源。

约束：

- `slot` 范围 `0..9`，同一配置内不能重复。
- 轮盘和视角必须占用独立 slot。
- 按钮默认从 `slot 2` 开始分配。
- 最多 8 个同时按下的按钮；超过时拒绝新增。
- `id` 在配置内唯一，使用小写字母/数字/下划线。

### 4.2 前台应用配置

配置文件按 `package` 匹配。检测不到时使用 `__default__`。

自动切换流程：

1. 每 750ms 读取一次前台 Activity。
2. 提取 package；包名变化且稳定超过 500ms 才切换，避免启动动画/弹窗抖动。
3. 如果正在操控：先释放全部触摸，再加载新配置。
4. 新配置存在且 `autoSwitch=true`：恢复“已捕获”状态，但等待用户再次按键，避免误触。
5. 没有配置：回退全局默认，并显示“未配置此应用，使用默认布局”。
6. 前台是系统桌面、设置、锁屏、SKPDB 自身时不自动激活操控。

前台检测的第一版可以通过现有命令通道执行：

```text
dumpsys activity activities | grep -m1 mResumedActivity
```

解析 `com.package/.Activity`。后续可以在模块里增加专用 opcode，避免每次 `dumpsys` 开销；第一版不改模块，避免再次重启安装。

## 5. 输入采集

### 5.1 键盘

第一版使用页面级采集，不装全局键盘钩子：

- 页面获得焦点且“已捕获”时，`KeyDown/KeyUp` 进入 `GamepadEngine`。
- `TextBox`/`NumberBox`/配置编辑器获得焦点时暂停采集，避免输入框里的文字被当成按键。
- `Escape` 立即停止捕获并释放全部触摸。
- 忽略系统按键重复（`e.KeyStatus.WasKeyDown`），只处理状态变化。

这样不会在用户切到其他桌面程序时偷偷拦截键盘，副作用最小。若后续需要“窗口失焦也继续”，再单独设计 `WH_KEYBOARD_LL` 全局模式，作为显式危险选项。

### 5.2 鼠标视角

使用 **Raw Input**，不用 `PointerMoved` 的屏幕绝对坐标：

- 在 WinUI 窗口 HWND 上注册 `RIDEV_INPUTSINK`，采集 `WM_INPUT` 的 `RAWMOUSE.lLastX/lLastY` 相对位移。
- 捕获时隐藏指针并把指针限制在页面控制区；停止时恢复指针和裁剪。
- 相对位移不会在屏幕边缘停住，适合无限视角旋转。
- 鼠标左键、右键、中键、滚轮映射到按钮；滚轮每格触发一次 tap。
- 只在页面为前台窗口且已捕获时处理 Raw Input。

如果第一版不做 Raw Input，可以退化为页面内 `PointerMoved` 累计位移，但鼠标到达屏幕边缘后视角会停；这只能作为调试模式，不作为最终交互。

### 5.3 输入状态机

```text
Idle
  └─ 点击“开始操控” -> Armed
Armed
  ├─ 前台 profile 可用 + 页面获得焦点 -> Capturing
  └─ 停止 -> Idle
Capturing
  ├─ 按键/鼠标事件 -> Engine.Tick -> 触摸请求
  ├─ 失去焦点 -> ReleaseAll -> Armed
  ├─ Escape/停止/断开 -> ReleaseAll -> Idle
  └─ 前台包变化 -> ReleaseAll -> 切换配置 -> Armed/Capturing
```

任何退出路径都必须执行 `ReleaseAll`，按 slot 从高到低发送 `Up`，保证不会留下按住不放的触点。

## 6. 触摸引擎

新增 `GamepadEngine`，与 UI 解耦，方便测试：

```csharp
public sealed class GamepadEngine : IAsyncDisposable
{
    public Task StartAsync(GamepadProfile profile, TouchDeviceInfo device);
    public void SetKey(string key, bool down);
    public void AddMouseDelta(int dx, int dy);
    public void SetMouseButton(string button, bool down);
    public Task StopAsync();          // 释放全部手指
    public GamepadMetrics Metrics { get; }
}
```

内部结构：

```text
KeyboardState      -> MoveVector / ButtonState / Sprint
MouseDelta         -> LookAccumulator
Profile            -> normalized positions
TouchScheduler     -> 60 Hz tick, coalesced moves
SkpdbClient        -> Down/Move/Up requests
```

### 6.1 轮盘算法

每个 tick：

1. 从 WASD 计算 `(dx,dy)`：
   - `dx = right - left`
   - `dy = down - up`（屏幕 Y 向下）
2. 长度为 0：如果当前轮盘按下，发送 `Up`，状态复位；结束。
3. 归一化：`(dx,dy) /= length`，保证斜向不会比直线快。
4. 可选八方向吸附：将角度吸附到最近的 45°。
5. 死区：`length < deadzone` 视为 0；因为键盘是数字输入，实际只在全速/停止之间切换，死区主要用于摇杆式输入扩展。
6. 目标点：

```text
tx = centerX * screenW + dx * radius * screenW
ty = centerY * screenH + dy * radius * screenH
```

7. 第一次越过死区：在轮盘中心 `Down(slotMove, center)`，随后 `Move(slotMove, tx, ty)`。
8. 持续按住：每个 tick 仅在坐标变化超过 1px 时发送 `Move`。
9. 松开全部方向键：`Up(slotMove)`。
10. 冲刺：`Sprint` 键映射为配置中的冲刺按钮，默认按住；不通过轮盘幅度模拟。

伪代码：

```csharp
var v = Keyboard.MoveVector();
if (v.Length == 0)
{
    if (moveDown) await UpAsync(move.Slot);
    moveDown = false;
    return;
}
v = v.Normalized();
var target = move.Center + v * move.Radius;
if (!moveDown)
{
    await DownAsync(move.Slot, move.Center);
    moveDown = true;
}
if (Distance(target, lastMovePoint) > 1)
    await MoveAsync(move.Slot, target);
```

### 6.2 视角算法

维护：

```text
lookAnchor      配置锚点，归一化
lookPosition    当前手指位置，归一化
lookAccum       累计鼠标位移
lookDown        当前是否按下
lastMouseTime   最后一次鼠标移动时间
```

每个 tick：

1. 取 `lookAccum`，清零。
2. `dx = accumX * sensitivity / screenW`，`dy = accumY * sensitivity / screenH`（归一化增量）。
3. `target = clamp(lookPosition + (dx,dy), lookAnchor ± maxRadiusX/Y)`。
4. 首次有位移：`Down(slotLook, lookAnchor)`，`lookPosition=lookAnchor`。
5. 每个 tick 发送 `Move(slotLook, target)`；只在变化超过 1px 时发。
6. 到达活动范围边缘：保留当前点，丢弃超出部分；不要立刻 Up/Down，否则游戏会看到断触。
7. 鼠标停止超过 `recenterDelayMs` 且仍有位移余量：执行 `Up(slotLook)`，再 `Down(slotLook, lookAnchor)`，把手指放回中心，允许继续向同方向旋转。
8. 鼠标离开控制区/失焦：`Up(slotLook)`。

`recenterMode` 可选：

- `onIdle`（默认）：停止移动后回中，移动过程中不断触。
- `edge`：到达边界立即回中；可能造成轻微视角跳变。
- `never`：不自动回中，适合短时间视角调整。

### 6.3 按钮

每个按钮是独立 slot 的触点：

- `hold`：按键按下 `Down`，松开 `Up`。
- `toggle`：第一次按 `Down`，第二次按 `Up`；切页/停用时强制 Up。
- `rapid`：按住期间每 `tapIntervalMs` 发送一次 `Down`+`Up`；松开停止。
- 滚轮：每次滚一格映射一次 tap，不保持按下。
- 同一个 slot 不允许两个按钮同时使用；配置校验阶段拦截。
- 按键映射去重：一个键盘键不能同时绑定两个持续按钮，除非显式允许组合。

按钮按下/抬起使用独立 slot，和轮盘/视角并行，保证移动、看视角、开火可以同时发生。

### 6.4 请求调度与延迟

现有 TCP 协议是“一请求一响应”，`SkpdbClient` 内部串行化，不能并行刷 60 个请求。

调度策略：

- 单飞行请求（single in-flight），最多一个 `Down/Move/Up` 等待响应。
- 每个 tick 生成 **最新状态快照**，不排队历史移动。
- 如果上一帧的 `Move` 还没返回：
  - 丢弃旧的未发送移动，只保留最新目标点（coalesce）。
  - `Down/Up` 永不丢弃，优先执行。
- 目标频率 60Hz；RTT > 30ms 自动降到 30Hz；RTT > 60ms 只保留按键事件并提示延迟过高。
- 所有请求按 slot 分组；一次 tick 内先处理 Up（释放），再处理 Down，最后 Move，避免 slot 冲突。

可选优化（第二期，不阻塞第一版）：

- 增加模块 opcode `I6` 批量移动：`[I][6][count][slot][x][y]...`，一次 TCP 请求发送多个 slot 的样本，降低 RTT 敏感度。
- 第一版不改模块协议，只补 `Down/Move/Up` 客户端封装。

## 7. 前台检测与自动配置

### 7.1 查询

第一版通过现有 `ExecAsync`：

```text
dumpsys activity activities | grep -m1 mResumedActivity
```

解析：

```text
mResumedActivity: ActivityRecord{... u0 com.example.game/.MainActivity t123}
```

提取 `com.example.game`。

每 750ms 轮询一次；只在游戏外设页可见且已连接时轮询，页面离开后停止。

### 7.2 切换策略

- 包名稳定 500ms 才生效。
- 前台变化时立刻 `ReleaseAll`，不能把旧游戏的手指带到新应用。
- 新配置存在：加载布局；如果 `autoSwitch` 开启，状态回到 `Armed`，等待第一次按键后恢复 `Capturing`。
- 新配置不存在：使用 `__default__`，显示“未配置”提示，不自动触发按键。
- 手动锁定按钮：用户可临时锁定当前配置，忽略前台变化；解锁后恢复自动切换。
- 系统包过滤：`com.android.systemui`、`com.android.settings`、桌面 launcher、锁屏、`com.skpd...` 自身不自动激活配置。

### 7.3 后续模块 opcode

如果 `dumpsys` 轮询开销不可接受，再增加：

```text
Request:  [F]
Response: [F][0]package=com.example.game\nactivity=.MainActivity\nts=...
```

由模块读取 ActivityManager/窗口服务，避免每次 fork `dumpsys`。该改动需要重新安装模块并重启，放在第二阶段。

## 8. 客户端代码结构

```text
desktop/SkpdbDesktop/
  Gamepad/
    GamepadProfile.cs       配置模型、校验、归一化坐标
    GamepadProfileStore.cs  profiles.json 读写、迁移、默认值
    GamepadEngine.cs        输入状态机、tick、触摸调度
    RawMouseInput.cs        HWND 子类化 + WM_INPUT
    ForegroundWatcher.cs    dumpsys 轮询、包名稳定/防抖
    TouchDeviceInfo.cs      touch info 解析
  Pages/
    GamepadPage.xaml
    GamepadPage.xaml.cs
  SkpdbClient.cs            增加 TouchDown/Move/Up
  MainWindow.xaml           侧边栏新增“游戏外设”
  MainWindow.xaml.cs        tag -> GamepadPage
```

### 8.1 SkpdbClient 扩展

```csharp
public Task<string> TouchDownAsync(int x, int y, int slot,
    CancellationToken ct = default);

public Task<string> TouchMoveAsync(int x, int y, int slot,
    CancellationToken ct = default);

public Task<string> TouchUpAsync(int slot,
    CancellationToken ct = default);
```

内部继续复用现有 `TouchAsync(action, ...)`：

- `kTouchDown = 1`
- `kTouchMove = 2`
- `kTouchUp = 3`

### 8.2 触摸信息结构化

把当前 `TouchInfoAsync` 的文本解析为：

```csharp
public sealed record TouchDeviceInfo(
    string Name,
    string EventPath,
    int ScreenWidth,
    int ScreenHeight,
    int SlotMax,
    bool HasButtonTouch);
```

解析 `screen=1280x2772`、`slots=0..9`。解析失败时禁止开始操控，并显示具体错误。

### 8.2.1 横竖屏旋转

`touch info` 返回的是触摸屏**物理原生**坐标（例如 1280x2772）。Android 旋转后应用看到的是逻辑坐标（例如 2772x1280），但内核 `input_event` 仍按物理触控板坐标注入。因此客户端必须做逆变换：

```text
rotation 0: native = (x, y)
rotation 1: native = (nativeW - y, x)
rotation 2: native = (nativeW - x, nativeH - y)
rotation 3: native = (y, nativeH - x)
```

其中 `(x,y)` 是当前逻辑屏幕坐标，`nativeW/nativeH` 是触摸屏原生宽高。旋转和逻辑尺寸来自 `dumpsys display` 的 `DisplayViewport{type=INTERNAL...}`；页面每 3 秒刷新一次，检测到旋转变化时先释放全部触点，更新尺寸，再按新方向恢复操控。横屏时画布直接按 2772:1280 预览，不再使用固定 1280:2772 比例。

### 8.3 页面生命周期

```csharp
OnNavigatedTo:
  - 连接可用则加载 profile 列表
  - 启动前台轮询
OnNavigatedFrom:
  - StopCaptureAsync()
  - 停止轮询
  - 恢复鼠标指针/裁剪
```

窗口失焦、页面隐藏、连接断开、应用退出都触发同一套 `ReleaseAll`。

## 9. 设置与新手流程

首次进入页面：

1. 若未连接：显示“请先在主页连接设备”。
2. 读取触摸信息；显示屏幕尺寸和 slot 范围。
3. 如果当前前台包没有配置，创建默认布局：
   - 轮盘：左下 `(0.18, 0.76)`，半径 `0.11`，slot 0。
   - 视角：右侧 `(0.82, 0.52)`，slot 1。
   - 开火：右下 `(0.84, 0.82)`，slot 2，`MouseLeft`。
   - 跳跃：右下 `(0.90, 0.72)`，slot 3，`Space`。
4. 显示“先点开始操控，再按 WASD/移动鼠标”。

顶部提示提供“恢复默认布局”，避免用户拖乱后无法恢复。

## 10. 错误处理与安全

- 触摸未就绪：禁止开始，显示 `touch=...` 原始错误。
- slot 冲突/越界：配置校验失败，拒绝开始。
- 发送失败：立即 `ReleaseAll`，停止捕获，显示错误。
- 连接断开：停止引擎，释放本地按键状态，恢复鼠标。
- 前台切换：强制释放所有触点后再加载新配置。
- 页面崩溃/关闭：`Dispose` 中同步尝试 Up；窗口关闭路径不等待 UI。
- 不拦截系统快捷键（Alt+F4、Win 键、Ctrl+Alt+Del）；Escape 只停止操控。
- 不把 Root 密钥写入配置。
- 不记录原始输入事件到磁盘。

## 11. 验收标准

功能验收：

- 能在侧边栏进入“游戏外设”，三栏布局和响应式折叠正常。
- 连接设备后读取触摸屏信息，显示屏幕尺寸/slot 范围。
- 配置按前台包名保存；重启客户端后配置仍在。
- 前台应用切换时自动切换配置，旧触点全部释放。
- WASD 四方向、斜向、停止均正确；轮盘不会卡住。
- 鼠标移动连续改变视角；停止后回中；Y 反转生效。
- 鼠标左键/右键/空格等按钮按住、松开、连发正确。
- 轮盘 + 视角 + 两个按钮可同时多指操作。
- 停止操控、Esc、失焦、断开、切页都能释放全部 slot。
- RTT/事件率/活动 slot 指标可见。

压力验收：

- 60Hz 移动 10 分钟无卡死、无残留触点。
- 快速切换 profile 20 次无 slot 泄漏。
- 触摸请求超时/断线后引擎能停止，不继续排队。
- 配置 JSON 损坏时回退默认布局并保留原文件备份。

手工验收场景：

1. 打开支持虚拟摇杆的游戏，进入训练场。
2. 用 WASD 移动，松开后角色停止。
3. 鼠标向右拖动，视角持续向右；停止后继续拖动不跳变。
4. 按住鼠标左键开火，松开停火。
5. 同时移动 + 转视角 + 开火，确认三指并行。
6. 切到另一个游戏，确认加载另一套布局。

## 12. 实施顺序

### 第一阶段：底层与最小可用页面

- `SkpdbClient` 增加 Down/Move/Up。
- 触摸信息结构化。
- 新页面骨架、侧边栏入口、连接状态。
- 固定默认布局：轮盘 + 视角 + 开火/跳跃。
- 页面焦点采集 + Raw Input 鼠标。
- 停止/失焦/断线全量释放。

### 第二阶段：配置

- `profiles.json` 读写。
- 按前台包名自动创建/切换配置。
- 配置列表、复制、删除、导入导出。
- 校验 slot、坐标、按键绑定。

### 第三阶段：可视化布局编辑

- 手机屏幕比例画布。
- 拖拽轮盘/视角/按钮。
- 属性面板编辑半径、死区、灵敏度、模式。
- 实时触摸点预览。

### 第四阶段：稳定性与优化

- 移动合并、单飞行调度、RTT 自适应。
- 连发/切换/滚轮映射。
- 指标面板和故障注入测试。
- 评估批量触摸 opcode，减少 RTT 影响。

## 13. 实施补充（0.12.11）

- **浏览 / 编辑分离**：默认只显示画布与状态；进入编辑后才出现配置文件列表、
  属性面板、网格和元素手柄。退出编辑即回到干净覆盖层。
- **右键添加按键**：12 种类型（不含重力感应、宏按键、操作录制），选择后放在
  点击位置；元素可拖动、拖动右下角手柄改半径，属性面板精确编辑。
- **按键捕获**：捕获后按键盘键或鼠标键直接写入绑定；鼠标捕获会吞掉触发它的
  那次画布点击，避免同时弹出添加菜单。
- **撤销/重做**：基于配置快照（最多 80 步），覆盖添加、删除、复制、类型变更、
  拖动、缩放、属性应用和恢复默认。
- **图层开关**：移动轮盘与视角区域可隐藏/恢复，不再只能靠“恢复默认”找回。
- **鼠标 / 视角模式**：视角键（屏幕上或绑定的键盘键）切换两种模式；视角模式
  隐藏手机光标，Raw Input 相对位移转触摸拖动，不受画布或桌面边缘限制；退出时
  立即抬起视角 slot 并恢复光标。
- **观察键**：按住期间临时看向，松开抬起视角触摸；光标同步隐藏/恢复。
- **输入隔离**：鼠标按键不再通过 `GetAsyncKeyState` 全局轮询，只由画布注入，
  因此点击“开始/停止”“编辑布局”或属性面板不会误触发开火键。
- **窗口布局**：画布铺满卡片可用空间，手机比例预览按高度或宽度自适应；面板
  在窄窗口下仍保持可用，状态与 RTT 合并到底部单行。
