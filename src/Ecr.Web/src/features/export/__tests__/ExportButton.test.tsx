import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { ExportButton } from '../ExportButton';

/**
 * Стеження за задачею експорту (Q-234).
 *
 * ⛔ До цього тесту `ExportButton` не мав ЖОДНОГО: саме тому кнопка лишилася
 * на старій, вручну написаній версії опитування (`refetchInterval` за
 * `job.data?.state`, без `job.isError`), тоді як той самий патерн для
 * «Перерахувати» (`PeriodsPage.tsx`) уже отримав захист через `jobFollow.ts`
 * (`outcomeOf`, `Q-156`). Наслідок — коли `GET /jobs/{id}` не вдавався (немає
 * права, мережа), `job.data` лишався `undefined` НАЗАВЖДИ: кнопка крутила
 * «Формується…» вічно, а `refetchInterval` бачив `undefined` замість
 * `'Queued'`/`'Running'` і зупиняв опитування — файл забрати було нічим і
 * ніщо про це не повідомляло.
 *
 * ⛔ Другий дефект тієї самої родини: повідомлення про відмову читало
 * `JobStatus.Message` (останній прогрес, `IJobProgress.ReportAsync`) замість
 * `JobStatus.Error` (справжня причина, `FinishAsync(..., ex.Message, ...)`).
 * Обидва поля — рядки, обидва можуть бути порожніми — тому tsc цього не ловив.
 */

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

interface JobBody {
  jobId: string;
  state: string;
  percent: number;
  message: string | null;
  error: string | null;
}

/** Маршрутизує фейковий `fetch` за адресою: постановка в чергу і опитування. */
function mockFetch(job: JobBody): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/export') && !url.includes('/jobs/')) {
        return new Response(JSON.stringify({ jobId: job.jobId }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes(`/jobs/${job.jobId}`)) {
        return new Response(JSON.stringify(job), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

/** Той самий маршрут, але опитування задачі відмовляє (немає права, `Q-156`). */
function mockFetchUnreadableJob(jobId: string): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/export') && !url.includes('/jobs/')) {
        return new Response(JSON.stringify({ jobId }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes(`/jobs/${jobId}`)) {
        return new Response(
          JSON.stringify({
            title: 'Немає права',
            status: 403,
            errorCode: 'ECR-ACCS-0403',
            correlationId: 'cid-1',
          }),
          { status: 403, headers: { 'Content-Type': 'application/json' } },
        );
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <ExportButton documentId={1} periodKey={202601} language="en" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
});

describe('ExportButton: стеження за задачею побудови книги', () => {
  it('завершену задачу — дає посилання на файл', async () => {
    mockFetch({
      jobId: 'job-1',
      state: 'Succeeded',
      percent: 100,
      message: 'export-key-abc',
      error: null,
    });

    const user = userEvent.setup();
    show();

    await user.click(screen.getByRole('button'));

    const link = await screen.findByRole('link');
    expect(link.getAttribute('href')).toContain('export-key-abc');
  });

  it('провалену задачу — показує ПРИЧИНУ (`error`), а не застарілий прогрес (`message`)', async () => {
    mockFetch({
      jobId: 'job-1',
      state: 'Failed',
      percent: 40,
      // ⚠ Навмисно РІЗНІ рядки: `message` — те, що лишилось із прогресу до
      // відмови, `error` — справжня причина. Тест ловить саме плутанину
      // полів, а не самий факт показу якогось тексту.
      message: 'Формується… 40%',
      error: 'Аркуш W-02 перевищує ліміт рядків',
    });

    const user = userEvent.setup();
    show();

    await user.click(screen.getByRole('button'));

    await waitFor(() => {
      expect(notifications.show).toHaveBeenCalled();
    });

    const call = vi.mocked(notifications.show).mock.calls[0]?.[0] as { message: string };
    expect(call.message).toBe('Аркуш W-02 перевищує ліміт рядків');
    expect(call.message).not.toBe('Формується… 40%');

    // ⛔ Кнопка не лишається «Формується…» назавжди — головне твердження
    // регресії: стара умова `state !== 'Failed'` тут ще працювала б, бо
    // стан ПРОЧИТАНО успішно. Дефект №1 нижче перевіряє випадок, коли стан
    // прочитати НЕ вдалося взагалі.
    await waitFor(() => {
      expect(screen.getByRole('button').textContent).not.toBe('⟦document.exportBuilding⟧');
    });
  });

  it('стан задачі прочитати не вдалося (`Q-156`) — кнопка НЕ крутиться вічно', async () => {
    mockFetchUnreadableJob('job-2');

    const user = userEvent.setup();
    show();

    await user.click(screen.getByRole('button'));

    // ⛔ Головне твердження регресії. До фіксу `job.data` лишався
    // `undefined` НАЗАВЖДИ (запит на стан провалився), `refetchInterval`
    // бачив `undefined` замість `'Queued'`/`'Running'` і зупиняв опитування,
    // а `building` лишався `true` — кнопка крутила «Формується…» вічно, і
    // побудований файл (якщо він і був) забрати було нічим.
    await waitFor(() => {
      expect(screen.getByRole('button').textContent).not.toBe('⟦document.exportBuilding⟧');
    });

    // ⚠ І без тосту: причина — брак права на ЧИТАННЯ стану задачі, а не
    // збій самого експорту (`PeriodsPage.tsx` дотримується того самого
    // правила для «Перерахувати»). Показ помилки тут звинуватив би експорт
    // у тому, чого він не робив.
    expect(notifications.show).not.toHaveBeenCalled();
  });
});
