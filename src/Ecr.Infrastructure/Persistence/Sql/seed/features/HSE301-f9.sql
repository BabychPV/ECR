-- HSE301:F9 ── мапінг подій джерела і стан зв'язку подія ↔ рядок: доменні відмови
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- (FEATURE-HSE301-VIEW §4.7.3–4.7.4, D-186). ⚠ Заведено ТУТ, а не в F8: кидає їх уже домен F9.
    -- F8 їх не дублює — повтор ключа ламає MERGE.
    (N'err.ECR-INT-0422.eventMapStartEndRequired', N'en', N'An event mapping must put the event start ($start) and end ($end) into Date columns; "{attribute}" is not mapped.', 1),
    (N'err.ECR-INT-0422.eventMapTargetNotDynamic', N'en', N'Events can only be written to a dynamic table; table "{tableCode}" has row mode {rowMode}.', 1),
    (N'err.ECR-INT-0422.eventMapColumnNotInTable', N'en', N'Column "{targetColumn}" belongs to a different table than the event mapping.', 1),
    (N'err.ECR-INT-0409.eventMapColumnTaken', N'en', N'Column "{targetColumn}" already has a field in this event mapping.', 1),
    (N'err.ECR-INT-0422.eventMapStartEndNotDate', N'en', N'Event time "{attribute}" can only be written to a Date column; "{targetColumn}" is {dataType}.', 1),
    (N'err.ECR-INT-0422.eventMapReservedAttributeInvalid', N'en', N'"{attribute}" is not a reserved event attribute ($start, $end, $name), or it is not mapped as a direct value of the event itself.', 1),
    (N'err.ECR-INT-0422.eventMapValueKindMismatch', N'en', N'Column "{targetColumn}" ({dataType}) cannot take event values mapped as {valueKind}: a Lookup column takes a registry entry by code, name or value map; other columns take the value directly; calculated columns take nothing.', 1),
    (N'err.ECR-INT-0422.eventMapFilterIncomplete', N'en', N'An event filter needs all three parts together: the attribute, where it is (event or primary element) and the value.', 1),
    (N'err.ECR-INT-0422.eventMapValueMapNotAllowed', N'en', N'The field for "{attribute}" finds registry entries as {valueKind}, so an explicit value map would never be used.', 1),
    (N'err.ECR-INT-0409.eventMapSourceValueTaken', N'en', N'Source value "{sourceValue}" is already mapped to a registry entry in this field.', 1),
    (N'err.ECR-INT-0422.eventLinkTransitionInvalid', N'en', N'The link of source event "{sourceEventId}" cannot change from {from} to {to}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:F9 ── кінець секції
