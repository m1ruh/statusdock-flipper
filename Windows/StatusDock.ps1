param(
    [ValidatePattern('^COM[0-9]+$')]
    [string]$Port,
    [switch]$MetricsOnly,
    [switch]$InstallStartup,
    [switch]$RemoveStartup,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$stopName = 'Local\StatusDock-Stop'
$appPath = '/ext/apps/Tools/statusdock.fap'
$settingsPath = Join-Path $PSScriptRoot 'StatusDock Settings.json'
$liveSamplePath = Join-Path $PSScriptRoot 'StatusDock Live.json'
$liveTempPath = "$liveSamplePath.tmp"

if ($InstallStartup -or $RemoveStartup) {
    $startup = [Environment]::GetFolderPath('Startup')
    $shortcutPath = Join-Path $startup 'StatusDock.lnk'
    if ($InstallStartup) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $shortcut.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
        if ($Port) { $shortcut.Arguments += ' -Port ' + $Port }
        $shortcut.WorkingDirectory = $PSScriptRoot
        $shortcut.Save()
        Write-Host 'StatusDock will start automatically at your next Windows sign-in.'
    } elseif (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath
        Write-Host 'The startup entry was removed.'
    } else {
        Write-Host 'Automatic startup is disabled.'
    }
    return
}

if ($Stop) {
    $stopEvent = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting($stopName, [ref]$stopEvent)) {
        try { $null = $stopEvent.Set() } finally { $stopEvent.Dispose() }
        Write-Host 'Stopping StatusDock.'
    } else {
        Write-Host 'StatusDock is stopped.'
    }
    return
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class StatusDockMetrics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static long previousIdle, previousTotal;

    public static int[] Read()
    {
        long idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) throw new Win32Exception();
        var total = kernel + user;
        var elapsed = total - previousTotal;
        var cpu = previousTotal > 0 && elapsed > 0
            ? Math.Max(0, Math.Min(100, (int)Math.Round(100.0 * (elapsed - (idle - previousIdle)) / elapsed)))
            : 255;
        previousIdle = idle;
        previousTotal = total;

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception();
        return new int[] { cpu, (int)memory.Load };
    }
}
'@

$nvidiaSmi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'

function Get-NvidiaMetrics {
    if (-not (Test-Path -LiteralPath $nvidiaSmi -PathType Leaf)) { return 127, 255 }
    $process = New-Object Diagnostics.Process
    try {
        $process.StartInfo.FileName = $nvidiaSmi
        $process.StartInfo.Arguments = '--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits'
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $null = $process.Start()
        if (-not $process.WaitForExit(1200)) {
            $process.Kill()
            return 127, 255
        }
        $output = $process.StandardOutput.ReadToEnd()
        if ($process.ExitCode -eq 0 -and $output -match '(?m)^\s*(\d{1,3})\s*,\s*(\d{1,3})\s*$') {
            $load = [int]$Matches[1]
            $temp = [int]$Matches[2]
            if ($load -le 100 -and $temp -le 150) { return $load, $temp }
        }
    } catch {
        # The GPU driver may be unavailable during startup or resume.
    } finally {
        $process.Dispose()
    }
    return 127, 255
}

function Get-CpuTemperature {
    if (-not (Get-Process -Name LibreHardwareMonitor -ErrorAction SilentlyContinue)) { return 255 }
    try {
        $sensors = @(Get-CimInstance -Namespace 'root\LibreHardwareMonitor' -ClassName Sensor `
            -Filter "SensorType = 'Temperature'" -OperationTimeoutSec 3 | Where-Object {
                $_.Identifier -match '^/(intelcpu|amdcpu)/' -and $null -ne $_.Value -and
                $_.Value -ge 0 -and $_.Value -le 150
            })
        $package = $sensors | Where-Object Name -Match 'CPU Package|Core \(Tctl/Tdie\)|CPU Die \(average\)' |
            Select-Object -First 1
        if ($package) { return [int][Math]::Round($package.Value) }
        if ($sensors.Count) { return [int][Math]::Round(($sensors | Measure-Object Value -Maximum).Maximum) }
    } catch {
        # The sensor namespace is optional and may be unavailable.
    }
    return 255
}

function Read-DisplaySettings {
    try {
        # Allow the settings window to replace its JSON file while it is being read.
        $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $reader = [IO.StreamReader]::new([IO.File]::Open($settingsPath, 'Open', 'Read', $share))
        try {
            $json = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
        if ($json.TrimStart().StartsWith('{')) {
            $settings = ConvertFrom-Json -InputObject $json
            if ($settings.display -is [string] -and $settings.display -cin @('stats', 'clock') -and
                $settings.showSeconds -is [bool]) {
                return $settings
            }
        }
    } catch {
        # Missing, unreadable or malformed settings use the standard display.
    }
    return @{ display = 'stats'; showSeconds = $true }
}

function New-DeskSample([DateTime]$Time, [int]$Cpu, [int]$Ram, [int]$Gpu, [int]$CpuTemp, [int]$GpuTemp) {
    $settings = Read-DisplaySettings
    $clockQuarters = [int][Math]::Floor($Time.TimeOfDay.TotalMilliseconds / 250)
    $prefix = 0x54000000
    if ($settings.display -eq 'clock' -or -not $settings.showSeconds) {
        # V3 keeps the V2 GPU/time payload and uses bits 27 and 26 for display settings.
        $prefix = 0x60000000
        if ($settings.display -eq 'clock') { $prefix = $prefix -bor 0x08000000 }
        if (-not $settings.showSeconds) { $prefix = $prefix -bor 0x04000000 }
    }
    $signal = $prefix -bor ($Gpu -shl 19) -bor $clockQuarters
    # Formatting the four bytes separately avoids signed integer conversion on Windows PowerShell 5.1.
    return @{
        command = 'loader signal {0} {1:X2}{2:X2}{3:X2}{4:X2}' -f $signal, $Cpu, $Ram, $CpuTemp, $GpuTemp
        sampledAtUtcMs = ([DateTimeOffset]$Time).ToUnixTimeMilliseconds()
        clockQuarters = $clockQuarters
        cpu = $Cpu
        ram = $Ram
        gpu = $Gpu
        cpuTemp = $CpuTemp
        gpuTemp = $GpuTemp
    }
}

function Publish-LiveSample($Sample) {
    try {
        # Publish one complete snapshot after the Flipper acknowledges its sample.
        [IO.File]::WriteAllText($liveTempPath, (ConvertTo-Json -InputObject $Sample -Compress))
        if ([IO.File]::Exists($liveSamplePath)) {
            [IO.File]::Replace($liveTempPath, $liveSamplePath, [NullString]::Value)
        } else {
            [IO.File]::Move($liveTempPath, $liveSamplePath)
        }
    } catch {
        # Preview storage is optional. A write failure must not interrupt USB updates.
    }
}

function Clear-LiveSample {
    try { [IO.File]::Delete($liveSamplePath) } catch { }
}

if ($MetricsOnly) {
    $null = [StatusDockMetrics]::Read()
    Start-Sleep -Milliseconds 750
    $cpu, $ram = [StatusDockMetrics]::Read()
    $gpu, $gpuTemp = Get-NvidiaMetrics
    $cpuTemp = Get-CpuTemperature
    $now = Get-Date
    [pscustomobject]@{
        clock = $now.ToString('HH:mm:ss')
        cpu_percent = $cpu
        ram_percent = $ram
        gpu_percent = $(if ($gpu -ne 127) { $gpu } else { $null })
        cpu_celsius = $(if ($cpuTemp -ne 255) { $cpuTemp } else { $null })
        gpu_celsius = $(if ($gpuTemp -ne 255) { $gpuTemp } else { $null })
        command = (New-DeskSample $now $cpu $ram $gpu $cpuTemp $gpuTemp).command
    } | ConvertTo-Json
    return
}

function Find-FlipperPort {
    if ($Port) { return $Port }
    if (-not [IO.Ports.SerialPort]::GetPortNames()) { return }
    $devices = (& "$env:SystemRoot\System32\pnputil.exe" /enum-devices /connected /class Ports) -join "`n"
    foreach ($device in ($devices -split '\r?\n\s*\r?\n')) {
        if ($device -match '(?i)USB\\VID_0483&PID_5740&MI_00\\' -and
            $device -match '\((COM\d+)\)') {
            return $Matches[1]
        }
    }
}

function Invoke-FlipperCommand($Serial, [string]$Command) {
    $Serial.DiscardInBuffer()
    $Serial.Write($Command + "`r")
    $output = ''
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.ElapsedMilliseconds -lt 2400) {
        $output += $Serial.ReadExisting()
        if ($output -match '(?m)>:[ \t]*$') { return $output }
        Start-Sleep -Milliseconds 25
    }
    throw 'The Flipper did not respond over USB.'
}

function Get-AppState($Serial) {
    $reply = Invoke-FlipperCommand $Serial 'loader info'
    if ($reply -match 'Application "(?:StatusDock|[^"\r\n]*statusdock\.fap)" is running') { return 'Dock' }
    if ($reply -match 'No application is running') { return 'Idle' }
    if ($reply -match 'Application .* is running') { return 'Busy' }
    throw 'Momentum returned an unexpected response to loader info.'
}

function Write-Status([string]$Message) {
    if ($script:lastStatus -ne $Message) {
        Write-Host $Message
        $script:lastStatus = $Message
    }
}

$created = $false
$mutex = [Threading.Mutex]::new($true, 'Local\StatusDock-Companion', [ref]$created)
if (-not $created) {
    $mutex.Dispose()
    Write-Host 'StatusDock is already running.'
    return
}

$stopEvent = [Threading.EventWaitHandle]::new($false, 'ManualReset', $stopName)
$serial = $null
$wasLive = $false
$started = $false
$lastStatus = ''
$lastCycle = [DateTime]::UtcNow
$lastSensors = [DateTime]::MinValue
$gpu = 127
$gpuTemp = 255
$cpuTemp = 255

Write-Host 'StatusDock | Press Ctrl+C to stop the connection.'
try {
    Clear-LiveSample
    do {
        try {
            $cycle = [DateTime]::UtcNow
            if (($cycle - $lastCycle).TotalSeconds -ge 15) {
                if ($serial) { $serial.Dispose(); $serial = $null }
                Clear-LiveSample
                $wasLive = $false
            }
            $lastCycle = $cycle
            if (-not $serial) {
                $portName = Find-FlipperPort
                if (-not $portName) {
                    Write-Status 'Waiting for the Flipper over USB.'
                    continue
                }
                $serial = [IO.Ports.SerialPort]::new($portName, 115200)
                $serial.DtrEnable = $true
                $serial.WriteTimeout = 1000
                $serial.Open()
                Start-Sleep -Milliseconds 350
                $null = Invoke-FlipperCommand $serial ''
                $started = $false
            }

            if (-not $started) {
                $state = Get-AppState $serial
                if ($state -eq 'Busy' -or ($state -eq 'Idle' -and $wasLive)) {
                    Write-Status 'Open StatusDock on the Flipper to resume.'
                    continue
                }
                if ($state -eq 'Idle') {
                    $reply = Invoke-FlipperCommand $serial ('loader open "' + $appPath + '"')
                    if ($reply -match '(?i)(not found|failed|incompatible|error|API mismatch)') {
                        Write-Status "Could not open StatusDock on the Flipper. Check the app file and Momentum version.`n$($reply.Trim())"
                        $null = $stopEvent.WaitOne(3500)
                        continue
                    }
                }
                $started = $true
            }

            if (($cycle - $lastSensors).TotalSeconds -ge 6) {
                $gpu, $gpuTemp = Get-NvidiaMetrics
                $cpuTemp = Get-CpuTemperature
                $lastSensors = $cycle
            }
            $cpu, $ram = [StatusDockMetrics]::Read()
            $sample = New-DeskSample (Get-Date) $cpu $ram $gpu $cpuTemp $gpuTemp
            $reply = Invoke-FlipperCommand $serial $sample.command
            if ($reply -match 'was handled') {
                $wasLive = $true
                Publish-LiveSample $sample
                Write-Status "Showing live data on the Flipper ($portName)."
            } else {
                Clear-LiveSample
                $started = $false
                Write-Status 'Waiting for StatusDock to respond on the Flipper.'
            }
        } catch {
            Clear-LiveSample
            if ($serial) { $serial.Dispose(); $serial = $null }
            $wasLive = $false
            Write-Status "Waiting for USB. $($_.Exception.GetBaseException().Message) Close qFlipper and Flipper Lab if they are open."
        }
    } while (-not $stopEvent.WaitOne(1750))
} finally {
    Clear-LiveSample
    try { [IO.File]::Delete($liveTempPath) } catch { }
    if ($serial) { $serial.Dispose() }
    $stopEvent.Dispose()
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
