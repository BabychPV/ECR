---
name: two-schema-sources
description: "Схема БД має ДВА джерела: міграції EF і сирі .sql. Перевірка одного дає впевнено хибний висновок"
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-17T21:50:51.068Z
---

У цьому проєкті схема створюється з **двох** джерел:

1. **Міграції EF** — основна схема: `doc`, `cfg`, `dic`, `ext`, `itg`, `calc`,
   `wf`, `uom` тощо. Сутності в `Ecr.Domain/Entities/**`, конфігурації в
   `Ecr.Infrastructure/Persistence/Configurations/**`.
2. **`src/Ecr.Infrastructure/Persistence/Sql/*.sql`** — лише партиціонування,
   файлові групи, системні таблиці (`sys_ecr`), аудит, архів, сід, тригери,
   агентські задачі.

⛔ **Пошук по `Sql/*.sql` і висновок «таблиці немає» — хибний.** Я зробив цю
помилку ДВІЧІ в одному документі (2026-09-18, `MIGRATION-ESTIMATE.md`):
стверджував, що `ext.Legacy*` не розгортається, і навіть назвав це
«найдешевшим першим кроком». Насправді всі чотири таблиці є.

**Надійна перевірка — запит до розгорнутої бази:**

```powershell
sqlcmd -S localhost -E -d EcrDev -Q "SELECT s.name + '.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='ext' ORDER BY t.name;" -h -1
```

⚠ `sqlcmd` тут працює лише з `-E` (Windows-автентифікація); без нього
«Login failed». Бази `EcrDev`, `EcrE2E`, `EcrTest_*` лишаються після
прогонів — на них і можна дивитися.

Ширший урок: перш ніж записати «чогось немає», спитай, чи джерело в цьому
проєкті одне. Див. [[smoke-and-e2e]] — там та сама помилка з іншого боку:
я оголосив дефект «не живим», перевіривши лише клієнта.
