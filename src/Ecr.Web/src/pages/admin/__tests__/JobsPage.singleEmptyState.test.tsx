import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * `U-09` для `/admin/jobs` — те саме правило, що встановлено в `U-08`: на
 * екрані одночасно видно рівно ОДИН порожній стан, той, що пояснює найближчу
 * перешкоду.
 *
 * ⛔ Було: «Enter a job id» (картка стеження) → прапорець «Only my jobs» →
 * «No jobs yet» (журнал). Фільтр між двома заглушками, і екран читається як
 * зламаний. Картка ПІДПОРЯДКОВАНА журналу: ідентифікатор беруть саме звідти
 * кнопкою «Watch», а на чистій базі його брати нізвідки.
 *
 * ⛔ Мутаційний доказ — прибрати `jobId !== null &&` перед `<AsyncBoundary>` у
 * `JobsPage.tsx`: перший випадок стає ЧЕРВОНИМ (два заголовки рівня 4).
 * Другий випадок стереже протилежний бік — картка ЗʼЯВЛЯЄТЬСЯ, щойно
 * ідентифікатор в адресі є; підміна умови на `false` робить червоним його.
 */

const SeededStrings: Record<string, string> = {
  'jobs.title': 'Jobs',
  'jobs.id': 'Job id',
  'jobs.pick': 'Enter a job id',
  'jobs.pickHint': 'Long operations return a job id; paste it here to follow the progress.',
  'jobs.watch': 'Watch',
  'jobs.recentEmpty': 'No jobs yet',
  'jobs.mineOnly': 'Only my jobs',
  'jobs.mineOnlyHint': 'Others need System.ViewHealth.',
  'jobs.recentCode': 'Job',
  'jobs.recentState': 'State',
  'jobs.recentMessage': 'Message',
  'jobs.createdBy': 'Started by',
  'jobs.createdAt': 'Queued',
  'jobs.recentStarted': 'Started',
  'jobs.recentWatch': 'Watch',
};

const watched = {
  jobId: 'watched-job-1',
  state: 'Running',
  percent: 40,
  message: null,
  error: null,
  errorCode: null,
  correlationId: null,
  attempt: null,
  maxAttempts: null,
  createdAt: null,
  documentId: null,
  startedAt: '2026-09-23T09:00:00Z',
  updatedAt: '2026-09-23T09:01:00Z',
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      // ⚠ Порядок перевірок значущий: адреса переліку (`/api/v1/jobs?…`) і
      // адреса однієї задачі (`/api/v1/jobs/{id}`) відрізняються саме
      // сегментом після `jobs`, і зворотний порядок віддав би переліку
      // картку однієї задачі.
      if (/\/api\/v1\/jobs\/[^?]/.test(url)) return json(watched);
      if (url.includes('/api/v1/jobs')) return json([]);

      return json(null);
    }),
  );
}

function show(path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Заголовки екранів-заглушок: `EmptyState`/`ForbiddenState` — `Title order={4}`. */
function emptyStateHeadings(): string[] {
  return screen.queryAllByRole('heading', { level: 4 }).map((node) => node.textContent ?? '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: рівно один порожній стан (U-09)', () => {
  it('задачі не обрано і журнал порожній — на екрані лише «No jobs yet»', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    show('/admin/jobs');

    await screen.findByText('No jobs yet');

    expect(
      emptyStateHeadings(),
      'на екрані більше одного порожнього стану',
    ).toEqual(['No jobs yet']);

    /*
     * ⛔ Текст не зник із продукту — він переїхав на поле вводу: «Enter a job
     * id» тепер плейсхолдер (атрибут, не текстовий вузол), а пояснення —
     * `description` поля. Тому перевіряється ОБОЄ: заглушки з цим заголовком
     * немає, а підказка на полі — є.
     */
    expect(screen.queryByRole('heading', { name: 'Enter a job id' })).toBeNull();
    expect(screen.getByRole('textbox', { name: /Job id/ }).getAttribute('placeholder')).toBe(
      'Enter a job id',
    );
    expect(
      screen.getByText('Long operations return a job id; paste it here to follow the progress.'),
    ).toBeDefined();
  });

  it('ідентифікатор в адресі — картка стеження на місці', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    show('/admin/jobs?id=watched-job-1');

    // ⚠ Ідентифікатор НЕ у формі `IТипJob-guid`, тож `humanizeJobId` лишає
    // його як є — картку видно за тим самим рядком, що в адресі.
    await screen.findByText('watched-job-1');

    // Картка — це ДАНІ, а не заглушка: єдиний порожній стан на екрані далі
    // журнальний, і жодного другого поруч із карткою не з'являється.
    expect(emptyStateHeadings()).toEqual(['No jobs yet']);
  });
});
