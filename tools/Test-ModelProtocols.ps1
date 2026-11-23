[CmdletBinding()]
param([string]$OutputDirectory, [switch]$AuditEffort)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts\protocol-tests" }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Security.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'ModelProtocolSmoke.exe'
$source = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& (Join-Path $framework 'csc.exe') /nologo /utf8output /target:exe /platform:x64 /main:ClaudeUI.ModelProtocolSmoke "/out:$output" @references @source (Join-Path $root 'tests\ModelProtocolSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Protocol checks compilation failed.' }
if ($AuditEffort) { & $output --audit-effort } else { & $output }
if ($LASTEXITCODE -ne 0) { throw 'Protocol checks failed.' }
