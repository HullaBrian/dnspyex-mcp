<#
.SYNOPSIS
    Builds the benign test fixture: Stage2.dll, then FakeLoader.exe with Stage2 embedded as
    payload.bin (GZip, then XOR with "fixture"). Output: tests\samples\out\FakeLoader.exe
#>
$ErrorActionPreference = 'Stop'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnet) { $dotnet.Source } elseif (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { throw "The .NET SDK is required" }

$root = $PSScriptRoot
$out = Join-Path $root "out"
$obj = Join-Path $root "obj"
New-Item -ItemType Directory -Force $out, $obj | Out-Null

& $dotnetExe build (Join-Path $root "Stage2\Stage2.csproj") -c Release -nologo -v:q -o (Join-Path $obj "stage2")
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# payload.bin = XOR("fixture", GZip(Stage2.dll))
$stage2 = [IO.File]::ReadAllBytes((Join-Path $obj "stage2\Stage2.dll"))
$ms = New-Object IO.MemoryStream
$gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress)
$gz.Write($stage2, 0, $stage2.Length)
$gz.Close()
$data = $ms.ToArray()
$key = [Text.Encoding]::ASCII.GetBytes("fixture")
for ($i = 0; $i -lt $data.Length; $i++) { $data[$i] = $data[$i] -bxor $key[$i % $key.Length] }
$payload = Join-Path $obj "payload.bin"
[IO.File]::WriteAllBytes($payload, $data)

& $dotnetExe build (Join-Path $root "FakeLoader\FakeLoader.csproj") -c Release -nologo -v:q -o $out "-p:PayloadPath=$payload"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Copy-Item (Join-Path $obj "stage2\Stage2.dll") (Join-Path $out "Stage2.expected.dll") -Force
Write-Host "Built $(Join-Path $out 'FakeLoader.exe')"
