---
name: toolchain-paths
description: "Де на цій машині лежить git (не в PATH), і чого в оточенні бракує для збірки ECR"
metadata: 
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-17T21:19:34.214Z
---

`git` **не встановлено як окремий продукт** і його немає в `PATH`. Робочий
Git for Windows 2.39.2 возить Visual Studio 2022 Enterprise:

`C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe`

Викликати через змінну в кожній команді — стан оболонки між викликами не
зберігається.

**Як прогнати тести без Docker.** Docker тут недоступний, а 15 тестів поза
категорією `Integration` усе одно тягнуть `SqlServerFixture`. Локальний
SQL Server (`MSSQLSERVER`) запущено, і фікстура має штатний обхід:

```
$env:ECR_TEST_SQL = "Server=localhost;Integrated Security=true;TrustServerCertificate=true"
```

З ним повний `dotnet test Ecr.sln` дає 1847/1847. Бази `EcrTest_*`
лишаються після прогону НАВМИСНЕ (щоб зазирнути після падіння), імена
детерміновані — не накопичуються. `dotnet test` приймає лише один проєкт
за виклик.

**NuGet.** Джерело `TelerikNuGetV3` у глобальному конфігу користувача
**вимкнено назавжди** (2026-09-17, користувач підтвердив: «Telerik мені не
потрібен»). Не вмикати назад.

Чому це було потрібно: за центрального керування пакетами два джерела дають
`NU1507`, а `TreatWarningsAsErrors` робить із нього помилку. Репозиторного
`nuget.config` для цього НЕ досить — `dotnet ef` оцінює проєкт у власному
контексті поза деревом репозиторію і бачить лише глобальний конфіг. Тому
зламаними були `setup-dev-db.ps1` і `smoke.ps1`, а не тільки `restore`.

У `H:\ECR\nuget.config` створено репозиторний конфіг (`<clear />` +
nuget.org). Станом на 2026-09-17 він **untracked** — рішення про коміт за
користувачем.

**`gh` встановлено через winget** — але НЕ в `C:\Program Files\GitHub CLI\`
і не в `PATH`:

`%LOCALAPPDATA%\Microsoft\WinGet\Packages\GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe\bin\gh.exe`

⚠ `gh` сам шукає `git` у `PATH` і без нього падає з «unable to find git
executable in PATH». Отже перед кожним викликом `gh`:

```powershell
$env:PATH = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd;" + $env:PATH
```

**Node — v24.19.0** (встановлено 2026-09-18 через `winget install OpenJS.NodeJS.LTS`;
до того стояв 18.17.1, і це блокувало `vitest@5` та Playwright). Після оновлення
клієнтський набір іде 84 с замість 170 с, junction'и на `node_modules` у
worktree'ях більше не потрібні для запуску — але самі `node_modules` у новому
worktree все одно треба або поставити (`npm ci`), або зробити junction на
головний чекаут.

Є також: npm 10, WinRAR (`C:\Program Files\WinRAR\UnRAR.exe`).
.NET SDK 10.0.401 встановлено (див. шлях вище).

Це доповнює розділ «Особливості середовища» в CLAUDE.md, який попереджає про
два різні git одночасно, але не називає шлях для цієї машини.

✎ 18.11: Python 3.12 стоїть у %LOCALAPPDATA%\Programs\Python\Python312, запуск — `py -3.12` (так кличе tools/land/model-gen/run-model-gen.ps1). `python` у PATH — заглушка Windows Store; висновок «Python не встановлено» хибний.
