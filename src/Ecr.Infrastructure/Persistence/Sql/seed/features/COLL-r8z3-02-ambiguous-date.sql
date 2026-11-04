-- COLL:r8z3-02-ambiguous-date ── ru/kz: R8-Z3 / Z3-02: дата з днем або місяцем спереду (4/1/2024, 01-04-2024, 01.04.24), яку розбір відхилив, — у сітці, шапці й імпорті .xlsx з порадою РРРР-ММ-ДД, а не «очікує дату»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-0422.ambiguousDate', N'ru', N'Столбец «{columnCode}»: порядок дня и месяца в дате «{value}» не определить (Excel записывает даты в формате региональных настроек). Запишите дату как ГГГГ-ММ-ДД или ДД.ММ.ГГГГ.'),
    (N'err.ECR-CELL-0422.ambiguousDate', N'kz', N'«{columnCode}» бағаны: «{value}» күніндегі күн мен айдың ретін анықтау мүмкін емес (Excel күндерді аймақтық баптаулар пішімінде жазады). Күнді ЖЖЖЖ-АА-КК немесе КК.АА.ЖЖЖЖ түрінде жазыңыз.'),
    (N'err.ECR-HDR-0422.ambiguousDate', N'ru', N'Поле шапки «{headerFieldCode}»: порядок дня и месяца в дате «{value}» не определить (Excel записывает даты в формате региональных настроек). Запишите дату как ГГГГ-ММ-ДД или ДД.ММ.ГГГГ.'),
    (N'err.ECR-HDR-0422.ambiguousDate', N'kz', N'«{headerFieldCode}» тақырып бөлігінің өрісі: «{value}» күніндегі күн мен айдың ретін анықтау мүмкін емес (Excel күндерді аймақтық баптаулар пішімінде жазады). Күнді ЖЖЖЖ-АА-КК немесе КК.АА.ЖЖЖЖ түрінде жазыңыз.'),
    (N'err.ECR-CELL-0422.importAmbiguousDate', N'ru', N'Порядок дня и месяца в этой дате не определить (Excel записывает даты в формате региональных настроек). Запишите дату как ГГГГ-ММ-ДД или ДД.ММ.ГГГГ.'),
    (N'err.ECR-CELL-0422.importAmbiguousDate', N'kz', N'Бұл күндегі күн мен айдың ретін анықтау мүмкін емес (Excel күндерді аймақтық баптаулар пішімінде жазады). Күнді ЖЖЖЖ-АА-КК немесе КК.АА.ЖЖЖЖ түрінде жазыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r8z3-02-ambiguous-date ── кінець секції ──

-- COLL:r8z3-02-ambiguous-date ── R8-Z3 / Z3-02: дата з днем або місяцем спереду (4/1/2024, 01-04-2024, 01.04.24), яку розбір відхилив, — у сітці, шапці й імпорті .xlsx з порадою РРРР-ММ-ДД, а не «очікує дату»; ru/kz — порцією COLL:r8z3-02-ambiguous-date нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-0422.ambiguousDate', N'en', N'Column "{columnCode}": the order of day and month in the date "{value}" cannot be determined (Excel writes dates in the format of your regional settings). Write the date as YYYY-MM-DD or DD.MM.YYYY.', 1),
    (N'err.ECR-HDR-0422.ambiguousDate', N'en', N'Header field "{headerFieldCode}": the order of day and month in the date "{value}" cannot be determined (Excel writes dates in the format of your regional settings). Write the date as YYYY-MM-DD or DD.MM.YYYY.', 1),
    (N'err.ECR-CELL-0422.importAmbiguousDate', N'en', N'The order of day and month in this date cannot be determined (Excel writes dates in the format of your regional settings). Write the date as YYYY-MM-DD or DD.MM.YYYY.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r8z3-02-ambiguous-date ── кінець секції ──
