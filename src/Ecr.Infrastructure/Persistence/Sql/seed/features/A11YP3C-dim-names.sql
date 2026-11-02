-- A11Y-P3C:dim-names ── ru/kz: CL-20 (B-6), назви розмірностей 20-23; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'units.dim.VolumeFlow', N'ru', N'Объёмный расход'),
    (N'units.dim.VolumeFlow', N'kz', N'Көлемдік шығын'),
    (N'units.dim.Power', N'ru', N'Мощность'),
    (N'units.dim.Power', N'kz', N'Қуат'),
    (N'units.dim.Frequency', N'ru', N'Частота'),
    (N'units.dim.Frequency', N'kz', N'Жиілік'),
    (N'units.dim.Pressure', N'ru', N'Давление'),
    (N'units.dim.Pressure', N'kz', N'Қысым')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- A11Y-P3C:dim-names ── кінець секції ──
