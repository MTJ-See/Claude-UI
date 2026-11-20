[CmdletBinding()]
param([switch]$Smoke, [switch]$Live, [string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework 4.x C# compiler was not found.'
}
$references = @(
    'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll', 'System.Security.dll',
    'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$source = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName
$resource = '/resource:' + (Join-Path $PSScriptRoot 'src\MainWindow.xaml') + ',ClaudeUI.MainWindow.xaml'
$avatarResource = '/resource:' + (Join-Path $PSScriptRoot 'src\Assets\UserAvatar.jpg') + ',ClaudeUI.UserAvatar.jpg'
$nativeIcon = '/win32icon:' + (Join-Path $PSScriptRoot 'src\Assets\ClaudeUI.ico')
$pricesResource = '/resource:' + (Join-Path $PSScriptRoot 'third_party\litellm\prices.json') + ',ClaudeUI.Prices.json'
$licenseResource = '/resource:' + (Join-Path $PSScriptRoot 'third_party\litellm\LICENSE') + ',ClaudeUI.LiteLLM.LICENSE'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory 'ClaudeUI.exe'

& $compiler /nologo /utf8output /target:winexe /platform:x64 /optimize+ "/out:$output" $nativeIcon $resource $avatarResource $pricesResource $licenseResource @references @source
if ($LASTEXITCODE -ne 0) { throw 'Claude UI compilation failed.' }
Write-Output "Built $output"

if ($Smoke -or $Live) {
    $smokeOutput = Join-Path $OutputDirectory 'ClaudeUI.Smoke.exe'
    $test = Join-Path $PSScriptRoot 'tests\Smoke.cs'
    try {
        & $compiler /nologo /utf8output /target:exe /platform:x64 /main:ClaudeUI.Smoke "/out:$smokeOutput" $resource $avatarResource $pricesResource $licenseResource @references @source $test
        if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed.' }
        $testArgs = @()
        if ($Live) { $testArgs += '--live' }
        & $smokeOutput @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
    }
    finally {
        if (Test-Path -LiteralPath $smokeOutput) { Remove-Item -LiteralPath $smokeOutput }
    }
}
