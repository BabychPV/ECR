-- PERMITS: ui-p3-small ── ru/kz: KI-04 підказка мінімальної довжини причини в End early; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.endEarly.minLengthHint', N'ru', N'Введите не менее {min} символов.'),
    (N'permits.endEarly.minLengthHint', N'kz', N'Кемінде {min} таңба енгізіңіз.'),
    (N'permits.endEarly.minLengthReached', N'ru', N'Причина достаточной длины.'),
    (N'permits.endEarly.minLengthReached', N'kz', N'Себеп жеткілікті ұзындықта.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: ui-p3-small ── кінець секції ──
