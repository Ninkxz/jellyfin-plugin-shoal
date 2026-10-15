# Shared helpers for build.ps1 and manifest.ps1: read build.yaml, the single place the plugin's identity and version live.
Set-StrictMode -Version Latest

function Get-BuildYaml {
    param([string]$Path)
    $values = @{}
    $lines = Get-Content -LiteralPath $Path
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^(\w+):\s*"(.*)"\s*$') {
            $values[$Matches[1]] = $Matches[2]
        }
        elseif ($line -match '^(\w+):\s*[>|]\s*$') {
            # A folded block: the indented lines that follow, joined with spaces.
            $key = $Matches[1]
            $parts = @()
            while ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s+\S') {
                $i++
                $parts += $lines[$i].Trim()
            }
            $values[$key] = ($parts -join ' ')
        }
    }
    return $values
}
