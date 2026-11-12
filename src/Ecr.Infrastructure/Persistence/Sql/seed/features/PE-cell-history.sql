-- PE:cell-history ── ru/kz: History комірки Q-17 — зміни з періоду-власника (не-місячна колонка — якір року); власна порція; en — порція #UiEn нижче ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'inspector.history.ownerPeriodNote', N'ru', N'Изменено в периоде {period} — значение общее для всех месяцев года.'),
    (N'inspector.history.ownerPeriodNote', N'kz', N'{period} кезеңінде өзгертілген — мән жылдың барлық айларына ортақ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PE:cell-history ── кінець секції ──
-- PE:cell-history ── en: History комірки Q-17 — зміни з періоду-власника (lane/integ-cell-history-routed); ru/kz — seed/features/PE-cell-history.sql ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'inspector.history.ownerPeriodNote', N'en', N'Changed in {period} — the value is shared by all months of the year.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PE:cell-history ── кінець секції ──
