import { apiFetch } from '@/api/client';
import type { FormulaDraft, FormulaDto, FormulaScope } from './formula';
import { formulaBody } from './formula';

/**
 * Звернення редактора формул (`W5.3`).
 *
 * ⛔ Наступний вертикальний зріз авторства структури шаблону через API, за
 * зразком `sheetApi.ts` (W5.0). До цього формулу колонки чи рядка міг завести
 * лише офлайновий генератор тестових даних чи тест напряму через конструктор
 * домену: у веб-інтерфейсі не було жодного способу прив'язати вираз до
 * колонки чи рядка, лише перевірити його ІЗОЛЬОВАНО (`/expressions/validate`,
 * `ExpressionsPage`).
 *
 * ⚠ Адреса — `(tableDefId, scope, target)`, а не код: формула не має власної
 * ідентичності окремо від колонки чи рядка, який обчислює. `target` означає
 * РІЗНЕ залежно від `scope`: `ColumnDefId` числом при `Column`, `RowKey`
 * текстом при `Row` — `RowDef.Id` не годиться, бо структура версії його
 * взагалі не показує клієнту (`FormulaDefHandlers.cs`).
 */

/** Записує формулу; створює її, якщо на цілі ще немає. */
export function saveFormula(templateVersionId: number, draft: FormulaDraft): Promise<FormulaDto> {
  return apiFetch<FormulaDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(draft.tableDefId)}/formulas/${routeScope(draft.scope)}/${encodeURIComponent(draft.target)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(formulaBody(draft)),
    },
  );
}

/** Прибирає формулу з чернетки (м'яко, `ФВ-7.6`). */
export function deleteFormula(
  templateVersionId: number,
  tableDefId: number,
  scope: FormulaScope,
  target: string,
): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableDefId)}/formulas/${routeScope(scope)}/${encodeURIComponent(target)}`,
    { method: 'DELETE' },
  );
}

/**
 * Сегмент маршруту для області формули.
 *
 * ⚠ Сервер приймає лише нижній регістр (`tables/{tableDefId}/formulas/{scope}/{target}`,
 * обмежено `regex(^(column|row)$)` у `TemplateVersionsController`), а DTO
 * `FormulaScope` — `PascalCase` (`Column`/`Row`/`Cell`), бо це той самий
 * тип, що й у C#-переліку. Двох стилів для одного значення тут не уникнути:
 * один живе в адресі URL, другий — у формі даних.
 */
function routeScope(scope: FormulaScope): 'column' | 'row' {
  return scope === 'Column' ? 'column' : 'row';
}
