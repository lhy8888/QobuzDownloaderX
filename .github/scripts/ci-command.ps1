function Invoke-CICommand {
    param([string]$Executable, [string[]]$Arguments, [string]$LogPath)
    New-Item (Split-Path $LogPath -Parent) -ItemType Directory -Force | Out-Null
    # Keep native failures inspectable before returning their nonzero status.
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        & $Executable @Arguments 2>&1 | Tee-Object -FilePath $LogPath
        $code = $LASTEXITCODE
    } catch {
        $_ | Out-File $LogPath -Append
        $code = 1
    }
    if ($code -ne 0) {
        $detail = (Get-Content $LogPath -Tail 50 | Out-String).Trim()
        if ($env:GITHUB_STEP_SUMMARY) {
            "`n$Executable failed (exit $code).`n`n~~~text`n$detail`n~~~`n" | Out-File $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
        }
        $annotation = $detail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error::$annotation"
        exit $code
    }
}
