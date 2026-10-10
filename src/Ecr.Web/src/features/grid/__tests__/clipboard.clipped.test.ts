import { describe, expect, it } from 'vitest';
import { planPaste } from '@/features/grid/clipboard';
import { parseClipboard } from '@/features/grid/tsvClipboard';

/**
 * F6-03: буфер, більший за таблицю, обрізається (рядків вставка не створює), але
 * НЕ мовчки — план каже, скільки рядків і колонок відкинуто, а сітка це показує.
 */
describe('Вставка, більша за таблицю (F6-03)', () => {
  const twoByFive = '1\t2\n3\t4\n5\t6\n7\t8\n9\t10\n';

  it('рядки нижче таблиці — пораховано', () => {
    const plan = planPaste(parseClipboard(twoByFive), ['R1', 'R2', 'R3'], ['C1', 'C2'], { rowIndex: 0, columnIndex: 0 }, () => null);

    // ⛔ Мутація: прибрати `clipped` з плану — тут `undefined`.
    expect(plan.clipped).toEqual({ rows: 2, columns: 0 });
    expect(plan.targets).toHaveLength(6);
  });

  it('колонки правіше якоря — пораховано; найширший рядок буфера визначає ширину', () => {
    const plan = planPaste(parseClipboard('1\n2\t3\t4\n'), ['R1', 'R2'], ['C1', 'C2'], { rowIndex: 0, columnIndex: 1 }, () => null);

    expect(plan.clipped).toEqual({ rows: 0, columns: 2 });
  });

  it('усе вмістилося — нічого не відкинуто', () => {
    const plan = planPaste(parseClipboard('1\t2\n'), ['R1', 'R2'], ['C1', 'C2'], { rowIndex: 1, columnIndex: 0 }, () => null);

    expect(plan.clipped).toEqual({ rows: 0, columns: 0 });
  });

  it('батч відхилено — обрізання все одно пораховано', () => {
    const plan = planPaste(parseClipboard(twoByFive), ['R1'], ['C1', 'C2'], { rowIndex: 0, columnIndex: 0 }, () => 'read only');

    expect(plan.targets).toHaveLength(0);
    expect(plan.clipped).toEqual({ rows: 4, columns: 0 });
  });
});
