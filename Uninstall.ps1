$ErrorActionPreference = 'Stop'
Unregister-ScheduledTask -TaskName 'WiFi AutoControl' -Confirm:$false -ErrorAction SilentlyContinue
Write-Host 'Tarefa de inicialização removida. Você pode apagar a pasta do programa.' -ForegroundColor Green
