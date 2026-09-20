$ErrorActionPreference = 'Stop'

$toolDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$binary = Join-Path $toolDirectory 'target\release\bili-uploader.exe'

if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    [Console]::Error.WriteLine('bili-uploader is not built. Run: cargo build --manifest-path biliUploader\Cargo.toml --release')
    exit 3
}

& $binary @args
exit $LASTEXITCODE

