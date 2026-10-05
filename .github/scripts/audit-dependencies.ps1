$ErrorActionPreference = 'Stop'
New-Item artifacts -ItemType Directory -Force | Out-Null
[xml]$packages = Get-Content QobuzDownloaderX/packages.config -Raw
[xml]$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup><ItemGroup /></Project>'
$references = $project.SelectSingleNode('/Project/ItemGroup')
foreach ($package in $packages.packages.package) {
    $reference = $project.CreateElement('PackageReference')
    $reference.SetAttribute('Include', [string]$package.id)
    $reference.SetAttribute('Version', '[' + [string]$package.version + ']')
    $references.AppendChild($reference) | Out-Null
}
$project.Save((Join-Path $PWD 'artifacts/dependency-audit.csproj'))
# NU1900 also fails the job: unavailable vulnerability data cannot mean a pass.
dotnet restore artifacts/dependency-audit.csproj --force --no-cache -p:NuGetAudit=true -p:NuGetAuditMode=all '-p:WarningsAsErrors=NU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904' 2>&1 | Tee-Object artifacts/dependency-audit-output.txt
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet list artifacts/dependency-audit.csproj package --vulnerable --include-transitive 2>&1 | Tee-Object artifacts/dependency-audit-summary.txt
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"`nDependency audit: restore completed with no known vulnerabilities reported for the resolved package graph. Bundled API DLL and future advisories need separate review.`n" | Out-File $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
