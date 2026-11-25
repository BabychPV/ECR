---
name: powershell-corrupts-cyrillic
description: "PowerShell 5.1 псує кирилицю у файлах цього репозиторію; як мутувати й відкочувати файли безпечно"
metadata:
  node_type: memory
  type: reference
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-18T06:06:02.137Z
---

⛔ `(Get-Content -Raw) | Set-Content` та `Get-Content | Out-File` **мовчки
псують кирилицю** у файлах цього репозиторію: PS 5.1 читає UTF-8 без BOM як
ANSI і дописує BOM назад. Файли тут майже всі з українськими коментарями,
тож це зачіпає будь-яку правку через пайп. Двічі за одну сесію (різні
агенти) це зіпсувало продуктові файли під час мутаційного тестування.

**Читати файли** — Read-інструментом; `Get-Content`/`Select-String` для
показу теж спотворюють вивід, але файл не чіпають.

**Правити файли** — Edit/Write-інструментами.

**Мутувати для доказу** — або Edit-інструментом, або через
`[System.IO.File]::ReadAllText(path)` / `WriteAllText(path, text)` (вони
тримають UTF-8).

**Відкочувати мутацію** — `git checkout -- <шлях>`, а НЕ зворотний пайп
PowerShell.

⚠ Після відкоту через `Copy-Item` обов'язково
`(Get-Item <шлях>).LastWriteTime = Get-Date` — див.
[[msbuild-stale-after-copy]].

⛔ **Текст падіння xUnit із кирилицею до консолі НЕ доїжджає.** `dotnet
test` друкує `[FAIL]` і зведення, а саме повідомлення `Assert` губиться —
і в пайпі, і через `cmd /c … > файл`. Тобто мутаційний доказ виглядає як
«впало, але не видно чому». Робочий спосіб — забрати текст із TRX:

```powershell
dotnet test <csproj> --filter "FullyQualifiedName~<ім'я>" --logger "trx;LogFileName=red.trx" --results-directory <каталог>
([xml][System.IO.File]::ReadAllText("<каталог>\red.trx")).TestRun.Results.UnitTestResult.Output.ErrorInfo.Message
```
