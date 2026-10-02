import { describe, expect, it } from 'vitest';
import { safeReturnPath } from '@/shared/safeReturnPath';

describe('safeReturnPath', () => {
  it('внутрішній шлях лишається', () => {
    expect(safeReturnPath('/documents/5')).toBe('/documents/5');
  });

  it.each([null, '', 'https://evil.example', '//evil.example', '/\\evil.example'])(
    'небезпечне значення %s → «/»',
    (from) => {
      expect(safeReturnPath(from)).toBe('/');
    },
  );

  // T1-15 (а): після зміни пароля повертатись на сторінку зміни пароля безглуздо.
  it.each(['/change-password', '/change-password?x=1', '/change-password#top'])(
    'T1-15: %s → головна',
    (from) => {
      expect(safeReturnPath(from)).toBe('/');
    },
  );

  it('схожий, але інший шлях не чіпається', () => {
    expect(safeReturnPath('/change-password-help')).toBe('/change-password-help');
  });
});
