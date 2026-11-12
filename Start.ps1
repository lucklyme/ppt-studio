param([int]$Port = 5188)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$baseUrl = "http://127.0.0.1:$Port"
try {
    $existing = Invoke-RestMethod -Uri "$baseUrl/api/session" -TimeoutSec 2
    if ($null -ne $existing.setup) {
        Write-Host "PPT Studio is running: $baseUrl"
        return
    }
} catch {}
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'data/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'data/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
# Some managed launchers omit Windows variables required by NuGet.
$windowsDrive = [System.IO.Path]::GetPathRoot($env:SystemRoot)
$defaults = @{
    'ProgramFiles' = (Join-Path $windowsDrive 'Program Files')
    'ProgramFiles(x86)' = (Join-Path $windowsDrive 'Program Files (x86)')
    'ProgramData' = (Join-Path $windowsDrive 'ProgramData')
    'APPDATA' = (Join-Path $PSScriptRoot 'data/appdata')
    'LOCALAPPDATA' = (Join-Path $PSScriptRoot 'data/localappdata')
}
foreach ($entry in $defaults.GetEnumerator()) {
    if (-not [Environment]::GetEnvironmentVariable($entry.Key, 'Process')) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
$runtimeDir = Join-Path $PSScriptRoot 'data/runtime'
New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'src/Platform/Platform.csproj') --nologo -p:ProduceReferenceAssembly=false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Please install .NET 9 SDK and try again.' }
$assemblyPath = Join-Path $PSScriptRoot 'src/Platform/bin/Debug/net9.0/Platform.dll'
$contentPath = Join-Path $PSScriptRoot 'src/Platform'
$arguments = @('"' + $assemblyPath + '"', '--contentRoot', '"' + $contentPath + '"', '--urls', $baseUrl)
$process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtimeDir 'server.log') -RedirectStandardError (Join-Path $runtimeDir 'server-error.log')
$process.Id | Set-Content -LiteralPath (Join-Path $runtimeDir 'server.pid')
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        $null = Invoke-RestMethod -Uri "$baseUrl/api/session" -TimeoutSec 1
        Write-Host "PPT Studio is ready: $baseUrl"
        Write-Host 'Open the URL in your browser. Create your administrator account on first use.'
        return
    } catch { Start-Sleep -Milliseconds 500 }
}
throw "Startup did not complete. See logs in $runtimeDir"
