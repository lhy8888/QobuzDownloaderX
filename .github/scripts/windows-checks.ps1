param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
dotnet build tests/WindowsChecks/WindowsChecks.csproj --configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$output = "tests/WindowsChecks/bin/$Configuration/net48"
Copy-Item "QobuzDownloaderX/bin/$Configuration/*" $output -Recurse -Force
& "$output/WindowsChecks.exe" 2>&1 | Tee-Object artifacts/windows-output.txt
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
