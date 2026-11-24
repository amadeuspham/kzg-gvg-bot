<#
.SYNOPSIS
  Runs the Kzg bot at every start of the PC, with no one signed in, and restarts it when it stops.
.DESCRIPTION
  Registers a scheduled task named "Kzg bot" that runs start.cmd from this folder as SYSTEM, with its log in bot.log in the data folder, kept to 30 MB.
  The data folder is C:\ProgramData\Kzg unless -DataDir says otherwise: SYSTEM has no user profile
  to keep the default folder in. Run it from an elevated PowerShell. Remove it with -Remove.
#>
[CmdletBinding()]
param(
    [string] $DataDir = 'C:\ProgramData\Kzg',
    [switch] $Remove
)
$ErrorActionPreference = 'Stop'
$name = 'Kzg bot'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator).'
}
if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    Write-Host "Removed '$name'."
    return
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$start = Join-Path $here 'start.cmd'
if (-not (Test-Path $start)) { throw "start.cmd isn't beside this script ($here)." }
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

# A task has no console, so the bot writes its log to bot.log in the data folder (it holds no secrets). The bot
# keeps it to 30 MB: at 10 MB it moves to bot.1.log, and two old files are kept.
$log = Join-Path $DataDir 'bot.log'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"`"$start`" --data-dir `"$DataDir`" --log-file `"$log`"`"" -WorkingDirectory $here
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

# Only SYSTEM and administrators may read the data folder: secrets.json holds the bot tokens.
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

Write-Host "Registered '$name'. Data folder: $DataDir"
Write-Host "Put config.json and secrets.json there, then: Start-ScheduledTask -TaskName '$name'"
