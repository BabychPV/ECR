import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { statusTable } from '@/shared/ui/StatusBadge';

/**
 * Стан задачі фарбує набір, а не сама сторінка.
 *
 * ⛔ Тут стояла власна `stateColor(state)`, і її `default: 'blue'` був не
 * «запасним кольором», а твердженням: `Unknown` і `Unavailable` — тобто
 * відмова планувальника ВІДПОВІСТИ про задачу (`QuartzJobScheduler.cs:365`,
 * `:369`: планувальник вимкнено або ідентифікатора вже немає) — малювалися
 * рівно тим самим синім, що й `Queued`. Оператор бачив «задача в черзі» там,
 * де про задачу не відомо нічого. `statusTable.job` розводить ці випадки:
 * `Queued` — нейтрально, `Unknown`/`Unavailable` — `warning`.
 *
 * ⚠ Тон читається з розмітки (`data-status-tone`), а не з кольору CSS: колір —
 * наслідок тону через `toneFills`. Контраст самих тонів стереже
 * `shared/ui/__tests__/statusBadgeContrast.test.ts`.
 */
function jobWith(state: string, n: number) {
  return {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: `IRecalculationJob#${String(n)}`,
    percent: 10 * n,
    startedAt: '2026-09-19T09:58:00Z',
    state,
    updatedAt: '2026-09-19T10:00:00Z',
  };
}

/*
 * ⚠ ВСІ сім станів переліку сервера одночасно, плюс восьмий — невідомий.
 * Тест на одному `Failed` лишався б зеленим і тоді, коли всі стани стали
 * червоними: доказ — саме розрізнення, а не присутність одного тону.
 */
const states = [
  'Queued',
  'Running',
  'Succeeded',
  'Failed',
  'Cancelled',
  'Unknown',
  'Unavailable',
  'Superseded',
];

const jobs = states.map((state, index) => jobWith(state, index + 1));

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/jobs')) {
        return new Response(JSON.stringify(jobs), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 404 });
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

function badgeOf(state: string): Element | null {
  return document.querySelector(`[data-status-state="${state}"]`);
}

function toneOf(state: string): string | null {
  return badgeOf(state)?.getAttribute('data-status-tone') ?? null;
}

/**
 * Чекає, доки перелік задач домалюється.
 *
 * ⚠ Ознака «домалювалося» — по одному моменту старту на рядок. Однина впала б
 * на «found multiple elements», тобто на власному локаторі, а не на предметі
 * тесту, тому рахуємо всі.
 *
 * ✎ 2026-09-19, ЗМІНА ПОВЕДІНКИ. Тут стояло
 * `findAllByText('2026-09-19T09:58:00Z')` — пошук СИРОГО рядка сервера як
 * видимого тексту. Відколи момент малює `Timestamp` (`UI-07`), на екрані
 * читабельна форма, а сирий рядок лишився в `dateTime`. Локатор переведено
 * на атрибут: він прив'язаний до значення точніше, ніж збіг тексту, і не
 * залежить від мови набору.
 */
async function ready(): Promise<void> {
  await waitFor(() =>
    expect(document.querySelectorAll('time[datetime="2026-09-19T09:58:00Z"]')).toHaveLength(
      states.length,
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: тон стану задачі приходить із набору', () => {
  it('сім станів сервера — і вони РІЗНІ, а не один колір на всіх', async () => {
    mockFetch();
    show();
    await ready();

    // ⛔ Мутаційний доказ (RED, якщо повернути `stateColor`): локальний помічник
    // не лишає в розмітці ані `data-status-tone`, ані `data-status-state`, тож
    // кожен рядок нижче стає `expected null to be …`.
    expect(toneOf('Failed')).toBe('danger');
    expect(toneOf('Running')).toBe('info');
    expect(toneOf('Cancelled')).toBe('muted');
    expect(toneOf('Queued')).toBe('neutral');
    expect(toneOf('Succeeded')).toBe('neutral');
  });

  it('«планувальник не відповів» — не те саме, що «задача в черзі»', async () => {
    mockFetch();
    show();
    await ready();

    /*
     * ⛔ Головне твердження цього файлу і рівно та різниця, якої не було:
     * `default: 'blue'` старого помічника накривав `Queued`, `Unknown` і
     * `Unavailable` одним кольором. `Unknown`/`Unavailable` — НЕ помилка
     * задачі (тоді був би `danger`, і оператор шукав би збій, якого не було),
     * а відмова відповісти про неї.
     */
    expect(toneOf('Unknown')).toBe('warning');
    expect(toneOf('Unavailable')).toBe('warning');
    expect(toneOf('Queued')).toBe('neutral');

    expect(toneOf('Unknown')).not.toBe(toneOf('Queued'));
    expect(toneOf('Unknown')).not.toBe('danger');
  });

  it('стан, якого набір не знає, позначений невідомим, а не мовчки перефарбований', async () => {
    /*
     * ⛔ Не гіпотеза: `JobSummary.state` і `JobStatus.state` доходять до
     * клієнта простим `string` (`schema.d.ts:9452`, `:9476`), тож новий стан
     * сервера приїде без жодної скарги компілятора.
     */
    expect(statusTable.job['Superseded'], 'фікстура має бути СПРАВДІ невідомим станом').toBeUndefined();

    mockFetch();
    show();
    await ready();

    expect(badgeOf('Superseded')?.getAttribute('data-status-known')).toBe('false');
    expect(toneOf('Superseded')).toBe('warning');

    // Відомий стан позначений відомим — атрибут не константа.
    expect(badgeOf('Failed')?.getAttribute('data-status-known')).toBe('true');
  });

  it('підпис бере рядок каталогу, а не друкує код сервера', async () => {
    mockFetch();
    show();
    await ready();

    /*
     * ⚠ Цей файл каталогу не завантажує, тож `t()` віддає позначений ключ
     * (`D-138`) — і саме ключ є доказом, що підпис пройшов через `t()`, а не
     * через `{job.state}`. Рядки `status.job.*` уже лежать у `09-seed.sql`; що
     * вони там є і не порожні, доводить `shared/ui/__tests__/StatusBadge.test.tsx`.
     */
    expect(badgeOf('Failed')?.textContent).toBe('⟦status.job.Failed⟧');
    expect(badgeOf('Failed')?.textContent).not.toBe('Failed');
  });
});
