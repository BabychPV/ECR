import { apiFetch } from '@/api/client';
import type {
  ExpressionDialect,
  ExpressionMetadataDto,
  ExpressionValidationDto,
  ValidateExpressionBody,
} from '@/api/types';

/** Де живе вираз — визначає, наскільки повно його можна перевірити. */
export interface ExpressionPlacement {
  /** Версія шаблону; без неї перевіряється лише синтаксис. */
  readonly templateVersionId?: number | undefined;
  /** Версія методології — джерело `CST.`, `!`, `@`. */
  readonly methodologyVersionId?: number | undefined;
  /** Таблиця, в якій живе вираз. */
  readonly tableDefId?: number | undefined;
  /** Рядок формули; `null` для формул рівня колонки. */
  readonly rowKey?: string | undefined;
  /** Колонка — для підстановки `{Month}`. */
  readonly columnDefId?: number | undefined;
}

/**
 * Перевіряє вираз на сервері.
 *
 * ⛔ Перевірку робить СЕРВЕР, а не клієнт. У `shared/formula/evaluate.ts` є
 * власний обчислювач, і спокуса перевіряти ним велика — але він уміє лише
 * арифметику літералів: він не знає ані структури шаблону, ані типів колонок,
 * ані одиниць. Перевіряти ним означало б показувати зелене там, де публікація
 * відмовить, — тобто рівно те, чого редактор має не допускати.
 */
export function validateExpression(
  expression: string,
  dialect: ExpressionDialect,
  placement: ExpressionPlacement,
  signal?: AbortSignal,
): Promise<ExpressionValidationDto> {
  const body: ValidateExpressionBody = {
    expression,
    dialect,
    templateVersionId: placement.templateVersionId ?? null,
    tableDefId: placement.tableDefId ?? null,
    rowKey: placement.rowKey ?? null,
    columnDefId: placement.columnDefId ?? null,
  };

  return apiFetch<ExpressionValidationDto>('/api/v1/expressions/validate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    ...(signal === undefined ? {} : { signal }),
  });
}

/** Склад мови: функції діалекту і символи контексту. */
export function expressionMetadata(
  dialect: ExpressionDialect,
  placement: ExpressionPlacement,
): Promise<ExpressionMetadataDto> {
  const query = new URLSearchParams({ dialect });

  if (placement.templateVersionId !== undefined) {
    query.set('templateVersionId', String(placement.templateVersionId));
  }

  if (placement.methodologyVersionId !== undefined) {
    query.set('methodologyVersionId', String(placement.methodologyVersionId));
  }

  return apiFetch<ExpressionMetadataDto>(`/api/v1/expressions/metadata?${query.toString()}`);
}
