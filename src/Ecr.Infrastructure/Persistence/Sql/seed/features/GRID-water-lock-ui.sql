-- GRID:water-lock-ui ── ru/kz: UAT 08.11, Water Report — причина червоного рядка словами, «!» → Issues, порожній Issues каже, що перевірено ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.rowAttention.orphaned', N'ru', N'Значение справочника в этой строке недействительно в этом периоде (или удалено) — выберите действующее.'),
    (N'grid.rowAttention.orphaned', N'kz', N'Бұл жолдағы анықтамалық мәні осы кезеңде жарамсыз (немесе жойылған) — жарамдысын таңдаңыз.'),
    (N'grid.rowAttention.locked', N'ru', N'Заблокировано: {months} — чтобы изменить, откройте месяц повторно.'),
    (N'grid.rowAttention.locked', N'kz', N'Бұғатталған: {months} — өзгерту үшін айды қайта ашыңыз.'),
    (N'grid.rowAttention.open', N'ru', N'Показать строку «{row}» в замечаниях'),
    (N'grid.rowAttention.open', N'kz', N'«{row}» жолын ескертулерде көрсету'),
    (N'grid.rowAttention.count.one', N'ru', N'{count} строка требует внимания'),
    (N'grid.rowAttention.count.one', N'kz', N'{count} жол назар аударуды қажет етеді'),
    (N'grid.rowAttention.count.few', N'ru', N'{count} строки требуют внимания'),
    (N'grid.rowAttention.count.few', N'kz', N'{count} жол назар аударуды қажет етеді'),
    (N'grid.rowAttention.count.many', N'ru', N'{count} строк требуют внимания'),
    (N'grid.rowAttention.count.many', N'kz', N'{count} жол назар аударуды қажет етеді'),
    (N'grid.rowAttention.count.other', N'ru', N'{count} строки требуют внимания'),
    (N'grid.rowAttention.count.other', N'kz', N'{count} жол назар аударуды қажет етеді'),
    (N'inspector.attention.title', N'ru', N'Строки, требующие внимания'),
    (N'inspector.attention.title', N'kz', N'Назар аударуды қажет ететін жолдар'),
    (N'inspector.attention.lockedMonths', N'ru', N'{table}: заблокировано {months} — значения только для чтения. Чтобы изменить, откройте месяц повторно.'),
    (N'inspector.attention.lockedMonths', N'kz', N'{table}: {months} бұғатталған — мәндер тек оқуға арналған. Өзгерту үшін айды қайта ашыңыз.'),
    (N'inspector.noIssuesChecked', N'ru', N'Проверено: правила всех доступных вам таблиц за {period}.'),
    (N'inspector.noIssuesChecked', N'kz', N'Тексерілді: сізге көрінетін барлық кестелердің {period} кезеңіне арналған ережелері.'),
    (N'inspector.checkedAt', N'ru', N'Время проверки: {time}.'),
    (N'inspector.checkedAt', N'kz', N'Тексеру уақыты: {time}.'),
    (N'grid.rowAttention.permitNotCovering', N'ru', N'Разрешение {permit} действует {validity} и не покрывает период {period}. Выберите разрешение, действующее в этом периоде.'),
    (N'grid.rowAttention.permitNotCovering', N'kz', N'{permit} рұқсаты {validity} аралығында жарамды және {period} кезеңін қамтымайды. Осы кезеңде жарамды рұқсатты таңдаңыз.'),
    (N'grid.rowAttention.valueNotCovering', N'ru', N'«{value}» ({column}) действует {validity} и не покрывает период {period}. Выберите значение, действующее в этом периоде.'),
    (N'grid.rowAttention.valueNotCovering', N'kz', N'«{value}» ({column}) {validity} аралығында жарамды және {period} кезеңін қамтымайды. Осы кезеңде жарамды мәнді таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- GRID:water-lock-ui ── кінець секції ──
-- GRID:water-lock-ui ── UAT 08.11, Water Report (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.rowAttention.orphaned', N'en', N'The registry value in this row is not valid for this period (or was removed) — choose a valid one.', 1),
    (N'grid.rowAttention.locked', N'en', N'Locked: {months} — reopen the month to edit.', 1),
    (N'grid.rowAttention.open', N'en', N'Show row “{row}” in Issues', 1),
    (N'grid.rowAttention.count.one', N'en', N'{count} row needs attention', 1),
    (N'grid.rowAttention.count.few', N'en', N'{count} rows need attention', 1),
    (N'grid.rowAttention.count.many', N'en', N'{count} rows need attention', 1),
    (N'grid.rowAttention.count.other', N'en', N'{count} rows need attention', 1),
    (N'inspector.attention.title', N'en', N'Rows that need attention', 1),
    (N'inspector.attention.lockedMonths', N'en', N'{table}: {months} locked — values there are read-only. Reopen the month to edit.', 1),
    (N'inspector.noIssuesChecked', N'en', N'Checked: the validation rules of every table you can see, for {period}.', 1),
    (N'inspector.checkedAt', N'en', N'Checked at {time}.', 1),
    (N'grid.rowAttention.permitNotCovering', N'en', N'Permit {permit} is valid {validity} and does not cover {period}. Pick a permit valid in {period}.', 1),
    (N'grid.rowAttention.valueNotCovering', N'en', N'“{value}” ({column}) is valid {validity} and does not cover {period}. Pick a value valid in {period}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- GRID:water-lock-ui ── кінець секції ──
-- GRID:water-lock-ui ── змінені тексти наявних ключів (UAT 08.11, людина: «штриховку прибрати залишити замок»; без «окремого погодження») ──
-- Нове значення вже стоїть у MERGE (CATALOG-en-base.sql і PERMITS-sheet-binding-ui.sql — en, 00-prelude.sql і
-- PERMITS-sheet-binding-ui.sql — ru/kz); епілог оновлює, лише поки в базі старе.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'deny.PeriodClosed', N'en', N'The period is closed: changes need a separate approval.', N'The period is locked — reopen it to make changes.'),
    (N'deny.PeriodClosed', N'ru', N'Период закрыт: изменения требуют отдельного согласования.', N'Период заблокирован — откройте его повторно, чтобы внести изменения.'),
    (N'deny.PeriodClosed', N'kz', N'Кезең жабық: өзгерістер жеке келісуді қажет етеді.', N'Кезең бұғатталған — өзгеріс енгізу үшін оны қайта ашыңыз.'),
    (N'permits.sheetBinding.preview.hatched', N'en', N'Hatched cells come from the permit and cannot be edited.', N'Cells with a lock come from the permit and cannot be edited.'),
    (N'permits.sheetBinding.preview.hatched', N'ru', N'Заштрихованные ячейки берутся из разрешения и не редактируются.', N'Ячейки с замком берутся из разрешения и не редактируются.'),
    (N'permits.sheetBinding.preview.hatched', N'kz', N'Штрихталған ұяшықтар рұқсаттан алынады және өңделмейді.', N'Құлып белгісі бар ұяшықтар рұқсаттан алынады және өңделмейді.'),
    -- F13-p209 P2-1: банер закритого місяця — «locked», період назвою (клієнт), дія замість «попросіть відповідального».
    (N'document.lock.periodClosed', N'en', N'Period {period} is closed: its data can no longer be edited, imported, submitted or recalculated. Ask a period manager to reopen it.', N'{period} is locked: its data can no longer be edited, imported, submitted or recalculated. To edit it, reopen the month on Projects → Periods, or reopen it for this document if that action is offered to you.'),
    (N'document.lock.periodClosed', N'ru', N'Период {period} закрыт: его данные больше нельзя редактировать, импортировать, подавать или пересчитывать. Попросите ответственного за периоды переоткрыть его.', N'Период {period} заблокирован: его данные больше нельзя редактировать, импортировать, подавать или пересчитывать. Чтобы изменить их, откройте месяц повторно в разделе «Проекты → Периоды» или откройте его повторно для этого документа, если вам доступно это действие.'),
    (N'document.lock.periodClosed', N'kz', N'{period} кезеңі жабық: оның деректерін енді өңдеуге, импорттауға, тапсыруға немесе қайта есептеуге болмайды. Кезеңдерге жауапты адамнан оны қайта ашуды сұраңыз.', N'{period} кезеңі бұғатталған: оның деректерін енді өңдеуге, импорттауға, тапсыруға немесе қайта есептеуге болмайды. Өзгерту үшін айды «Жобалар → Кезеңдер» бөлімінде қайта ашыңыз немесе, егер бұл әрекет сізге қолжетімді болса, оны осы құжат үшін қайта ашыңыз.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- GRID:water-lock-ui ── кінець секції ──
