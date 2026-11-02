-- COLL:ui-rc9 ── ru/kz UI RC9: пошук/проєкт/автори у Documents; назви розмірностей; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.search', N'ru', N'Поиск'),
    (N'documents.search', N'kz', N'Іздеу'),
    (N'documents.searchPlaceholder', N'ru', N'Название или ключ документа'),
    (N'documents.searchPlaceholder', N'kz', N'Құжат атауы немесе кілті'),
    (N'documents.projectAll', N'ru', N'Все проекты'),
    (N'documents.projectAll', N'kz', N'Барлық жобалар'),
    (N'documents.ownersAll', N'ru', N'Документы всех'),
    (N'documents.ownersAll', N'kz', N'Барлығының құжаттары'),
    (N'units.dim.Mass', N'ru', N'Масса'),
    (N'units.dim.Mass', N'kz', N'Масса'),
    (N'units.dim.Volume', N'ru', N'Объём'),
    (N'units.dim.Volume', N'kz', N'Көлем'),
    (N'units.dim.Energy', N'ru', N'Энергия'),
    (N'units.dim.Energy', N'kz', N'Энергия'),
    (N'units.dim.Time', N'ru', N'Время'),
    (N'units.dim.Time', N'kz', N'Уақыт'),
    (N'units.dim.Temperature', N'ru', N'Температура'),
    (N'units.dim.Temperature', N'kz', N'Температура'),
    (N'units.dim.Amount', N'ru', N'Количество вещества'),
    (N'units.dim.Amount', N'kz', N'Зат мөлшері'),
    (N'units.dim.Dimensionless', N'ru', N'Безразмерная'),
    (N'units.dim.Dimensionless', N'kz', N'Өлшемсіз'),
    (N'units.dim.MassFlow', N'ru', N'Массовый расход'),
    (N'units.dim.MassFlow', N'kz', N'Массалық шығын'),
    (N'units.dim.MassPerMass', N'ru', N'Масса на массу'),
    (N'units.dim.MassPerMass', N'kz', N'Массаға шаққандағы масса'),
    (N'units.dim.MassPerEnergy', N'ru', N'Масса на энергию'),
    (N'units.dim.MassPerEnergy', N'kz', N'Энергияға шаққандағы масса'),
    (N'units.dim.MassPerVolume', N'ru', N'Масса на объём'),
    (N'units.dim.MassPerVolume', N'kz', N'Көлемге шаққандағы масса'),
    (N'units.dim.StdVolume', N'ru', N'Объём при стандартных условиях'),
    (N'units.dim.StdVolume', N'kz', N'Стандартты жағдайдағы көлем'),
    (N'units.dim.StdVolumeFlow', N'ru', N'Расход при стандартных условиях'),
    (N'units.dim.StdVolumeFlow', N'kz', N'Стандартты жағдайдағы шығын'),
    (N'units.dim.Velocity', N'ru', N'Скорость'),
    (N'units.dim.Velocity', N'kz', N'Жылдамдық'),
    (N'units.dim.Area', N'ru', N'Площадь'),
    (N'units.dim.Area', N'kz', N'Ауданы'),
    (N'units.dim.MassPerStdVolume', N'ru', N'Масса на стандартный объём'),
    (N'units.dim.MassPerStdVolume', N'kz', N'Стандартты көлемге шаққандағы масса'),
    (N'units.dim.EnergyPerStdVolume', N'ru', N'Энергия на стандартный объём'),
    (N'units.dim.EnergyPerStdVolume', N'kz', N'Стандартты көлемге шаққандағы энергия'),
    (N'units.dim.EnergyPerMass', N'ru', N'Энергия на массу'),
    (N'units.dim.EnergyPerMass', N'kz', N'Массаға шаққандағы энергия'),
    (N'units.dim.MassPerAmount', N'ru', N'Масса на количество вещества'),
    (N'units.dim.MassPerAmount', N'kz', N'Зат мөлшеріне шаққандағы масса')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-rc9 ── кінець секції ──
-- COLL:ui-rc9 ── UI RC9: пошук/проєкт/автори у Documents; назви розмірностей (en); ru/kz — порцією COLL:ui-rc9 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.search', N'en', N'Search', 1),
    (N'documents.searchPlaceholder', N'en', N'Document name or key', 1),
    (N'documents.projectAll', N'en', N'All projects', 1),
    (N'documents.ownersAll', N'en', N'Everyone’s documents', 1),
    (N'units.dim.Mass', N'en', N'Mass', 1),
    (N'units.dim.Volume', N'en', N'Volume', 1),
    (N'units.dim.Energy', N'en', N'Energy', 1),
    (N'units.dim.Time', N'en', N'Time', 1),
    (N'units.dim.Temperature', N'en', N'Temperature', 1),
    (N'units.dim.Amount', N'en', N'Amount of substance', 1),
    (N'units.dim.Dimensionless', N'en', N'Dimensionless', 1),
    (N'units.dim.MassFlow', N'en', N'Mass flow', 1),
    (N'units.dim.MassPerMass', N'en', N'Mass per mass', 1),
    (N'units.dim.MassPerEnergy', N'en', N'Mass per energy', 1),
    (N'units.dim.MassPerVolume', N'en', N'Mass per volume', 1),
    (N'units.dim.StdVolume', N'en', N'Standard volume', 1),
    (N'units.dim.StdVolumeFlow', N'en', N'Standard volume flow', 1),
    (N'units.dim.Velocity', N'en', N'Velocity', 1),
    (N'units.dim.Area', N'en', N'Area', 1),
    (N'units.dim.MassPerStdVolume', N'en', N'Mass per standard volume', 1),
    (N'units.dim.EnergyPerStdVolume', N'en', N'Energy per standard volume', 1),
    (N'units.dim.EnergyPerMass', N'en', N'Energy per mass', 1),
    (N'units.dim.MassPerAmount', N'en', N'Mass per amount of substance', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-rc9 ── кінець секції ──
