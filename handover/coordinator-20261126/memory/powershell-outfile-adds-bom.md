---
name: powershell-outfile-adds-bom
description: Out-File -Encoding utf8 у PowerShell 5.1 пише BOM; у git commit -F він потрапляє в рядок теми
metadata: 
  node_type: memory
  type: reference
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-19T15:09:02.340Z
---

`Out-File -Encoding utf8` у **Windows PowerShell 5.1** пише UTF-8 **з BOM**.
Якщо таким файлом годувати `git commit -F`, BOM опиняється на початку рядка
теми:

```
[280633b] ﻿[DOCS] Q15-01a закрито: IBM Plex дозволено
```

Хук формату коміта (`[TYPE] опис`) це пропустив, але рядок псується в
`git log`, у переліку PR і в будь-якому сторожі, що читає тему регуляркою з
`^`.

⚠ **Виправити після пушу нічим** — `git push --force` заборонений (CLAUDE.md).
Рятує лише те, що `gh pr merge --squash` складає повідомлення з **заголовка й
тіла PR**, а не з коміта гілки, тож у `main` BOM не доходить. Тобто наслідок
косметичний рівно доти, доки гілка squash-мержиться.

**How to apply:** для повідомлень коміта писати файл **Write-інструментом**
(він BOM не додає — перевірено) або
`[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))`.
`git commit -m @'…'@` теж працює, але ламається, коли в тексті є рядки, що
починаються з `—` або `-`: PowerShell читає їх як аргументи, і git відповідає
`did not match any file(s) known to git`.

Пов'язане: [[powershell-corrupts-cyrillic]], [[dotnet-cwd-ignores-powershell-cd]].
