import type { Query } from '@tanstack/react-query';

/**
 * Ключ запиту чисел методологій документа за період.
 *
 * ⚠ Один вираз для панелі й для тих, хто її інвалідує (перерахунок зачеплених у RT-25): розбіжні
 * ключі давали «панель не оновилась після перерахунку» без жодної помилки.
 */
export function calculationResultsKey(documentId: number, periodKey: number): readonly unknown[] {
  return ['document', documentId, periodKey, 'calculation-results'];
}

/** Запити чисел методологій усіх документів і періодів — для `invalidateQueries({ predicate })`. */
export function isCalculationResultsQuery(query: Pick<Query, 'queryKey'>): boolean {
  const key = query.queryKey;
  return key[0] === 'document' && key[3] === 'calculation-results';
}

/** Ідентифікатор тосту «Перерахуйте» після імпорту: один на тип, знімається, коли числа свіжі. */
export const RecalculateHintId = 'import-recalculate-hint';
