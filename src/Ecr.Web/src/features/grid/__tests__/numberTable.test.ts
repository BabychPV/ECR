import { describe, expect, it } from 'vitest';
import { readNumber } from '../clipboard';

/**
 * T2-10: та сама ТАБЛИЦЯ, що й на сервері (`CultureNumberTableTests`): клієнтська вставка читає рядок так само.
 * ⚠ Єдина розбіжність із сервером — змішані роздільники: клієнт бере останній за десятковий (1234.5), а сервер
 * суворіший (2026-09-29: відмова). Так було ДО T2-10 і не змінилось; клієнт шле канонічне 1234.5.
 * Це поведінка до T2-10 — вона не змінилась (змінилися лише `.5`/`-.5` і `1 2`).
 * en: кома — розряди; ru/kk: кома — десятковий, розряди — пробіл.
 */
type Row = [locale: string, text: string, expected: { kind: 'number'; text: string } | { kind: 'text' } | { kind: 'ambiguous' }];

const table: Row[] = [
  ['en', '1 234,5', { kind: 'number', text: '1234.5' }],
  ['ru', '1 234,5', { kind: 'number', text: '1234.5' }],
  ['kk', '1 234,5', { kind: 'number', text: '1234.5' }],

  ['en', '1,234.5', { kind: 'number', text: '1234.5' }],
  ['ru', '1,234.5', { kind: 'number', text: '1234.5' }], // сервер суворіший: відмова
  ['kk', '1,234.5', { kind: 'number', text: '1234.5' }], // сервер суворіший: відмова

  ['en', '1.234,5', { kind: 'number', text: '1234.5' }], // сервер суворіший: відмова
  ['ru', '1.234,5', { kind: 'number', text: '1234.5' }], // сервер суворіший: відмова
  ['kk', '1.234,5', { kind: 'number', text: '1234.5' }], // сервер суворіший: відмова

  ['en', '1.234', { kind: 'number', text: '1.234' }],
  ['ru', '1.234', { kind: 'number', text: '1.234' }],
  ['kk', '1.234', { kind: 'number', text: '1.234' }],

  ['en', '1,234', { kind: 'ambiguous' }],
  ['ru', '1,234', { kind: 'number', text: '1.234' }],
  ['kk', '1,234', { kind: 'number', text: '1.234' }],
];

describe('readNumber: таблиця за мовою (клієнт = сервер)', () => {
  it.each(table)('%s «%s»', (locale, text, expected) => {
    const reading = readNumber(text, locale);

    expect(reading.kind).toBe(expected.kind);
    if (expected.kind === 'number' && reading.kind === 'number') {
      expect(reading.text).toBe(expected.text);
    }
  });
});
