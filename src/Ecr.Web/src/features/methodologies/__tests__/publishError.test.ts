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
  // HSE301 L — тексти дослівно із секції `-- HSE301:L` у 09-seed.sql.
  'publish.problem.formulaNotFound':
    'Formula {formula}: reference !{name} at position {position} does not match a formula of this version or of any imported methodology, so it would always give #REF.',
  'publish.problem.importCycle':
    'Imported methodologies form a cycle: {chain}. A methodology cannot use its own formulas through its imports; the calculation would give #CYCLE.',
  'publish.problem.importModeMismatch':
    'Imported methodology {library} calculates in {libraryNumeric}/{libraryCalendar} mode, but this version uses {numeric}/{calendar}: its formulas would give different numbers here than in the library itself.',
  'publish.problem.rowScopeReferencesLibrarySubstance':
    'Formula {formula} is calculated once per row, but !{name} of imported methodology {library} has a value only for a substance. Make {formula} a per-substance formula or remove the reference.',
};

async function loadStrings(): Promise<void> {
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
}

function publishFailure(problems: unknown[]): EcrApiError {
  return new EcrApiError({
    title: 'Unprocessable',
    status: 422,
    errorCode: 'ECR-CALC-0422',
    extensions2: { problems },
  } as never);
}

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

  /**
   * HSE301 L: чотири нові пункти замикання бібліотечних формул доїжджають
   * текстом із підстановками, а не зникають у «N problems».
   * Мутація: прибрати ключ із `PublishProblemKeys` — його рядок червоний.
   */
  it.each([
    [
      'publish.problem.formulaNotFound',
      { formula: 'Total', name: 'Missing', position: '7' },
      'Formula Total: reference !Missing at position 7 does not match a formula of this version or of any imported methodology, so it would always give #REF.',
    ],
    [
      'publish.problem.importCycle',
      { chain: 'A → LIB → A' },
      'Imported methodologies form a cycle: A → LIB → A. A methodology cannot use its own formulas through its imports; the calculation would give #CYCLE.',
    ],
    [
      'publish.problem.importModeMismatch',
      { library: 'LIB', libraryNumeric: 'Decimal', libraryCalendar: 'Gregorian', numeric: 'Double', calendar: 'Fiscal' },
      'Imported methodology LIB calculates in Decimal/Gregorian mode, but this version uses Double/Fiscal: its formulas would give different numbers here than in the library itself.',
    ],
    [
      'publish.problem.rowScopeReferencesLibrarySubstance',
      { formula: 'RowSum', name: 'Mass', library: 'LIB' },
      'Formula RowSum is calculated once per row, but !Mass of imported methodology LIB has a value only for a substance. Make RowSum a per-substance formula or remove the reference.',
    ],
  ])('%s — текст із підстановками', async (messageKey, args, expected) => {
    await loadStrings();

    expect(publishProblemLines(publishFailure([{ messageKey, args, text: 'укр' }]))).toEqual([expected]);
  });

  it('прибраний сервером importedFormulaNotEvaluated не показується', async () => {
    await loadStrings();

    expect(
      publishProblemLines(
        publishFailure([
          { messageKey: 'publish.problem.importedFormulaNotEvaluated', args: { formula: 'F' }, text: 'укр' },
        ]),
      ),
    ).toEqual([]);
  });

  it('відмова без переліку — порожньо (тост показує звичайну відмову)', () => {
    expect(publishProblemLines(new Error('boom'))).toEqual([]);
  });
});
