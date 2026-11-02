# LiteClip release packaging. Run build.ps1 first, then:
#   powershell -ExecutionPolicy Bypass -File .\package.ps1
#
# Makes release\LiteClip-<version>.zip (the app, FFmpeg, languages, the full source code and a
# FINGERPRINTS.txt), plus release\NOTES.md with SHA-256 fingerprints for the GitHub release page.
# GitHub Actions runs exactly this script for every release (see .github\workflows\release.yml).
param([string]$Repo = $env:GITHUB_REPOSITORY)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
if (-not (Test-Path (Join-Path $dist 'LiteClip.exe'))) { throw "Run build.ps1 first." }
$version = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'src\Program.cs')), 'Version = "([^"]+)"').Groups[1].Value

$stage = Join-Path $env:TEMP ('liteclip-package-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'LiteClip'
New-Item -ItemType Directory -Force $app, "$app\tools", "$app\lang", "$app\source" | Out-Null

# The app
Copy-Item "$dist\LiteClip.exe" $app
Copy-Item "$dist\tools\ffmpeg.exe", "$dist\tools\ffprobe.exe", "$dist\tools\FFMPEG-LICENSE.txt" "$app\tools"
Copy-Item "$dist\lang\*.txt" "$app\lang"
Copy-Item (Join-Path $root 'IS IT SAFE.txt'), (Join-Path $root 'LICENSE') $app

# The full source code, so anyone can read it or build it themselves
foreach ($item in 'src', 'lang', '.github', 'build.ps1', 'package.ps1', 'README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'PRIVACY.md', 'SECURITY.md', 'LICENSE', '.gitignore') {
  $p = Join-Path $root $item
  if (Test-Path $p) { Copy-Item $p "$app\source" -Recurse }
}

# Fingerprints
$files = 'LiteClip.exe', 'tools\ffmpeg.exe', 'tools\ffprobe.exe'
$hashes = foreach ($f in $files) { [pscustomobject]@{ File = $f; Hash = (Get-FileHash "$app\$f" -Algorithm SHA256).Hash } }
$lines = @("LiteClip $version - SHA-256 fingerprints", "Check one in PowerShell:  Get-FileHash .\LiteClip.exe", "")
$lines += $hashes | ForEach-Object { "{0,-20} {1}" -f $_.File, $_.Hash }
[IO.File]::WriteAllLines("$app\FINGERPRINTS.txt", $lines)

# Zip
$release = Join-Path $root 'release'
New-Item -ItemType Directory -Force $release | Out-Null
$zip = Join-Path $release "LiteClip-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash

# Release notes (fingerprints + how to check them)
$verify = if ($Repo) { "gh attestation verify LiteClip-$version.zip --repo $Repo" } else { "gh attestation verify LiteClip-$version.zip --repo <owner>/LiteClip" }
$notes = @(
  "## LiteClip $version", "",
  "Download **LiteClip-$version.zip**, unzip it anywhere and run ``LiteClip.exe``. No installer, no admin rights, no internet.", "",
  "### Check it's the real thing", "",
  "This zip was built by GitHub from the source code in this repository. The build log is public (Actions tab).", "",
  "| File | SHA-256 |", "| --- | --- |",
  "| LiteClip-$version.zip | ``$zipHash`` |"
)
$notes += $hashes | ForEach-Object { "| $($_.File) | ``$($_.Hash)`` |" }
$notes += @("",
  "In PowerShell: ``Get-FileHash .\LiteClip-$version.zip`` must show the same fingerprint.", "",
  "With the GitHub CLI you can also prove the file came from this repository's build:", "",
  '```', $verify, '```', "",
  "See [CHANGELOG.md](CHANGELOG.md) for what's new.")
[IO.File]::WriteAllLines((Join-Path $release 'NOTES.md'), $notes)

Write-Host "Made $zip" -ForegroundColor Green
"{0,-24} SHA-256 {1}" -f "LiteClip-$version.zip", $zipHash
$hashes | ForEach-Object { "{0,-24} SHA-256 {1}" -f $_.File, $_.Hash }
