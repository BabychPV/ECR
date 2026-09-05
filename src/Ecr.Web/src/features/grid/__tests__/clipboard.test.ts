import { describe, it, expect } from 'vitest';
import { parseClipboard, parseNumber, planPaste, toClipboard } from '@/features/grid/clipboard';

/**
 * Вставка з буфера Excel — критерій FQ-1 №3 і найчастіша причина, з якої
 * grid-бібліотека не підходить.
 */
describe('Вставка з буфера Excel', () => {
  it('ФВ-3.3: розбирає багатоклітинний буфер із табуляціями і переносами рядків', () => {
    const matrix = parseClipboard('1\t2\t3\r\n4\t5\t6\r\n');

    expect(matrix).toEqual([
      ['1', '2', '3'],
      ['4', '5', '6'],
    ]);
  });

  it('розпізнає десяткову кому в локалі користувача', () => {
    expect(parseNumber('12,5')).toBe(12.5);
    expect(parseNumber('12.5')).toBe(12.5);
    expect(parseNumber('1 234,56')).toBe(1234.56);

    // ⛔ Текст лишається текстом: «н/д» не стає нулем. Нуль тут читався б як
    // вимірювання, якого не робили.
    expect(parseNumber('н/д')).toBeNull();
    expect(parseNumber('')).toBeNull();
  });

  it('ФВ-14.4: вставка 500×60 не блокує UI довше за 200 мс', () => {
    const rows = Array.from({ length: 500 }, (_, r) =>
      Array.from({ length: 60 }, (_, c) => String(r * 60 + c)).join('\t'),
    ).join('\n');

    const rowKeys = Array.from({ length: 500 }, (_, r) => `R${r}`);
    const columnCodes = Array.from({ length: 60 }, (_, c) => `C${c}`);

    const started = performance.now();
    const matrix = parseClipboard(rows);
    const plan = planPaste(matrix, rowKeys, columnCodes, { rowIndex: 0, columnIndex: 0 }, () => null);
    const elapsed = performance.now() - started;

    expect(plan.targets).toHaveLength(500 * 60);
    expect(elapsed).toBeLessThan(200);
  });

  it('вставка у read-only комірки відхиляє ВЕСЬ батч і показує перелік заборонених', () => {
    // ⛔ Часткове застосування заборонене на рівні API (B04 §2.3), і UI не має
    // його імітувати: користувач побачив би, що «вставилося», і не помітив би,
    // що половина чисел не потрапила.
    const matrix = parseClipboard('1\t2\n3\t4');

    const plan = planPaste(
      matrix,
      ['R1', 'R2'],
      ['C1', 'C2'],
      { rowIndex: 0, columnIndex: 0 },
      (_row, column) => (column === 'C2' ? 'Комірку рахує система.' : null),
    );

    expect(plan.targets).toEqual([]);
    expect(plan.rejected).toHaveLength(2);
    expect(plan.rejected[0]?.columnCode).toBe('C2');
    expect(plan.rejected[0]?.reason).toContain('рахує система');
  });

  it('копіювання у буфер дає формат, який приймає Excel', () => {
    const text = toClipboard([
      ['1', '2'],
      ['3', '4'],
    ]);

    expect(text).toBe('1\t2\n3\t4\n');

    // Завершальний перенос обов'язковий: без нього Excel вставляє останній
    // рядок у поточну комірку замість наступної.
    expect(text.endsWith('\n')).toBe(true);
  });

  it('буфер, більший за сітку, обрізається, а не створює рядків', () => {
    const matrix = parseClipboard('1\t2\t3\n4\t5\t6\n7\t8\t9');

    const plan = planPaste(matrix, ['R1', 'R2'], ['C1'], { rowIndex: 0, columnIndex: 0 }, () => null);

    expect(plan.targets).toHaveLength(2);
  });
});
