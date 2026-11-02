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

