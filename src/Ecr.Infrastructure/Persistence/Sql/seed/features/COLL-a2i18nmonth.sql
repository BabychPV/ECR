-- COLL:a2i18nmonth ── ru/kz A2-10: назви місяців підпису періоду, «помилок не знайдено»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'periods.monthOf', N'ru', N'{month} {year}'),
    (N'periods.monthOf', N'kz', N'{month} {year}'),
    (N'periods.month.1', N'ru', N'Январь'),
    (N'periods.month.1', N'kz', N'Қаңтар'),
    (N'periods.month.2', N'ru', N'Февраль'),
    (N'periods.month.2', N'kz', N'Ақпан'),
    (N'periods.month.3', N'ru', N'Март'),
    (N'periods.month.3', N'kz', N'Наурыз'),
    (N'periods.month.4', N'ru', N'Апрель'),
    (N'periods.month.4', N'kz', N'Сәуір'),
    (N'periods.month.5', N'ru', N'Май'),
    (N'periods.month.5', N'kz', N'Мамыр'),
    (N'periods.month.6', N'ru', N'Июнь'),
    (N'periods.month.6', N'kz', N'Маусым'),
    (N'periods.month.7', N'ru', N'Июль'),
    (N'periods.month.7', N'kz', N'Шілде'),
    (N'periods.month.8', N'ru', N'Август'),
    (N'periods.month.8', N'kz', N'Тамыз'),
    (N'periods.month.9', N'ru', N'Сентябрь'),
    (N'periods.month.9', N'kz', N'Қыркүйек'),
    (N'periods.month.10', N'ru', N'Октябрь'),
    (N'periods.month.10', N'kz', N'Қазан'),
    (N'periods.month.11', N'ru', N'Ноябрь'),
    (N'periods.month.11', N'kz', N'Қараша'),
    (N'periods.month.12', N'ru', N'Декабрь'),
    (N'periods.month.12', N'kz', N'Желтоқсан'),
    (N'document.validationNoErrors', N'ru', N'Ошибок не найдено'),
    (N'document.validationNoErrors', N'kz', N'Қате табылмады')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a2i18nmonth ── кінець секції ──
