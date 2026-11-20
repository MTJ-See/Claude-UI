[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidatePattern('^[a-f0-9]{40}$')][string]$Revision)

$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'ClaudeUI-price-snapshot' }
$uri = "https://raw.githubusercontent.com/BerriAI/litellm/$Revision"
$upstream = Invoke-RestMethod -Uri "$uri/model_prices_and_context_window.json" -Headers $headers
$license = (Invoke-WebRequest -Uri "$uri/LICENSE" -Headers $headers).Content
$commit = Invoke-RestMethod -Uri "https://api.github.com/repos/BerriAI/litellm/commits/$Revision" -Headers $headers
$models = [ordered]@{}
foreach ($model in $upstream.PSObject.Properties) {
    if ($model.Value.litellm_provider -ne 'anthropic' -or $model.Value.mode -ne 'chat') { continue }
    $costs = [ordered]@{}
    foreach ($field in $model.Value.PSObject.Properties) {
        if ($field.Name -like '*cost*' -and $field.Value -is [ValueType] -and $field.Value -isnot [bool]) {
            $costs[$field.Name] = $field.Value
        }
    }
    if ($costs.Contains('input_cost_per_token') -and $costs.Contains('output_cost_per_token')) { $models[$model.Name] = $costs }
}
if ($models.Count -eq 0 -or $license -notmatch 'MIT License') { throw 'Invalid upstream price data or license.' }
$target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\third_party\litellm'))
$snapshot = [ordered]@{ source = 'https://github.com/BerriAI/litellm'; revision = $Revision; updated = $commit.commit.committer.date; models = $models }
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $target 'prices.json'), ($snapshot | ConvertTo-Json -Depth 10), $utf8)
[IO.File]::WriteAllText((Join-Path $target 'LICENSE'), $license, $utf8)
[IO.File]::WriteAllText((Join-Path $target 'source.json'), (@{ sha = $Revision; date = $commit.commit.committer.date } | ConvertTo-Json), $utf8)
Write-Output "Updated $($models.Count) Anthropic price entries at $Revision. Rebuild with ./Build.ps1 -Smoke."
