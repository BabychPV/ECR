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

-- COLL:an105-coverage ── AN-105 / D2-02: матеріалізація скінченого періоду — неповне покриття і «немає даних» (HU-13 Q2, A); ru/kz — порцією COLL:an105-coverage нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'status.coverage.PartialCoverage', N'en', N'Partial coverage', 1),
    (N'status.coverage.SkippedNoData', N'en', N'No data for the period', 1),
    (N'coverageEvents.partialCoverage', N'en', N'Field {field} (mapping {mapId}): the source covered only {percentGood}% of the period (threshold {min}%). The value was written, but it is incomplete: gaps and bad-quality points are not counted. Check the source before relying on the number.', 1),
    (N'coverageEvents.noData', N'en', N'Field {field} (mapping {mapId}): there are no usable points for the ended period. The cell was not updated and may still hold the value of an earlier run.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an105-coverage ── кінець секції ──
