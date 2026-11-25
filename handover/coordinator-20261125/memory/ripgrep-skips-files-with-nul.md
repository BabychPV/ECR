---
name: ripgrep-skips-files-with-nul
description: Сирий байт 0x00 у джерелі робить файл невидимим для рекурсивного rg; git grep дивиться лише перші 8 КБ
metadata: 
  node_type: memory
  type: reference
  originSessionId: c1e67b1c-ff06-460f-ba27-deb627d4f29b
  modified: 2026-09-18T20:50:58.998Z
---

Файл із байтом `0x00` ripgrep вважає двійковим. При пошуку **по каталогу**
він пропускає його **мовчки** — без повідомлення про пропуск. При явному
іменуванні файлу збіги показує, тож перевірка «на одному файлі» дефекту не
виявляє.

Виміряно 2026-09-18 на `src/Ecr.Web/src/app/Breadcrumbs.tsx` (виправлено
в BabychPV/ECR#337): `rg crumbSignature src/Ecr.Web/src/app` давав
`No files found` — для функції, яка **оголошена** в цьому ж каталозі.
`rg useCacheVersion src/Ecr.Web/src` перелічував три файли, що її згадують,
і пропускав той, де вона визначена.

⚠ `git grep` у цьому випадку **працював** і тому не був сигналом: git шукає
NUL лише в перших ~8 КБ файлу, а тут байти лежали на зміщенні 10353. Отже
«`git grep` знаходить» ще не означає «`rg` знаходить».

Це прямо підриває вимогу CLAUDE.md перевіряти датовані записи одним
`git grep`/пошуком: для такого файлу перевірка тихо повертає порожнечу —
той самий режим відмови, що й [[claude-md-entries-go-stale]].

**Як перевірити каталог на такі файли:**

```powershell
Get-ChildItem -Recurse -File -Include *.ts,*.tsx,*.cs | Where-Object {
  [System.IO.File]::ReadAllBytes($_.FullName) -contains 0
} | Select-Object FullName
```

**Виправлення** — escape-послідовність замість сирого байта; рядок на виході
побайтно той самий. ⚠ Записати її інструментами `Edit`/`Write` напряму не
можна, вони її декодують назад у NUL —
див. [[write-tool-decodes-unicode-escapes]].

Роздільники `0x1F`/`0x1E` пошуку НЕ ламають — двійковим файл робить лише
`0x00`.
