# Pass arguments unchanged to the prebuilt CLI. Never echo arguments or credential files.
$binaryPath = Join-Path $PSScriptRoot 'bin/Release/net8.0/NexusUploader.dll'
if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
    @{ status = 'error'; code = 'BUILD_REQUIRED'; message = 'Run dotnet build NexusUploader/NexusUploader.csproj -c Release first.'; exitCode = 2 } | ConvertTo-Json -Compress
    exit 2
}
& dotnet $binaryPath @args
exit $LASTEXITCODE
