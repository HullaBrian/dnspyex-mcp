<#
.SYNOPSIS
    Builds dnSpy.Mcp and installs it into a dnSpyEx installation.
.EXAMPLE
    .\scripts\install.ps1 -DnSpyDir "C:\Tools\dnSpy"
    .\scripts\install.ps1                      # uses .tools\dnSpy (the dev copy)

    # Remote access (agent on another machine, e.g. dnSpy in a VM). Add -Token to require a bearer token.
    .\scripts\install.ps1 -DnSpyDir "C:\Tools\dnSpy" -Bind 0.0.0.0

    # Build on this machine into a folder you copy to the VM's <dnSpy>\bin\Extensions\
    .\scripts\install.ps1 -OutputDir .\dist -Bind 0.0.0.0
#>
param(
    # Folder containing dnSpy.exe (the .NET build of dnSpyEx 6.6+). A "bin" subfolder is detected automatically.
    [string]$DnSpyDir = (Join-Path $PSScriptRoot "..\.tools\dnSpy"),
    [string]$Configuration = "Release",
    # Address the server listens on: 127.0.0.1 (default, local only), 0.0.0.0 (all interfaces) or a specific IP.
    [string]$Bind,
    # Optional bearer token clients must send.
    [string]$Token,
    # Fixed port (default: 13380, scanning upwards if busy).
    [int]$Port,
    # Instead of installing into $DnSpyDir, put the extension folder (dnSpy.Mcp\...) here. $DnSpyDir is still
    # used to compile against, so it must be the same dnSpyEx version as the target.
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'

$DnSpyDir = (Resolve-Path $DnSpyDir).Path
$binDir = if (Test-Path (Join-Path $DnSpyDir "bin\dnSpy.Contracts.DnSpy.dll")) { Join-Path $DnSpyDir "bin" } else { $DnSpyDir }
if (-not (Test-Path (Join-Path $binDir "dnSpy.Contracts.DnSpy.dll"))) {
    throw "dnSpy.Contracts.DnSpy.dll not found under $DnSpyDir. Pass the folder that contains dnSpy.exe."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnet) { $dotnet.Source } elseif (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { throw "The .NET 10 SDK is required (https://dot.net)" }

$project = Join-Path $PSScriptRoot "..\src\dnSpy.Mcp\dnSpy.Mcp.csproj"
& $dotnetExe build $project -c $Configuration -nologo -v:m "-p:DnSpyBin=$binDir"
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$outDir = Join-Path $PSScriptRoot "..\src\dnSpy.Mcp\bin\$Configuration"
$target = if ($OutputDir) { Join-Path $OutputDir "dnSpy.Mcp" } else { Join-Path $binDir "Extensions\dnSpy.Mcp" }
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($f in "dnSpy.Mcp.x.dll", "dnSpy.Mcp.x.pdb", "dnSpy.Mcp.x.xml") {
    Copy-Item (Join-Path $outDir $f) $target -Force
}

# Settings file (read by the extension at startup; environment variables override it)
$settingsPath = Join-Path $target "dnSpy.Mcp.json"
$isRemote = $Bind -and ($Bind -notin @("127.0.0.1", "localhost", "loopback", "::1"))
if ($Bind -or $Token -or $Port) {
    $settings = [ordered]@{}
    if ($Bind) { $settings.bind = $Bind }
    if ($Port) { $settings.port = $Port }
    if ($Token) { $settings.token = $Token }
    $settings | ConvertTo-Json | Set-Content -Encoding utf8 $settingsPath
    Write-Host "Wrote $settingsPath"
}

Write-Host "Installed to $target"
if ($OutputDir) {
    Write-Host "Copy the dnSpy.Mcp folder to <dnSpy>\bin\Extensions\ on the target machine."
}
$p = if ($Port) { $Port } else { 13380 }
if ($isRemote) {
    Write-Host ""
    Write-Host "Remote access is enabled. On the dnSpy machine, allow the port through the firewall (elevated):"
    Write-Host "  netsh advfirewall firewall add rule name=`"dnSpy MCP`" dir=in action=allow protocol=TCP localport=$p"
    Write-Host "Then, on the agent machine:"
    $header = if ($Token) { " --header `"Authorization: Bearer $Token`"" } else { "" }
    Write-Host "  claude mcp add --transport http dnspy http://<dnspy-machine-ip>:$p/mcp$header"
    Write-Host "dnSpy's Output window ('MCP' pane) lists the exact URLs it is reachable at."
}
else {
    Write-Host "Start dnSpy; the MCP endpoint is http://127.0.0.1:$p/mcp (see dnSpy's Output window, 'MCP' pane)."
}
