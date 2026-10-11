import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import { loadCatalog } from '@/shared/i18n';
import { addRowDenialText } from '../useAddRow';

/**
 * D9 (приймальна Land 12.10): «Add row» від viewer давала «…: InsufficientGrantLevel» — сирий код.
 * Причина береться з каталогу за тим самим `deny.<Причина>`, що й підказка сірої комірки.
 *
 * ⛔ Мутаційний доказ: поверни `reason` без `denyText` — падає «причина — текст каталогу».
 */
const Strings = {
  'err.ECR-ACCS-0403.addRowDenied': 'В таблицу нельзя добавить строку: {reason}.',
  'deny.InsufficientGrantLevel': 'Недостаточный уровень доступа.',
};

beforeAll(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'ru', revision: 1, strings: Strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"public-ru-1"' },
        }),
      ),
    ),
  );
  await loadCatalog('ru', 'public');
});

afterAll(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

function denied(extra: Record<string, unknown>, errorCode = 'ECR-ACCS-0403'): EcrApiError {
  return new EcrApiError({
    title: 'Forbidden',
    status: 403,
    errorCode,
    correlationId: 'c-1',
    extensions2: { messageKey: 'err.ECR-ACCS-0403.addRowDenied', ...extra },
  });
}

describe('addRowDenialText', () => {
  it('причина — текст каталогу, не сирий код', () => {
    const text = addRowDenialText(denied({ reason: 'InsufficientGrantLevel' }));

    expect(text).toBe('В таблицу нельзя добавить строку: Недостаточный уровень доступа.');
    expect(text).not.toContain('InsufficientGrantLevel');
  });

  it('невідома причина, інший код чи не EcrApiError — null (показує showApiError)', () => {
    expect(addRowDenialText(denied({ reason: 'Unheard' }))).toBeNull();
    expect(addRowDenialText(denied({ reason: 'InsufficientGrantLevel' }, 'ECR-ROW-0409'))).toBeNull();
    expect(addRowDenialText(new Error('boom'))).toBeNull();
  });
});
