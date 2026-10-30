# 内核 CPU 光标叠加层设计

> 0.12.38 / 模块 0.12.7：同一用户态渲染器另建左上调试与左下测试版面板，
> 使用相同的可信、无输入通道和 eSkipScreenshot 设置。面板心跳与箭头显隐独立，
> 光标 destroy 仅隐藏箭头；控制停止关闭面板，六秒无心跳自动隐藏。
> 新增显示层尚未真机验证，详见 `REVIEW-0.12.38.md`。

> 实施结论（更新）：直接在内核里调用 `drm_atomic_commit` / `drm_atomic_nonblocking_commit`
> 在本机 `msm_drm` 上会导致花屏并触发重启，已放弃。当前落地方案改为
> `SurfaceControl` + `eSkipScreenshot(0x40)` + CPU `Canvas` 绘制：光标在物理屏幕上
> 可见，但不进入 screencap/录屏；坐标和显隐由桌面游戏外设页通过 `C` opcode 控制。
> 该方案不抢 DRM master，也不执行内核原子提交。
>
> 与最初“内核纯 C 绘制”需求的差异：最终 CPU 绘制运行在模块启动的独立
> `app_process`（Java `Canvas`）中，不是内核 shellcode。原因是本机内核 DRM
> 原子提交路径验证失败；SurfaceControl 方案保留了 CPU 绘制、顶层显示和截图不可见，
> 但绘制进程属于 Android 用户态。若后续恢复纯内核路线，应先在可回滚设备上验证
> DRM 提交，不能直接用于日常机。

## 1. 需求

在 Android 游戏画面上叠加一个跟随 PC 鼠标移动的光标：

- 不进入视角移动时显示光标，跟随 PC 鼠标位置。
- 进入视角拖动时隐藏光标，由游戏外设页接管鼠标。
- 屏幕顶层显示。
- 普通 screencap/录屏不可见。
- 绘制逻辑用纯 C，内核侧 CPU 完成，不经过 SurfaceFlinger 或应用层 Surface。

## 2. 设备实测结论

测试设备：`192.0.2.1`，SM8735，Android 17，内核 6.6.118，msm_drm 厂商模块。

```text
/sys/class/drm/card0                 存在
/dev/fb0, /dev/graphics             不存在
CRTC                                 4 个，活动 CRTC 173
DRM plane                            10 个，全部是 OVERLAY，没有 CURSOR
活动 plane                           3 个
空闲 plane                           7 个
活动 FB                              1280x2784
pixel_format                         ABGR8888
modifier                             0x500000000000001（UBWC）
pitch                                5120
```

`drmModeSetPlane` 以 root 直接调用返回 `Permission denied`，因为 SurfaceFlinger 持有 DRM master。

结论：

1. **不能直接 CPU memcpy 到主帧缓冲**。活动 FB 是 UBWC 压缩格式，直接写 RGBA 像素会花屏/错位；还必须处理 scanout 与 HWC 的竞态。
2. **没有现成的 DRM cursor plane**。10 个 plane 都是 overlay。
3. 想要“截图/录屏不可见”的顶层光标，唯一可行路线是：
   - 用一个空闲 overlay plane 挂一个小的线性 ARGB framebuffer；
   - 由内核把该 plane 加进 DRM atomic state；
   - 该 plane 不属于 SurfaceFlinger layer，普通 screencap 不会合成它。
4. 这条路线仍会让 **外接相机拍摄到光标**，也能被内核/DRM 调试接口枚举；不能承诺“完全无痕”。

## 3. 推荐架构

```text
Windows SKPDB 游戏外设页
        │ 鼠标坐标 / visible
        ▼
skpdb 模块控制通道
        │ 写入 kernel CursorState
        ▼
内核 C 代码
  ├─ 64x64 线性 ABGR 光标 buffer
  ├─ CPU alpha blend 绘制箭头
  ├─ 查找空闲 overlay plane
  ├─ 构造 drm_atomic_state
  ├─ 设置 plane fb / crtc / src / dst / zpos
  └─ drm_atomic_commit
        ▼
SDE overlay plane  →  面板扫描输出
```

### 3.1 Cursor buffer

用户态模块（或一个 C 辅助进程）通过 DRM dumb buffer 创建：

```c
drmModeCreateDumb(width=64, height=64, bpp=32)  // linear, pitch 256
mmap()
drmModeAddFB2(DRM_FORMAT_ABGR8888, DRM_FORMAT_MOD_LINEAR)
```

实测这条路径在 root 下可以成功，不需要 DRM master。只有 `SETPLANE` 需要 master，所以 plane 的提交必须由内核代码完成。

### 3.2 光标绘制（纯 C）

```c
void cursor_draw(uint32_t *dst, int stride_px,
                 const uint8_t *sprite, int w, int h)
{
    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            uint8_t a = sprite[(y * w + x) * 4 + 3];
            if (!a) continue;
            uint32_t src = *(const uint32_t *)&sprite[(y * w + x) * 4];
            uint32_t *p = dst + y * stride_px + x;
            // ABGR8888 预乘 alpha 混合
            uint32_t rb = ((src & 0x00ff00ff) * a +
                           (*p & 0x00ff00ff) * (255 - a)) >> 8;
            uint32_t g  = ((src & 0x0000ff00) * a +
                           (*p & 0x0000ff00) * (255 - a)) >> 8;
            *p = (rb & 0x00ff00ff) | (g & 0x0000ff00);
        }
    }
}
```

光标使用预乘 alpha 的 32x32 或 64x64 箭头，热点在左上角。只在位置变化或可见性变化时重绘，不每帧全屏刷新。

### 3.3 内核 plane 提交

内核 C 代码需要：

- 通过 `/dev/dri/card0` 的 `struct file` 找到 `drm_device`，或从 DRM minor 结构反查。
- 遍历 `dev->mode_config.plane_list`，选一个 `possible_crtcs` 包含活动 CRTC 的空闲 overlay plane。
- 通过 `drm_framebuffer_lookup(dev, NULL, fb_id)` 找到用户态创建的 FB。
- 构造：

```c
state = drm_atomic_state_alloc(dev);
plane_state = drm_atomic_get_plane_state(state, plane);
drm_atomic_set_crtc_for_plane(plane_state, crtc);
drm_atomic_set_fb_for_plane(plane_state, fb);
plane_state->src_x = 0;
plane_state->src_y = 0;
plane_state->src_w = 64 << 16;
plane_state->src_h = 64 << 16;
plane_state->crtc_x = mouse_x;
plane_state->crtc_y = mouse_y;
plane_state->crtc_w = 64;
plane_state->crtc_h = 64;
/* alpha/zpos 等属性按 plane 支持情况设置 */
drm_atomic_commit(state);
```

关键风险：

- `drm_atomic_commit` 会睡眠，**不能放在 kprobe handler 里**。必须从 workqueue/kthread 或可睡眠的内核执行上下文调用。
- 这个 overlay plane 不能和 SurfaceFlinger/HWC 同时使用。必须预留一个 plane，并处理 HWC 尝试使用同一 plane 的情况。
- 每次 SF 提交都可能覆盖/关闭光标 plane。需要在内核侧持续重新提交，或在提交链上拦截。
- 设备上 7 个 plane 空闲，但 HWC 可以动态占用，不能假设永远空闲。
- 失败路径必须把 plane 关闭（`fb=NULL`），否则会留下残影。

### 3.4 控制通道

新增模块 opcode：

```text
[C][u8 action][u16 x][u16 y][u8 visible]
action 0 = set position/visible
action 1 = destroy cursor
```

模块收到后写入内核 `CursorState`：

```c
struct CursorState {
    uint32_t magic;
    uint32_t visible;
    int32_t x;
    int32_t y;
    uint32_t generation;
};
```

内核提交线程/工作项读取该结构，位置未变则跳过。游戏外设页鼠标移动时限频到 60Hz，避免每个 `WM_MOUSEMOVE` 都提交。

### 3.5 视角拖动时隐藏

游戏外设页知道当前是否在视角拖动：

- 未拖动：光标 visible=1，鼠标位置直接映射到手机坐标。
- 按下视角键/进入视角拖动：visible=0，光标 plane 关闭。
- 松开视角回到空闲：visible=1，光标回到当前鼠标位置。

这正好符合“没有进入视角移动操作时显示鼠标光标”。

## 4. 为什么不选其他方案

### 4.1 直接 CPU 绘制主帧缓冲

拒绝。活动 FB 是 UBWC，CPU 写 RGBA 不正确；还要处理双/三缓冲、scanout 撕裂和 HWC 覆盖。即使能写，截图也可能包含。

### 4.2 SurfaceFlinger 悬浮层/应用窗口

不满足“截图不可见”：截图会包含 Surface。

### 4.3 面板固定准星

`msm_drm.ko` 内有 `gbEnableCrossCursor`，属于 Visionox 面板 usecase 位，最多能做固定中心准星，不能跟随鼠标。用户要求跟随鼠标，不采用。

### 4.4 临时抢 DRM master

`drmSetMaster` 后 `SETPLANE` 可以工作，但会让 SurfaceFlinger 失去 master，显示会异常，不能作为产品方案。

## 5. 实施阶段

### 阶段 A：只读可行性

- 已完成：DRM resources/plane/FB 探测。
- 结论：无 cursor plane，有 7 个空闲 overlay plane，FB 为 UBWC。
- 产物：`drm_probe`、`drm_dumb` 测试程序。

### 阶段 B：线性光标 FB

- 模块用户态创建 64x64 线性 ABGR dumb buffer + FB。
- 纯 C 绘制箭头并验证像素。
- 不挂 plane，先确认 FB 生命周期和释放。

### 阶段 C：内核提交单个 plane

- 内核 C 代码查找一个空闲 overlay plane。
- 用 `drm_atomic_state` 设置 plane，提交后显示红色方块。
- 用 `screencap` 对比：屏幕有方块、截图没有，才算“截图不可见”。
- 任何失败自动关闭 plane；模块带 30 秒 watchdog，避免残影。

### 阶段 D：鼠标坐标控制

- 新增 `C` opcode。
- 60Hz 更新坐标。
- 视角拖动时隐藏。
- 处理多显示器/虚拟显示/分辨率变化。

### 阶段 E：稳定性

- HWC plane 冲突处理。
- 休眠/唤醒、锁屏、旋转、分辨率切换。
- 拔线/模块退出自动关闭 plane。
- 长稳测试和 bootloop 保护。

## 6. 风险声明

- 这不是普通用户态悬浮窗，涉及 DRM atomic 提交和厂商 display 驱动；失败可能导致黑屏、显示异常或 bootloop。
- 之前的内核 hook 已经导致过 bootloop，必须在可恢复设备上分阶段验证。
- “无痕”只能定义为 **SurfaceFlinger 截图/录屏不可见**；外接相机、DRM 调试、内核扫描仍能发现。
- 需要明确测试范围和恢复方案后再安装到日常设备。

## 7. 最小可行版本

先只做：

- 64x64 线性 ARGB 光标；
- 一个固定空闲 overlay plane；
- 鼠标坐标 60Hz 更新；
- 视角拖动时隐藏；
- 截图不可见验证；
- 退出/异常关闭 plane。

不做：动画、光标主题、多显示器、复杂 plane 冲突调度。
