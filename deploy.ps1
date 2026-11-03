# Builds the mod manager from this repo and mirrors it into the working copy
# at %USERPROFILE%\source\CotLModManager (where CotLModManager.exe is used).
# Usage:  .\deploy.ps1

$src = $PSScriptRoot
$dst = Join-Path $env:USERPROFILE 'source\CotLModManager'

& cmd /c "`"$src\build.bat`""
if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed'; exit 1 }

# Mirror sources + built exe; build folders and git files are not copied or purged.
robocopy $src $dst /MIR /NFL /NDL /NJH /NP `
  /XD .git .github .vs bin obj `
  /XF .gitattributes .gitignore deploy.ps1 | Out-Host
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed ($LASTEXITCODE)"; exit 1 }
Write-Host "Deployed to $dst"
exit 0
