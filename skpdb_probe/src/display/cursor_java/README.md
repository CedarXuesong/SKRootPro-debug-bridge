# CPU 光标 Java 辅助进程

`Main.java` 由模块通过 `app_process` 启动，创建带 `eSkipScreenshot(0x40)` 的
`SurfaceControl` 层，用 CPU `Canvas` 绘制 64×64 箭头，并从 stdin 读取
`move x y`、`visible 0|1`、`destroy`。模块内的 `cursor_dex.h` 是编译后的
dex 字节数组，安装时写到 `/data/local/tmp/skpdb_cursor.dex`。

Android 12 及以上在第一帧显示前调用 `Transaction.setTrustedOverlay`。
此层没有输入通道和可触区域，只用于显示；普通 SurfaceControl 会参与遮挡判定，
导致光标所在区域的触摸被 InputDispatcher 丢弃。可信标记调用失败时终止光标创建，
避免留下阻挡输入的覆盖层。stdin 关闭时渲染循环退出，桥接重启后不会残留旧光标。

## 重新生成 dex

需要 Android SDK 的 `android.jar`（API 35）和 Build Tools 的 `d8`：

`skpdb_probe/build.ps1` 已在 NDK 编译前调用 `tools/build_cursor.ps1`，同步生成 dex 和头文件。
也可以单独执行该脚本，使用 `-AndroidSdk`、`-Jdk` 指定工具路径。

```powershell
$androidJar = "$env:LOCALAPPDATA\Android\Sdk\platforms\android-35\android.jar"
$d8 = "$env:LOCALAPPDATA\Android\Sdk\build-tools\35.0.0\d8.bat"

New-Item -ItemType Directory -Force .scratch\cursor_java\classes | Out-Null
javac -source 8 -target 8 -classpath $androidJar `
  -d .scratch\cursor_java\classes `
  src\skpdb\cursor\Main.java

& $d8 --min-api 29 --output .scratch\cursor_java\dex `
  .scratch\cursor_java\classes\skpdb\cursor\Main.class `
  .scratch\cursor_java\classes\skpdb\cursor\Main`$1.class
```

`Main$1.class` 是 stdin 读取线程的匿名类，必须一起输入 d8，否则
`app_process` 会报 `NoClassDefFoundError: skpdb.cursor.Main$1`。

把 `classes.dex` 转成 C 头文件：

```powershell
$bytes = [IO.File]::ReadAllBytes('.scratch\cursor_java\dex\classes.dex')
$sb = [Text.StringBuilder]::new()
$sb.AppendLine('// Generated from Main.java; do not edit by hand.') | Out-Null
$sb.AppendLine('static const unsigned char kCursorDex[] = {') | Out-Null
for ($i = 0; $i -lt $bytes.Length; $i++) {
    if ($i % 12 -eq 0) { $sb.Append('    ') | Out-Null }
    $sb.Append(('0x{0:x2},' -f $bytes[$i])) | Out-Null
    if ($i % 12 -eq 11) { $sb.AppendLine() | Out-Null } else { $sb.Append(' ') | Out-Null }
}
$sb.AppendLine('};') | Out-Null
$sb.AppendLine("static const unsigned int kCursorDexSize = $($bytes.Length);") | Out-Null
Set-Content -Encoding ascii ..\cursor_dex.h $sb.ToString()
```

生成后重新构建模块，确保 `cursor_dex.h` 与 `Main.java` 同步。
