param(
    [ValidatePattern('^COM[0-9]+$')]
    [string]$Port,
    [switch]$MetricsOnly,
    [switch]$InstallDesktopShortcut,
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
$connectionPath = Join-Path $PSScriptRoot 'StatusDock Connection.json'
$connectionTempPath = "$connectionPath.tmp"

if ($InstallDesktopShortcut) {
    $settingsExe = Join-Path $PSScriptRoot 'StatusDock Settings.exe'
    if (-not (Test-Path -LiteralPath $settingsExe -PathType Leaf)) {
        throw 'Build or install StatusDock Settings.exe before creating the desktop shortcut.'
    }
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $shortcutPath = Join-Path $desktop 'StatusDock.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $settingsExe
    $shortcut.Arguments = '--open-and-start'
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description = 'Open StatusDock Settings and start the sender'
    $shortcut.Save()
    Write-Host "Desktop shortcut ready: $shortcutPath"
    return
}

if ($InstallStartup -or $RemoveStartup) {
    $startup = [Environment]::GetFolderPath('Startup')
    $shortcutPath = Join-Path $startup 'StatusDock.lnk'
    $taskName = 'StatusDock'
    if ($InstallStartup) {
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $launcher = Join-Path $PSScriptRoot 'StatusDock Settings.exe'
        if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
            throw 'Build or install StatusDock Settings.exe before enabling automatic startup.'
        }
        $arguments = '--start-sender'
        if ($Port) { $arguments += ' ' + $Port }
        $action = New-ScheduledTaskAction -Execute $launcher -Argument $arguments -WorkingDirectory $PSScriptRoot
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force | Out-Null
        if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
        Write-Host 'StatusDock will start automatically at your next Windows sign-in.'
    } else {
        $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
        if ($task) { Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false }
        if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
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

function Read-SettingsFile {
    try {
        # Allow the settings window to replace its JSON file while it is being read.
        $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
        $reader = [IO.StreamReader]::new([IO.File]::Open($settingsPath, 'Open', 'Read', $share))
        try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } catch [IO.FileNotFoundException] {
        return [pscustomobject]@{}
    }
    try {
        if ($json.TrimStart().StartsWith('{')) {
            return ConvertFrom-Json -InputObject $json
        }
    } catch {
        # A malformed file has no settings to merge.
    }
    return [pscustomobject]@{}
}

function Read-DisplaySettings {
    try {
        $settings = Read-SettingsFile
        if ($settings.display -is [string] -and $settings.display -cin @('stats', 'clock') -and
            $settings.showSeconds -is [bool]) {
            return $settings
        }
    } catch {
        # Missing, unreadable or malformed settings use the standard display.
    }
    return @{ display = 'stats'; showSeconds = $true }
}

function Save-FlipperView([string]$Display) {
    $settingsMutex = [Threading.Mutex]::new($false, 'Local\StatusDock-Preferences')
    $locked = $false
    $temporary = "$settingsPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        try { $locked = $settingsMutex.WaitOne(500) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { return $false }
        $settings = Read-SettingsFile
        $settings | Add-Member -NotePropertyName display -NotePropertyValue $Display -Force
        if ($settings.showSeconds -isnot [bool]) {
            $settings | Add-Member -NotePropertyName showSeconds -NotePropertyValue $true -Force
        }
        [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $settings -Compress -Depth 100))
        if ([IO.File]::Exists($settingsPath)) {
            [IO.File]::Replace($temporary, $settingsPath, [NullString]::Value)
        } else {
            [IO.File]::Move($temporary, $settingsPath)
        }
        return $true
    } catch {
        # Leave the choice pending on the Flipper so the next cycle can retry.
        return $false
    } finally {
        try { [IO.File]::Delete($temporary) } catch { }
        if ($locked) { $settingsMutex.ReleaseMutex() }
        $settingsMutex.Dispose()
    }
}

function New-DeskSample([DateTime]$Time, [int]$Cpu, [int]$Ram, [int]$Gpu, [int]$CpuTemp, [int]$GpuTemp) {
    $settings = Read-DisplaySettings
    $invert = $settings.invertDisplay -is [bool] -and $settings.invertDisplay
    $layout = $settings.pcStatsLayout
    if (($layout -isnot [int] -and $layout -isnot [long]) -or
        $layout -lt 0 -or $layout -gt 63 -or ($layout -band 3) -gt 2 -or (($layout -shr 2) -band 7) -gt 5) {
        $layout = 0
    }
    if ($settings.flipDisplay -is [bool] -and $settings.flipDisplay) { $layout = $layout -bor 64 }
    $clockQuarters = [int][Math]::Floor($Time.TimeOfDay.TotalMilliseconds / 250)
    $prefix = 0x54000000
    if ($settings.display -eq 'clock' -or -not $settings.showSeconds -or $invert) {
        # V3 keeps the V2 GPU/time payload and uses bits 28 through 26 for display settings.
        $prefix = 0x60000000
        if ($invert) { $prefix = $prefix -bor 0x10000000 }
        if ($settings.display -eq 'clock') { $prefix = $prefix -bor 0x08000000 }
        if (-not $settings.showSeconds) { $prefix = $prefix -bor 0x04000000 }
    }
    $signal = $prefix -bor ($Gpu -shl 19) -bor $clockQuarters
    # Format bytes separately for Windows PowerShell 5.1 and mark the argument as hex.
    # Without 0x, Momentum strips a leading 0B when CPU is 11%.
    return @{
        command = 'loader signal {0} 0x{1:X2}{2:X2}{3:X2}{4:X2}' -f $signal, $Cpu, $Ram, $CpuTemp, $GpuTemp
        layoutCommand = 'loader signal {0} 0x{1:X}' -f 0x53544404, $layout
        sampledAtUtcMs = ([DateTimeOffset]$Time).ToUnixTimeMilliseconds()
        clockQuarters = $clockQuarters
        cpu = $Cpu
        ram = $Ram
        gpu = $Gpu
        cpuTemp = $CpuTemp
        gpuTemp = $GpuTemp
    }
}

function Write-Snapshot([string]$Path, [string]$Temporary, [string]$Json) {
    try {
        [IO.File]::WriteAllText($Temporary, $Json)
        if ([IO.File]::Exists($Path)) {
            [IO.File]::Replace($Temporary, $Path, [NullString]::Value)
        } else {
            [IO.File]::Move($Temporary, $Path)
        }
        return $true
    } catch {
        # Preview storage is optional. A write failure must not interrupt USB updates.
        return $false
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

function Invoke-FlipperCommand($Serial, [string]$Command, [switch]$WaitForLaunch) {
    $Serial.DiscardInBuffer()
    $Serial.Write($Command + "`r")
    $output = ''
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $reportedWait = $false
    $nextPortCheck = 2400
    while ($true) {
        $output += $Serial.ReadExisting()
        if ($output -match '(?m)>:[ \t]*$') { return $output }
        if ($deadline.ElapsedMilliseconds -ge 2400) {
            if (-not $WaitForLaunch) { throw 'The Flipper did not respond over USB.' }
            if (-not $reportedWait) {
                Set-ConnectionStatus 'Opening' 'Waiting for the Flipper to open StatusDock. If it shows an API warning, choose Cancel.'
                $reportedWait = $true
            }
            if ($deadline.ElapsedMilliseconds -ge $nextPortCheck) {
                if ([IO.Ports.SerialPort]::GetPortNames() -notcontains $Serial.PortName) {
                    throw 'The Flipper disconnected while opening StatusDock.'
                }
                $nextPortCheck = $deadline.ElapsedMilliseconds + 1000
            }
        }
        if ($WaitForLaunch) {
            if ([Threading.WaitHandle]::WaitAny($wakeEvents, 25) -ne [Threading.WaitHandle]::WaitTimeout) { return }
        } else {
            Start-Sleep -Milliseconds 25
        }
    }
}

function Read-FlipperFirmware([string]$Reply) {
    $fields = @{}
    foreach ($field in [regex]::Matches($Reply, '(?im)^firmware[_.](version|origin[_.](?:fork|git)|api[_.](?:major|minor))[ \t]*:[ \t]*([^\r\n]*)')) {
        $fields[$field.Groups[1].Value.Replace('.', '_')] = $field.Groups[2].Value.Trim()
    }
    $version = $fields.version
    $fork = $fields.origin_fork
    $family = 'Unknown'
    if ($fork -eq 'Momentum' -or (-not $fork -and $version -match '^mntm-')) {
        $family = 'Momentum'
    } elseif ((-not $fork -or $fork -in @('Official', 'Flipper Devices')) -and
              (-not $fields.origin_git -or $fields.origin_git -match '^https://github\.com/flipperdevices/flipperzero-firmware(?:\.git)?/?$') -and
              $version -match '^\d+\.\d+\.\d+$') {
        $family = 'Official'
    }
    $api = $null
    if ($fields.api_major -match '^\d+$' -and $fields.api_minor -match '^\d+$') {
        $api = '{0}.{1}' -f $fields.api_major, $fields.api_minor
    }
    return [pscustomobject]@{ version = $version; fork = $fork; family = $family; api = $api }
}

function Get-FlipperLaunchIssue([string]$Reply) {
    $plain = [regex]::Replace($Reply, '\x1b\[[0-?]*[ -/]*[@-~]', '')
    $detail = (($plain -split '[\r\n]+' | ForEach-Object {$_.Trim()} |
        Where-Object { $_ -and $_ -notmatch '^(?:.*>:\s*)?loader open\s|^.*>:\s*$' }) -join ' ').Trim()
    if (-not $detail -or $detail -eq 'App started') { return }
    $issue = switch -Regex ($detail) {
        '(?i)\b(?:ApiTooOld|ApiTooNew|API mismatch|MissingImports)\b' { 'ApiMismatch'; break }
        '(?i)\b(?:Application\s+".+"\s+not found|File not found|Unknown application)\b' { 'MissingApp'; break }
        '(?i)\bLoader is locked\b' { 'AppBusy'; break }
        default { 'LaunchFailed' }
    }
    return [pscustomobject]@{ kind = $issue; detail = $detail.Substring(0, [Math]::Min(400, $detail.Length)) }
}

function Sync-FlipperView($Serial) {
    # Match VIEW_STATS_SIGNAL, VIEW_CLOCK_SIGNAL and VIEW_SAVED_SIGNAL in the app.
    if ((Invoke-FlipperCommand $Serial ('loader signal {0} 0' -f 0x53544401)) -match 'was handled') {
        $display = 'stats'
    } elseif ((Invoke-FlipperCommand $Serial ('loader signal {0} 0' -f 0x53544402)) -match 'was handled') {
        $display = 'clock'
    } else {
        return
    }
    if (Save-FlipperView $display) {
        $null = Invoke-FlipperCommand $Serial ('loader signal {0} {1}' -f 0x53544403, [int]($display -eq 'clock'))
    }
}

function Get-AppState($Serial) {
    $reply = Invoke-FlipperCommand $Serial 'loader info'
    if ($reply -match 'Application "(?:StatusDock|[^"\r\n]*statusdock\.fap)" is running') { return 'Dock' }
    if ($reply -match 'No application is running') { return 'Idle' }
    if ($reply -match 'Application .* is running') { return 'Busy' }
    throw 'The Flipper returned an unexpected response to loader info.'
}

function Set-ConnectionStatus([string]$State, [string]$Message) {
    if ($script:lastStatus -ne $Message) {
        Write-Host $Message
        $script:lastStatus = $Message
    }
    $snapshot = [ordered]@{
        state = $State
        message = $Message
        firmware = $script:firmware
        appIssue = $script:appIssue
    }
    $json = ConvertTo-Json -InputObject $snapshot -Compress
    if ($script:lastConnection -cne $json -and (Write-Snapshot $connectionPath $connectionTempPath $json)) {
        $script:lastConnection = $json
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
$pauseEvent = [Threading.EventWaitHandle]::new($false, 'ManualReset', "Local\StatusDock-Pause-$PID")
$pausedEvent = [Threading.EventWaitHandle]::new($false, 'ManualReset', "Local\StatusDock-Paused-$PID")
$wakeEvents = [Threading.WaitHandle[]]@($stopEvent, $pauseEvent)
$serial = $null
$wasLive = $false
$started = $false
$lastStatus = ''
$lastConnection = ''
$firmware = $null
$appIssue = $null
$launchRejected = $false
$lastCycle = [DateTime]::UtcNow
$lastSensors = [DateTime]::MinValue
$gpu = 127
$gpuTemp = 255
$cpuTemp = 255

Write-Host 'StatusDock | Press Ctrl+C to stop the connection.'
try {
    Clear-LiveSample
    Set-ConnectionStatus 'Waiting' 'Waiting for the Flipper over USB.'
    $trayExe = Join-Path $PSScriptRoot 'StatusDock Settings.exe'
    if (Test-Path -LiteralPath $trayExe -PathType Leaf) {
        try {
            Start-Process -FilePath $trayExe -ArgumentList '--tray',$PID -WindowStyle Hidden
        } catch {
            Write-Host "Could not start the StatusDock tray icon. $($_.Exception.GetBaseException().Message)"
        }
    }
    while (-not $stopEvent.WaitOne(0)) {
        if ($pauseEvent.WaitOne(0)) {
            if (-not $pausedEvent.WaitOne(0)) {
                if ($serial) { $serial.Dispose(); $serial = $null }
                Clear-LiveSample
                $wasLive = $false
                $started = $false
                $null = $pausedEvent.Set()
                Set-ConnectionStatus 'Paused' 'Paused. The USB port is available to qFlipper.'
            }
            $null = $stopEvent.WaitOne(200)
            continue
        }
        $null = $pausedEvent.Reset()
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
                    Set-ConnectionStatus 'Waiting' 'Waiting for the Flipper over USB.'
                    continue
                }
                $serial = [IO.Ports.SerialPort]::new($portName, 115200)
                $serial.DtrEnable = $true
                $serial.WriteTimeout = 1000
                try {
                    $serial.Open()
                } catch [UnauthorizedAccessException] {
                    $serial.Dispose()
                    $serial = $null
                    Set-ConnectionStatus 'UsbBusy' "USB port $portName is busy or unavailable. Close qFlipper and Flipper Lab if open."
                    continue
                }
                Start-Sleep -Milliseconds 350
                $null = Invoke-FlipperCommand $serial ''
                $info = Invoke-FlipperCommand $serial 'device_info'
                if ($null -eq $info) { continue }
                $detected = Read-FlipperFirmware $info
                if ($firmware.version -cne $detected.version -or $firmware.fork -cne $detected.fork -or
                    $firmware.api -cne $detected.api) { $appIssue = $null }
                $firmware = $detected
                $launchRejected = $false
                $started = $false
                Set-ConnectionStatus 'Connected' "Connected over USB ($portName)."
            }

            if (-not $started) {
                $state = Get-AppState $serial
                if ($state -eq 'Busy') {
                    Set-ConnectionStatus 'AppBusy' 'Another app is open on the Flipper. Close it to start StatusDock.'
                    continue
                }
                if ($state -eq 'Idle' -and $wasLive) {
                    Set-ConnectionStatus 'AppClosed' 'Open StatusDock on the Flipper to resume.'
                    continue
                }
                if ($state -eq 'Idle') {
                    if ($launchRejected) { continue }
                    $reply = Invoke-FlipperCommand $serial ('loader open "' + $appPath + '"') -WaitForLaunch
                    # Time spent waiting for the Flipper's warning is not a PC sleep or USB reconnect.
                    $lastCycle = [DateTime]::UtcNow
                    if ($null -eq $reply) { continue }
                    $issue = Get-FlipperLaunchIssue $reply
                    if ($issue) {
                        $appIssue = $issue
                        $launchRejected = $issue.kind -eq 'ApiMismatch'
                        Set-ConnectionStatus 'Connected' "Connected over USB ($portName)."
                        $null = [Threading.WaitHandle]::WaitAny($wakeEvents, 3500)
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
            Sync-FlipperView $serial
            $sample = New-DeskSample (Get-Date) $cpu $ram $gpu $cpuTemp $gpuTemp
            # Send the display layout before the sample. Older apps still accept the sample.
            $null = Invoke-FlipperCommand $serial $sample.layoutCommand
            $reply = Invoke-FlipperCommand $serial $sample.command
            if ($reply -match 'was handled') {
                $wasLive = $true
                $null = Write-Snapshot $liveSamplePath $liveTempPath (ConvertTo-Json -InputObject $sample -Compress)
                $appIssue = $null
                $launchRejected = $false
                Set-ConnectionStatus 'Live' "Showing live data on the Flipper ($portName)."
            } else {
                Clear-LiveSample
                $started = $false
                Set-ConnectionStatus 'Connected' 'Waiting for StatusDock to respond on the Flipper.'
            }
        } catch {
            Clear-LiveSample
            if ($serial) { $serial.Dispose(); $serial = $null }
            $wasLive = $false
            Set-ConnectionStatus 'UsbError' "USB connection unavailable. $($_.Exception.GetBaseException().Message)"
        } finally {
            $null = [Threading.WaitHandle]::WaitAny($wakeEvents, 1750)
        }
    }
} finally {
    Clear-LiveSample
    Set-ConnectionStatus 'Stopped' 'The Windows sender is stopped.'
    try { [IO.File]::Delete($liveTempPath) } catch { }
    try { [IO.File]::Delete($connectionTempPath) } catch { }
    if ($serial) { $serial.Dispose() }
    $pausedEvent.Dispose()
    $pauseEvent.Dispose()
    $stopEvent.Dispose()
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
