-- COLL:r7y5-02-ambiguous-date ── ru/kz: R7-Y5 / Y5-02: неоднозначна слеш-дата (4/1/2024) у CSV довідника — відмова з порадою РРРР-ММ-ДД, а не переставлені день і місяць; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.valueAmbiguousDate', N'ru', N'Дата «{value}» неоднозначна: день и месяц можно поменять местами (Excel записывает даты в формате региональных настроек). Запишите дату как ГГГГ-ММ-ДД.'),
    (N'err.ECR-REG-0422.valueAmbiguousDate', N'kz', N'«{value}» күні екіұшты: күн мен айдың орнын ауыстыруға болады (Excel күндерді аймақтық баптаулар пішімінде жазады). Күнді ЖЖЖЖ-АА-КК түрінде жазыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-02-ambiguous-date ── кінець секції ──

-- COLL:r7y5-02-ambiguous-date ── R7-Y5 / Y5-02: неоднозначна слеш-дата (4/1/2024) у CSV довідника — відмова з порадою РРРР-ММ-ДД, а не переставлені день і місяць; ru/kz — порцією COLL:r7y5-02-ambiguous-date нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.valueAmbiguousDate', N'en', N'The date "{value}" is ambiguous: its day and month can be swapped (Excel writes dates in the format of your regional settings). Write the date as YYYY-MM-DD.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-02-ambiguous-date ── кінець секції ──
