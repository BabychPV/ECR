import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * BE-08: «лише мої задачі» на екрані черги.
 *
 * ⛔ `mine=true` — межа доступу, а не косметика: власні задачі сервер віддає
 * БЕЗ права `System.ViewHealth` (`Q-156`), чужі — ні, і без цього параметра
 * перелік власних перерахунків був недосяжний узагалі. Тому прапорець має
 * доходити до АДРЕСИ: галочка, яка лише фільтрує вже отриманий масив, не
 * зробила б нічого — сервер такому користувачеві відповів би `403` ще до
 * фільтрації.
 *
 * ⚠ Ідентифікатора власника в адресі немає і бути не може: сервер бере його з
 * сеансу. Тест на це живе на сервері (`JobListMineTests`) — там, де є
 * прив'язка моделі, яку тільки й можна спробувати обійти.
 */

const allJobs = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#42',
    percent: 40,
    startedAt: '2026-09-19T09:58:00Z',
    state: 'Running',
    updatedAt: '2026-09-19T10:00:00Z',
  },
  {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: 'IExcelExportJob-alien',
    percent: 100,
    startedAt: '2026-09-19T08:55:00Z',
    state: 'Succeeded',
    updatedAt: '2026-09-19T09:00:00Z',
  },
];

/** Те саме, що віддав би сервер на `mine=true`: чужої задачі в наборі немає. */
const mineJobs = [allJobs[0]];

/** Усі адреси переліку, куди сторінка сходила. */
const requested: string[] = [];

function mockFetch(): void {
  requested.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/jobs')) {
        requested.push(url);

        return Promise.resolve(
          new Response(JSON.stringify(url.includes('mine=true') ? mineJobs : allJobs), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(new Response(JSON.stringify(null), { status: 404 }));
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/jobs']}>
        <QueryClientProvider client={client}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/**
 * Чи є в переліку рядок у цьому стані.
 *
 * ✎ 2026-09-19. Тут стояло `screen.getByText('Succeeded')` — пошук за КОДОМ
 * СЕРВЕРА як за видимим текстом. Відколи стан малює `StatusBadge`, підпис
 * береться з каталогу (`status.job.*`): код сервера не є текстом інтерфейсу й
 * не перекладається. Локатор переведено на `data-status-state` — атрибут, який
 * набір кладе в розмітку саме для тестів і e2e.
 *
 * ⚠ Твердження тесту НЕ змінилося: чужа задача має зникнути, своя — лишитись.
 * Атрибут прив'язаний до стану точніше, ніж збіг слова в тексті.
 */
function hasState(state: string): boolean {
  return document.querySelector(`[data-status-state="${state}"]`) !== null;
}

afterEach(() => {
  vi.unstubAllGlobals();
  requested.length = 0;
});

describe('JobsPage: лише мої задачі', () => {
  it('прапорець доходить до адреси, і чужа задача зникає з переліку', async () => {
    mockFetch();
    const user = userEvent.setup();
    show();

    // Спершу — уся черга: екран відкривається лише з `System.ViewHealth`, і
    // для його власника звуження до своїх було б несподіванкою.
    await waitFor(() => expect(hasState('Succeeded')).toBe(true));
    await waitFor(() => expect(requested.length).toBeGreaterThan(0));
    expect(requested.every((u) => !u.includes('mine='))).toBe(true);

    await user.click(screen.getByRole('checkbox', { name: '⟦jobs.mineOnly⟧' }));

    // ⛔ Мутаційний доказ: приберіть `mine` з адреси в `recentJobsUrl`
    // (`features/jobs/api.ts`) або з ключа запиту в `useRecentJobs` — і сюди
    // ніколи не приїде адреса з `mine=true`: у першому випадку її не буде
    // взагалі, у другому React Query віддасть кешовану відповідь на ІНШЕ
    // питання, не сходивши на сервер.
    await waitFor(() => expect(requested.some((u) => u.includes('mine=true'))).toBe(true));

    // Чужа задача зникла, своя лишилася — тобто показано те, що віддав сервер
    // на звужений запит, а не відфільтрований на клієнті старий масив.
    await waitFor(() => expect(hasState('Succeeded')).toBe(false));
    expect(hasState('Running')).toBe(true);
  });

  it('момент старту задачі видно в переліку', async () => {
    mockFetch();
    show();

    /*
     * ⚠ Поле `startedAt` додано сервером у цьому ж PR. Колонка, яку ніхто не
     * показує, зробила б його «даними, що доїхали й нікому не потрібні»:
     * питання «коли це почалося» стоїть першим, щойно задача висить.
     *
     * ✎ 2026-09-19, ЗМІНА ПОВЕДІНКИ, а не підгін локатора. Тут стояло
     * `findByText('2026-09-19T09:58:00Z')` — і воно падало б, бо користувач
     * СИРОГО рядка більше не бачить: момент малює `Timestamp` (`UI-07`).
     * Локатор переведено на `<time dateTime>` саме тому, що точне значення
     * нікуди не діло́ся — воно в розмітці. Твердження тесту не змінилося:
     * момент старту першої задачі є на екрані.
     */
    await waitFor(() =>
      expect(
        document.querySelector('time[datetime="2026-09-19T09:58:00Z"]'),
        'момент старту не доїхав до розмітки',
      ).not.toBeNull(),
    );
  });
});
