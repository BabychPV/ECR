---
name: dotnet-cwd-ignores-powershell-cd
description: "[System.IO.File]::ReadAllText/WriteAllText з відносним шляхом пише не туди, куди вказує cd — агент двічі влучив у H:\\ECR замість свого worktree"
metadata: 
  node_type: memory
  type: reference
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-19T12:15:15.374Z
---

⛔ **`cd` у PowerShell НЕ змінює робочий каталог процесу .NET.** Тому

```powershell
cd H:\ECR-wt\be06-conflict
[System.IO.File]::WriteAllText('src\App\Foo.cs', $text)   # ← запише в H:\ECR\src\App\Foo.cs
```

Агент BE-06 так двічі записав файли в `H:\ECR` — інтеграційний чекаут, з якого
я мержу. Там вони й лежали незакоміченими, відрізняючись від правильних копій у
worktree. У чужий PR або в `main` половинчастий файл поїхав би так, що жоден
гейт цього не показав би: він виглядав би як частина сусідньої зміни.

Те саме стосується `::ReadAllText` — читає не той файл, який видно в `ls`.

**How to apply:**
- у `[System.IO.File]::*`, `Get-FileHash`, `Resolve-Path` всередині агента —
  **лише абсолютні шляхи**;
- PowerShell-командлети (`Get-Content`, `Set-Content`, `Copy-Item`) `cd`
  поважають — проблема саме у викликах .NET-API;
- перед комітом: `git status --short` у worktree і звірка списку з тим, що
  справді правилося;
- інтегратору: `git status --short` у `H:\ECR` перед кожним мержем. Чужі
  незакомічені правки там — сигнал, а не шум.

Пов'язане: [[subagents-need-write-guard]], [[worktree-branch-switches]],
[[powershell-corrupts-cyrillic]].
