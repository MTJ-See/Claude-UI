[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\marketplace-tests'), [switch]$Live)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Security.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'MarketplaceSmoke.exe'
$source = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$resources = @('/resource:' + (Join-Path $root 'src\MainWindow.xaml') + ',ClaudeUI.MainWindow.xaml')
& (Join-Path $framework 'csc.exe') /nologo /utf8output /target:exe /platform:x64 /main:ClaudeUI.MarketplaceSmoke "/out:$output" @references @resources @source (Join-Path $root 'tests\MarketplaceSmoke.cs') (Join-Path $root 'tests\InstalledExtensionsSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Marketplace checks compilation failed.' }
$testArguments = @($OutputDirectory)
if ($Live) { $testArguments += '--live' }
& $output @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Marketplace checks failed.' }
