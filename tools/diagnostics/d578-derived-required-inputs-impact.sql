-- tools/diagnostics/d578-derived-required-inputs-impact.sql
-- ✎ D-578 (рішення людини 26.11): ЛИШЕ ЧИТАННЯ. Скільки рядків НОВО заблокують подання виведені обов'язкові входи
-- (поля, які читає скрипт методології, calc.MethodologyReadField), — по документу / аркушу / місяцю. Запускати на КОПІЇ
-- live перед вікном (координатор). Нічого не пише, без тимчасових таблиць; READ UNCOMMITTED не потрібен.
--
-- ⚠ Це ВЕРХНЯ МЕЖА, а не точне число:
--   * застосовність правил методології (MatchJson / категорія / фільтр активності D-425) тут НЕ зіставляється — рядок
--     рахується, якщо скрипт версії, прив'язаної до таблиці, читає поле, а поле порожнє; сервер (RequiredInputsScanner)
--     вимагає поле лише від рядків, до яких методологія застосовна (дизельний рядок не вимагає полів газу);
--   * дефолти колонок (C-5) і колонки власників річної розкладки (Q-17) не враховано — порожня тут може бути заповненою
--     дефолтом чи місяцем-власником на екрані;
--   * рядок рахується лише «розпочатий» (є хоч одне введене непорожнє значення), як Q4 (порожні запасні — ні).
-- Винятки методолога (calc.MethodologyRequiredInput зі Severity 1 = Warn чи 2 = Optional) знімають вимогу; Severity 0 =
-- Block уже блокує збереження сьогодні — такі колонки НЕ рахуються як «нові».
-- Версія: опублікована (Status = 1), чинна на кінець місяця (найпізніша EffectiveFrom ≤ кінця місяця), рушій Script (Engine = 1);
-- версія з маркером '*' (скрипт не скомпілювався) чи без запису — виведених вимог немає.
-- Параметри: @Year — рік; @DocumentId — NULL для всіх.
DECLARE @Year int = 2026;
DECLARE @DocumentId bigint = NULL;

WITH months AS (
    SELECT DISTINCT i.PeriodKey, i.DocumentId, i.Id AS TableInstanceId, i.TableDefId
      FROM doc.TableInstance AS i
     WHERE i.PeriodKey > @Year * 100 AND i.PeriodKey < (@Year + 1) * 100
       AND (@DocumentId IS NULL OR i.DocumentId = @DocumentId)
       AND i.SheetCopyId IS NULL),
current_versions AS (
    SELECT m.TableInstanceId, m.PeriodKey, m.DocumentId, m.TableDefId, v.Id AS VersionId, v.MethodologyId
      FROM months AS m
      JOIN (SELECT DISTINCT b.TableDefId, b.MethodologyId FROM cfg.CalculationBinding AS b WHERE b.IsActive = 1) AS b
        ON b.TableDefId = m.TableDefId
     CROSS APPLY (SELECT TOP (1) mv.Id, mv.MethodologyId, mv.Engine
                    FROM calc.MethodologyVersion AS mv
                   WHERE mv.MethodologyId = b.MethodologyId AND mv.Status = 1
                     AND mv.EffectiveFrom <= EOMONTH(DATEFROMPARTS(m.PeriodKey / 100, m.PeriodKey % 100, 1))
                   ORDER BY mv.EffectiveFrom DESC, mv.Id DESC) AS v
     WHERE v.Engine = 1
       AND NOT EXISTS (SELECT 1 FROM calc.MethodologyReadField AS f WHERE f.MethodologyVersionId = v.Id AND f.FieldCode = N'*')),
derived AS (
    SELECT DISTINCT cv.TableInstanceId, cv.PeriodKey, cv.DocumentId, c.Id AS ColumnDefId
      FROM current_versions AS cv
      JOIN calc.MethodologyReadField AS f ON f.MethodologyVersionId = cv.VersionId AND f.FieldCode <> N'' AND f.OtherMonths = 0
      JOIN cfg.ColumnDef AS c ON c.TableDefId = cv.TableDefId AND c.Code = f.FieldCode AND c.IsDeleted = 0
     WHERE NOT EXISTS (SELECT 1 FROM calc.MethodologyRequiredInput AS r
                        WHERE r.MethodologyVersionId = cv.VersionId AND r.ColumnDefId = c.Id)),
started AS (
    SELECT r.PeriodKey, r.TableInstanceId, r.Id AS TableRowId
      FROM doc.TableRow AS r
     WHERE r.IsDeleted = 0
       AND EXISTS (SELECT 1 FROM months AS m WHERE m.TableInstanceId = r.TableInstanceId AND m.PeriodKey = r.PeriodKey)
       AND EXISTS (SELECT 1 FROM doc.CellValue AS v
                    WHERE v.PeriodKey = r.PeriodKey AND v.TableRowId = r.Id AND v.IsCalculated = 0 AND v.IsEmpty = 0)),
missing AS (
    SELECT d.DocumentId, d.PeriodKey, d.TableInstanceId, s.TableRowId
      FROM derived AS d
      JOIN started AS s ON s.TableInstanceId = d.TableInstanceId AND s.PeriodKey = d.PeriodKey
     WHERE NOT EXISTS (SELECT 1 FROM doc.CellValue AS v
                        WHERE v.PeriodKey = s.PeriodKey AND v.TableRowId = s.TableRowId AND v.ColumnDefId = d.ColumnDefId
                          AND v.IsEmpty = 0))
SELECT ms.DocumentId, sd.Code AS SheetCode, ms.PeriodKey,
       COUNT(DISTINCT ms.TableRowId) AS RowsThatWouldBlock,
       COUNT(*) AS EmptyDerivedCells
  FROM missing AS ms
  JOIN doc.TableInstance AS i ON i.Id = ms.TableInstanceId AND i.PeriodKey = ms.PeriodKey
  JOIN cfg.TableDef AS td ON td.Id = i.TableDefId
  JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
 GROUP BY ms.DocumentId, sd.Code, ms.PeriodKey
 ORDER BY ms.DocumentId, sd.Code, ms.PeriodKey
OPTION (RECOMPILE);
