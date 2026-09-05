param([string]$Dotnet = "dotnet")
$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$localDotnet = Join-Path $projectRoot "..\.tools\dotnet10\dotnet.exe"
if ($Dotnet -eq "dotnet" -and (Test-Path -LiteralPath $localDotnet)) { $Dotnet = (Resolve-Path -LiteralPath $localDotnet).Path }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
Push-Location -LiteralPath $projectRoot
try {
    & $Dotnet publish "src/LLG.Helper/LLG.Helper.csproj" -c Release -r win-x64 -o "artifacts/win-x64" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    $published = Join-Path $projectRoot "artifacts/win-x64/LLG.SubscriptionHelper.exe"
    $delivery = Join-Path $projectRoot "dist"
    New-Item -ItemType Directory -Path $delivery -Force | Out-Null
    Copy-Item -LiteralPath $published -Destination (Join-Path $delivery "LLG订阅助手.exe") -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "使用说明.txt") -Destination $delivery -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "ThirdPartyNotices.txt") -Destination $delivery -Force
    Get-FileHash -LiteralPath (Join-Path $delivery "LLG订阅助手.exe") -Algorithm SHA256 | Format-List
} finally { Pop-Location }
