import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

const shown = vi.hoisted(() => ({ done: vi.fn(), error: vi.fn() }));

vi.mock('@/shared/ui/notify', () => ({
  showDone: shown.done,
  showApiError: shown.error,
}));

const Script = '-- comment\nEXEC arc.usp_EnsurePartitions @MonthsAhead = 6;\n';

const Facts = {
  productVersion: '1.4.2',
  startedAt: '2026-09-19T03:00:00Z',
  environment: 'Production',
  notificationTransport: { isConfigured: false, kind: null },
  logDirectory: null,
};

/** `/health/*` — порожній звіт; факти й скрипт — те, що задав тест. */
function serve(facts: unknown, script: Response = new Response(Script, { status: 200 })): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/partitions/script')) return Promise.resolve(script);

      const body = url.includes('/api/v1/health/facts')
        ? facts
        : { status: 'Healthy', totalDurationMs: 1, checks: [] };

      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  shown.done.mockReset();
  shown.error.mockReset();
});

describe('Дашборд здоров’я — факти про процес і команда для DBA (BE-18)', () => {
  it('показує версію й середовище, а відсутній транспорт НАЗИВАЄ відсутнім', async () => {
    serve(Facts);
    show();

    expect(await screen.findByText('1.4.2')).toBeDefined();
    expect(screen.getByText('Production')).toBeDefined();

    // ⛔ Не порожній рядок і не «Smtp»: черга без транспорту накопичує
    // сповіщення, і саме це адміністратор має прочитати.
    expect(screen.getByText('⟦health.facts.transportNotConfigured⟧')).toBeDefined();
  });

  it('налаштований транспорт показується своїм видом', async () => {
    serve({ ...Facts, notificationTransport: { isConfigured: true, kind: 'Smtp' } });
    show();

    expect(await screen.findByText('Smtp')).toBeDefined();
    expect(screen.queryByText('⟦health.facts.transportNotConfigured⟧')).toBeNull();
  });

  it('тека журналу показується, коли файл пишеться', async () => {
    serve({ ...Facts, logDirectory: 'C:\\ProgramData\\ECR\\logs' });
    show();

    expect(await screen.findByText('C:\\ProgramData\\ECR\\logs')).toBeDefined();
    expect(screen.getByText('⟦health.facts.logDirectory⟧')).toBeDefined();
  });

  it('без файлового журналу рядка про теку немає — ні підпису, ні прочерку', async () => {
    serve(Facts);
    show();

    await screen.findByText('1.4.2');
    expect(screen.queryByText('⟦health.facts.logDirectory⟧')).toBeNull();
  });

  it('без фактів секція не малюється взагалі — ні заголовка, ні порожніх рядків', async () => {
    serve(null);
    show();

    await screen.findByText('⟦health.copyPartitionScript⟧');
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThanOrEqual(3));

    expect(screen.queryByText('⟦health.facts⟧')).toBeNull();
    expect(document.querySelector('[data-health-facts]')).toBeNull();
  });

  it('кнопка кладе в буфер рівно той текст, який віддав сервер', async () => {
    const writeText = vi.fn(async () => Promise.resolve());
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

    serve(Facts);
    show();

    fireEvent.click(await screen.findByText('⟦health.copyPartitionScript⟧'));

    await waitFor(() => expect(writeText).toHaveBeenCalledWith(Script));
    expect(shown.done).toHaveBeenCalledWith('⟦health.partitionScriptCopied⟧');
    expect(shown.error).not.toHaveBeenCalled();
  });

  it('відмова сервера НЕ потрапляє в буфер і не видається за успіх', async () => {
    const writeText = vi.fn(async () => Promise.resolve());
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

    serve(Facts, new Response('{"title":"Forbidden"}', { status: 403, statusText: 'Forbidden' }));
    show();

    fireEvent.click(await screen.findByText('⟦health.copyPartitionScript⟧'));

    await waitFor(() => expect(shown.error).toHaveBeenCalledTimes(1));
    expect(writeText).not.toHaveBeenCalled();
    expect(shown.done).not.toHaveBeenCalled();
  });

  it('недоступний буфер обміну показує відмову, а не мовчить', async () => {
    vi.stubGlobal('navigator', {
      ...navigator,
      clipboard: { writeText: vi.fn(async () => Promise.reject(new Error('NotAllowedError'))) },
    });

    serve(Facts);
    show();

    fireEvent.click(await screen.findByText('⟦health.copyPartitionScript⟧'));

    await waitFor(() => expect(shown.error).toHaveBeenCalledTimes(1));
    expect(shown.done).not.toHaveBeenCalled();
  });
});
