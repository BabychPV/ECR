import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { SourceEntitiesTab } from '@/features/integration/SourceEntitiesTab';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { testTheme } from '@/test/render';

/**
 * AN-40 / L9-27: стежка каталогу у формі «Додати сутність» належить ОДНОМУ з'єднанню. Шухляду (і вкладку в ній)
 * React перевикористовував для іншого з'єднання разом зі станом форми, і шлях каталогу A їхав на сервер із
 * `dataSourceId` B.
 *
 * Мутація (лише локально): прибрати `key={source.id}` з `AddSourceEntityModal` — після зміни з'єднання форма
 * лишається на рівні `\\AF\A\Plant` і питає каталог B цим шляхом, червоніє цей тест.
 */
configure({ asyncUtilTimeout: 10_000 });

function connection(id: number, code: string): DataSource {
  return {
    catalog: null,
    code,
    collectionSchedules: 0,
    endpoint: `https://${code.toLowerCase()}.example.invalid/api`,
    hasSecret: false,
    id,
    isActive: true,
    maxParallel: 4,
    nameL10n: { en: code },
    rowVersion: 'AAAAAAAAB9E=',
    secondaryEndpoint: null,
    sourceEntities: 0,
    transport: 'PiWebApi',
  };
}

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

/** Запити каталогу: з'єднання й шлях рівня (`''` — корінь). */
let catalog: { source: string; path: string }[] = [];

function serve(): void {
  catalog = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.View', 'Integration.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      const level = /^\/api\/v1\/data-sources\/(\d+)\/catalog$/.exec(path);
      if (level !== null) {
        const source = level[1] ?? '';
        const at = url.searchParams.get('path') ?? '';
        catalog.push({ source, path: at });

        const database = source === '7' ? 'A' : 'B';
        const items =
          at === ''
            ? [{ code: 'Plant', displayName: 'Plant', path: `\\\\AF\\${database}\\Plant`, kind: 'Element', dataType: 'Element', unitSymbol: null }]
            : [];

        return json({ items, nextCursor: null });
      }

      return json([]);
    }),
  );
}

function Tab({ source }: { readonly source: DataSource }): JSX.Element {
  return <SourceEntitiesTab source={source} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceEntitiesTab — інше з\'єднання (L9-27)', () => {
  it('зміна з\'єднання скидає стежку каталогу форми: каталог B читається з кореня, а не шляхом A', async () => {
    serve();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrap = (source: DataSource): JSX.Element => (
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <Tab source={source} />
        </QueryClientProvider>
      </MantineProvider>
    );

    const { rerender } = render(wrap(connection(7, 'PI-A')));

    fireEvent.click(await screen.findByRole('button', { name: /sources\.addEntity/ }));
    const form = await screen.findByRole('dialog');
    const plant = await waitFor(() => {
      const row = form.querySelector<HTMLElement>('[data-catalog-item="Plant"]');
      expect(row).not.toBeNull();
      return row as HTMLElement;
    });
    fireEvent.click(within(plant).getByRole('button', { name: /sources\.catalogOpenLevel/ }));
    await waitFor(() => expect(catalog).toContainEqual({ source: '7', path: '\\\\AF\\A\\Plant' }));

    rerender(wrap(connection(8, 'PI-B')));

    // Форма B — з кореня: жодного запиту каталогу B шляхом бази A.
    await waitFor(() => expect(catalog.some((call) => call.source === '8')).toBe(true));
    expect(catalog.filter((call) => call.source === '8' && call.path !== '')).toEqual([]);
  });
});
