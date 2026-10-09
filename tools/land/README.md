# tools/land: завантаження вкладки «2. Contract» шаблону Land

Скрипти PowerShell (Windows PowerShell 5.1 і PowerShell 7) працюють через REST API ECR.
Нічого не видаляють. Не запускати проти робочої бази замовника без рішення людини.

## На якому шаблоні запускати

`Apply-ContractHeader.ps1 -TemplateCode Land` потребує шаблон **Land**. Шаблон з фіксованими
таблицями без рядків (наприклад, DataGen) публікацію не пройде: `ECR-TMPL-4228`. Скрипт тоді
зупиняється з поясненням і переліком доступних шаблонів (`GET /api/v1/templates`).
Шаблон Land створюється імпортом методології: `tools/Ecr.MethodologyImport`.

## Порядок

1. Довідники LAND_*: `Import-ContractDictionaries.ps1` (спершу `-DryRun`).
2. Шапка: `Apply-ContractHeader.ps1 -TemplateCode Land` (спершу `-DryRun`).
   `-MigrateDocumentId 3,4` переносить документи на нову версію (Safe) і тоді, коли шапка вже
   на потрібній версії (0 змін). Dry-run міграції виконується завжди, застосування лише при `canApply`.
   Документи переносяться лише на останню опубліковану версію (старіший `-SourceVersionId` без змін —
   помилка). Фонова міграція: відповідь 202 обробляється автоматично (опитування `GET /jobs/{id}`,
   `-MigrateTimeoutMinutes`, за замовчуванням 60). `-Async` просить сервер працювати у фоні.
   Якщо в шаблону вже є чернетка, скрипт зупиняється: вона може містити чужі незавершені правки.
   Перегляньте її, опублікуйте чи видаліть вручну або запустіть із `-UseExistingDraft`
   (несумісний з `-SourceVersionId`).

## Адреса і вхід

- `-BaseUrl` або `$env:ECR_BASE_URL` (за замовчуванням `http://localhost:5092`).
- Обліковий запис: `-User` (або `$env:ECR_USER`) і `-Password` (SecureString). Пароль вводьте
  приховано: `$pw = Read-Host 'Пароль ECR' -AsSecureString`, далі `-Password $pw`. Пароль у відкритому
  тексті в командному рядку (`-Password 'пароль'`, `$env:ECR_PASSWORD = 'пароль'`) осідає в історії
  PSReadLine (у PowerShell 5.1 вона зберігається у файлі), тож так не робіть. `$env:ECR_PASSWORD`
  скрипти читають лише для автоматизації (CI, де значення дає менеджер секретів): змінна середовища
  видна всім дочірнім процесам сеансу, і скрипти не обіцяють її приховувати.
- **https**: працює як є.
- **http**: дозволено лише для localhost, 127.0.0.1, ::1. Сесійний cookie має прапор Secure і по
  http його не надсилає Windows PowerShell 5.1 (інакше 401), тому скрипт копіює cookie без Secure
  тільки для loopback. Для інших адрес по http скрипт відмовляє до відправлення пароля:
  використовуйте https.

## -DryRun

`-DryRun` (або `-WhatIf`) виконує лише читання: показує, що було б змінено.

```powershell
$pw = Read-Host 'Пароль ECR' -AsSecureString   # прихований ввід, до історії не потрапляє
.\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092 -User admin -Password $pw -DryRun
.\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092 -User admin -Password $pw
.\Apply-ContractHeader.ps1 -TemplateCode Land -BaseUrl http://localhost:5092 -User admin -Password $pw -DryRun
.\Apply-ContractHeader.ps1 -TemplateCode Land -BaseUrl http://localhost:5092 -User admin -Password $pw -MigrateDocumentId 3,4
```
