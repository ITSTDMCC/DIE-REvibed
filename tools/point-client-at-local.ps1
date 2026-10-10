# Points the game at the local server by editing ip.cfg in the install folder.
# The original file is kept once as ip.cfg.original; run with -Restore to put it back.
# This changes a configuration file only, never the game's programs.
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic",
    [string]$Address = "127.0.0.1",
    [switch]$Restore
)
$cfg = Join-Path $GameDir "ip.cfg"
$backup = Join-Path $GameDir "ip.cfg.original"
if ($Restore) {
    if (-not (Test-Path $backup)) { Write-Error "No backup found at $backup"; exit 1 }
    Copy-Item $backup $cfg -Force
    Write-Host "Restored the original ip.cfg"
    exit 0
}
if (-not (Test-Path $cfg)) { Write-Error "ip.cfg not found in $GameDir"; exit 1 }
if (-not (Test-Path $backup)) { Copy-Item $cfg $backup }
[xml]$xml = Get-Content $cfg
$branch = $xml.root.branch | Where-Object { $_.name -eq "public" }
if (-not $branch) { Write-Error "No 'public' branch in ip.cfg"; exit 1 }
# Every entry of the public branch is a server address (request, matchmaking and stats hosts):
# point each one at this PC, whatever the entries are called.
foreach ($node in $branch.ChildNodes) {
    if ($node.NodeType -eq [System.Xml.XmlNodeType]::Element) { $node.InnerText = $Address }
}
$xml.Save($cfg)
Write-Host "ip.cfg now points at $Address (original saved as ip.cfg.original)"
