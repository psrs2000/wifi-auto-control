# Execute este script como Administrador, depois de gerar publish\WiFiAutoControl.exe
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'publish\WiFiAutoControl.exe'
if (-not (Test-Path $exe)) { throw 'Executável não encontrado. Execute Build.ps1 primeiro.' }
$taskName = 'WiFi AutoControl'
$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Gerencia o Wi-Fi conforme a inatividade do teclado e mouse.' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host 'WiFi AutoControl instalado e iniciado. Ele aparecerá na bandeja do sistema.' -ForegroundColor Green
