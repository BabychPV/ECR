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
-- A11Y-P3C:dim-names ── CL-20 (B-6): назви розмірностей 20-23 (kz/ru лишались англійськими кодами); ru/kz - порцією A11Y-P3C:dim-names нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'units.dim.VolumeFlow', N'en', N'Volumetric flow', 1),
    (N'units.dim.Power', N'en', N'Power', 1),
    (N'units.dim.Frequency', N'en', N'Frequency', 1),
    (N'units.dim.Pressure', N'en', N'Pressure', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- A11Y-P3C:dim-names ── кінець секції ──
