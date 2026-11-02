# Реліз-нотатки: права облікового запису служби в базі (DB-1)

## ⚠ Перед оновленням (для DBA)

Якщо служба ECR працює під логіном **без** `sysadmin`/`db_owner` (рекомендовано, `D-66`), до запуску нової версії видайте
користувачу бази облікового запису служби **три нові права `EXECUTE`**. Без них:

- без `doc.fn_VersionOf` — **кожен запис** рядка й комірки падає з «The EXECUTE permission was denied on the object 'fn_VersionOf'»
  (функцію додала міграція `RPTV01_ProjectVersionTimeline`);
- без `rpt.usp_GenerateTemplateViews` / `rpt.usp_GenerateStableViews` — вʼюхи `rpt.*` для SSRS не генеруються на старті й після
  публікації версії шаблону (`/health/ready`, картка `reportviews` = Degraded), а в журналі старту — попередження
  «Відсутні процедури … або обліковий запис служби не має на них EXECUTE … rpt.usp_GenerateTemplateViews, rpt.usp_GenerateStableViews»
  (до цієї версії воно називало лише першу причину — пропущений крок схеми).

```sql
USE [<DB>];
GRANT EXECUTE ON OBJECT::doc.fn_VersionOf TO [<SERVICE_ACCOUNT>];
GRANT EXECUTE ON OBJECT::rpt.usp_GenerateTemplateViews TO [<SERVICE_ACCOUNT>];
GRANT EXECUTE ON OBJECT::rpt.usp_GenerateStableViews TO [<SERVICE_ACCOUNT>];
```

`[<DB>]` — база ECR, `[<SERVICE_ACCOUNT>]` — користувач бази логіну служби. Виконати після кроку 2 (схема) `deploy-ecr.ps1` і до
старту служб. Повторна видача безпечна. Під `sysadmin`/`db_owner` нічого робити не треба.

## Повний перелік

Повний скрипт прав для встановлення з нуля (користувач бази, ролі `db_datareader` + `db_datawriter`, `UPDATE` на
послідовностях, `EXECUTE` на TVP-типах, процедурах і функції) — `docs/build/11-install-guide.md`, п. 2.3, і
`docs/admin/operations-runbook.md`, абзац «Права облікового запису служби» (генерується з переліку прав). Його достатність перевіряє тест
`ServiceAccountLeastPrivilegeTests` (користувач без входу рівно з цими правами: старт, сід, додавання/видалення рядка, правка
комірки, подання, нічна перевірка партицій, читання `rpt.v_DocumentCells`), а збіг переліку з runbook і з кодом —
`ServiceAccountGrantsRunbookTests`. Відкрите питання DBA `DB-1` закрито.
