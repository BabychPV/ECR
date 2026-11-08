import { describe, expect, it } from 'vitest';
import type { ValidationResultResponse } from '@/api/types';
import { validationCheckedAt } from '../validationCheckedAt';

/**
 * ✎ F13-p209 (d4): порожній Issues має казати, КОЛИ була остання перевірка — і для збереженого результату
 * (`GET …/validation`), а не лише для прогону в цій вкладці.
 * ⛔ Червоне без фіксу: збережений результат давав `null` (сторінка брала лише момент свіжого прогону).
 */
function result(over: Partial<ValidationResultResponse>): ValidationResultResponse {
  return { documentId: 2, periodKey: 202601, messages: [], validated: true, validatedAt: null, ...over };
}

describe('validationCheckedAt', () => {
  it('Water lock: збережений результат — момент із сервера', () => {
    const at = validationCheckedAt(null, result({ validatedAt: '2026-11-07T09:15:00Z' }));

    expect(at?.toISOString()).toBe('2026-11-07T09:15:00.000Z');
  });

  it('свіжий прогін — момент сервера, інакше момент завершення запиту', () => {
    const local = new Date('2026-11-08T10:00:00Z');

    expect(validationCheckedAt({ result: result({ validatedAt: '2026-11-08T09:59:59Z' }), at: local }, undefined)?.toISOString()).toBe(
      '2026-11-08T09:59:59.000Z',
    );
    expect(validationCheckedAt({ result: result({ validatedAt: null }), at: local }, undefined)).toBe(local);
  });

  it('не перевіряли — моменту немає', () => {
    expect(validationCheckedAt(null, result({ validated: false, validatedAt: null }))).toBeNull();
    expect(validationCheckedAt(null, undefined)).toBeNull();
  });
});
