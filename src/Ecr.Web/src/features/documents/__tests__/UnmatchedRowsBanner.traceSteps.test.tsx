import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import { UnmatchedRowsBanner } from '@/features/documents/UnmatchedRowsBanner';
import type { UnmatchedRows } from '@/features/documents/useUnmatchedRows';
import { testTheme } from '@/test/render';

/**
 * ✎ D-565 (PERF-03; вимога людини: «коли помилка — система допомагає знайти, де проблема»): проблема рядка `StepError`.
 *
 * - прогін НЕ записав кроки методології (трасування вимкнено) — група каже це, називає методологію й веде до її прапора;
 *   кнопки «крок» немає (відкривати нічого);
 * - прогін записав кроки — рядок має «показати крок», що відкриває крок, який упав, з виразом і входами
 *   (`GET …/calculation-steps`).
 *
 * ⛔ Мутації: не показувати пояснення при `NotRecorded` — червоний перший; показувати кнопку кроку без факту «записано» —
 * червоний перший; не передати `tableInstanceId`/`rowKey` у запит — червоний другий; показати всі кроки замість тих, що
 * впали, — червоний другий (крок `X` з'явився б).
 */

const urls: string[] = [];

function rows(issues: UnmatchedRows['issues']): UnmatchedRows {
  return { issues, hints: [], finishedAt: null, formulaErrors: [] };
}

function show(value: UnmatchedRows): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UnmatchedRowsBanner
          userId={7}
          rows={value}
          tables={[table(1, '9.01 Unit 400')]}
          onOpenRow={vi.fn()}
          documentId={55}
          periodKey={202609}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  urls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));

      return new Response(
        JSON.stringify({
          runId: 900,
          recordedMethodologyIds: [41],
          notRecordedMethodologyIds: [],
          truncated: false,
          steps: [
            {
              id: 1, stepOrder: 1, stepCode: 'X', expression: '@A + 1', value: '3', error: null, failed: false,
              masked: null, substanceEntryId: null, inputs: [],
            },
            {
              id: 2, stepOrder: 2, stepCode: 'Y', expression: 'EF[@Fuel] * @A', value: null, error: '#N/A', failed: true,
              masked: null, substanceEntryId: null,
              inputs: [
                { kind: 'arg', code: 'Fuel', value: 'oil', unit: null },
                { kind: 'arg', code: 'A', value: '7', unit: 't' },
              ],
            },
          ],
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('UnmatchedRowsBanner — StepError і трасування (D-565)', () => {
  it('трасування вимкнено: пояснення з методологією й переходом до її прапора, кнопки кроку немає', async () => {
    show(
      rows([
        issue(1, 6, {
          rowKey: 'R6', kind: 'StepError', stepCode: 'Y', errorCode: '#N/A', affectedCount: 1,
          methodologyCode: 'HSE301', methodologyId: 42, traceSteps: 'NotRecorded',
        }),
      ]),
    );
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));

    expect(screen.getByTestId('document-step-error-trace-off').textContent).toContain(
      '⟦document.stepErrorRows.traceOff (methodologies=HSE301)⟧',
    );
    const link = screen.getByTestId('document-step-error-trace-link');
    expect(link.getAttribute('href')).toBe('/admin/methodologies/42/versions');
    expect(screen.queryByTestId('document-step-error-show-step')).toBeNull();
  });

  it('кроки записано: «показати крок» відкриває крок, що впав, з виразом і входами', async () => {
    show(
      rows([
        issue(1, 6, {
          rowKey: 'R6', kind: 'StepError', stepCode: 'Y', errorCode: '#N/A', affectedCount: 1,
          methodologyCode: 'HSE301', methodologyId: 41, traceSteps: 'Recorded',
        }),
      ]),
    );
    await userEvent.click(screen.getByTestId('document-unmatched-rows-toggle'));
    expect(screen.queryByTestId('document-step-error-trace-off')).toBeNull();

    await userEvent.click(screen.getByTestId('document-step-error-show-step'));

    await waitFor(() => expect(screen.getAllByTestId('step-trace-step')).toHaveLength(1));
    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain('/api/v1/documents/55/calculation-steps?');
    expect(urls[0]).toContain('periodKey=202609');
    expect(urls[0]).toContain('tableInstanceId=101');
    expect(urls[0]).toContain('rowKey=R6');

    const step = screen.getByTestId('step-trace-step');
    expect(step.textContent).toContain('Y');
    expect(step.textContent).toContain('#N/A');
    expect(screen.getByTestId('step-trace-expression').textContent).toBe('EF[@Fuel] * @A');
    const inputs = screen.getByTestId('step-trace-inputs').textContent ?? '';
    expect(inputs).toContain('Fuel');
    expect(inputs).toContain('oil');
    expect(step.textContent).not.toContain('@A + 1');
  });
});
