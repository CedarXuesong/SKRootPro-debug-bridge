param(
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$Jdk = $env:JAVA_HOME
)
$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path -Parent $PSScriptRoot
$cursorSources = @(Get-ChildItem -LiteralPath (Join-Path $moduleRoot 'src/display/cursor_java/src/skpdb/cursor') -Filter '*.java' | ForEach-Object FullName)
$cursorClasses = Join-Path $moduleRoot 'obj/cursor-java/classes'
$cursorDex = Join-Path $moduleRoot 'obj/cursor-java/dex'
$androidJar = Join-Path $AndroidSdk 'platforms/android-35/android.jar'
$d8 = Join-Path $AndroidSdk 'build-tools/35.0.0/d8.bat'
New-Item -ItemType Directory -Force $cursorClasses,$cursorDex | Out-Null
$staleClasses = @(Get-ChildItem -LiteralPath $cursorClasses -Recurse -File -Filter '*.class')
$staleClasses | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
& (Join-Path $Jdk 'bin/javac.exe') --release 8 -encoding UTF-8 -classpath $androidJar -d $cursorClasses @cursorSources
if ($LASTEXITCODE -ne 0) { throw 'cursor Java compilation failed' }
$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $Jdk
    $classFiles = @(Get-ChildItem -LiteralPath $cursorClasses -Recurse -File -Filter '*.class' | ForEach-Object FullName)
    & $d8 --min-api 29 --lib $androidJar --output $cursorDex @classFiles
    if ($LASTEXITCODE -ne 0) { throw 'cursor dex compilation failed' }
} finally { $env:JAVA_HOME = $previousJavaHome }
$cursorBytes = [IO.File]::ReadAllBytes((Join-Path $cursorDex 'classes.dex'))
$header = [Text.StringBuilder]::new()
[void]$header.AppendLine('// Generated from Main.java by tools/build_cursor.ps1; do not edit by hand.')
[void]$header.AppendLine('static const unsigned char kCursorDex[] = {')
for ($i = 0; $i -lt $cursorBytes.Length; $i++) {
    if ($i % 12 -eq 0) { [void]$header.Append('    ') }
    [void]$header.Append(('0x{0:x2},' -f $cursorBytes[$i]))
    if ($i % 12 -eq 11) { [void]$header.AppendLine() } else { [void]$header.Append(' ') }
}
[void]$header.AppendLine()
[void]$header.AppendLine('};')
[void]$header.AppendLine("static const unsigned int kCursorDexSize = $($cursorBytes.Length);")
[IO.File]::WriteAllText((Join-Path $moduleRoot 'src/display/cursor_dex.h'), $header.ToString(), [Text.Encoding]::ASCII)
Write-Host "cursor dex: $($cursorBytes.Length) bytes"
