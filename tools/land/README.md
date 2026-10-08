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
   Фонова міграція: відповідь 202 обробляється автоматично (опитування `GET /jobs/{id}`,
   `-MigrateTimeoutMinutes`, за замовчуванням 60). `-Async` просить сервер працювати у фоні.

## Адреса і вхід

- `-BaseUrl` або `$env:ECR_BASE_URL` (за замовчуванням `http://localhost:5092`).
- Обліковий запис: `-User` / `-Password` (SecureString) або `$env:ECR_USER` / `$env:ECR_PASSWORD`
  (лише для поточного сеансу; у файли й логи не потрапляє).
- **https**: працює як є.
- **http**: дозволено лише для localhost, 127.0.0.1, ::1. Сесійний cookie має прапор Secure і по
  http його не надсилає Windows PowerShell 5.1 (інакше 401), тому скрипт копіює cookie без Secure
  тільки для loopback. Для інших адрес по http скрипт відмовляє до відправлення пароля:
  використовуйте https.

## -DryRun

`-DryRun` (або `-WhatIf`) виконує лише читання: показує, що було б змінено.

```powershell
$env:ECR_USER = 'admin'; $env:ECR_PASSWORD = '...'
.\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092 -DryRun
.\Import-ContractDictionaries.ps1 -BaseUrl http://localhost:5092
.\Apply-ContractHeader.ps1 -TemplateCode Land -BaseUrl http://localhost:5092 -DryRun
.\Apply-ContractHeader.ps1 -TemplateCode Land -BaseUrl http://localhost:5092 -MigrateDocumentId 3,4
```
