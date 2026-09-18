param([switch]$Package, [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & $Dotnet publish savecloud/src/SaveCloud.Bridge/SaveCloud.Bridge.csproj -c Release -r win-x64 --self-contained true -o src-tauri/tools/savecloud
    if ($LASTEXITCODE -ne 0) { throw '同步核心編譯失敗' }
    if ($Package) {
        pnpm tauri build --no-bundle
        if ($LASTEXITCODE -ne 0) { throw 'ReinaManager 編譯失敗' }
        $destination = Join-Path $repo 'artifacts/ReinaManager-SaveCloud-win-x64'
        New-Item -ItemType Directory -Force -Path "$destination/resources/data", "$destination/tools" | Out-Null
        Copy-Item -LiteralPath 'src-tauri/target/release/ReinaManager.exe' -Destination $destination
        Copy-Item -LiteralPath 'src-tauri/tools/savecloud' -Destination "$destination/tools" -Recurse -Force
        Copy-Item -LiteralPath 'src-tauri/target/7zip' -Destination "$destination/tools" -Recurse -Force
        Copy-Item -LiteralPath 'LICENSE', 'docs/savecloud.md' -Destination $destination
        Compress-Archive -Path "$destination/*" -DestinationPath 'artifacts/ReinaManager-SaveCloud-win-x64.zip' -Force
        Get-FileHash -Algorithm SHA256 'artifacts/ReinaManager-SaveCloud-win-x64.zip'
    }
} finally { Pop-Location }
