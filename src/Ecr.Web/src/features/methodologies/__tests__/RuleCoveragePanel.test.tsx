import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import type { RuleCoverageDto } from '../ruleCoverage';

/**
 * Матриця покриття «рядки реальних даних × правила» (`ФВ-13.4`, `ФВ-13.9`).
 *
 * ⛔ Тут перевіряється не «панель намалювалася», а чотири твердження, кожне з
 * яких при поломці перетворює екран на тихо неправдивий:
 *
 *  1. конфлікт НЕ виглядає як покрито — інакше два правила з однаковим
 *     пріоритетом читаються як «усе гаразд», і рядок рахується непередбачувано;
 *  2. усічення НАЗВАНО — стеля 5000, і розрив може бути саме за нею;
 *  3. порожнє вікно (`from > to`) НЕ йде мережею і називає причину — інакше
 *     людина бачить порожню матрицю замість пояснення;
 *  4. переможне правило ВИДІЛЕНО серед збіжних — перелік кодів без переможця
 *     відповідає на інше питання, ніж те, заради якого екран існує.
 *
 * ⚠ П'ятий тест — дзеркало: коли все покрито, нічого зайвого не домальовується.
 * Без нього три перші можна задовольнити, малюючи попередження завжди.
 */

const Strings: Record<string, string> = {
  'methodologies.ruleCoverage': 'Rule coverage',
  'methodologies.ruleCoverageHint': 'Which rows of real data each rule claims.',
  'methodologies.ruleCoverageTable': 'Table',
  'methodologies.ruleCoverageAllTables': 'All tables',
  'methodologies.ruleCoverageTableOption': 'Table {id}',
  'methodologies.ruleCoveragePeriodFrom': 'Period from',
  'methodologies.ruleCoveragePeriodTo': 'Period to',
  'methodologies.ruleCoveragePeriodHint': 'Period key as YYYYMM, for example 202601.',
  'methodologies.ruleCoverageValues': 'Values',
  'methodologies.ruleCoverageState': 'State',
  'methodologies.ruleCoverageRules': 'Rules',
  'methodologies.ruleCoverageRows': 'Rows',
  'methodologies.ruleCoverageDocuments': 'Documents',
  'methodologies.ruleCoverageCovered': 'Covered',
  'methodologies.ruleCoverageGap': 'No rule',
  'methodologies.ruleCoverageConflict': 'Two rules, same priority',
  'methodologies.ruleCoverageShadowed': 'shadowed by priority',
  'methodologies.ruleCoverageNoCell': 'no cell',
  'methodologies.ruleCoverageTruncatedTitle': 'Not everything is shown',
  'methodologies.ruleCoverageTruncatedHint':
    'The server stopped at {shown} combinations; a gap may be outside them.',
  'methodologies.noRuleCoverage': 'No rows match this window',
  'methodologies.noRuleCoverageHint': 'Widen the period window or pick another table.',
  'err.ECR-CALC-0422.coverageWindow':
    'The period window is empty: periodFrom {periodFrom} is after periodTo {periodTo}.',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
  'common.loading': 'Loading',
};

/** Вікно за замовчуванням — те саме, що рахує `defaultRuleCoverageWindow`. */
const Year = new Date().getFullYear();
const DefaultFrom = (Year - 1) * 100 + 1;
const DefaultTo = Year * 100 + 99;

function matrix(overrides: Partial<RuleCoverageDto>): RuleCoverageDto {
  return {
    methodologyVersionId: 42,
    periodFrom: DefaultFrom,
    periodTo: DefaultTo,
    tableDefIds: [7],
    columnDefIds: [101],
    rules: [
      { code: 'CO2_A', priority: 10 },
      { code: 'NOX', priority: 10 },
    ],
    combinations: [],
    truncated: false,
    ...overrides,
  };
}

/** Три стани контракту в серверному порядку: розрив → конфлікт → покрито. */
const ThreeStates = matrix({
  combinations: [
    {
      values: ['SO2'],
      state: 'Gap',
      winnerRuleCode: null,
      matchedRuleCodes: [],
      rows: 1,
      documents: 1,
    },
    {
      values: ['CO2'],
      state: 'Conflict',
      winnerRuleCode: 'CO2_A',
      matchedRuleCodes: ['CO2_A', 'CO2_B'],
      rows: 3,
      documents: 2,
    },
    {
      values: ['NOX'],
      state: 'Covered',
      winnerRuleCode: 'NOX',
      matchedRuleCodes: ['NOX'],
      rows: 1,
      documents: 1,
    },
  ],
});

function mockApi(body: RuleCoverageDto): { calls: string[] } {
  const calls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      if (url.includes('/rule-coverage') && method === 'GET') {
        calls.push(url);

        return json(body);
      }

      if (url.endsWith('/api/v1/methodologies/1/bindings') && method === 'GET') {
        return json([
          {
            id: 1,
            methodologyId: 1,
            tableDefId: 7,
            columnDefId: 101,
            outputCode: 'CO2',
            matchJson: '{}',
            isActive: true,
          },
        ]);
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { calls };
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { MethodologyRuleCoveragePanel } = await import('../RuleCoveragePanel');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyRuleCoveragePanel methodologyId={1} versionId={42} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Рядок таблиці за комбінацією значень.
 *
 * ⚠ Не `getByText(value)`: код правила буває тим самим рядком, що й значення
 * комірки (`NOX` і `NOX`), і пошук за текстом знаходив би два елементи.
 */
function rowOf(values: string): HTMLElement | null {
  return document.querySelector(`[data-combination="${values}"]`);
}

/**
 * ПІДПИС стану комбінації — те, що бачить людина.
 *
 * ⛔ Навмисно не `data-coverage-state`: той атрибут переписує поле відповіді
 * дослівно, тож переплутані ПІДПИСИ («конфлікт» намальовано як «покрито») він
 * не помітив би взагалі — а саме цього й треба не допустити.
 */
function stateOf(values: string): string | null {
  return rowOf(values)?.querySelector('[data-coverage-state]')?.textContent ?? null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Матриця покриття правил: стани комбінацій', () => {
  it('конфлікт названо конфліктом, а не покриттям', async () => {
    mockApi(ThreeStates);
    await show();

    expect(await screen.findByText('CO2')).toBeTruthy();

    // ⛔ Мутація «`Conflict` показано як `Covered`» валить саме це.
    expect(stateOf('CO2')).toBe('Two rules, same priority');

    // Сусідні стани лишаються собою — інакше тест пройшов би й на «усе конфлікт».
    expect(stateOf('SO2')).toBe('No rule');
    expect(stateOf('NOX')).toBe('Covered');
  }, 60000);

  it('переможне правило виділено серед збіжних', async () => {
    mockApi(ThreeStates);
    await show();

    await screen.findByText('Two rules, same priority');
    const row = rowOf('CO2');
    expect(row).not.toBeNull();

    // ⛔ Мутація «показано лише `matchedRuleCodes` без виділення переможця»
    // валить саме це: обидва коди в рядку є, але переможець рівно один.
    const winners = row?.querySelectorAll('[data-winner-rule]') ?? [];
    expect(winners).toHaveLength(1);
    expect(winners[0]?.getAttribute('data-winner-rule')).toBe('CO2_A');

    const others = row?.querySelector('[data-other-rules]')?.getAttribute('data-other-rules');
    expect(others).toBe('CO2_B');
  }, 60000);

  it('затінене правило показано приглушено і лише в покритій комбінації', async () => {
    mockApi(
      matrix({
        combinations: [
          {
            values: ['NOX'],
            state: 'Covered',
            winnerRuleCode: 'NOX',
            matchedRuleCodes: ['NOX', 'NOX_OLD'],
            rows: 2,
            documents: 1,
          },
        ],
      }),
    );
    await show();

    await screen.findByText(/shadowed by priority/);
    const row = rowOf('NOX');

    expect(row?.querySelector('[data-winner-rule]')?.getAttribute('data-winner-rule')).toBe('NOX');
    expect(row?.querySelector('[data-other-rules]')?.getAttribute('data-other-rules')).toBe(
      'NOX_OLD',
    );

    // ⚠ Затінене — НЕ конфлікт: приписка пояснює це словами, а стан лишається `Covered`.
    expect(row?.textContent).toContain('shadowed by priority');
    expect(stateOf('NOX')).toBe('Covered');
    expect(screen.queryByText('Two rules, same priority')).toBeNull();
  }, 60000);
});

describe('Матриця покриття правил: усічення', () => {
  it('усічену відповідь названо смугою', async () => {
    mockApi(matrix({ ...ThreeStates, truncated: true }));
    await show();

    // ⛔ Мутація «`truncated: true` не показано» валить саме це.
    const banner = await screen.findByTestId('rule-coverage-truncated');
    expect(banner.textContent).toContain('Not everything is shown');
    expect(banner.textContent).toContain('3');
  }, 60000);
});

describe('Матриця покриття правил: порожнє вікно періодів', () => {
  it('нижня межа вища за верхню — запиту немає, причина названа', async () => {
    const { calls } = mockApi(ThreeStates);
    await show();

    await screen.findByText('CO2');
    expect(calls).toHaveLength(1);

    fireEvent.change(screen.getByLabelText('Period to'), {
      target: { value: String(DefaultFrom - 1) },
    });

    // ⛔ Мутація «не показує причину» валить цей рядок…
    const banner = await screen.findByTestId('rule-coverage-window');
    expect(banner.textContent).toContain(
      `The period window is empty: periodFrom ${String(DefaultFrom)} is after periodTo ${String(
        DefaultFrom - 1,
      )}.`,
    );

    // …а мутація «не блокує запит» — цей: жодного нового звернення не сталося.
    await waitFor(() => {
      expect(calls).toHaveLength(1);
    });

    // Матриці на екрані немає: показано причину, а не порожній стан (`L10`).
    expect(screen.queryByText('Two rules, same priority')).toBeNull();
    expect(screen.queryByText('No rows match this window')).toBeNull();
  }, 60000);
});

describe('Матриця покриття правил: дзеркало', () => {
  it('усе покрито, без затінених і без усічення — жодних додаткових позначок', async () => {
    mockApi(
      matrix({
        combinations: [
          {
            values: ['CO2'],
            state: 'Covered',
            winnerRuleCode: 'CO2_A',
            matchedRuleCodes: ['CO2_A'],
            rows: 4,
            documents: 2,
          },
          {
            values: ['NOX'],
            state: 'Covered',
            winnerRuleCode: 'NOX',
            matchedRuleCodes: ['NOX'],
            rows: 1,
            documents: 1,
          },
        ],
      }),
    );
    await show();

    await screen.findByText('CO2');

    expect(screen.queryByTestId('rule-coverage-truncated')).toBeNull();
    expect(screen.queryByTestId('rule-coverage-window')).toBeNull();
    expect(screen.queryByText('No rule')).toBeNull();
    expect(screen.queryByText('Two rules, same priority')).toBeNull();
    expect(screen.queryByText(/shadowed by priority/)).toBeNull();
    expect(document.querySelectorAll('[data-other-rules]')).toHaveLength(0);
    expect(document.querySelectorAll('[data-coverage-state="Covered"]')).toHaveLength(2);
  }, 60000);
});
