# Лист готовності: lane/integ-permit-usage («де використано» дозвіл + стабільні rpt-вʼюхи; сервер і контракт, клієнт — Permites)

- Гілка / lane: `lane/integ-permit-usage`
- Пункт: PERMIT-USAGE-PLAN п.2 (перевірка сервером), п.3 (`GET /permits/{id}/usages` + лічильник у переліку), п.4 (rpt `<Col>_Id` і вʼюхи даних дозволів)
- Хеш tip: `cb61fce6900454c8eea21cc3fdbc24515b7b82e1`
- Хеш контракту (раніше для клієнта): `22a3a4dfe7840d87f88fc30fdd5e936f67b9605b`
- База: `origin/main` `4cfeff59d` (PR #671); main за час роботи не рухався
- Лінійна: 3 коміти (контракт+сервер, rpt+тести, фікс фікстури клієнта), merge-комітів 0; мутацій у кінчику немає
- Sql: так — `05-rpt-views.sql` (процедура `usp_GenerateStableViews` + 3 статичні вʼюхи); `09-seed.sql` НЕ чіпали, нових ключів UiString немає
- EF-міграція: ні
- Контракт openapi.snapshot.json / schema.d.ts: так, перегенеровано ОСТАННІМ кроком у коміті 22a3a4dfe; повторна генерація побайтно та сама; `02-contracts.md` — рядок маршруту
- shared/ui, package*.json, lock, CLAUDE.md, .github: ні. Клієнт: лише тестова фікстура `permits/list/__tests__/permitFixture.ts` (+`usedInDocumentCount: 0`, інакше TS2322)
- Нові коди помилок / ключі: ні (422 `ECR-REQ-0422` на наявних `malformedRequest` і `pageSizeOutOfRange`)
- Нові маршрути: `GET /api/v1/permits/{id}/usages?periodKey=&limit=` (Registry.View + грант Read; документи лише проєктів із Document.View); EndpointCoverage — тимчасове звільнення до клієнтської lane
- Зміна відповіді: `PermitListItem.usedInDocumentCount` (int, обовʼязкове)

## CI
- CI (повний, push lane/integ-*): run 41393941314 — success
- Evidence: run 41393941344 — success
- (на 22a3a4dfe CI був червоним лише через TS2322 у фікстурі; виправлено cb61fce69)

## Локально
- build ✓; Architecture 1170/1170 ✓ (після правки `ReportDefinitionTests.IsRawDocumentView`: вʼюхи над `dic.RegistryValue` — шар сирих даних)
- Api (PermitUsagesHttpTests) 5/5 ✓; Infrastructure: ReportViewStable* 13/13, Reporting/RptContract/ViewerRole/SchemaValidator 74/74 ✓
- Мутація: без `where !r.IsDeleted` у запиті комірок `FindUsagesAsync` — червоні 2 тести Api (usages по рядках, фільтр періоду)
- Бази `EcrTest_*` цього worktree видалено

## Рішення
- «Де використано» — власний запит по `doc.DocumentHeaderValue` і `doc.CellValue` (значення = запис дозволу), а не `RegistryUsage` (той відповідає «які колонки вказують на довідник»); прихований реєстр шапки не виключається
- Шапка не має періоду: `periodKey=null`; з фільтром `periodKey` шапка показується, якщо документ має таблиці в цьому періоді
- Лічильник і перелік звужені проєктами з `Document.View` (читач без доступу бачить 0)
- п.2: пікер колонки вже працює як шапка (`LookupRegistryGuard` без виключення прихованих; перевірка значення — `LookupTargetMatcher` + дата кінця періоду); доказ HTTP-тестом (чинний — 200, протермінований і чужий — 422 ECR-CELL-4223). Різниця «колонка: на кінець періоду, шапка: хоч один день вікна» — наявна, не змінювалась
- rpt: `<Col>` Lookup лишається Id (не ламаємо звіти), додано явне `<Col>_Id` (те саме Id); код/номер дозволу — у `rpt.v_Permit.PermitNumber` (код запису дозволу автогенерований і не є номером)

## Ризики / відкрите
- Питання координатору: чи потрібно змінити `<Col>` на читабельне значення (код запису) з `<Col>_Id` як ключем — зламає наявні SSRS-звіти на Lookup-колонках
- Hybrid-режим зберігання комірок (`CellStorageMode.Hybrid`) у «де використано» не охоплено — запит читає лише `doc.CellValue`, як і наявний `FindDefinitionUsageAsync`
- Вʼюхи даних дозволів жорстко знають `AIR_LIMITS` / `AIR_SOURCES` / `AIR_SOURCE_SUBSTANCES`; інші типи — окремо за запитом

## Хто зводить
- Координатор (RC з SHA cb61fce69; повний CI уже зелений на цьому SHA)
