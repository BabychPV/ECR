# UX-огляд екрана документа: повідомлення, перерахунок, обов'язкові комірки

База: `4980a1837` (код на live). Лише план — коду не змінено. Шляхи: `W/` = `src/Ecr.Web/src/`, `DG` = `W/features/grid/DocumentGrid.tsx`, `SA` = `W/features/workflow/SheetActions.tsx`, `DP` = `W/pages/DocumentPage.tsx`.
Перевірено читанням коду; на live і в браузері нічого не запускалось. Висоти блоків — оцінка за розміткою, не вимір.

## 0. Підсумок (для рішення)

**Чому банер перерахунку «висить».** На екрані три різні «банери перерахунку», і кожен має підтверджений у коді шлях, де він не зникає. Який саме ви бачили — зі скрина не знаю (питання Q1), тому план закриває всі.

1. **«Calculation results are out of date … Recalculate»** (жовтий, заголовок + текст + кнопка). Найімовірніший.
   - Автоперерахунку методологій немає: банер повертається після кожної правки і під час введення даних стоїть практично завжди.
   - Річний документ: «застаріло» = застаріло хоч в одному з 12 місяців, включно із **закритими і погодженими**, а перерахунок ці місяці **пропускає**. Тост каже «figures are up to date», банер лишається назавжди. Який місяць застарів — не сказано.
   - Аркуш у стані Submitted (у річному — якщо поданий/погоджений **січень**): кнопка вимкнена з текстом «Return it for edits first», хоча сервер подані перераховує (правило 07.11/16.11). Дії немає.
   - Перерахунок упав: лише тост на кілька секунд «Open Jobs to see why» (у більшості ролей доступу до Jobs немає). Банер без змін.
   - Перезавантажили сторінку під час перерахунку: екран забуває, що задача йде, і знову показує «Recalculate».
2. **Смуга перерахунку проєкту** («Finished: 92 recalculated…»): стоїть на кожному документі проєкту 24 години, поки не натиснути Dismiss; у згорнутому вигляді Dismiss сховано.
3. **Смуга «Calculation: rows not included…»**: розгорнутий стан Details запам'ятовується і відкривається розгорнутим на кожному документі (матриця + технічні панелі = пів екрана); після виправлення комірки не оновлюється до перемикання вікна.

**Чому «!» в комірці не працює.** Це 10-піксельний символ у лівому нижньому куті без фону. Головне: він з'являється **лише після збереження** і лише для входів методологій; зникає після перезавантаження. Порожня обов'язкова комірка шаблону не позначена ніяк, доки не натиснути Validate. Переліку «що заповнити» з переходом до комірки немає.

**Що пропоную.**
- Один **чип стану розрахунку** в рядку стану замість трьох банерів: Up to date / Out of date · Recalculate / Queued (позиція, час) / Recalculating (місяць k з N) / Failed · Retry. Стан береться із сервера, тому переживає перезавантаження і показує чужі й нічні задачі.
- Над сіткою — **щонайбільше один рядок** повідомлень (найважливіше + «ще N»), решта — у панелі праворуч (той самий інспектор, нова вкладка Status).
- Застарілість у закритих/погоджених місяцях — не попередження, а довідка в чипі («Jan, Feb: closed — not recalculated»).
- Обов'язкова порожня комірка: слово **«Required»** у самій комірці + рамка, одразу при відкритті аркуша; лічильник «3 required cells empty ‹ ›» з переходом (F8); червона зірочка в заголовку колонки; Submit веде до першої порожньої.

**Рішення за вами** (деталі — розділ 7): Q1 який текст банера ви бачили; Q2 автоперерахунок методологій після правки (рекомендую: так, тихо, через чип); Q3 чи рахувати закриті/погоджені місяці «застарілими» (рекомендую: ні, лише довідка); Q4 коли показувати «Required» (рекомендую: одразу, у рядках, де вже є хоч одне значення); Q5 смуга перерахунку проєкту на екрані документа (рекомендую: прибрати, лишити на Documents).

Обсяг: 9 lane (розділ 6), 3 з них потребують зміни сервера. Перші дві (L1, L2) — лише клієнт, знімають найгостріше за один захід.

---

## 1. Інвентар повідомлень

Порядок — зверху вниз, як на екрані. Позначки: **S** застрягає, **D** дублює, **A** немає дії, **W** показує неправду.

### 1.1 Рівень сторінки (над шапкою документа)

| # | Повідомлення | Умова (код) | Вигляд | Коли зникає | Дія | Вади |
|---|---|---|---|---|---|---|
| 1 | Помилка фонового перезапиту | `DP:1068` | ErrorAlert | наступний успішний запит | Retry | — |
| 2 | Чип «Reopened: Jan–Sep · until…» | `DP:1097`, річний | чип у шапці | строк минув | — | ок (b14) |
| 3 | Стан аркуша + заповненість + збереження | `DP:1170-1186` | 1 рядок | — | Show (незбережені) | «Read-only» **D** з №4 |
| 4 | **DocumentSheetBanner** Submitted/Approved/Rejected | `W/features/documents/DocumentSheetBanner.tsx:71-124`, з `DocumentActionBar.tsx:341` | Alert: заголовок + 1–2 рядки; у річному до 2 штук | зміна стану | немає | постійний стан як банер; **A** («Waiting for the approver's decision») |
| 5 | **StaleResultsBanner** «Calculation results are out of date» | `summary.resultsStale === true`, `DocumentActionBar.tsx:357` | Alert: заголовок + рядок + кнопка | `resultsStale=false` після успішної задачі, за якою стежить ця вкладка (`SA:1196-1237`) | Recalculate | **S** (1.3), **D** з №20 |
| 6 | **CalculationIssuesStrip** | `DP:1197`, `calculation-issues` має рядки | 1 рядок 32 px; Details → матриця + панелі місяців | перезапит | Details, Show rows | **S**: стан Details у localStorage; після правки комірки запит не інвалідується (`W/features/grid/cellPatchRequest.ts:226-227` — лише картка й результати, `exact`) |
| 7 | **ProjectRecalcBanner** | власна задача перерахунку проєкту активна або завершена < 24 год (`W/features/recalc/ProjectRecalcBannerSlot.tsx:29`, `recalcProgress.ts:147-154`) | Paper: рядок + прогрес + список провалених | Dismiss (localStorage) або 24 год | Dismiss, Open documents, Open in Jobs (лише з `System.ViewHealth`) | **S**: Dismiss сховано у згорнутому стані й під час виконання (`ProjectRecalcBanner.tsx:162-171`); не стосується цього документа; `failed` без «що робити» |
| 8 | DocumentLockBanner | `DP:1221`, лише без аркуша | Alert | — | Reopen (лише closed) | **A** для archived / not open |
| 9 | Банер «Reopened for this document until…» | `DP:1229`, місячний | Alert: заголовок + текст | строк | — | **A**; у річному вже чип — зробити так само |
| 10 | ExecutorStrip «Month assigned to you…» | `W/features/editAssignments/ExecutorStrip.tsx:42` | рядок на призначення | перезапит по фокусу | Mark as filled | «filled, awaiting…» без опитування |
| 11 | PermitRefreshStrip | `DP:1239` | рядок на таблицю + банер результату | Dismiss | Refresh from permit | постійний стан як рядок |
| 12 | Narrow-screen | `DP:1247` | Alert | — | — | ок |
| 13 | RestoreEditsBanner | `DP:1276` | Alert + кнопки | Discard / Restore | так | ок |
| 14 | HeaderSplitBanner | `DP:1286` | Alert + список | після Split | Split all… | ок |
| 15 | Панель шапки (+ конфлікт, «Recalculating…/failed») | `DP:1296` | згорнута | — | — | «Recalculation failed» **A** |
| 16 | «Зауваження прочитати не вдалося» | `DP:1333` | ErrorAlert | Retry | Retry | ок |

### 1.2 Рівень сітки (всередині робочого місця аркуша; у режимі `view=all` повторюється на кожну таблицю)

| # | Повідомлення | Умова (код) | Вигляд | Коли зникає | Дія | Вади |
|---|---|---|---|---|---|---|
| 17 | Live: «Status changed by X: Submitted» | `W/features/grid/liveRefresh/SheetLiveRefresh.tsx:115-127` | 1 рядок | хрестик | Close | **D** з №4 |
| 18 | Live: «Updated by X at 14:02» / **«This sheet was updated by another user»** | `SheetLiveRefresh.tsx:128-141`, опитування 30–120 с | 1 рядок | лише Refresh / Shift+F5 / зміна аркуша | Refresh | **W** після власних дій (1.4); **S**: не знімається, коли ті самі зрізи вже перечитано іншим шляхом |
| 19 | Тулбар: «Retry save (n)», «Not saved», «Recalculating…/Recalculated 14:02», бейдж «Recalculation failed» | `DG:3060-3140` | в рядку | наступна задача | Retry save | «Recalculation failed» **A**; збій збереження показано 4 сигналами (**D**: бейдж, Retry, №21, №3) |
| 20 | Кнопка «N required column(s) not filled yet · saving is not blocked» | `W/features/grid/requiredInputsAlert.tsx:180-196`, `DG:3079` | кнопка + поповер зі списком | комірки заповнено | лише список | **A**: без переходу до комірки; лічить колонки, не комірки |
| 21 | «The server rejected this change» | `DG:3202` | Alert + список | наступне повністю успішне збереження (`DG:824-827`) | — | **S** після Undo відхиленої правки (не перевірено) |
| 22 | «Rounded N value(s)» | `DG:3249` | Alert | **ніколи** (лише наступна вставка, `DG:1879`) | Show the list | **S** |
| 23 | ConflictPanel | `DG:3263` | Alert + список | вибір | Keep mine / Discard mine | **D** з №18 на наступному опитуванні |
| 24 | «Cannot save: N required column(s) missing» | `DG:3281` | Alert + список | рядок збережено | — | **A**: без переходу |
| 25 | «Saved with remarks: N» | `DG:3283`, `patchNotices.tsx:59` | Alert + список | хрестик | Close | ок |
| 26 | Під сіткою (місячний): «These results are out of date» | `W/features/methodologies/CalculationResultsPanel.tsx:211-219` | Alert | як №5 | — | **D** з №5, **A** |

**Найгірший випадок:** до ~21 блока над першим рядком сітки (більшість — Alert на 2–3 рядки). Типовий поданий річний документ із застарілими результатами після перерахунку проєкту: №4 ×2 + №5 + №6 + №7 = п'ять блоків, ~300–350 px до шапки сітки.

### 1.3 Банер перерахунку: коли саме застрягає

`resultsStale` не зберігається, а виводиться: після `StartedAt` актуального прогону є запис `aud.CellChange` документа за період (крім `Recalculation`, `CalcError`, `CalcErrorResolved`) або довідник змінено після входів прогону (`src/Ecr.Infrastructure/Persistence/StaleResultsQuery.cs:76-121`).

| Код | Умова | Доказ | Що бачить людина |
|---|---|---|---|
| ST-1 | Немає автоперерахунку методологій: кожна правка вмикає банер | `cellPatchRequest.ts:212-227`; картка `integ-auto-recalc-methodology` (planned) | банер стоїть увесь час введення; F9 = 13–17 с |
| ST-2 | Річний документ: застарілість — АБО по всіх місяцях року з актуальним прогоном, без огляду на стан місяця (`src/Ecr.Infrastructure/Persistence/DocumentStore.cs:249-253`, `StaleResultsYearCardAsync`). Перерахунок вилучає Closed (не перевідкриті), Scheduled і погоджені (`src/Ecr.Infrastructure/Jobs/RecalculationJob.cs:3190-3290`) | читання коду | «Recalculate» → тост «figures are up to date» → банер на місці. Назавжди. Приклад: doc 96 — перевідкриття січ–вер минуло 25.11 21:00; правки, зроблені під час перевідкриття після прогону місяця, або зміна довідника після закриття |
| ST-3 | Клієнт вимикає «Recalculate», коли стан аркуша Submitted/Approved (`SA:1447-1452`); у річному це стан **якоря** (`DP:605-608`). Сервер подані перераховує, пропускає лише погоджені (`src/Ecr.Application/Documents/RecalculateDocumentHandler.cs:259-311`) | суперечність клієнт ↔ сервер | банер + сіра кнопка + «Return it for edits first»; у річному з поданим січнем заблоковано весь рік, F9 і «Full» теж (`SA:1698`) |
| ST-4 | Замок даних (закритий період / архів / вузький екран): `actions.recalculate === null` (`SA:1441`) | `StaleResultsBanner.tsx:47` | банер без кнопки й без пояснення |
| ST-5 | Задача впала або `partial`: лише тост `workflow.recalcFailed` (`SA:1239-1258`) | — | банер як був; причини й «Retry» немає; «Open Jobs» недоступний без `System.ViewHealth` |
| ST-6 | Стеження за задачею — `useState` компонента (`SA:1027`): перезавантаження, інша вкладка, задача іншого користувача / нічна / каскад | — | «Recalculate», хоча задача вже в черзі; після її завершення картку ніхто не інвалідує (оновиться по фокусу вікна через 30 с) |
| ST-7 | Правка під час прогону (`ChangedAt > StartedAt`) | предикат | «figures are up to date» і одразу знову «out of date» |
| ST-8 | «Recalculate» рахує «лише змінені місяці + місяць на екрані» окремим детектором (`RecalculationJob.Selective.cs`, `ChangedMonthsAsync`), а банер — предикатом застарілості. Два правила | розбіжності не знайшов (детектор консервативний), але тесту рівності немає | ризик: місяць застарілий, а детектор його пропустив → як ST-2 |

Черга/зависла задача: покинуті задачі закриває `AbandonedWorkSweeper` (биття серця > 5 хв, `src/Ecr.Application/Ports/IJobProgressStore.cs:30`), тобто «вічно Running» на сервері не знайшов. Довга черга (200 Land × рік) — легітимна; позицію й оцінку старту сервер віддає (`W/features/jobs/queueEta.ts`), але показано це лише дрібним сірим текстом у меню «More» (`DocumentActionBar.tsx:212-214`).

### 1.4 «This sheet was updated by another user» після власних дій

Сервер вважає рядок «чужим», якщо його `RowVersion` новіша за маркер і останній запис журналу за вікно (маркер − 15 хв) не мій, **або запису немає взагалі** (`src/Ecr.Application/Documents/SheetChanges/GetSheetChangesHandler.cs:59,117-129`; `src/Ecr.Infrastructure/Persistence/SheetChangeStore.cs:33-48,87-106`). Клієнт не пересуває маркер після власних мутацій (`rebase` викликається лише в `SheetLiveRefresh.tsx:59`).

Власні дії, що дають банер:
1. Add row — рядок без `aud.CellChange` (`src/Ecr.Application/Documents/CreateRowHandler.cs:578-586`).
2. Delete row — `RowStore.SoftDeleteRowAsync` (`src/Ecr.Infrastructure/Persistence/RowStore.cs:461-476`).
3. Refresh from permit — журнал від системного користувача (`PermitBindingService.cs:457-480`) → «Updated by <system>».
4. PATCH без зміни значення (імпорт/вставка) — рядок «торкнуто», журналу немає (`PatchCellsHandler.cs:1609,1622,4287`) — не перевірено для імпорту.
5. Прапор сироти (`OrphanScanner.cs:762-784`) після зміни довідника або вночі.
6. Хибне авторство: будь-який «дотик» без журналу до рядка, який інший користувач правив за останні 15 хв, показує «Updated by <той користувач> at <старий час>».

Сам перерахунок значень банера не дає (`RowVersion` не змінює). Тест покриває лише власний PATCH (`tests/Ecr.Api.Tests/SheetChangesApiTests.cs`).

---

## 2. Діагноз: позначка обов'язкової комірки

**Як намальовано.** Псевдоелемент `::before { content: '!'; left: 2px; bottom: 0; font-size: 10px; font-weight: 700 }` (`W/shared/theme/cell-states.css:298-307`) у рядку ~28 px, без фону й рамки. Кольори — літерали, не токени (`:309-323`): світла `#b3170c` / `#8a6100`, темна `#f07369` / `#ffd24d`.

**Чому не видно і не зрозуміло.**
1. Розмір і місце: найменший маркер сітки, єдиний у лівому нижньому куті; у порожній комірці немає нічого, що привертає око.
2. З'являється лише **після збереження** і лише для входів методологій (`ECR-CALC-0437`, `DG:801-816, 848-852`). Стан — `useState` сітки (`DG:993-998`): після перезавантаження чи перемикання аркуша позначок немає.
3. Обов'язкова колонка шаблону (`ColumnDef.IsRequired`) з порожньою коміркою не позначається взагалі; дізнатися можна лише через Validate → Issues.
4. Пояснення — лише нативний `title` (з'являється через ~1 с наведення; на клавіатурі недоступний).
5. Заголовок: чорна ` *` у тексті назви (`DG:3617-3636`), підказка «This column is required.» — навіть коли колонка обов'язкова лише для частини рядків (`isRequiredByMethodology`).
6. Перелік «що заповнити» (№20, №24) — текст без посилань; кнопки «наступна порожня» немає, хоча механізм переходу є (`W/features/grid/cellNavigation.ts:22-65`, прецедент ‹ › для ручних комірок `DG:2890-2912`).
7. Легенди маркерів немає (`GridKeyHint.tsx` — лише клавіші).

**Доступність.** Немає `aria-required` / `aria-invalid` / опису. Темна тема: `#f07369` на заливці «dirty» `#3a3320` = 4.40:1 (< 4.5 AA). Кольори маркера не покриті жодним сторожем контрасту (`contrast.test.ts`, `gridCellContrast.test.ts`, `cellStateMeasured.test.ts`).

---

## 3. Пропонований дизайн

### 3.1 Принципи

1. **Одне місце для стану документа** — рядок стану (уже є: стан аркуша · заповненість · збереження). Додається чип розрахунку. Постійні стани (подано, погоджено, перевідкрито, з дозволу, застаріло) — чипи, не банери.
2. **Ієрархія:** блокує роботу (червоне) > потребує дії (жовте) > довідка (сіре/синє). Колір лише разом зі словом.
3. **Кожне повідомлення має одну головну дію і ясний кінець.** Немає дії — це не повідомлення, а чип або рядок у панелі.
4. **Над сіткою — один рядок (32 px, фіксована висота).** Показує найважливіше + «+N more». Сітка не стрибає.
5. **Подробиці — у правій панелі** (наявний `DocumentInspector`, нова вкладка **Status** поруч з Issues / History / Info; макет `docs/design/hybrid/KIT.md` — закритий `.aside`).
6. **Прогрес — справжній:** черга (позиція, оцінка старту), крок (місяць k з N), явний стан збою з причиною людською мовою і кнопкою Retry. Стан — із сервера, не з пам'яті вкладки.
7. **Не казати неправду:** «оновив інший користувач» — лише коли справді інший; «актуально» — лише коли предикат сервера це підтверджує.
8. **Розгорнутість не запам'ятовується між документами** (лише в межах відкритого документа).

### 3.2 Чип розрахунку (замінює №5, №7 на документі, №26, «Recalculation failed» у №15/№19)

Один компонент, один хук `useDocumentCalcStatus(documentId, periodKey)`.

| Стан | Вигляд | Клік / дія | Кінець |
|---|---|---|---|
| Актуально | `✓ Calculated 14:02` (сіре) | підказка: коли, ким | — |
| Застаріло, можна рахувати | `● Results out of date · Recalculate F9` (жовте) | перерахунок; ▾ — які місяці, відколи, причина (правка / довідник «X»), Full | → Queued |
| У черзі | `◷ Queued · position 3 · ≈ 4 min` | ▾ — хто поставив, Cancel (свою) | → Running |
| Рахується | `⟳ Recalculating · Mar (3 of 9)` + тонка смужка | ▾ — деталі | → Актуально / Збій |
| Збій | `✕ Recalculation failed · Retry` (червоне, лишається до Retry або успіху) | Retry; Details — причина з каталогу помилок, код і кореляція для підтримки | → Queued |
| Застаріло, рахувати не можна | `ⓘ Jan, Feb: closed — not recalculated` (сіре) | ▾ — «Reopen month…» (з правом) або «Approved figures are never recalculated» | довідка |
| Невідомо | `Freshness unknown` (як зараз) | підказка | — |

Після завершення, якщо під час прогону були правки (ST-7): одразу `● Out of date (changed during recalculation) · Recalculate`, тост «figures are up to date» не показується.

### 3.3 Рядок повідомлень і панель Status

Рядок (один, 32 px) показує перше за пріоритетом:
1. Відновлення втрачених правок (№13) — з кнопками.
2. Збій збереження («2 changes not saved · Retry · Show»).
3. Конфлікт («Cells changed by Olena K. · Review»).
4. Блок подання («Cannot submit: 3 required cells empty · Go to next F8»).
5. Потрібен поділ рядків (№14), місяць передано вам (№10).
6. Проблеми розрахунку (№6 — як зараз, але без пам'яті Details).
7. Чужа зміна («Updated by Olena K. at 14:02 · Refresh»).

Праворуч у рядку — `+2 more`: відкриває Status із повним списком; кожен пункт має свою дію. «Rounded N values», «Saved with remarks» — тости з кнопкою «Show the list» + пункт у Status (зникає при закритті або наступному збереженні).

Чипи рядка стану: стан аркуша (з підказкою «Submitted 24 Nov by Ivan P. · waiting for approver» замість №4), `Reopened until …` (замість №9), `From permit ▾ Refresh` (замість №11), `Recalculated after submission` (жовтий чип, лише для погоджувача — дія «Review»).

### 3.4 Макети верхньої частини екрана

```
НОРМА (чернетка, все актуально)
← Documents   Land · LND-014                                      [Year 2026]
[Draft · 2026] [Filled 86%] Saved 14:03 [✓ Calculated 14:02]   Validate  More▾  [Submit: Oct]
───────────────────────────────────────────────────────────────────────────────
 Table: Fuel   ⟲ ⟳  3 required cells empty ‹ ›                 Issues 0 · History
┌────┬───────────┬──────────┬──────────┐
```

```
ЗАСТАРІЛО (потрібен перерахунок)
[Draft · 2026] [Filled 86%] Saved 14:05 [● Results out of date · Recalculate F9 ▾]   Validate  More▾  [Submit: Oct]
───────────────────────────────────────────────────────────────────────────────  (рядка повідомлень немає)
```

```
У ЧЕРЗІ / РАХУЄТЬСЯ
[Draft · 2026] … [◷ Queued · position 3 · ≈ 4 min ▾]
[Draft · 2026] … [⟳ Recalculating · Mar (3 of 9) ▾]
                  ▔▔▔▔▔▔▔▔▁▁▁▁▁▁▁▁▁▁▁
```

```
ЗБІЙ ПЕРЕРАХУНКУ
[Draft · 2026] … [✕ Recalculation failed · Retry ▾]
 ▾: Step failed in March: formula in “Fuel / CO2” divides by zero.
    [Show cell]  [Retry]     ECR-CALC-0512 · 7f3a… (copy)
```

```
ЗАСТАРІЛО В ЗАКРИТИХ МІСЯЦЯХ (рахувати нічого)
[Partly submitted · 2026] … [✓ Calculated 14:02 · ⓘ Jan, Feb closed ▾]
 ▾: Inputs of Jan, Feb changed after their last calculation (20 Nov).
    These months are closed and are not recalculated.  [Reopen month…]
```

```
ДЖЕРЕЛО ЗМІНИЛОСЬ (F5 / F5-R) + проблеми розрахунку
[Submitted · Sep 2026] … [● Results out of date · Recalculate F9]
───────────────────────────────────────────────────────────────────────────────
⚠ Source changed after approval: HSE400 Sep · 12 rows   [Open source] [Details]   +1 more
```

```
ПОДАНО / ПОГОДЖЕНО
[Submitted · Sep 2026 ⓘ] [Filled 100%] Read-only [✓ Calculated 24 Nov]      More▾  [Reject] [Approve: Sep]
 ⓘ: Submitted 24 Nov 16:10 by Ivan P. · waiting for the approver's decision
[Approved · Sep 2026 ⓘ] … Read-only                                          More▾  [Return for edits]
```

```
ІНШИЙ КОРИСТУВАЧ ЗМІНИВ АРКУШ
───────────────────────────────────────────────────────────────────────────────
ⓘ Updated by Olena K. at 14:02 · 3 rows        [Refresh  Shift+F5]   ✕
```
(після власних дій рядка немає; якщо автора не встановлено — «This sheet changed on the server · Refresh», без слів «another user».)

---

## 4. Обов'язкові комірки: рішення

### 4.1 Одна модель «потрібне значення»

Хук `useNeedsInput(table)` → `Map<rowKey, Map<columnCode, { level: 'block' | 'warn', reason }>>` із трьох джерел:

| Джерело | Рівень | Звідки | Коли відоме |
|---|---|---|---|
| Колонка шаблону `isRequired`, комірка порожня | block (не дасть подати, `ECR-CELL-0422`) | клієнт, із зрізу | одразу при відкритті |
| Вхід методології (`ECR-CALC-0437`) | block або warn — як налаштовано | **сервер** (новий перелік на таблицю, L6) | одразу при відкритті |
| «Missing input» з `calculation-issues` | warn | наявний запит | після перерахунку |

Незбережені правки враховуються: набрали значення — позначка зникає одразу (усуває розбіжність `DG:1133-1141` ↔ `DG:1687`).

### 4.2 Комірка

- Клас-накладка `ecr-cell-needs-input` + `--block` / `--warn` (поверх будь-якого стану, як зараз).
- **Текст-заповнювач у порожній комірці: «Required»** (локалізований: значення в CSS-змінній `--ecr-required-label` на корені сітки, `content: var(--ecr-required-label)`), розмір шрифту сітки, вирівнювання як у колонки.
- **Внутрішня рамка 1 px** кольору рівня (`box-shadow: inset 0 0 0 1px`); у блокувального рівня — 2 px після невдалої спроби Submit/Validate.
- Старий «!» 10 px прибрати.
- Підказка (Mantine `Hint`, не `title`; доступна з клавіатури при фокусі комірки — у рядку формул): «Required — sheet cannot be submitted without it» / «Required for methodology “X” — the row is not calculated without it».
- `aria-required="true"`, `aria-invalid="true"` на комірці; текст причини — у живий регіон при фокусі.
- Лише редаговні комірки. Рядок підсумків — ні.

Токени (додати в `cellState` у `W/shared/theme/theme.ts` і дзеркало в `cell-states.css`; літерали `:309-323` прибрати):

| Токен | Світла | Темна | Найгірший контраст тексту |
|---|---|---|---|
| `--ecr-cell-required-block` | `#b3170c` | `#ff9a8f` | 6.03:1 (на `#fdecea`) / 6.13:1 (на `#3a3320`) |
| `--ecr-cell-required-warn` | `#8a6100` | `#ffd24d` | 4.84:1 (на `#fdecea`) / 8.71:1 (на `#3a3320`) |

Пораховано проти всіх п'яти заливок станів обох тем; усі ≥ 4.5:1 (текст) і ≥ 3:1 (рамка). Заливку не міняємо: жовта вже означає «незбережено», червона — «сирота».

### 4.3 Заголовок колонки

- ` *` → окремий `span` кольору `--ecr-cell-required-block`, `aria-hidden`; на заголовку `aria-required`.
- Підказка: `isRequired` → «Required»; лише `isRequiredByMethodology` → «Required for some rows (methodology “X”)».
- Коли в колонці є порожні обов'язкові: лічильник поруч із зірочкою `* 3`; клік — до першої.

### 4.4 Лічильник і навігація

- Тулбар таблиці: кнопка №20 → **`3 required cells empty  ‹ ›`** (лічить комірки; червона, якщо є block, інакше жовта). ‹ › — попередня / наступна (той самий механізм, що `goToManual`, `DG:2890-2912`).
- Клавіші: **F8** наступна, **Shift+F8** попередня (вільні; додати в `shortcutKey.ts` і `GridKeyHint.tsx`). Після останньої в таблиці — перша наступної таблиці аркуша; після останньої на аркуші — тост «No more required cells on this sheet».
- Поповер зі списком лишається, рядки — посилання (`requestCellNavigation`).
- Смуга аркушів унизу: бейдж кількості на вкладці аркуша (`SheetStrip`).
- Легенда маркерів — у поповері `GridKeyHint` (другий стовпчик: Required, Unsaved, Calculated, Locked, Error…).

### 4.5 Збереження, перевірка, подання

- **Save:** без змін для warn. Block (`422 ECR-CALC-0437`): замість Alert №24 — рядок повідомлень «Not saved: 2 required cells empty · Go to next F8», комірки з рамкою 2 px.
- **Validate:** без змін у правилах; знахідки `ECR-CELL-0422` потрапляють у ту саму модель (рамка 2 px).
- **Submit:** якщо модель має block-комірки в цілі подання — кнопка активна, клік не шле запит: фокус на першу порожню + рядок «Cannot submit: 3 required cells empty · Go to next F8». Відмова сервера `ECR-SUB-4221` з `messages` — те саме (а не тост).
- Сервер (L6): Validate і Submit зараз не перевіряють входи методологій (див. BUG-9) — питання Q6.

---

## 5. Знайдені дефекти

| # | Дефект | Де | Обґрунтування |
|---|---|---|---|
| BUG-1 | «Recalculate» заблоковано на поданому аркуші, хоча сервер його перераховує; у річному документі поданий/погоджений якір блокує перерахунок усього року (кнопка, F9, банер, Full) | `SA:1447-1452`, `SA:1698`; сервер `RecalculateDocumentHandler.cs:259-311` | клієнтське правило старіше за рішення 07.11/16.11 |
| BUG-2 | Річний документ: банер застарілості через закриті/погоджені місяці не зникає після успішного перерахунку | `DocumentStore.cs:249-253` ↔ `RecalculationJob.cs:3190-3290` | предикат не знає про стан місяця |
| BUG-3 | Збій перерахунку — лише тост; `partial` показано як збій без переліку | `SA:1196-1258` | стану збою на екрані немає |
| BUG-4 | Стеження за перерахунком губиться при перезавантаженні; чужі/системні задачі не видно | `SA:1027` | стан у пам'яті компонента |
| BUG-5 | Dismiss смуги перерахунку проєкту недоступний у згорнутому стані | `ProjectRecalcBanner.tsx:162-171` | згорнутість пам'ятається → смуга на 24 год |
| BUG-6 | Смуга проблем розрахунку не оновлюється після виправлення комірки | `cellPatchRequest.ts:226-227`, `useCellPatch.ts:149` | `calculation-issues` не інвалідується |
| BUG-7 | «Updated by another user» після власних Add row / Delete row / Refresh from permit; хибний автор у вікні 15 хв | розділ 1.4 | рядки без журналу = «чужі» |
| BUG-8 | «Rounded N value(s)» не закривається | `DG:1879, 3249` | немає ні хрестика, ні таймера |
| BUG-9 | Входи методологій не перевіряються при Validate / Submit; попередження зникає після перезавантаження | `SubmitSheetHandler.cs:580,637`, `ValidateDocumentHandler.cs:148`; `DG:993-998` | рядок, якого не правили, проходить подання мовчки |
| BUG-10 | «!» у комірці й лічильник кнопки розходяться (сирий перелік ↔ «живий») | `DG:1133-1141` ↔ `DG:1687` | щойно заповнена комірка тримає «!» |
| BUG-11 | Колізії `::before`: на обчисленій комірці «!» витісняє «ƒ» і їде у верхній кут; «×» збою збереження витісняє «!» | `cell-states.css:163, 298, 336` | один псевдоелемент на кілька станів |
| BUG-12 | Темна тема: маркер `#f07369` на «dirty» 4.40:1; кольори маркера поза сторожами контрасту й токенами | `cell-states.css:309-323` | літерали |
| BUG-13 | Підказка «This column is required.» для колонки, обов'язкової лише для частини рядків | `DG:3527-3528` | `isRequiredByMethodology` — рівня колонки |
| BUG-14 | «Status changed by X: Submitted» дублює банер стану; «These results are out of date» під сіткою дублює верхній банер | №17/№4, №26/№5 | два повідомлення про одне |

---

## 6. Розбиття на lane

Порядок = порядок зведення. Усі UI-lane: рядок «Макет: цей документ §3.4 / §4 + знімок до/після Light/Dark», a11y-прохід (axe light/dark), сторожі `buttonScale`, бюджет маршруту D-132 (нові компоненти — лінивими чанками). Кожен коміт ≤ 800 рядків. Нові тексти — нова секція сіду `COLL-doc-ux-*.sql` (en/ru/kz + uk, де є), наявні ключі не міняти.

| Lane | Що | Файли (основні) | Сервер | Ризик |
|---|---|---|---|---|
| **L1** recalc-unblock | BUG-1: блок лише коли погоджено все, що просили (як сервер); річний — не дивитись на якір. BUG-5: Dismiss завжди видимий. BUG-8: хрестик. BUG-6: інвалідувати `calculation-issues` після збереження рядка, що був у переліку, і після перерахунку сітки. Details смуги — без localStorage між документами | `SA`, `ProjectRecalcBanner.tsx`, `DG`, `cellPatchRequest.ts`, `useCellPatch.ts`, `CalculationIssuesStrip.tsx` | ні | R1 |
| **L2** required-cells-client | §4.2–4.4 для джерел, відомих клієнту (шаблонні `isRequired` + наявні `ECR-CALC-0437` + `calculation-issues`): модель, накладка «Required», токени, заголовок, лічильник ‹ ›, F8, посилання в поповері, легенда; BUG-10/11/12/13 | `DG`, `requiredInputsAlert.tsx`, `cell-states.css`, `theme.ts`, `shortcutKey.ts`, `GridKeyHint.tsx`, новий `needsInput.ts` | ні | R2 |
| **L3** calc-status-api | Картка документа: `staleMonths[] {periodKey, since, cause, registryName, recalculable, lockedReason}`, `activeRecalculation {jobId, state, queuePosition, estimatedStartAt, stepDone, stepTotal, startedBy}`, `lastRecalculation {finishedAt, state, errorCode, correlationId}`. `resultsStale` = лише recalculable (за Q3). Тест рівності детектора й предиката (ST-8) | `DocumentStore.cs`, `StaleResultsByIdsQuery.cs`, DTO, `contracts/` | **так** | R3 |
| **L4** calc-status-chip | §3.2: чип + `useDocumentCalcStatus` (опитує, поки є активна задача; переживає перезавантаження); прибрати `StaleResultsBanner`, Alert у `CalculationResultsPanel`, бейджі «Recalculation failed», `ProjectRecalcBannerSlot` з документа (за Q5); ST-7 | новий `features/documents/calcStatus/*`, `DocumentActionBar.tsx`, `SA`, `DP`, `CalculationResultsPanel.tsx` | після L3 | R2 |
| **L5** live-banner-truth | BUG-7 (= картка `integ-stale-banner-audit`): сервер — рядок без журналу не є «чужим» (власні Add/Delete/Permit пишуть авторство дії або клієнтський маркер пересувається після власної мутації); невстановлений автор → нейтральний текст; клієнт — `rebase` після власних мутацій, хрестик, зняття після перечитування зрізів | `GetSheetChangesHandler.cs`, `SheetChangeStore.cs`, `CreateRowHandler.cs`, `DeleteRowHandler.cs`, `PermitBindingService.cs`; `useSheetChanges.ts`, `SheetLiveRefresh.tsx`, `useAddRow.ts`, `PermitRefreshStrip.tsx` | **так** | R2 |
| **L6** required-inputs-api | Перелік входів методологій, яких бракує, на таблицю при відкритті (поле зрізу або `GET …/required-inputs`; той самий обчислювач, що в `PatchCellsHandler.cs:2037-2374`, для всіх рядків, зі стелею); за Q6 — у Validate/Submit. Клієнт: третє джерело моделі L2 | `PatchCellsHandler.cs` (винести обчислювач), `TableValidation.cs`, DTO; `needsInput.ts` | **так** | R3 (продуктивність: Land до 174 тис. рядків) |
| **L7** notice-line + Status | §3.3: рядок повідомлень із пріоритетом і «+N more», вкладка Status в інспекторі; перенести №13, №14, №10, №17, №18, №21–25 | новий `features/documents/notices/*`, `DocumentInspector`, `DP`, `DG` | ні | R2 (багато тестів змінюють селектори) |
| **L8** state-chips | №4 → чип стану з підказкою; №9 → чип; №11 → чип-меню; «Recalculated after submission» → чип погоджувача | `DocumentSheetBanner.tsx`, `DocumentProgress`, `useDocumentPeriodReopen.tsx`, `PermitRefreshStrip.tsx` | ні | R2 |
| **L9** submit-guides | §4.5: Submit / Validate / 422 ведуть до комірки через рядок повідомлень | `SA`, `DG`, `notices/*` | ні | R1 |
| (окремо) | Автоперерахунок методологій — картка `integ-auto-recalc-methodology`, після L4 (чип уже показує тихий прогрес) | — | так | R3 |

### Критерії приймання (тест, що падає без правки)

| AC | Спостерігаємо | Тест |
|---|---|---|
| L1-1 | Аркуш Submitted, результати застарілі → «Recalculate» активна, запит іде | vitest `SheetActions.recalcSubmitted` (зараз кнопка `aria-disabled`) |
| L1-2 | Річний документ, якір Approved, жовтень Draft → «Recalculate» і F9 активні | vitest `SheetActions.yearAnchorApproved` |
| L1-3 | Смуга перерахунку проєкту згорнута, задача завершена → Dismiss видно й працює | vitest `ProjectRecalcBanner.dismissCollapsed` |
| L1-4 | Виправили комірку з переліку проблем → смуга оновилась без фокусу вікна | vitest `DocumentPage.calcIssuesAfterFix` (лічить запити `calculation-issues`) |
| L1-5 | «Rounded» закривається хрестиком | vitest `DocumentGrid.roundedDismiss` |
| L2-1 | Відкрили аркуш із порожньою коміркою `isRequired` у редаговному рядку → клас `ecr-cell-needs-input--block`, `aria-required`, видимий текст «Required» **без** збереження | vitest `DocumentGrid.needsInputOnLoad` + e2e `requiredCells` (знімок Light/Dark) |
| L2-2 | Набрали значення (ще не збережено) → позначка зникла, лічильник −1 | vitest |
| L2-3 | F8 / ‹ › переводять фокус на наступну порожню обов'язкову; з останньої — тост | vitest `DocumentGrid.needsInputNavigation`, `shortcutKey.layouts` |
| L2-4 | Нові токени: ≥ 4.5:1 на всіх заливках станів, обидві теми; синхронність theme.ts ↔ CSS | `contrast.test.ts`, `gridCellContrast.test.ts`; e2e `cellStates` (відтінки сірого) |
| L2-5 | Обчислена комірка з позначкою зберігає «ƒ» | `cellStateMeasured.test.ts` |
| L3-1 | Річний документ: правка в закритому місяці після його прогону → `resultsStale=false`, `staleMonths[0].recalculable=false, lockedReason='closed'` | API `DocumentStaleResultsStoreTests.ClosedMonthNotActionable` |
| L3-2 | Після звичайного «Recalculate» річного документа з відкритими застарілими місяцями → `resultsStale=false` (рівність детектора й предиката) | API `RecalculationStaleParityTests` |
| L3-3 | Задача в черзі → картка віддає `activeRecalculation` з позицією; збій → `lastRecalculation.state='Failed'` з кодом | API |
| L4-1 | Перезавантаження під час перерахунку → чип «Recalculating…», по завершенні «Calculated HH:MM» без дії користувача | vitest `CalcStatusChip.resume` + e2e `calcStatusChip` |
| L4-2 | Збій → червоний чип лишається, Retry ставить нову задачу, Details показує текст каталогу | vitest |
| L4-3 | Правка під час прогону → після завершення «Out of date», тосту «up to date» немає | vitest |
| L4-4 | Над сіткою немає `document-stale-results`; висота від шапки до першого рядка сітки на doc із застарілими результатами ≤ висоти без них | e2e (вимір, як `docNoticesCompact`) |
| L5-1 | Власні Add row, Delete row, Refresh from permit → `changed=false` на наступному опитуванні | API `SheetChangesApiTests.OwnRowActions` |
| L5-2 | Інший користувач додав рядок → банер з його іменем і часом | API + e2e два користувачі |
| L6-1 | Рядок, якого не правили, з порожнім входом методології → є в переліку при відкритті; час запиту на Land у межах бюджету зрізу | API + вимір на копії Land |
| L7-1 | 5 активних повідомлень → над сіткою один рядок 32 px і «+4 more»; Status перелічує всі з діями | vitest + e2e + axe |
| L9-1 | Submit із порожніми обов'язковими → запиту немає, фокус у першій порожній, рядок «Cannot submit: N…» | vitest `SheetActions.submitGuides` |

### Перевірити на live після кожної пачки

- L1: doc 96 — «Recalculate» активна; смуга проєкту закривається; Details не розгорнуто на сусідньому документі.
- L3+L4: doc 96 — чип називає застарілі місяці; після перерахунку або «Calculated», або сіра довідка про закриті місяці. Запит до live (лише читання) до початку L3: які місяці doc 96 зараз застарілі та їхній стан — підтвердити ST-2.
- L2/L6: новий документ LAND_UI — порожні обов'язкові видно одразу; F8 обходить усі.
- L5: додати й видалити рядок на doc 100 — банера немає.

---

## 7. Питання до людини

| # | Питання | Рекомендація |
|---|---|---|
| Q1 | Який текст був на банері, що «не зникає»: «Calculation results are out of date», «Finished: … recalculated» чи «Calculation: rows not included…»? І який документ? | План закриває всі три; відповідь лише визначає, що перевірити на live першим |
| Q2 | Автоматично перераховувати методології після правки (через ~20 с тиші, одна задача в черзі)? | Так. Без цього чип «Out of date» буде жовтим увесь час введення |
| Q3 | Застарілість у закритих / погоджених місяцях — це попередження? | Ні: сіра довідка в чипі + перевірка узгодженості. Попередження — лише те, що можна перерахувати |
| Q4 | Коли показувати «Required» у порожній комірці? | Одразу при відкритті, у рядках, де вже є хоч одне введене значення, і в наперед заданих рядках. Порожні «запасні» рядки — без позначок |
| Q5 | Смуга перерахунку проєкту на екрані документа | Прибрати (лишити на Documents і в My tasks); стан цього документа каже чип |
| Q6 | Порожній вхід методології рівня Block має блокувати Submit? | Так (зараз блокує лише збереження рядка, і лише якщо рядок правили) |
| Q7 | Банер «Submitted … by …» → чип із підказкою | Так; для погоджувача дія і так у кнопках Approve / Reject |

---

## Додаток A. Тексти (нові ключі, EN)

| Ключ | Текст |
|---|---|
| `calcStatus.upToDate` | Calculated {time} |
| `calcStatus.stale` | Results out of date |
| `calcStatus.staleDuringRun` | Out of date: inputs changed during recalculation |
| `calcStatus.queued` | Queued · position {position} |
| `calcStatus.running` | Recalculating · {month} ({done} of {total}) |
| `calcStatus.failed` | Recalculation failed |
| `calcStatus.retry` | Retry |
| `calcStatus.lockedMonths` | {months}: closed — not recalculated |
| `calcStatus.approvedMonths` | {months}: approved — figures are never recalculated |
| `grid.needsInput.label` | Required |
| `grid.needsInput.counter` | {count} required cell(s) empty |
| `grid.needsInput.next` / `.prev` | Next required cell (F8) / Previous required cell (Shift+F8) |
| `grid.needsInput.none` | No more required cells on this sheet |
| `grid.needsInput.hintTemplate` | Required — the sheet cannot be submitted without it |
| `grid.needsInput.hintMethodology` | Required for methodology “{methodologies}” — the row is not calculated without it |
| `grid.columnRequiredSomeRows` | Required for some rows (methodology “{methodologies}”) |
| `document.notices.more` | +{count} more |
| `document.notices.cannotSubmitRequired` | Cannot submit: {count} required cell(s) empty |
| `liveRefresh.updatedUnknown` | This sheet changed on the server |

## Додаток B. Наявні тексти, про які йдеться

- `document.staleResults.title` — «Calculation results are out of date»; `.hintSince` — «Input data was changed on {date}, after the last calculation. Recalculate to refresh the results.»
- `workflow.recalcDone` — «Recalculation finished: the figures are up to date.»; `workflow.recalcFailed` — «Recalculation failed. Open Jobs to see why.»
- `workflow.recalculateSheetSubmitted` — «Sheet {sheet} is submitted or approved and cannot be recalculated. Return it for edits first…»
- `recalcProgress.finished` — «Finished: {recalculated} recalculated, {skipped} skipped (approved), {failed} failed ({project})»
- `liveRefresh.updatedNoTime` — «This sheet was updated by another user»
- `grid.requiredInput.summary` — «{count} required column(s) not filled yet · saving is not blocked»

## Додаток C. Наявні тести, яких торкнеться зміна

vitest: `DocumentPage.staleCalculations`, `DocumentPage.yearCalcIssues`, `DocumentPage.yearLayoutL7`, `CalculationIssuesStrip.test`, `DocumentGrid.requiredInputs`, `requiredInputsAlert(.a11y)`, `gridColumns.requiredIndicator`, `DocumentGrid.cellNavigation`, `shortcutKey.layouts`, `DocumentGrid.shortcutsByCode`, `cellStateMeasured`, `gridCellContrast`, `contrast.test`.
e2e: `requiredInputsBanner`, `cellStates`, `docNoticesCompact`, `calculationIssues`, `calculationMissingInputs`, `sourceChangedAfterApproval`.
API: `SheetChangesApiTests`, `DocumentStaleResultsStoreTests`, `PatchCellsTests.RequiredInputMessages`, `UserFacingMessageLanguageTests`.

## Рішення людини 26.11 (квіз координатора)
- Q2: автоперерахунок методологій після правок — **так, тихо** (статус через позначку в рядку статусу: у черзі / рахується / актуально / помилка · повторити).
- Q3: закриті/погоджені «застарілі» місяці — **сіра примітка** («Січ, лют закриті — не перераховано»), не попередження; банер про застарілість зникає після перерахунку відкритих місяців.
- Q6: порожній обов'язковий вхід методології — **блокує подання**; Submit ставить фокус у першу порожню обов'язкову комірку з поясненням.
- Q5: смугу «перерахунок проєкту» — **прибрати з екрана документа** (лишається на дошці документів).
- Q4, Q7 — за замовчуванням плану: «Required» показувати одразу при завантаженні (рядки зі значенням і предвизначені рядки); «Submitted by…» — чип.
- Q1 (який саме банер бачила людина) — не з'ясовано; план покриває всі три.
- ✎ 26.11 (через координатора, до L6) — обов'язкові входи методології **виводяться автоматично** з полів, які читає скрипт застосовної до рядка версії (`D-578`): Q1 — порожній такий вхід блокує подання одразу (і «Перевірити» його показує), збереження комірки не блокується; Q2 — порожній вхід — «бракує даних» (проблема `MissingInputs`), не 0; Q3 — послабити вимогу може лише методолог (виняток `Optional`/`Warn` у переліку вимог версії); без автоматичного переліку (скрипт не скомпілювався) — лише перелік методолога й попередження в редакторі; поля інших місяців/таблиць — етап 2.
- ✎ 26.11 (через координатора) — інспектор комірки: «Calculated by: методологія · крок» і «Read by: методології (+ обов'язковість)» — по колонці у зрізі (`columns[].calculatedBy`/`readBy`).

- ✎ 27.11 (незалежний огляд lane, `D-577` (8)) — **автоперерахунок після правки перед live:** межа важких документів `Calculations:AutoRecalcAfterEdit:MaxRowsPerPeriod` (типово 20 000 рядків документа за період) і аварійний вимикач `Calculations:AutoRecalcAfterEdit:Enabled`; ⚠ потрібен вимір на стенді Land (тривалість прогону місяця й відкладення проходів формул під локом документа) — до нього межу не знижувати й вимикач тримати під рукою. Натискання «Перерахувати» знімає автоперерахунок, що чекає; правка погоджувача поданого аркуша позначки «після подання» не дає (рішення людини 27.11). Клієнтські виклики `calc-status` і `required-inputs` — `features/documents/calcStatusApi.ts` (лана doc-ux їх підхоплює).
