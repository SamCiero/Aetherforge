param(
  [string]$Distro = "",
  [string]$CoreProjectDirWsl = "/mnt/d/Aetherforge/src/Aetherforge.Core",
  [string]$Urls = "http://127.0.0.1:8484"
)

$wsl = "wsl.exe"
$distroArgs = @()
if ($Distro.Trim()) { $distroArgs = @("-d", $Distro) }

# NOTE: keep it simple; no localhost; no multi-line quoting games.
$cmd = "cd '$CoreProjectDirWsl' && dotnet run --urls '$Urls'"
& $wsl @distroArgs -- bash -lc $cmd
exit $LASTEXITCODE
