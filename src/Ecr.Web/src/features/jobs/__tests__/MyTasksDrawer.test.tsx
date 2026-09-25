import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JobSummary } from '@/api/types';
import { testTheme } from '@/test/render';
import { MyTasksDrawer } from '@/features/jobs/MyTasksDrawer';

/**
 * Вміст шухляди «My tasks»: порожній стан (`L10`) і факти задачі (`BE-08`).
 *
 * ⚠ Шухляда рендериться НАПРЯМУ, з пропами, а не через `MyTasksLauncher`:
 * лаунчер у сусідньому файлі навмисно підмінений заглушкою (сторож бюджету
 * `D-132`), і перевіряти крізь нього справжній вміст означало б зламати саме
 * ту перевірку.
 *
 * ⚠ `QueryClientProvider` тут обов'язковий і для рядків, де кнопка «Повторити»
 * НЕ показується: `JobFacts.JobRetry` кличе `useRestartJob` (тобто
 * `useMutation`) БЕЗУМОВНО, до власної перевірки `canRestartJob` — React
 * забороняє умовний виклик хука. Без провайдера падає навіть рендер задачі
 * `Running`.
 */

function row(over: Partial<JobSummary>): JobSummary {
  return {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: 'IExcelExportJob#7',
    percent: 0,
    startedAt: '2026-09-22T09:58:00Z',
    state: 'Running',
    updatedAt: '2026-09-22T10:00:00Z',
    ...over,
  } as JobSummary;
}

function show(jobs: readonly JobSummary[] | undefined, isPending = false): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <MyTasksDrawer
            opened
            onClose={() => undefined}
            jobs={jobs}
            isPending={isPending}
            error={null}
            onRetry={() => undefined}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('шухляда «My tasks»', () => {
  it('порожньо — і сказано ЧОМУ, а не сама лише порожнеча (L10)', () => {
    show([]);

    /*
     * ⛔ Мутаційний доказ. Приберіть `emptyHint` із `MyTasksDrawer.tsx` — і
     * другий рядок почервоніє, лишивши в шухляді саме той стан, проти якого
     * `L10` і написане: «порожньо» без причини не відрізняється від збою.
     */
    expect(screen.getByText('⟦jobs.recentEmpty⟧')).toBeTruthy();
    expect(screen.getByText('⟦jobs.myTasksHint⟧')).toBeTruthy();
  });

  it('закрита шухляда не малює нічого (L2)', () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter>
            <MyTasksDrawer
              opened={false}
              onClose={() => undefined}
              jobs={[row({})]}
              isPending={false}
              error={null}
              onRetry={() => undefined}
            />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.querySelector('[data-my-task]')).toBeNull();
  });

  it('провал показує код каталогу, кореляцію і посилання на документ', () => {
    show([
      row({
        jobId: 'IExcelExportJob#9',
        state: 'Failed',
        errorCode: 'ECR-JOB-0409',
        correlationId: 'corr-abc-123',
        documentId: 42,
        attempt: 3,
        maxAttempts: 4,
      }),
    ]);

    // Код провалу — через каталог помилок, а не сирим рядком сервера.
    expect(screen.getByText('⟦err.ECR-JOB-0409⟧')).toBeTruthy();
    expect(screen.getByText('corr-abc-123')).toBeTruthy();

    // Спроба показується лише з другої — тут третя з чотирьох.
    expect(document.querySelector('[data-job-attempt]')).not.toBeNull();

    const link = screen.getByRole('link');
    expect(link.getAttribute('href')).toBe('/documents/42');
  });

  it('смуга прогресу лише в активної задачі, і рядок несе свій jobId', () => {
    show([row({ jobId: 'a', state: 'Running', percent: 40 })]);
    expect(document.querySelector('[data-my-task][data-job-id="a"]')).not.toBeNull();
    expect(document.querySelectorAll('[role="progressbar"]').length).toBeGreaterThan(0);

    cleanup();

    /*
     * ⛔ Завершена задача смуги не має: «100 %» у `Succeeded` нічого не
     * повідомляє, а в `Failed` прямо бреше про результат.
     */
    show([row({ jobId: 'b', state: 'Failed', percent: 100 })]);
    expect(document.querySelectorAll('[role="progressbar"]')).toHaveLength(0);
  });

  it('порядок рядків — той, що віддав сервер (найновіша подія зверху)', () => {
    show([
      row({ jobId: 'newest', updatedAt: '2026-09-22T12:00:00Z' }),
      row({ jobId: 'older', updatedAt: '2026-09-22T08:00:00Z' }),
    ]);

    const ids = [...document.querySelectorAll('[data-my-task]')].map((node) =>
      node.getAttribute('data-job-id'),
    );

    expect(ids).toEqual(['newest', 'older']);
  });
});

/**
 * «Повторити» й посилання на результат (`UX-09`).
 *
 * ⛔ Перелік шухляди — ЗАВЖДИ `mine=true` (`Q-156`, `useMyTasks`), тож кожен
 * рядок тут ВЛАСНИЙ за побудовою: перевірка «чужа задача без ViewHealth»
 * лишається доказом `JobFacts.canRestartJob` (чистої функції, без мережі й
 * без дерева) — тут її повторювати нема чим підмінити «чужість», бо шухляда
 * чужих задач не показує НІКОЛИ.
 */
describe('«Повторити» і посилання на результат (UX-09)', () => {
  /** Перехоплює запит на `/restart` і повертає надіслані адресу й метод. */
  function stubRestart(): { url: () => string; method: () => string | undefined } {
    let url = '';
    let method: string | undefined;

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        url = String(input);
        method = init?.method;

        return new Response(JSON.stringify({ jobId: 'IExcelExportJob#9' }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }) as typeof fetch,
    );

    return { url: () => url, method: () => method };
  }

  it('провалена задача — кнопка «Повторити» є, клік шле POST на /restart', async () => {
    const sent = stubRestart();

    show([row({ jobId: 'IExcelExportJob#9', state: 'Failed' })]);

    fireEvent.click(screen.getByRole('button', { name: '⟦jobs.restart⟧' }));

    // ⚠ Той самий привід, що в `cancelJob.test.ts`: `#` у `jobId` починає
    // фрагмент URL і мусить бути закодований, інакше сервер бачить інший шлях.
    await waitFor(() => expect(sent.url()).toBe('/api/v1/jobs/IExcelExportJob%239/restart'));
    expect(sent.method()).toBe('POST');
  });

  it('мутація: не-Failed власна задача — кнопки «Повторити» немає (дзеркало)', () => {
    show([row({ jobId: 'x', state: 'Running' })]);

    expect(screen.queryByRole('button', { name: '⟦jobs.restart⟧' })).toBeNull();
    expect(document.querySelector('[data-job-retry]')).toBeNull();
  });

  it('resultUrl не null — посилання «Завантажити файл» веде на нього як є', () => {
    show([
      row({
        jobId: 'y',
        state: 'Succeeded',
        resultUrl: '/api/v1/documents/42/export/abc-123',
      }),
    ]);

    /*
     * ⛔ Мутаційний доказ. Підставте замість `resultUrl` побудову з `message`
     * чи з `documentId` — і `href` перестане збігатися з тим, що віддав
     * сервер: посилання поведе на неіснуючий файл.
     */
    const link = screen.getByRole('link', { name: '⟦jobs.resultDownload⟧' });
    expect(link.getAttribute('href')).toBe('/api/v1/documents/42/export/abc-123');
  });

  it('мутація: resultUrl = null — посилання ігнорується', () => {
    show([row({ jobId: 'z', state: 'Succeeded', resultUrl: null })]);

    expect(document.querySelector('[data-job-result]')).toBeNull();
    expect(screen.queryByRole('link', { name: '⟦jobs.resultDownload⟧' })).toBeNull();
  });
  it('F-27: експорт — людський текст і посилання, а не hex; успішний перерахунок формул прихований', () => {
    show([
      row({
        jobId: 'exp',
        state: 'Succeeded',
        message: '3f1c0b0e9a2d4c6e8b7a5f4d3c2b1a09',
        resultUrl: '/api/v1/documents/42/export/3f1c0b0e9a2d4c6e8b7a5f4d3c2b1a09',
      }),
      row({
        jobId: 'recalc',
        jobCode: 'Ecr.Application.Ports.IFormulaRecalculationJob',
        state: 'Succeeded',
        message: 'Recalculated cells: 0.',
      }),
    ]);

    expect(screen.getByText('⟦jobs.exportReady⟧')).toBeTruthy();
    expect(screen.queryByText('3f1c0b0e9a2d4c6e8b7a5f4d3c2b1a09')).toBeNull();
    expect(screen.getByRole('link', { name: '⟦jobs.resultDownload⟧' })).toBeTruthy();

    // ⛔ Мутація: прибрати `filter(isShownInMyTasks)` — рядок перерахунку повернеться.
    expect(document.querySelector('[data-job-id="recalc"]')).toBeNull();
    expect(screen.queryByText('Recalculated cells: 0.')).toBeNull();
  });
});
