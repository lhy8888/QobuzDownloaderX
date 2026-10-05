param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/ci-command.ps1"
Invoke-CICommand dotnet @('build', 'tests/WindowsChecks/WindowsChecks.csproj', '--configuration', $Configuration) artifacts/windows-check-build.txt
$output = "tests/WindowsChecks/bin/$Configuration/net48"
Copy-Item "QobuzDownloaderX/bin/$Configuration/*" $output -Recurse -Force
Invoke-CICommand "$output/WindowsChecks.exe" @() artifacts/windows-output.txt
