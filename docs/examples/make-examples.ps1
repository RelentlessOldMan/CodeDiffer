<#
.SYNOPSIS
  Regenerate the example HTML reports (docs/examples/compare, docs/examples/compare3) and their screenshots
  (docs/examples/img) from CodeDiffer built from this checkout.

.DESCRIPTION
  By default the trees are a small made-up "shop" project written by this script into docs/examples/trees
  (git-ignored), with one file for each kind of change: a 2-way compare (modified, renamed with an edit, added,
  removed, whitespace-only, line endings only, binary) and a 3-way one (clean merge, conflict, one-sided on either
  side, agreed, add/add, modify/delete, a rename on one side merged with an edit on the other).

  Give -Left/-Right and/or -Base/-V1/-V2 to report on trees of your own instead (a small CodeSpawner corpus, say).

  Nothing about the machine is published: each tree's path is written as its path relative to the repo (or as its
  name, -Left -> "left", for one outside it), the saved-result path is dropped, the compare reads with --no-cache
  and saves its result into a temp directory removed afterwards. The output is then searched for the repo's full
  path, the user name, the computer name, the user profile and temp paths, and a drive letter or UNC path: any
  match fails the run (and lists where), so nothing is committed by mistake.

  Screenshots need Microsoft Edge or Chrome (headless); -NoScreenshots skips them.

.EXAMPLE
  ./docs/examples/make-examples.ps1
.EXAMPLE
  ./docs/examples/make-examples.ps1 -Left D:\corpus\a -Right D:\corpus\b -NoScreenshots
#>
param(
    [string]$Left, [string]$Right,
    [string]$Base, [string]$V1, [string]$V2,
    [string]$Out = $PSScriptRoot,
    [string]$Cli,
    [string]$Browser,
    [switch]$NoScreenshots
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\')
$Out = [IO.Path]::GetFullPath($Out).TrimEnd('\')
$utf8 = New-Object Text.UTF8Encoding($false)

function Put([string]$root, [string]$rel, [string]$text, [switch]$Crlf) {
    $p = Join-Path $root $rel
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)) | Out-Null
    $t = $text.Replace("`r`n", "`n")
    if (-not $t.EndsWith("`n")) { $t += "`n" } # a here-string has no last line ending; source files do
    if ($Crlf) { $t = $t.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($p, $t, $utf8)
}

# Natives write progress to stderr: under Stop on PowerShell 5.1 that is a terminating error even on exit 0. Run
# them under Continue (stderr shown) and decide on $LASTEXITCODE.
function Run {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $args[0] @($args | Select-Object -Skip 1) 2>&1 | ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { "$_" } } | Out-Host }
    finally { $ErrorActionPreference = $prev }
}

function PutBytes([string]$root, [string]$rel, [byte[]]$bytes) {
    $p = Join-Path $root $rel
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)) | Out-Null
    [IO.File]::WriteAllBytes($p, $bytes)
}

# ---------------------------------------------------------------- the made-up trees

function Make2([string]$l, [string]$r) {
    $cart = @'
"""The shopping cart."""

from decimal import Decimal

from .prices import price_of


class Cart:
    def __init__(self):
        self.items = {}

    def add(self, sku, quantity=1):
        self.items[sku] = self.items.get(sku, 0) + quantity

    def remove(self, sku):
        self.items.pop(sku, None)

    def total(self):
        return sum(price_of(sku) * n for sku, n in self.items.items())
'@
    Put $l 'src/cart.py' $cart
    Put $r 'src/cart.py' (@'
"""The shopping cart."""

from decimal import Decimal

from .discounts import apply_discounts
from .prices import price_of


class Cart:
    def __init__(self):
        self.items = {}

    def add(self, sku, quantity=1):
        if quantity < 1:
            raise ValueError(f"quantity must be at least 1, got {quantity}")
        self.items[sku] = self.items.get(sku, 0) + quantity

    def remove(self, sku):
        self.items.pop(sku, None)

    def total(self):
        subtotal = sum(price_of(sku) * n for sku, n in self.items.items())
        return apply_discounts(subtotal, self.items)
'@)

    $pricing = @'
"""Prices, in the shop's currency."""

from decimal import Decimal

PRICES = {
    "apple": Decimal("0.40"),
    "bread": Decimal("2.10"),
    "cheese": Decimal("5.75"),
    "coffee": Decimal("8.90"),
    "milk": Decimal("1.15"),
    "tea": Decimal("4.20"),
}


def price_of(sku):
    """The unit price of one item."""
    return PRICES[sku]


def known(sku):
    return sku in PRICES
'@
    Put $l 'src/pricing.py' $pricing
    Put $r 'src/prices.py' ($pricing.Replace('"coffee": Decimal("8.90")', '"coffee": Decimal("9.40")').Replace('    return PRICES[sku]', "    if sku not in PRICES:`n        raise KeyError(f`"no price for {sku!r}`")`n    return PRICES[sku]"))

    Put $l 'src/legacy_tax.py' (@'
"""Old flat-rate tax, replaced by per-region rates."""

RATE = 0.2


def with_tax(amount):
    return amount * (1 + RATE)
'@)
    Put $r 'src/discounts.py' (@'
"""Discounts applied at checkout."""

from decimal import Decimal

BULK_THRESHOLD = 10
BULK_RATE = Decimal("0.05")


def apply_discounts(subtotal, items):
    """5% off once the cart holds ten items or more."""
    if sum(items.values()) >= BULK_THRESHOLD:
        return subtotal * (1 - BULK_RATE)
    return subtotal
'@)

    $inv = @'
"""Stock levels."""

import json
from pathlib import Path

STOCK_FILE = Path("stock.json")


def load():
    if not STOCK_FILE.exists():
        return {}
    return json.loads(STOCK_FILE.read_text())


def save(stock):
    STOCK_FILE.write_text(json.dumps(stock))


def reserve(stock, sku, quantity):
    have = stock.get(sku, 0)
    if have < quantity:
        return False
    stock[sku] = have - quantity
    return True


def restock(stock, sku, quantity):
    stock[sku] = stock.get(sku, 0) + quantity


def low(stock, below=5):
    return [sku for sku, n in stock.items() if n < below]
'@
    Put $l 'src/inventory.py' $inv
    Put $r 'src/inventory.py' ($inv.Replace('    STOCK_FILE.write_text(json.dumps(stock))', "    tmp = STOCK_FILE.with_suffix(`".tmp`")`n    tmp.write_text(json.dumps(stock, indent=2, sort_keys=True))`n    tmp.replace(STOCK_FILE)").Replace('def low(stock, below=5):', 'def low(stock, below=10):').Replace('    return [sku for sku, n in stock.items() if n < below]', '    return sorted(sku for sku, n in stock.items() if n < below)'))

    $readme = @'
# Shop

A tiny shop used to show what CodeDiffer reports.

## Running

- install the requirements
- run `python -m shop`
- open the printed address
'@
    Put $l 'README.md' $readme
    Put $r 'README.md' ($readme.Replace('- install the requirements', '- install the requirements   ').Replace('## Running', '## Running  '))

    $ini = "[shop]`nname = Shop`ncurrency = EUR`nport = 8080`n"
    Put $l 'config/settings.ini' $ini -Crlf
    Put $r 'config/settings.ini' $ini

    $logo = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) + [byte[]](0..247 | ForEach-Object { [byte](($_ * 7) % 256) })
    PutBytes $l 'assets/logo.png' $logo
    $logo2 = [byte[]]$logo.Clone(); $logo2[100] = 0; $logo2[101] = 0
    PutBytes $r 'assets/logo.png' $logo2

    $log = "# Changelog`n`n## 1.0`n`n- First release.`n"
    Put $l 'docs/CHANGELOG.md' $log
    Put $r 'docs/CHANGELOG.md' $log
}

function Make3([string]$b, [string]$x1, [string]$x2) {
    $orders = @'
"""Orders."""

from datetime import datetime, timezone


def new_order(cart, customer):
    return {
        "customer": customer,
        "items": dict(cart.items),
        "placed": datetime.now(timezone.utc),
        "status": "new",
    }


def cancel(order):
    order["status"] = "cancelled"


def describe(order):
    n = sum(order["items"].values())
    return f"{n} item(s) for {order['customer']}"
'@
    Put $b 'src/orders.py' $orders
    Put $x1 'src/orders.py' ($orders.Replace("def cancel(order):`n    order[`"status`"] = `"cancelled`"", "def cancel(order, reason=None):`n    if order[`"status`"] == `"shipped`":`n        raise ValueError(`"a shipped order can't be cancelled`")`n    order[`"status`"] = `"cancelled`"`n    order[`"reason`"] = reason"))
    Put $x2 'src/orders.py' ($orders.Replace('    return f"{n} item(s) for {order[''customer'']}"', '    return f"{n} item(s) for {order[''customer'']} ({order[''status'']})"'))

    $ship = @'
"""Shipping costs."""

FREE_OVER = 50
FLAT_RATE = 4.95


def cost(subtotal, country):
    if subtotal >= FREE_OVER:
        return 0
    if country != "NL":
        return FLAT_RATE * 2
    return FLAT_RATE
'@
    Put $b 'src/shipping.py' $ship
    Put $x1 'src/shipping.py' ($ship.Replace('FLAT_RATE = 4.95', 'FLAT_RATE = 5.50'))
    Put $x2 'src/shipping.py' ($ship.Replace('FLAT_RATE = 4.95', 'FLAT_RATE = 3.95  # summer offer'))

    $cart = "class Cart:`n    def __init__(self):`n        self.items = {}`n`n    def clear(self):`n        self.items = {}`n"
    Put $b 'src/cart.py' $cart
    Put $x1 'src/cart.py' ($cart + "`n    def count(self):`n        return sum(self.items.values())`n")
    Put $x2 'src/cart.py' $cart

    $users = "def display_name(user):`n    return user[`"name`"]`n"
    Put $b 'src/users.py' $users
    Put $x1 'src/users.py' $users
    Put $x2 'src/users.py' "def display_name(user):`n    return user.get(`"nickname`") or user[`"name`"]`n"

    Put $b 'src/version.py' "VERSION = `"1.4.0`"`n"
    Put $x1 'src/version.py' "VERSION = `"1.5.0`"`n"
    Put $x2 'src/version.py' "VERSION = `"1.5.0`"`n"

    Put $x1 'src/coupons.py' "def valid(code):`n    return code.isalnum() and len(code) == 8`n"
    Put $x2 'src/coupons.py' "def valid(code):`n    return code.upper().startswith(`"SHOP`")`n"

    $old = "def fetch(path):`n    raise NotImplementedError(`"the v1 API is gone`")`n"
    Put $b 'src/old_api.py' $old
    Put $x2 'src/old_api.py' ($old + "`n`ndef status():`n    return `"retired`"`n")

    $catalog = @'
"""The product catalog."""

PRODUCTS = [
    {"sku": "apple", "name": "Apple", "unit": "piece"},
    {"sku": "bread", "name": "Bread", "unit": "loaf"},
    {"sku": "cheese", "name": "Cheese", "unit": "kg"},
    {"sku": "coffee", "name": "Coffee", "unit": "bag"},
    {"sku": "milk", "name": "Milk", "unit": "litre"},
]


def find(sku):
    for p in PRODUCTS:
        if p["sku"] == sku:
            return p
    return None


def names():
    return [p["name"] for p in PRODUCTS]
'@
    Put $b 'src/catalog.py' $catalog
    Put $x1 'src/catalog.py' ($catalog.Replace('    {"sku": "milk", "name": "Milk", "unit": "litre"},', "    {`"sku`": `"milk`", `"name`": `"Milk`", `"unit`": `"litre`"},`n    {`"sku`": `"tea`", `"name`": `"Tea`", `"unit`": `"box`"},"))
    Put $x2 'src/products.py' ($catalog.Replace('    return [p["name"] for p in PRODUCTS]', '    return sorted(p["name"] for p in PRODUCTS)'))
}

# ---------------------------------------------------------------- build, run, scrub

$own2 = -not ($Left -or $Right)
$own3 = -not ($Base -or $V1 -or $V2)
if (-not $own2 -and -not ($Left -and $Right)) { throw "give both -Left and -Right (or neither, for the made-up trees)" }
if (-not $own3 -and -not ($Base -and $V1 -and $V2)) { throw "give all of -Base, -V1 and -V2 (or none, for the made-up trees)" }

$trees = Join-Path $Out 'trees'
if (Test-Path $trees) { Remove-Item -Recurse -Force $trees }
if ($own2) {
    $Left = Join-Path $trees 'left'; $Right = Join-Path $trees 'right'
    Make2 $Left $Right
}
if ($own3) {
    $Base = Join-Path $trees 'base'; $V1 = Join-Path $trees 'v1'; $V2 = Join-Path $trees 'v2'
    New-Item -ItemType Directory -Force $Base | Out-Null
    Make3 $Base $V1 $V2
}

if (-not $Cli) {
    Write-Host '== Building the CLI from this checkout =='
    Run dotnet build (Join-Path $repo 'src\CodeDiffer.Cli') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    $Cli = Join-Path $repo 'src\CodeDiffer.Cli\bin\Release\net10.0\win-x64\CodeDiffer.Cli.exe'
}
if (-not (Test-Path $Cli)) { throw "no CLI at $Cli (give -Cli)" }

$results = Join-Path ([IO.Path]::GetTempPath()) ("codediffer-examples-" + [guid]::NewGuid().ToString('N'))
$oldResults = $env:CODEDIFFER_RESULTS_DIR
$env:CODEDIFFER_RESULTS_DIR = $results
$reports = @{ compare = (Join-Path $Out 'compare'); compare3 = (Join-Path $Out 'compare3') }
try {
    foreach ($d in $reports.Values) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
    Write-Host '== compare (2-way) =='
    Run $Cli compare $Left $Right --no-cache --html --out $reports.compare | Out-Host
    if ($LASTEXITCODE -ge 2) { throw "compare failed (exit $LASTEXITCODE)" }
    Write-Host '== compare3 (3-way) =='
    Run $Cli compare3 $Base $V1 $V2 --no-cache --all --html --out $reports.compare3 | Out-Host
    if ($LASTEXITCODE -ge 2) { throw "compare3 failed (exit $LASTEXITCODE)" }
}
finally {
    $env:CODEDIFFER_RESULTS_DIR = $oldResults
    if (Test-Path $results) { Remove-Item -Recurse -Force $results }
}

# Each tree's path as the report shows it: relative to the repo, else its name.
$labels = [ordered]@{}
foreach ($pair in @(@('left', $Left), @('right', $Right), @('base', $Base), @('v1', $V1), @('v2', $V2))) {
    $full = [IO.Path]::GetFullPath($pair[1]).TrimEnd('\')
    $labels[$full] = if ($full.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $full.Substring($repo.Length + 1).Replace('\', '/')
    } else { $pair[0] }
}
$textFiles = Get-ChildItem -Path @($reports.Values) -Recurse -File | Where-Object { $_.Extension -in '.html', '.js', '.txt', '.patch', '.json' }
foreach ($f in $textFiles) {
    $t = [IO.File]::ReadAllText($f.FullName)
    $t = [regex]::Replace($t, '"resultDir":\s*"(?:[^"\\]|\\.)*"', '"resultDir":null')
    foreach ($full in ($labels.Keys | Sort-Object Length -Descending)) {
        foreach ($form in @($full, $full.Replace('\', '\\'), $full.Replace('\', '/'))) {
            $t = $t.Replace($form, $labels[$full])
        }
    }
    [IO.File]::WriteAllText($f.FullName, $t, $utf8)
}

# Nothing about this machine may be left.
$leaks = @($repo, $env:USERPROFILE, [IO.Path]::GetTempPath().TrimEnd('\'), $env:LOCALAPPDATA) | Where-Object { $_ } |
    ForEach-Object { $_; $_.Replace('\', '\\'); $_.Replace('\', '/') } | ForEach-Object { [regex]::Escape($_) }
$leaks += @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -ge 3 } | ForEach-Object { '\b' + [regex]::Escape($_) + '\b' }
$leaks += '\b[A-Za-z]:(\\\\|\\|/)', '\\\\\\\\[A-Za-z0-9]', 'AppData'
$found = Select-String -Path ($textFiles.FullName) -Pattern $leaks -AllMatches
if ($found) {
    $found | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber): $($_.Matches[0].Value)" }
    throw "the reports still name something of this machine (above): not safe to publish"
}
Write-Host 'OK: no machine paths, user or computer name in the reports.'

# ---------------------------------------------------------------- screenshots

if ($NoScreenshots) { exit 0 }
if (-not $Browser) {
    $Browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
                 "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Browser) { throw "no Edge or Chrome found for the screenshots (give -Browser, or -NoScreenshots)" }
$img = Join-Path $Out 'img'
New-Item -ItemType Directory -Force $img | Out-Null

# A copy of the report that opens some files' diffs once loaded, captured headless, then removed.
function Shot([string]$dir, [string]$png, [string[]]$open, [switch]$Split) {
    $page = Join-Path $dir '_shot.html'
    $cfg = '{"split":' + $(if ($Split) { 'true' } else { 'false' }) + ',"open":[' + (($open | ForEach-Object { '"' + $_ + '"' }) -join ',') + ']}'
    $js = @"
<script>
window.addEventListener("load", function () { setTimeout(function () {
  var cfg = $cfg;
  if (cfg.split) document.getElementById("m-split").click();
  document.getElementById("expand").click();
  setTimeout(function () {
    document.querySelectorAll(".row.file").forEach(function (r) {
      var n = r.querySelector(".name").textContent;
      if (cfg.open.some(function (o) { return n === o || n.slice(-(o.length + 2)) === "\u2192 " + o; })) r.click();
    });
  }, 200);
}, 300); });
</script>
"@
    $html = [IO.File]::ReadAllText((Join-Path $dir 'index.html'))
    [IO.File]::WriteAllText($page, $html.Replace('</body>', $js + '</body>'), $utf8)
    $prof = Join-Path ([IO.Path]::GetTempPath()) ("codediffer-shot-" + [guid]::NewGuid().ToString('N'))
    try {
        $url = 'file:///' + $page.Replace('\', '/')
        $p = Start-Process -FilePath $Browser -PassThru -Wait -WindowStyle Hidden -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', "--user-data-dir=`"$prof`"",
            '--window-size=1400,1000', '--virtual-time-budget=8000', "--screenshot=`"$png`"", $url)
        if (-not (Test-Path $png)) { throw "no screenshot written for $png (exit $($p.ExitCode))" }
        Write-Host "  $png"
    }
    finally {
        Remove-Item $page -Force -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $prof -ErrorAction SilentlyContinue
    }
}

Write-Host '== Screenshots =='
if ($own2) {
    Shot $reports.compare (Join-Path $img 'compare-inline.png') @('cart.py')
    Shot $reports.compare (Join-Path $img 'compare-split.png') @('inventory.py') -Split
} else { Shot $reports.compare (Join-Path $img 'compare.png') @() }
if ($own3) {
    Shot $reports.compare3 (Join-Path $img 'compare3.png') @('shipping.py')
    Shot $reports.compare3 (Join-Path $img 'compare3-merge.png') @('orders.py')
}
else { Shot $reports.compare3 (Join-Path $img 'compare3.png') @() }
exit 0  # compare3 exits 1 when it finds conflicts, as the made-up trees have
