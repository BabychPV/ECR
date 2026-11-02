-- HSE301:F4 ── тип запиту джерела не налаштовано (FEATURE-HSE301-VIEW §4.3, D-172):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- інтерпольований запит PI SQL Client без ключа конфігурації не підміняється сирим.
    (N'err.ECR-INT-0422.queryKindNotConfigured', N'en', N'No {queryKind} query is configured for this source: set {configKey} in the environment settings.', 1),
    -- REGISTRY-SYNC:S1 ── поточні значення атрибутів (FEATURE-REGISTRY-SYNC §4, ФВ-8.11):
    -- транспорт без поточних значень відмовляє, а не віддає порожній знімок; шлях без значення — відмова шляху.
    (N'err.ECR-INT-0422.currentValueNotSupported', N'en', N'Data source transport {transport} does not read current attribute values, so registries cannot be synchronized from it.', 1),
    (N'err.ECR-INT-0503.currentValueUnreadable',   N'en', N'The source returned no readable current value for this attribute.', 1),
    -- Борг F4 (прохання HSE301): PI Web API і SQL інтерпольованих запитів не виконують взагалі — налаштування, яке б їх увімкнуло, немає.
    (N'err.ECR-INT-0422.queryKindNotSupported',    N'en', N'Data source transport {transport} does not run {queryKind} queries.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:F4 ── кінець секції
