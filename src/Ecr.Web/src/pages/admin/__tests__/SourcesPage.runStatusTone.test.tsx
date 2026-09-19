import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { statusTable } from '@/shared/ui/StatusBadge';

/**
 * Провал збору перестає виглядати попередженням.
 *
 * ⛔ Дефект, зафіксований у самому наборі (`StatusBadge.statusTable`,
 * різновид `collectionRun`): сторінка фарбувала статус останнього прогону
 * тернаркою `status === 'Succeeded' ? 'statusSuccess' : 'statusWarning'`.
 * Тобто `Failed` — джерело не віддало НІЧОГО — і `Degraded` — віддало
 * частину — мали ОДИН колір. Оператор бачив жовте там, де даних немає
 * зовсім, і шукав би різницю лише в числі точок поруч.
 *
 * ⚠ Тон читається з розмітки бейджа (`data-status-tone`), а не з кольору CSS:
 * колір — наслідок тону через `toneFills`, і перевіряти його означало б
 * перевіряти Mantine, а не власне рішення. Контраст самих тонів уже стереже
 * `shared/ui/__tests__/statusBadgeContrast.test.ts`.
 */
function sourceWith(status: string, id: number) {
  return {
    id,
    code: `FLD-${String(id)}`,
    displayName: `Source ${String(id)}`,
    entityPath: null,
    isActive: true,
    lastRun: { status, pointsRetrieved: 0, startedUtc: '2026-09-19T00:00:00Z' },
    oldestGap: null,
    transport: 'Rest',
  };
}

/*
 * ⚠ Усі три стани переліку сервера ОДНОЧАСНО, в одній таблиці: тест на одному
 * `Failed` лишався б зеленим і тоді, коли всі три стани стали червоними
 * («полагодили» тернарку навпаки). Доказ — саме РОЗРІЗНЕННЯ.
 */
const sources = [sourceWith('Succeeded', 1), sourceWith('Degraded', 2), sourceWith('Failed', 3)];

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'Тестовий адміністратор',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/sources')) {
        return new Response(JSON.stringify(sources), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SourcesPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function toneOf(state: string): string | null {
  return document.querySelector(`[data-status-state="${state}"]`)?.getAttribute('data-status-tone') ?? null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourcesPage: стан останнього збору', () => {
  it('Failed — відмова, Degraded — попередження, Succeeded — нейтрально', async () => {
    respond();
    show();

    await screen.findByText('Source 1');

    // ⛔ Мутаційний доказ (RED до фіксу): тернарка давала `Failed` той самий
    // `statusWarning`, що й `Degraded`, тобто тут стояло б `'warning'`.
    expect(toneOf('Failed')).toBe('danger');

    expect(toneOf('Degraded')).toBe('warning');
    expect(toneOf('Succeeded')).toBe('neutral');
  });

  it('стан, якого набір не знає, позначений як невідомий, а не мовчки перефарбований', async () => {
    /*
     * ⛔ Не гіпотеза: статус прогону доходить до клієнта РЯДКОМ
     * (`schema.d.ts`), тож новий стан сервера приїде без жодної скарги
     * компілятора. Тернарка робила з будь-якого такого стану попередження —
     * тобто новий `Aborted` роками виглядав би як «майже успіх» і ніде не
     * лишав сліду. Набір натомість ставить `data-status-known="false"`, за
     * яким його видно і в DOM, і в знімку.
     */
    expect(statusTable.collectionRun['Aborted'], 'фікстура має бути СПРАВДІ невідомим станом').toBeUndefined();

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes('/api/v1/sources')
          ? new Response(JSON.stringify([sourceWith('Aborted', 9)]), {
              status: 200,
              headers: { 'Content-Type': 'application/json' },
            })
          : new Response(JSON.stringify(null), { status: 200 }),
      ),
    );
    show();

    await screen.findByText('Source 9');

    const badge = document.querySelector('[data-status-state="Aborted"]');
    expect(badge?.getAttribute('data-status-known')).toBe('false');
    expect(badge?.getAttribute('data-status-tone')).toBe('warning');
  });
});
