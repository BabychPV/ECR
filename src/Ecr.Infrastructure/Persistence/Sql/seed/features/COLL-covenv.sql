-- COLL:covenv ── ru/kz подробиць журналу покриття конвертом (MaterializeCollectedDataJob, SourceEventSyncJob); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'coverageEvents.periodClosed', N'ru', N'Период в состоянии {state}: поздний сбор остаётся сырым.'),
    (N'coverageEvents.periodClosed', N'kz', N'Кезең {state} күйінде: кеш жиналған деректер шикі күйінде қалады.'),
    (N'coverageEvents.periodMissing', N'ru', N'Состояние периода неизвестно: поздний сбор остаётся сырым.'),
    (N'coverageEvents.periodMissing', N'kz', N'Кезең күйі белгісіз: кеш жиналған деректер шикі күйінде қалады.'),
    (N'coverageEvents.pointCeiling', N'ru', N'Поле {field}: более {limit} точек за период; значение не записано, потому что свёртка неполной строки дала бы неверное число.'),
    (N'coverageEvents.pointCeiling', N'kz', N'{field} өрісі: кезең ішінде {limit} нүктеден астам; мән жазылмады, өйткені толық емес жолды жинақтау қате сан берер еді.'),
    (N'coverageEvents.keptManual', N'ru', N'В ячейке {cell} есть правка человека: собранное значение не применено.'),
    (N'coverageEvents.keptManual', N'kz', N'{cell} ұяшығында адамның түзетуі бар: жиналған мән қолданылмады.'),
    (N'coverageEvents.writeConflict', N'ru', N'Ячейка {cell}: строку изменяли во время записи, повторы исчерпаны; собранное значение не записано. Следующий прогон попробует снова.'),
    (N'coverageEvents.writeConflict', N'kz', N'{cell} ұяшығы: жол жазу кезінде өзгертілді, қайталаулар таусылды; жиналған мән жазылмады. Келесі іске қосу қайта көреді.'),
    (N'coverageEvents.needsConfirmation', N'ru', N'Ячейка {cell}: правило периода требует подтверждения человека; интеграция не подтверждает, собранное значение не записано.'),
    (N'coverageEvents.needsConfirmation', N'kz', N'{cell} ұяшығы: кезең ережесі адамның растауын талап етеді; интеграция растамайды, жиналған мән жазылмады.'),
    (N'coverageEvents.eventWriteFailed', N'ru', N'Событие {eventId} не записано: {reason}'),
    (N'coverageEvents.eventWriteFailed', N'kz', N'{eventId} оқиғасы жазылмады: {reason}'),
    (N'coverageEvents.eventWritePartial', N'ru', N'Событие {eventId} записано не полностью (строка {rowKey}): конфликт записи или подтверждение человека; следующий прогон попробует снова.'),
    (N'coverageEvents.eventWritePartial', N'kz', N'{eventId} оқиғасы толық жазылмады ({rowKey} жолы): жазу қақтығысы немесе адамның растауы; келесі іске қосу қайта көреді.'),
    (N'coverageEvents.eventRowNotCreated', N'ru', N'Событие {eventId} не записано: строка {rowKey} не создана, значение отклонено.'),
    (N'coverageEvents.eventRowNotCreated', N'kz', N'{eventId} оқиғасы жазылмады: {rowKey} жолы жасалмады, мән қабылданбады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO

-- COLL:covenv ── кінець секції ──

-- COLL:covenv ── Подробиці журналу покриття конвертом, а не готовою фразою (`MaterializeCollectedDataJob`, `SourceEventSyncJob`, `CoverageDetails`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'coverageEvents.periodClosed',            N'en', N'Period is in state {state}: late collection stays raw.', 1),
    (N'coverageEvents.periodMissing',           N'en', N'Period state is unknown: late collection stays raw.', 1),
    (N'coverageEvents.pointCeiling',            N'en', N'Field {field}: more than {limit} points per period; the value was not written, because folding a partial row would give a wrong number.', 1),
    (N'coverageEvents.keptManual',              N'en', N'Cell {cell} has a manual edit: the collected value was not applied.', 1),
    (N'coverageEvents.writeConflict',           N'en', N'Cell {cell}: the row was changed during the write and retries ran out; the collected value was not written. The next run will try again.', 1),
    (N'coverageEvents.needsConfirmation',       N'en', N'Cell {cell}: the period rule requires a human to confirm; the integration does not confirm, the collected value was not written.', 1),
    (N'coverageEvents.eventWriteFailed',        N'en', N'Event {eventId} was not written: {reason}', 1),
    (N'coverageEvents.eventWritePartial',       N'en', N'Event {eventId} was written only in part (row {rowKey}): a write conflict or a human confirmation is pending; the next run will try again.', 1),
    (N'coverageEvents.eventRowNotCreated',      N'en', N'Event {eventId} was not written: row {rowKey} was not created, the value was rejected.', 1)
    -- ru/kz — окремою порцією `COLL:covenv` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:covenv ── кінець секції ──
