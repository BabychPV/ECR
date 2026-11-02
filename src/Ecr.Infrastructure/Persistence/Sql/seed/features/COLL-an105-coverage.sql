-- COLL:an105-coverage ── ru/kz: AN-105 / D2-02: неповне покриття і «немає даних» матеріалізації; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'status.coverage.PartialCoverage', N'ru', N'Неполное покрытие'),
    (N'status.coverage.PartialCoverage', N'kz', N'Толық емес қамту'),
    (N'status.coverage.SkippedNoData', N'ru', N'Нет данных за период'),
    (N'status.coverage.SkippedNoData', N'kz', N'Кезең үшін деректер жоқ'),
    (N'coverageEvents.partialCoverage', N'ru', N'Поле {field} (сопоставление {mapId}): источник покрыл лишь {percentGood}% периода (порог {min}%). Значение записано, но оно неполное: пропуски и точки плохого качества не учтены. Сверьте источник, прежде чем полагаться на число.'),
    (N'coverageEvents.partialCoverage', N'kz', N'{field} өрісі ({mapId} сәйкестендіру): дереккөз кезеңнің тек {percentGood}% қамтыды (шегі {min}%). Мән жазылды, бірақ ол толық емес: үзілістер мен сапасы нашар нүктелер ескерілмеген. Санға сенбес бұрын дереккөзді салыстырыңыз.'),
    (N'coverageEvents.noData', N'ru', N'Поле {field} (сопоставление {mapId}): за завершившийся период нет пригодных точек. Ячейка не обновлена и может содержать значение предыдущего прогона.'),
    (N'coverageEvents.noData', N'kz', N'{field} өрісі ({mapId} сәйкестендіру): аяқталған кезеңде жарамды нүктелер жоқ. Ұяшық жаңартылмады және алдыңғы іске қосудың мәнін сақтауы мүмкін.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an105-coverage ── кінець секції ──

