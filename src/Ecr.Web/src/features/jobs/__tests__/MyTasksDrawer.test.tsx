import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
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
  render(
    <MantineProvider theme={testTheme}>
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
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
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
    render(
      <MantineProvider theme={testTheme}>
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
