import type { TemplateColumnDto, TemplateStructureDto } from '@/api/types';
import { draftOfFormula, emptyFormulaDraft, type FormulaDraft } from '@/features/templates/formula';

/** Аркуш, таблиця й рядок структури версії — як їх віддає `GET …/structure`. */
export type TemplateSheet = TemplateStructureDto['sheets'][number];
export type TemplateTable = TemplateSheet['tables'][number];
export type TemplateRow = TemplateTable['rows'][number];

/**
 * Чернетка формули для колонки чи рядка.
 *
 * ⛔ Дефект 2026-09-23: раніше тут завжди був `emptyFormulaDraft` — повторне
 * відкриття на колонці (рядку) зі збереженою формулою показувало порожній
 * редактор, хоча PUT зберігав вираз (структура тепер несе його — `GET …/structure`).
 */
export function columnFormulaDraft(tableId: number, column: TemplateColumnDto): FormulaDraft {
  return column.formulaExpression !== null
    ? draftOfFormula(tableId, 'Column', String(column.id), {
        dialect: column.formulaDialect ?? 'Template',
        expression: column.formulaExpression,
      })
    : emptyFormulaDraft(tableId, 'Column', String(column.id));
}

export function rowFormulaDraft(tableId: number, row: TemplateRow): FormulaDraft {
  return row.formulaExpression !== null
    ? draftOfFormula(tableId, 'Row', row.rowKey, {
        dialect: row.formulaDialect ?? 'Template',
        expression: row.formulaExpression,
      })
    : emptyFormulaDraft(tableId, 'Row', row.rowKey);
}
