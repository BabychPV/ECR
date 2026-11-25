---
name: stand-data-lives-on-two-disks
description: "Старі бази SQL Server на F:, нові стенди на H:\\EcrData; EcrTest_* теж на H:, тому чистка F: не звільняє"
metadata: 
  node_type: memory
  type: reference
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-19T09:42:23.385Z
---

Дисків **два**, і плутанина між ними коштувала години 2026-09-19:

- **`F:\MS SQL\SQL\MSSQL17.MSSQLSERVER\MSSQL\DATA`** — типовий шлях інстанса.
  Там лежать **старі** бази: `EcrDev`, `EcrE2E`, `EcrE2ET`, `EcrGate`, ~14 ГБ
  кожна. ⚠ Каталог закритий ACL-ами: `Get-ChildItem` з-під звичайної оболонки
  бачить **нуль файлів**. Розмір міряється лише через `sys.master_files`.
- **`H:\EcrData`** — куди йдуть **нові** стенди. `tools/setup-dev-db.ps1`:
  `-DataPath` із дефолтом `$(if (Test-Path 'H:\') { 'H:\EcrData' } else { '' })`.

⛔ **`EcrTest_*` лежать на `H:`.** Знесення 62 таких баз не зрушило `F:` ані на
гігабайт, а `H:` звільнилося з 59 до 86 ГБ. Рецепт прибирання в CLAUDE.md
правильний — але диск, який він звільняє, `H:`.

Критерій перед стендом: **~16 ГБ на `H:`**, незалежно від `F:`. Два прогони
`smoke.ps1` (22/22) і один `e2e-stand.ps1` пройшли з `F:` на 13.5 ГБ.

```powershell
Get-PSDrive -Name H,F | Select-Object Name, @{n='вільно_ГБ';e={[math]::Round($_.Free/1GB,1)}}
```

Перевірка всередині `setup-dev-db.ps1` міряє диск із `-DataPath`, тобто `H:`, і
вона правильна — помилковим був датований запис у CLAUDE.md (виправлено).

Пов'язане: [[smoke-and-e2e]], [[claude-md-entries-go-stale]].
