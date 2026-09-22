import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10.
 *
 * ⛔ Одиниця бралася як `(units.data ?? []).find(…)?.code ?? result.unitId`,
 * тож при відмові `GET /api/v1/units` у колонці одиниці друкувалося ГОЛЕ
 * ЧИСЛО — ідентифікатор поруч із порахованим значенням. Це гірше за порожню
 * колонку: «7» читається як одиниця, і звірку показника роблять не в тій
 * розмірності. Панель існує саме для того, щоб ДОВЕСТИ, що методологія
 * порахувала те саме, — двозначність тут коштує найдорожче.
 */

const Results = [
  {
    sourceRowKey: 'R1',
    outputCode: 'EMIS',
    value: 1234.5,
    unitId: 7,
    substanceEntryId: 3,
    methodologyVersionId: 11,
  },
];

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'довідник одиниць прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-units-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(unitsFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/units')) {
        return unitsFail ? json(Refusal, 500) : json([{ id: 7, code: 'kg', nameL10n: { values: {} } }]);
      }

      if (path.endsWith('/calculation-results')) {
        return json(Results);
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CalculationResultsPanel documentId={1} periodKey={202401} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CalculationResultsPanel: відмова довідника одиниць ≠ «одиниця 7»', () => {
  it('довідник не приїхав — причина з кодом, а ідентифікатор показано ЯК ідентифікатор', async () => {
    mockServer(true);
    show();

    const alert = await waitFor(() => screen.getByRole('alert'), { timeout: 30_000 });

    expect(alert.textContent ?? '').toContain('довідник одиниць прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    /*
     * ⛔ Головне твердження картки, і воно саме про ФОРМУ показу: число 7 і
     * далі на екрані (ховати його означало б ховати частину доказу), але воно
     * в `<code>`, тобто позначене як ідентифікатор, а не як позначка одиниці.
     * Перевіряємо рівно це — а не наявність тексту «7», яка була б зеленою й
     * до фіксу.
     */
    const row = screen.getByRole('row', { name: /EMIS/ });
    const identifier = within(row).getByText('7');

    expect(identifier.tagName.toLowerCase()).toBe('code');
  });

  it('довідник приїхав — у колонці код одиниці, банера немає', async () => {
    mockServer(false);
    show();

    // ⚠ Дзеркало: спершу дочекатися самого коду одиниці — інакше твердження
    // «банера немає» зелене на будь-якому коді.
    await screen.findByText('kg');

    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText('7')).toBeNull();
  });
});
