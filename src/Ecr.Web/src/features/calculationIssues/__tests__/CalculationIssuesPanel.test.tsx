import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import { TestProviders } from '@/test/render';
import { CalculationIssuesPanel } from '../CalculationIssuesPanel';
import { issue, table } from './fixtures';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

/**
 * Панель «рядки без правила методології» (AN-69): список з назвою таблиці, номером рядка й кодом методології;
 * порожньо - нічого; `404` - нічого й без помилки; інша відмова - `ErrorAlert`; клік вибирає таблицю в адресі.
 *
 * Мутаційний доказ (виконано, відновлено): прибрати `issues.length === 0 -> null` валить «порожньо»; прибрати
 * гілку `isNotFound` валить «404»; прибрати `setTableKey(…)` валить «клік вибирає таблицю»; прибрати відсікання
 * за таблицями аркуша в `issuesOfSheet` валить «чужі таблиці не рахуються».
 */
const tables = [table(1, 'Mass emissions'), table(2, 'Fuel use')];

function Probe(): JSX.Element {
  return <output data-testid="search">{useLocation().search}</output>;
}

function mount(sheetCopyId: number | null = null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/documents/55?sheet=S1']}>
          <CalculationIssuesPanel documentId={55} periodKey={202607} sheetCopyId={sheetCopyId} tables={tables} />
          <Probe />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
}

/** Дає запиту й рендеру осісти: перевірка «нічого немає» без цього йде раніше, ніж могло б щось з'явитися. */
const settle = (): Promise<void> =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

const notFound = () =>
  new EcrApiError({ title: 'Not found', status: 404, errorCode: 'ECR-DOC-4040', correlationId: 'c1' });

beforeEach(() => {
  vi.mocked(apiFetch).mockResolvedValue({ items: [] });
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('CalculationIssuesPanel', () => {
  it('показує кількість і перелік: таблиця, рядок, методологія', async () => {
    vi.mocked(apiFetch).mockResolvedValue({
      items: [issue(1, 3), issue(2, 1, { methodologyCode: null })],
    });
    mount();

    expect(await screen.findByTestId('calculation-issues-title')).toHaveProperty(
      'textContent',
      '⟦calculationIssues.title (count=2)⟧',
    );

    fireEvent.click(screen.getByTestId('calculation-issues-toggle'));

    const items = screen.getAllByTestId('calculation-issue-item').map((item) => item.textContent);
    expect(items).toEqual([
      '⟦calculationIssues.item (table=Mass emissions, row=3, methodology=M1)⟧',
      '⟦calculationIssues.item (table=Fuel use, row=1, methodology=⟦calculationIssues.methodologyUnknown⟧)⟧',
    ]);
  });

  it('порожньо - панелі немає', async () => {
    mount();

    await waitFor(() => {
      expect(apiFetch).toHaveBeenCalled();
    });
    await settle();

    expect(screen.queryByTestId('calculation-issues-panel')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('чужі таблиці й чужа копія не рахуються', async () => {
    vi.mocked(apiFetch).mockResolvedValue({
      items: [issue(1, 1), issue(9, 1), issue(2, 4, { sheetCopyId: 77 })],
    });
    mount();

    expect(await screen.findByTestId('calculation-issues-title')).toHaveProperty(
      'textContent',
      '⟦calculationIssues.title (count=1)⟧',
    );
  });

  it('404 - ні панелі, ні помилки', async () => {
    vi.mocked(apiFetch).mockRejectedValue(notFound());
    mount();

    await waitFor(() => {
      expect(apiFetch).toHaveBeenCalledTimes(1);
    });
    await settle();

    expect(screen.queryByTestId('calculation-issues-panel')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('інша відмова - стандартна помилка', async () => {
    vi.mocked(apiFetch).mockRejectedValue(
      new EcrApiError({ title: 'Boom', status: 500, errorCode: 'ECR-SYS-5000', correlationId: 'c2' }),
    );
    mount();

    expect(await screen.findByRole('alert')).toBeDefined();
  });

  it('клік вибирає таблицю в адресі', async () => {
    vi.mocked(apiFetch).mockResolvedValue({ items: [issue(2, 5)] });
    mount();

    await screen.findByTestId('calculation-issues-title');
    fireEvent.click(screen.getByTestId('calculation-issues-toggle'));
    fireEvent.click(await screen.findByRole('button', { name: /Fuel use/ }));

    expect(screen.getByTestId('search').textContent).toContain('table=T2');
  });

  it('копія аркуша: запит із sheetCopyId', async () => {
    mount(9);

    await waitFor(() => {
      expect(apiFetch).toHaveBeenCalledWith('/api/v1/documents/55/calculation-issues?periodKey=202607&sheetCopyId=9');
    });
  });
});
