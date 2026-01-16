param(
  [string]$Distro = "",
  [string]$CoreDirWsl = "/mnt/d/Aetherforge/wsl/core",
  [string]$Script = "./scripts/run-dev.sh"
)

$wsl = "wsl.exe"
$distroArgs = @()
if ($Distro -and $Distro.Trim().Length -gt 0) {
  $distroArgs = @("-d", $Distro)
}

$cmd = "cd $CoreDirWsl && $Script"
& $wsl @distroArgs -- bash -lc $cmd
exit $LASTEXITCODE
