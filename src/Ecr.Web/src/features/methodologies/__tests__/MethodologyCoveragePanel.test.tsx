import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import type { MethodologyCoverageDto } from '../api';

/**
 * Панель покриття «виходи → колонки» (`BE-25`) — на відміну від
 * `RuleCoveragePanel` («рядки реальних даних × правила», `ФВ-13.9`), ця
 * відповідає на питання «куди лягає порахований вихід».
 *
 * Три твердження, кожне з яких при поломці робить екран тихо неправдивим:
 *
 *  1. вихід без активних прив'язок ПОЗНАЧЕНО «нікуди не пише» — інакше він
 *     виглядає як рядок, що просто ще не встиг наповнитися, хоча насправді
 *     перерахунок завершується успіхом і не пише туди НІКОЛИ (`D-69`);
 *  2. `waitingBindings` ПОКАЗАНО — активна прив'язка на вихід, якого версія
 *     не оголошує, лишиться порожньою назавжди, і мовчати про це означає
 *     ховати прогалину;
 *  3. дзеркало до (2): порожній `waitingBindings` не малює порожнього блоку
 *     (`D15-06`) — інакше кожен екран без прогалин показував би порожню
 *     смугу, і сама смуга переставала б щось значити.
 */

const Strings: Record<string, string> = {
  'methodologies.outputCoverage': 'Output coverage',
  'methodologies.outputCoverageHint':
    'Where each declared output of this version writes, and which columns wait for an output this version does not declare.',
  'methodologies.outputCoverageBindings': 'Bindings',
  'methodologies.outputCoverageNowhere': 'Writes nowhere',
  'methodologies.noOutputCoverage': 'No coverage to show',
  'methodologies.noOutputCoverageHint':
    'This version has no declared outputs and no bindings are waiting on it.',
  'methodologies.outputCoverageWaiting': 'Waiting bindings',
  'methodologies.outputCoverageWaitingHint':
    'These bindings are active but point at an output this version does not declare; they will stay empty.',
  'methodologies.outputCode': 'Output',
  'methodologies.tableDefId': 'Table',
  'methodologies.columnDefId': 'Column',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
  'common.retry': 'Retry',
  'common.loading': 'Loading',
};

function coverage(overrides: Partial<MethodologyCoverageDto>): MethodologyCoverageDto {
  return {
    methodologyVersionId: 42,
    outputs: [],
    waitingBindings: [],
    ...overrides,
  };
}

function mockApi(body: MethodologyCoverageDto): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }

      // ⚠ `/versions/{vid}/coverage`, а не голе `includes('/coverage')`: та
      // сама пастка, від якої застерігає коментар біля `RuleCoverageFixture`
      // в a11y-фікстурах — `/rule-coverage` теж закінчується на «coverage».
      if (/\/versions\/\d+\/coverage$/.test(url)) {
        return json(body);
      }

      throw new Error(`Немає мока для GET ${url}`);
    }),
  );
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { MethodologyCoveragePanel } = await import('../MethodologyCoveragePanel');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyCoveragePanel methodologyId={1} versionId={42} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function outputRow(code: string): HTMLElement | null {
  return document.querySelector(`[data-output-row="${code}"]`);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Покриття «виходи → колонки»: вихід без прив\'язок', () => {
  it('позначено «нікуди не пише», а сусідній вихід із прив\'язкою — ні', async () => {
    mockApi(
      coverage({
        outputs: [
          { code: 'CO2', bindings: [] },
          {
            code: 'NOX',
            bindings: [
              {
                id: 1,
                methodologyId: 1,
                tableDefId: 7,
                columnDefId: 101,
                outputCode: 'NOX',
                matchJson: '{}',
                isActive: true,
              },
            ],
          },
        ],
      }),
    );
    await show();

    await screen.findByText('CO2');

    // ⛔ Мутація «вихід без прив'язок показано без позначки» валить саме це.
    const gapRow = outputRow('CO2');
    expect(gapRow?.querySelector('[data-coverage-gap]')).not.toBeNull();
    expect(gapRow?.textContent).toContain('Writes nowhere');

    // Сусідній вихід лишається собою — інакше тест пройшов би й тоді, коли
    // позначку «нікуди не пише» малюють УСІМ виходам поспіль.
    const boundRow = outputRow('NOX');
    expect(boundRow?.querySelector('[data-coverage-gap]')).toBeNull();
    expect(boundRow?.textContent).toContain('7.101');
  });
});

describe('Покриття «виходи → колонки»: очікують', () => {
  it('активна прив\'язка на невідомий версії вихід показана в окремому блоці', async () => {
    mockApi(
      coverage({
        outputs: [{ code: 'CO2', bindings: [] }],
        waitingBindings: [
          {
            id: 9,
            methodologyId: 1,
            tableDefId: 3,
            columnDefId: 55,
            outputCode: 'RETIRED',
            matchJson: '{}',
            isActive: true,
          },
        ],
      }),
    );
    await show();

    await screen.findByText('CO2');

    // ⛔ Мутація «waitingBindings не показано» валить саме це.
    const waiting = screen.getByTestId('coverage-waiting');
    expect(waiting.textContent).toContain('RETIRED');
    expect(waiting.textContent).toContain('3');
    expect(waiting.textContent).toContain('55');
    expect(waiting.textContent).toContain('Waiting bindings');
  });

  it('дзеркало: порожній waitingBindings не малює блоку взагалі', async () => {
    mockApi(
      coverage({
        outputs: [
          {
            code: 'CO2',
            bindings: [
              {
                id: 1,
                methodologyId: 1,
                tableDefId: 7,
                columnDefId: 101,
                outputCode: 'CO2',
                matchJson: '{}',
                isActive: true,
              },
            ],
          },
        ],
        waitingBindings: [],
      }),
    );
    await show();

    await screen.findByText('CO2');

    // ⛔ Мутація «порожній waitingBindings малює порожній блок» валить саме це.
    expect(screen.queryByTestId('coverage-waiting')).toBeNull();
    expect(screen.queryByText('Waiting bindings')).toBeNull();
  });
});
