import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyTraceStepsPanel } from '@/features/methodologies/TraceStepsPanel';
import { testTheme } from '@/test/render';

/**
 * `D-565` (PERF-03): прапор «трасувати проміжні кроки» — вимкнено за замовчуванням, і екран це КАЖЕ (стан видно навіть у
 * згорнутій секції, а вимкнений стан пояснює, що кроків для чисел цього часу немає й хто може ввімкнути); перемикач шле
 * `PUT …/trace-steps`; без права — лише показ.
 *
 * ⛔ Мутації: прибрати `offNote` — червоний перший тест; не блокувати перемикач без права — червоний другий.
 */

const puts: unknown[] = [];

function mockServer(initial: boolean): void {
  puts.length = 0;
  let state = initial;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/trace-steps') && init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)) as { enabled: boolean };
        puts.push(body);
        state = body.enabled;
      }

      return new Response(JSON.stringify({ methodologyId: 7, traceSteps: state }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(editable: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyTraceStepsPanel methodologyId={7} editable={editable} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('MethodologyTraceStepsPanel (D-565)', () => {
  it('вимкнено: стан видно в згорнутій секції, пояснення чесне; перемикач вмикає прапор', async () => {
    mockServer(false);
    show(true);

    expect((await screen.findByTestId('trace-steps-state')).textContent).toContain('⟦methodologies.traceSteps.off⟧');

    fireEvent.click(screen.getByTestId('methodology-section-toggle-traceSteps'));
    expect(screen.getByTestId('trace-steps-off-note').textContent).toContain('⟦methodologies.traceSteps.offNote⟧');

    fireEvent.click(screen.getByLabelText('⟦methodologies.traceSteps.label⟧'));

    await waitFor(() => expect(puts).toEqual([{ enabled: true }]));
    await waitFor(() =>
      expect(screen.getByTestId('trace-steps-state').textContent).toContain('⟦methodologies.traceSteps.on⟧'),
    );
    expect(screen.queryByTestId('trace-steps-off-note')).toBeNull();
  });

  it('без права редагування — лише показ: перемикач заблоковано, PUT не йде', async () => {
    mockServer(false);
    show(false);

    await screen.findByTestId('trace-steps-state');
    fireEvent.click(screen.getByTestId('methodology-section-toggle-traceSteps'));

    const toggle = screen.getByLabelText('⟦methodologies.traceSteps.label⟧');
    expect((toggle as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText('⟦methodologies.traceSteps.readOnly⟧')).toBeTruthy();

    fireEvent.click(toggle);
    expect(puts).toEqual([]);
  });
});
