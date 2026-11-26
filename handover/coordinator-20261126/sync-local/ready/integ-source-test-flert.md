# Картка: source-test-flert «Test connection» FLERT відмовляє без причини; live API не пише журнал
Статус: live-verified
Гілка: lane/integ-source-test-flert
База: d75032896 (origin/lane/integ-b13)
Пачка: b14 (SHA в AC — cherry-pick пачки lane/integ-b14 → live 7afb19c97)
Ризик: R2
Рішення: людина 25.11 (скрин) «ще додай це» — Sources → FLERT (gas composition, read only) → Test connection → «The source refused the connection / could not be reached, or it answered with an error»
Дизайн: не потрібен

## 1. Що і навіщо
- aud.SecurityEvent 34816/34817 (land-owner, 25.11 16:41–16:42): DataSourceConnectionTested FLERT ok=false. Причини ніде: UI — загальний текст; серверний журнал live порожній (останній файл %ProgramData%\ECR\logs 16:32:47 — від seed, не від API b13 PID 32856; так само було на b12 — БАГ-2 25.11 07:26).
- Ймовірна причина (код): `TestDataSourceConnectionHandler.ProbeAsync` → `SqlDataSource.DiscoverAsync` → `Query(source.Code, CatalogQueryKey)`; джерело FLERT створене для імпорту складу газу (без каталогу сутностей) → немає запиту каталогу → виняток → «refused». Тобто з'єднання може бути цілим, а тест — хибно червоним. Перевірити першим кроком (AC-1), не вгадувати.
- ✎ AC-1 ВСТАНОВЛЕНО (25.11 17:05, до фіксу): копія COPY_ONLY EcrLandUi → EcrLandB14Flert (G:\EcrData\b14flert), власний Api з worktree d75032896 на :5191, detached через WMI, журнал G:\EcrData\b14flert\logs; f13-b14flert-admin (SystemAdministrator, лише на копії) → POST /api/v1/data-sources/1/test → 200 {"ok":false,"error":"unreachable","messageKey":"integration.test.failed.unreachable"}. Журнал (WRN, uid:18):
  `Ecr.Application.Errors.BusinessRuleException: Джерело FLERT не має запиту Sql:FLERT:CatalogQuery (або спільного Sql:CatalogQuery): збирати нічим.` at SqlDataSource.Query (SqlDataSource.cs:390) ← DiscoverAsync (:137) ← TestDataSourceConnectionHandler.ProbeAsync (:1093). Гіпотезу ПІДТВЕРДЖЕНО. Той самий текст — у живому журналі %ProgramData%\ECR\logs\ecr-20261125_015.log о 16:41:12 і 16:42:00 (читання файлу, live не чіпався).
- ✎ AC-4 причина: live Api ПИШЕ журнал — дописує в ecr-20261125_015.log (створений сідом). Get-ChildItem показує розмір/час відкритого файлу застарілими: перелік 200647 Б / 16:32:47, справжня довжина (FileStream) 248114 Б; stdout api-b13-163256.log «0 Б» насправді 32876 Б. Відтворено на копії тим самим detached-стартом: перелік 0 Б, файл 17023 Б. Дефекту запису в продукті немає; виправлено вимір (маркер pid + скрипт).
- Після фіксу (7afb19c97, той самий detached-старт новим tools/live/Start-LiveDetached.ps1): «OK: API pid 49292 writes its server log … (pid=49292).»; FLERT → 200 {"ok":true,"error":null,"entities":0,"messageKey":"integration.test.connectedNoCatalog"}.

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live: Sources → FLERT → Test connection → зелене «Connected» (або точна зрозуміла причина відмови українською/англ. мовою користувача, з тим, що робити) | live скрин | 7d2a4f1a2 — live: координатор після вікна (на копії з цим SHA: ok, connectedNoCatalog) |
| AC-1 | справжня причина відмови FLERT встановлена на копії live (текст винятку з журналу) і записана в картку | журнал | d75032896 (до фіксу; §1) |
| AC-2 | джерело без запиту каталогу: тест перевіряє з'єднання (відкрити + SELECT 1) і пише «Connected; entity catalog not configured», а не «refused» | тест API | 7d2a4f1a2 (B14F_01/02, B14F_04, B14F-UI-01) |
| AC-3 | категорії відмови розрізнені в UI: не налаштовано / автентифікація / недоступний / тайм-аут / помилка запиту — кожна своїм текстом (без сирого тексту винятку, S3) | тест + vitest | 7d2a4f1a2 (B14F_03, B14F_05, B14F-UI-02) |
| AC-4 | live API пише серверний журнал (файл у %ProgramData%\ECR\logs або теці вікна) при detached-старті; запуск-скрипт вікна перевіряє, що за 30 с після старту з'явився рядок журналу | вимір на копії + скрипт | 7afb19c97 (B14F-06, B14F_07/08; detached-прогін на копії OK) |

## 4. Звіт виконавця
- Гілка lane/integ-source-test-flert: 7d2a4f1a2 [FIX] AC-1..AC-3, 7afb19c97 [CODE] AC-4 (база d75032896).
- Перевірено локально: dotnet build Ecr.sln 0 помилок; Ecr.Architecture.Tests 1484/1484; Application DataSourceHandlersTests 17/17; Adapters Sql* + SourceConnectFailureText 28/28; Api OpenApiSnapshot+FileLog+DataSourcesController 21/21; vitest TestDataSourceModal 15/15. Мутації (зібрані): handler 3 червоні, adapter 2 червоні, FileLog 1 червоний, UI-01 червоний до фіксу.
- Контракт: лише опис /data-sources/{id}/test → openapi.snapshot.json + schema.d.ts (той самий коміт); нових кодів помилок немає (типи-нащадки BusinessRuleException з тими самими кодами).
- Сід: features/SRCTEST-flert-test-connection.sql (en/ru/kz нові ключі; kz — робочий переклад) + #UiChanged sources.testFailed «The connection test did not pass».
- CI на 7afb19c97 (повний, lane/integ-*): CI 45649226940 success (34m34s); Evidence 45649226959 success. e2e-stand 45649457355 (workflow_dispatch, grep=sourceFormHints — екран Sources; окремого e2e на модаль «Test connection» немає) success.
- Лишено: база EcrLandB14Flert + бекап G:\EcrData\b14flert (координатору); акаунт f13-b14flert-admin лише на копії; власні Api (43764, 49292) зупинено.
- Для вікна: старт live через tools/live/Start-LiveDetached.ps1 -StartScript … -ExitFile … -ApiPort 5094 (або -ProcessId <pid> для вже запущеного).

Координатор 25.11: прийнято (CI 45649226940, e2e 45649457355 success на 7afb19c97). Перевірено мною: справжня довжина live-журналу 310 363 Б при 200 647 у переліку теки — журнал пишеться; «не пише» було хибним висновком координатора. Відкриті 2–3 (PI SQL і тайм-аут як «unreachable») → картка integ-source-test-categories.

Координатор 25.11 22:5x live b14: POST /api/v1/data-sources/1/test (тимчасовий f13-b14-sa, потім вимкнено) → 200 {"ok":true,"entities":0,"messageKey":"integration.test.connectedNoCatalog"}; Start-LiveDetached -ProcessId 22168 → журнал пишеться. UI: Sources → FLERT → Test connection відкриває модаль із обов'язковою причиною (від імені людини не надсилав — пише в журнал безпеки). AC-0 PASS (API) → live-verified.
