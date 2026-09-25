import { afterEach, describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import { loadCatalog } from '@/shared/i18n';
import { publishProblemLines } from '../publishError';

/**
 * F-15/B-12 (четвертий раунд UX): перелік проблем публікації доїжджає до
 * людини мовою інтерфейсу, пункт за пунктом, з підстановками.
 *
 * ⛔ Доти тост казав лише «The version failed pre-publication checks
 * ({count} problems)» — без числа й без жодної назви формули чи константи.
 */

const Strings: Record<string, string> = {
  'publish.problem.numberReturnsText': 'Formula {formula} is declared numeric but returns only text.',
  'publish.problem.libraryHasRules': 'Methodology {code} is a library but has {count} active rules.',
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('publishProblemLines', () => {
  /** Мутація: повертати порожній перелік або `messageKey` без `t()` — червоне. */
  it('перекладає кожен відомий пункт із підстановками й пропускає невідомий', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: Strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const error = new EcrApiError({
      title: 'Unprocessable',
      status: 422,
      errorCode: 'ECR-CALC-0422',
      extensions2: {
        problems: [
          { messageKey: 'publish.problem.numberReturnsText', args: { formula: 'Verdict' }, text: 'укр' },
          { messageKey: 'publish.problem.libraryHasRules', args: { code: 'LIB', count: '2' }, text: 'укр' },
          { messageKey: 'publish.problem.fromTheFuture', args: {}, text: 'укр' },
        ],
      },
    } as never);

    expect(publishProblemLines(error)).toEqual([
      'Formula Verdict is declared numeric but returns only text.',
      'Methodology LIB is a library but has 2 active rules.',
    ]);
  });

  it('відмова без переліку — порожньо (тост показує звичайну відмову)', () => {
    expect(publishProblemLines(new Error('boom'))).toEqual([]);
  });
});
