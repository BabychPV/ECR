-- D475:formula-errors ── ru/kz: помилка формули — блок подання, позначки сітки, група «Помилки формул», журнал (C-1, D-475); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'validation.formula.error', N'ru', N'{column}: ошибка формулы {code} — значение не пересчитано (оставлено последнее правильное). Подача заблокирована, пока формула не рассчитается.'),
    (N'validation.formula.error', N'kz', N'{column}: формула қатесі {code} — мән қайта есептелмеді (соңғы дұрыс мән қалдырылды). Формула есептелмейінше тапсыру бұғатталған.'),
    (N'validation.formula.blocked', N'ru', N'{column}: не обновлено из-за ошибки формулы в {source}. Подача заблокирована до исправления.'),
    (N'validation.formula.blocked', N'kz', N'{column}: {source} ішіндегі формула қатесіне байланысты жаңартылмады. Түзетілгенше тапсыру бұғатталған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D475:formula-errors ── кінець секції ──
-- D475:formula-errors ── en: помилка формули (C-1, D-475): блок подання, сітка, панель проблем, журнал; ru/kz — порцією D475:formula-errors вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'validation.formula.error', N'en', N'{column}: formula error {code} — the value was not recalculated (the last correct value is kept). Submit is blocked until the formula calculates.', 1),
    (N'validation.formula.blocked', N'en', N'{column}: not updated because of a formula error in {source}. Submit is blocked until it is fixed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D475:formula-errors ── кінець секції ──
