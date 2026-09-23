import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * `U-09` для `/admin/sources` — те саме правило, що встановлено в `U-08`: на
 * екрані одночасно видно рівно ОДИН порожній стан, той, що пояснює найближчу
 * перешкоду.
 *
 * ⛔ Було ТРИ поспіль: «No collection sources configured», «No connections
 * configured», «No collection runs». Причинний порядок між ними зворотний до
 * візуального: сутність збору належить З'ЄДНАННЮ, прогін належить сутності.
 * Отже найближча перешкода — «з'єднань немає», і саме її розділ (єдиний із
 * власною дією «New connection») лишається сам.
 *
 * ⛔ Мутаційний доказ — `hasConnections = true` у `SourcesPage.tsx`: перший
 * випадок стає ЧЕРВОНИМ (три заголовки рівня 4 замість одного).
 * `hasConnections = false` робить червоним другий — той стереже, що правка не
 * з'їла обидва розділи там, де з'єднання Є.
 *
 * ⚠ Третє твердження першого випадку — про заголовок розділу сутностей,
 * якого в нього не було зовсім (на відміну від «Connections» поруч); воно
 * перевіряється у випадку, де розділ видно.
 */

const Connection = {
  catalog: 'ProdAF',
  code: 'PI-MAIN',
  collectionSchedules: 3,
  endpoint: 'https://pi.example.invalid/piwebapi',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 12,
  transport: 'PiWebApi',
};

const SeededStrings: Record<string, string> = {
  'sources.title': 'Sources',
  'sources.entity': 'Entity',
  'sources.entities': 'Entities',
  'sources.transport': 'Transport',
  'sources.lastRun': 'Last run',
  'sources.gap': 'Gap',
  'sources.connection': 'Connection',
  'sources.connections': 'Connections',
  'sources.state': 'State',
  'sources.schedules': 'Schedules',
  'sources.empty': 'No collection sources configured',
  'sources.emptyHint': 'Without sources the system works fine: data is entered by hand.',
  'sources.connectionsEmpty': 'No connections configured',
  'sources.connectionsEmptyHint':
    'A connection says where data is collected from; entities and schedules are attached to it.',
  'collectionRuns.title': 'Collection runs',
  'collectionRuns.empty': 'No collection runs',
  'collectionRuns.emptyHint': 'Nothing has been collected in this window.',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Сервер: з'єднань стільки, скільки просить випадок; усе решта — порожнє. */
function serve(connections: readonly unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const path = new URL(url, 'http://localhost').pathname;

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.View'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/data-sources')) return json(connections);
      if (path.endsWith('/api/v1/sources')) return json([]);
      if (path.endsWith('/api/v1/collection-runs')) return json({ items: [], nextCursor: null });

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/sources']}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
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

describe('SourcesPage: рівно один порожній стан (U-09)', () => {
  it("з'єднань немає — на екрані лише «No connections configured»", async () => {
    serve([]);
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('No connections configured');

    expect(
      emptyStateHeadings(),
      'на екрані більше одного порожнього стану',
    ).toEqual(['No connections configured']);

    // Два підпорядковані розділи мовчать ЦІЛКОМ — не лише їхні заглушки, а й
    // самі розділи: заголовок «Collection runs» без вмісту був би тією самою
    // порожнечею, лише без пояснення.
    expect(screen.queryByText('No collection sources configured')).toBeNull();
    expect(screen.queryByText('No collection runs')).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Collection runs' })).toBeNull();
  });

  it("з'єднання є — розділ сутностей повертається і має власний заголовок", async () => {
    serve([Connection]);
    await loadCatalog('en', 'private');

    show();

    await screen.findByText('No collection sources configured');

    /*
     * ⛔ Заголовка в цього розділу не було зовсім — на відміну від
     * «Connections» поруч, через що три таблиці поспіль читалися як одна
     * зламана. Рівень той самий, що в сусідніх розділів (`order={2}`).
     */
    expect(screen.getByRole('heading', { level: 2, name: 'Entities' })).toBeDefined();
    expect(screen.getByRole('heading', { level: 2, name: 'Connections' })).toBeDefined();
    expect(screen.getByRole('heading', { level: 2, name: 'Collection runs' })).toBeDefined();
  });
});
