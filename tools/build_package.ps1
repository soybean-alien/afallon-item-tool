# Builds the portable Afallon Item Tool package (folder + zip) under dist\.
# Usage (from the project root):  powershell -ExecutionPolicy Bypass -File tools\build_package.ps1
param([string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Afallon', [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Select-String -Path "$root\src\AfallonItemSpawner.BepInEx\Plugin.cs" -Pattern 'PluginVersion = "([^"]+)"').Matches[0].Groups[1].Value
$name = "AfallonItemTool-$version"
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist $name

dotnet build "$root\src\AfallonItemSpawner.BepInEx\AfallonItemSpawner.BepInEx.csproj" -c Release "-p:AfallonGameRoot=$GameRoot" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$out = "$root\src\AfallonItemSpawner.BepInEx\bin\Release\net6.0"

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$plugin = Join-Path $stage 'BepInEx\plugins\AfallonItemTool'
New-Item -ItemType Directory -Force "$plugin\fonts" | Out-Null
Copy-Item "$out\AfallonItemSpawner.dll", "$out\AfallonItemSpawner.Core.dll" $plugin
Copy-Item "$out\fonts\*" "$plugin\fonts"
Copy-Item "$root\dist-template\INSTALL.md" $stage
Copy-Item "$root\dist-template\INSTALL.md" (Join-Path $stage 'README.md')
Copy-Item "$root\LICENSE", "$root\NOTICE.md", "$root\THIRD-PARTY-NOTICES.md", "$root\DISCLAIMER.md" $stage

$hashes = Get-ChildItem $stage -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    "$((Get-FileHash $_.FullName).Hash)  $($_.FullName.Substring($stage.Length + 1))" }
Set-Content (Join-Path $stage 'SHA256SUMS.txt') $hashes -Encoding ascii

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

& $Python "$root\tools\verify_release_contents.py" $zip
if ($LASTEXITCODE -ne 0) { throw 'Release content check failed.' }
"Package: $zip  ($((Get-Item $zip).Length) bytes)"
"SHA-256: $((Get-FileHash $zip).Hash)"
