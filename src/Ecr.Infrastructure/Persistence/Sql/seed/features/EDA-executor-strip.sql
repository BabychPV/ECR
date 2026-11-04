-- EDA:executor-strip ── ru/kz смуги виконавця (EA-5b); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'editAssign.bar.assigned', N'ru', N'Месяц передан вам: {slice}'),
    (N'editAssign.bar.assigned', N'kz', N'Ай сізге берілді: {slice}'),
    (N'editAssign.bar.returned', N'ru', N'{slice} возвращён владельцем: {reason}'),
    (N'editAssign.bar.returned', N'kz', N'{slice} иесі қайтарды: {reason}'),
    (N'editAssign.bar.returnedNoReason', N'ru', N'{slice} возвращён владельцем'),
    (N'editAssign.bar.returnedNoReason', N'kz', N'{slice} иесі қайтарды'),
    (N'editAssign.bar.filled', N'ru', N'{slice}: заполнено, ожидает валидации владельцем'),
    (N'editAssign.bar.filled', N'kz', N'{slice}: толтырылды, иесінің валидациясын күтуде'),
    (N'editAssign.bar.markFilled', N'ru', N'Отметить заполненным'),
    (N'editAssign.bar.markFilled', N'kz', N'Толтырылды деп белгілеу'),
    (N'editAssign.bar.confirmFill', N'ru', N'После этого вы не сможете править {slice}, пока владелец не вернёт его на доработку.'),
    (N'editAssign.bar.confirmFill', N'kz', N'Осыдан кейін иесі қайтармайынша {slice} өзгерту мүмкін болмайды.'),
    (N'editAssign.bar.note', N'ru', N'Примечание владельца: {note}'),
    (N'editAssign.bar.note', N'kz', N'Иесінің ескертпесі: {note}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- EDA:executor-strip ── кінець секції ──

-- EDA:executor-strip ── EA-5b: смуга виконавця над таблицею документа (`ExecutorStrip`); власна порція.
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'editAssign.bar.assigned', N'en', N'Month assigned to you: {slice}', 1),
    (N'editAssign.bar.returned', N'en', N'{slice} returned by the owner: {reason}', 1),
    (N'editAssign.bar.returnedNoReason', N'en', N'{slice} returned by the owner', 1),
    (N'editAssign.bar.filled', N'en', N'{slice}: filled, awaiting the owner''s validation', 1),
    (N'editAssign.bar.markFilled', N'en', N'Mark as filled', 1),
    (N'editAssign.bar.confirmFill', N'en', N'After this you can''t edit {slice} until the owner returns it.', 1),
    (N'editAssign.bar.note', N'en', N'Owner''s note: {note}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- EDA:executor-strip: кінець секції
