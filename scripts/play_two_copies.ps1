# Two copies of the game on this PC, connected for real (UDP over 127.0.0.1, the same network code as with a friend):
# the host on the left half of the screen, the guest on the right half, each window exactly half the screen wide
# (the mod keeps them windowed with -versus-window: the game would put its own full-screen settings back).
#   -Versus      versus instead of co-op
#   -FreshSaves  start both from a fresh copy of the owner's saves (otherwise they keep their own progress)
# Each copy is its own game folder (test-installs\restory-steam-a, restory-steam-b: the Steam build) with its own save folder (test_saves\manual-*),
# so the owner's real saves are only read, once, to make those copies. The copies share the game's settings in the
# registry with the real game (Unity also remembers the window size there), so the key is saved before and put back
# once both copies are closed.
param([switch]$Versus, [switch]$FreshSaves)
$ErrorActionPreference = "Stop"

$local = Split-Path $PSScriptRoot -Parent
$port = 27515
# Each window's visible frame is half the work area wide; the game inside keeps 16:9. Windows 11 draws a 1 px
# border around the game, so the game's own picture is 2 px narrower than the frame.
$dll = Join-Path $local "mod\RestoryVersus.Plugin\bin\Release\net472\RestoryVersus.dll"
$realSaves = Join-Path $env:USERPROFILE "AppData\LocalLow\Mandragora\Restory\SaveData"
$regKey = "HKCU\Software\Mandragora\Restory"
$regBackup = Join-Path $local "backup\registry_before_two_copies.reg"
$players = @(
    @{ Name = "Хост";  Copy = "restory-steam-a"; Saves = "manual-host";  Right = $false },
    @{ Name = "Гость"; Copy = "restory-steam-b"; Saves = "manual-guest"; Right = $true }
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int action, int param, out RECT r, int winIni);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
"@

# Not twice: a copy that is already running holds its files.
foreach ($p in $players) {
    $exe = Join-Path $local ("test-installs\" + $p.Copy + "\Restory.exe")
    if (-not (Test-Path $exe)) { Write-Host "Нет копии игры: $exe"; exit 1 }
    $running = Get-Process Restory -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($running) { Write-Host ("Копия " + $p.Copy + " уже запущена — закрой её и запусти скрипт снова."); exit 1 }
}

foreach ($p in $players) {
    $game = Join-Path $local ("test-installs\" + $p.Copy)
    # The latest build of the mod into this copy.
    if (Test-Path $dll) {
        $target = Join-Path $game "BepInEx\plugins\RestoryVersus\RestoryVersus.dll"
        if (-not (Test-Path $target) -or (Get-Item $dll).LastWriteTime -gt (Get-Item $target).LastWriteTime) { Copy-Item $dll $target -Force }
    }
    # Its own saves: a copy of the owner's, made once (or again with -FreshSaves).
    $saves = Join-Path $local ("test_saves\" + $p.Saves)
    if ($FreshSaves -and (Test-Path $saves)) { Remove-Item $saves -Recurse -Force }
    if (-not (Test-Path (Join-Path $saves "SaveData"))) {
        New-Item -ItemType Directory -Force $saves | Out-Null
        Copy-Item $realSaves (Join-Path $saves "SaveData") -Recurse
    }
    $p.SaveDir = $saves
    $p.Exe = Join-Path $game "Restory.exe"
}

$work = New-Object Win+RECT
[Win]::SystemParametersInfo(0x0030, 0, [ref]$work, 0) | Out-Null   # SPI_GETWORKAREA
$half = [int](($work.Right - $work.Left) / 2)
$width = $half - 2
$height = [int]($width * 9 / 16)

# The real game's settings, to put back afterwards.
New-Item -ItemType Directory -Force (Split-Path $regBackup) | Out-Null
& reg export $regKey $regBackup /y | Out-Null

$hostArgs = if ($Versus) { @("-versus-host", "$port") } else { @("-coop-host", "$port") }
$joinArgs = if ($Versus) { @("-versus-join", "127.0.0.1:$port") } else { @("-coop-join", "127.0.0.1:$port") }
$extra = @($hostArgs, $joinArgs)

for ($i = 0; $i -lt 2; $i++) {
    $p = $players[$i]
    $gameArgs = @("-screen-fullscreen", "0", "-screen-width", "$width", "-screen-height", "$height", "-versus-window", ("" + $width + "x" + $height),
              "-versus-name", $p.Name, "-versus-savedir", ('"' + $p.SaveDir + '"'), "-versus-route", "ip",
              "-logFile", ('"' + (Join-Path $p.SaveDir "player.log") + '"')) + $extra[$i]
    $p.Process = Start-Process -FilePath $p.Exe -WorkingDirectory (Split-Path $p.Exe) -ArgumentList $gameArgs -PassThru
    Write-Host ("Запущен " + $p.Name + " (" + $p.Copy + ")")
    if ($i -eq 0) { Start-Sleep -Seconds 3 }
}

# To the edges of the screen. The windows change size a few times while the games start (the mod puts the small
# window back after the game's own settings), so they are put in place again for the first minute and a half.
Write-Host "Ставлю окна по половинам экрана..."
for ($t = 0; $t -lt 90; $t++) {
    foreach ($p in $players) {
        $p.Process.Refresh()
        if ($p.Process.HasExited) { continue }
        $h = $p.Process.MainWindowHandle
        if ($h -eq [IntPtr]::Zero) { continue }
        # The window rectangle includes invisible resize borders; the visible frame (DWM) is what has to line up.
        $r = New-Object Win+RECT
        $v = New-Object Win+RECT
        [Win]::GetWindowRect($h, [ref]$r) | Out-Null
        if ([Win]::DwmGetWindowAttribute($h, 9, [ref]$v, 16) -ne 0) { $v = $r }   # DWMWA_EXTENDED_FRAME_BOUNDS
        $vw = $v.Right - $v.Left
        if ($vw -le 0 -or $vw -gt $half + 40) { continue }   # still full size: not yet
        $wantLeft = if ($p.Right) { $work.Left + $half } else { $work.Left }
        $x = $r.Left + ($wantLeft - $v.Left)
        $y = $r.Top + ($work.Top - $v.Top)
        if ($x -ne $r.Left -or $y -ne $r.Top) { [Win]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010) | Out-Null }
    }
    Start-Sleep -Seconds 1
}

Write-Host ""
if ($Versus) { Write-Host "Версус: оба в лобби. Хост выбирает устройство и режим, гость жмёт «Готов»." }
else { Write-Host "Кооператив: хост (слева) жмёт «Начать», гость (справа) подключится сам." }
Write-Host "Логи: test-installs\restory-steam-a\BepInEx\LogOutput.log (хост) и restory-steam-b (гость)."
Write-Host ""
Write-Host "Не закрывай это окно: когда закроешь обе игры, вернутся настройки твоей обычной игры."
foreach ($p in $players) { if (-not $p.Process.HasExited) { $p.Process.WaitForExit() } }
& reg import $regBackup 2>$null | Out-Null
Write-Host "Обе игры закрыты, настройки обычной игры возвращены."
