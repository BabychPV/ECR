import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import { checkRegistryKey, existingDuplicates } from '@/features/registries/keys/api';

/**
 * Споживач `POST /api/v1/registries/{code}/keys/check` і розбір відмови
 * `409 existingDuplicates` (RT-11, FEATURE-REGISTRY-TABLES §4.5).
 *
 * ⚠ Предмет — адреса, метод, тіло й форма відповіді, а не екран (екран — RT-34):
 * сторож маршрутів бачить літерал, але не бачить, ЩО відправлено.
 */

interface Attempt {
  url: string;
  method: string;
  body: unknown;
}

/** Замінює мережу і запам'ятовує запити. */
function mockFetch(body: unknown): Attempt[] {
  const attempts: Attempt[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      attempts.push({
        url: String(input),
        method: (init?.method ?? 'GET').toUpperCase(),
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
      });

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  return attempts;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Sample = [
  {
    keyText: 'North',
    entries: [
      { id: 11, code: 'A1' },
      { id: 12, code: 'B1' },
    ],
  },
];

describe('жива перевірка дублікатів ключа', () => {
  it('шле поля ключа методом POST на адресу цього довідника', async () => {
    const attempts = mockFetch({ checked: 4, groups: 1, sample: Sample });

    const report = await checkRegistryKey('GAS/COMP', { fieldCodes: ['CASE', 'COMPONENT'], ignoreCase: true });

    expect(attempts).toHaveLength(1);
    expect(attempts[0]!.method).toBe('POST');
    // ⛔ Код довідника закодований: «/» інакше звернувся б до чужої адреси.
    expect(attempts[0]!.url).toBe('/api/v1/registries/GAS%2FCOMP/keys/check');
    expect(attempts[0]!.body).toEqual({ fieldCodes: ['CASE', 'COMPONENT'], ignoreCase: true });

    // ⚠ `groups` і довжина `sample` — різні числа: приклади обрізані двадцятьма.
    expect(report.checked).toBe(4);
    expect(report.groups).toBe(1);
    expect(report.sample[0]!.entries.map((e) => e.code)).toEqual(['A1', 'B1']);
  });
});

describe('приклади з відмови публікації ключа', () => {
  function conflict(messageKey: string, sample?: unknown): EcrApiError {
    return new EcrApiError({
      title: 'Key already in use',
      status: 409,
      errorCode: 'ECR-REG-4092',
      correlationId: 'c-1',
      extensions2: { messageKey, ...(sample === undefined ? {} : { sample }) },
    });
  }

  it('дістає приклади з existingDuplicates', () => {
    const duplicates = existingDuplicates(conflict('err.ECR-REG-4092.existingDuplicates', Sample));

    expect(duplicates).not.toBeNull();
    expect(duplicates![0]!.keyText).toBe('North');
    expect(duplicates![0]!.entries).toHaveLength(2);
  });

  it('не вигадує прикладів для іншого випадку того самого коду', () => {
    // `keyTaken` — конфлікт одного запису, а не дублікати на даних.
    expect(existingDuplicates(conflict('err.ECR-REG-4092.keyTaken'))).toBeNull();
  });

  it('не приймає чужу відмову чи звичайну помилку за дублікати', () => {
    const other = new EcrApiError({
      title: 'Invalid',
      status: 422,
      errorCode: 'ECR-REG-0422',
      correlationId: 'c-2',
      extensions2: { messageKey: 'err.ECR-REG-4092.existingDuplicates', sample: Sample },
    });

    expect(existingDuplicates(other)).toBeNull();
    expect(existingDuplicates(new Error('boom'))).toBeNull();
  });
});
