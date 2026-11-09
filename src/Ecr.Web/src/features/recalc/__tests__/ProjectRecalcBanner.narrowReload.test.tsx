import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';

/**
 * ✎ debt-1109:
 * - x4: при 375 px дії банера (Open in Jobs / Open documents / Dismiss / ▾) стояли в рядку `nowrap` поруч із текстом
 *   і вилазили за край. Тепер і рядок, і група дій переносяться (`--group-wrap: wrap`).
 * - x5: перезавантаження ДО розкладу губило N — банер писав «Queued recalculation of X…». N, запам'ятане при
 *   постановці, тепер переживає перезавантаження вкладки (`sessionStorage`).
 */
function serve(state: 'Queued' | 'Succeeded'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(
        JSON.stringify([
          {
            jobId: 'P9',
            jobCode: 'Ecr.Application.Ports.IRecalculationJob',
            state,
            percent: 0,
            updatedAt: new Date().toISOString(),
            startedAt: new Date().toISOString(),
            documentId: null,
            projectId: 7,
            projectCode: 'LANDUI2026',
            effectiveState: state === 'Queued' ? 'Queued' : 'Succeeded',
            fanOut: state === 'Queued' ? null : { total: 3, queued: 0, running: 0, succeeded: 3, failed: 0, skippedSheets: 0 },
          },
        ]),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    ),
  );
}

async function show(): Promise<void> {
  const { ProjectRecalcBanner } = await import('@/features/recalc/ProjectRecalcBanner');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['me'], { userId: 42, permissions: ['System.ViewHealth'], grants: {}, denies: [] });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <ProjectRecalcBanner projectId={7} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  globalThis.sessionStorage?.clear();
  globalThis.localStorage?.clear();
});

/** Перелік задач (рядок у черзі, без розкладу) і перелік проєктів із `documentCount`. */
function serveWithProjects(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/projects')
        ? { items: [{ id: 7, code: 'LANDUI2026', documentCount: 3 }], nextCursor: null, totalCount: 1 }
        : [
            {
              jobId: 'P10',
              jobCode: 'Ecr.Application.Ports.IRecalculationJob',
              state: 'Queued',
              percent: 0,
              updatedAt: new Date().toISOString(),
              startedAt: null,
              documentId: null,
              projectId: 7,
              projectCode: 'LANDUI2026',
              effectiveState: 'Queued',
              fanOut: null,
            },
          ];

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

describe('ProjectRecalcBanner: вузький екран і раннє перезавантаження (debt-1109)', () => {
  it('x5: перезавантаження ще до відповіді на постановку — N з переліку проєктів, щойно він приїхав', async () => {
    vi.resetModules();
    serveWithProjects();
    await show();

    expect(
      await screen.findByText('⟦recalcProgress.queuedDocuments (project=LANDUI2026, total=3, documentCount=⟦recalcProgress.documentCount.other (count=3)⟧)⟧'),
    ).toBeTruthy();
  });

  it('x4: рядок банера й група дій переносяться, а не стоять у nowrap', async () => {
    serve('Succeeded');
    await show();

    const head = await screen.findByText((_, el) => el?.hasAttribute('data-recalc-head') === true);
    const actions = head.querySelector('[data-recalc-actions]') as HTMLElement | null;

    expect(head.getAttribute('style') ?? '').toContain('--group-wrap: wrap');
    expect(actions?.getAttribute('style') ?? '').toContain('--group-wrap: wrap');
  });

  // ✎ debt-1109 F13 (P3 C). Мутація: повернути `className="ecr-ellipsis"` безумовно — червоне.
  it('C: розгорнутий банер переносить текст, а не обрізає «…»; згорнутий — один рядок з обрізанням', async () => {
    serve('Succeeded');
    await show();

    const text = await screen.findByText((_, el) => el?.hasAttribute('data-recalc-text') === true);
    expect(text.classList.contains('ecr-ellipsis')).toBe(false);
    expect(text.getAttribute('style') ?? '').toContain('overflow-wrap: anywhere');

    (document.querySelector('[data-recalc-toggle]') as HTMLElement).click();
    const collapsed = await screen.findByText(
      (_, el) => el?.hasAttribute('data-recalc-text') === true && el.classList.contains('ecr-ellipsis'),
    );
    expect(collapsed).toBeTruthy();
  });

  it('x5: N, запам\'ятане при постановці, видно після перезавантаження (новий модуль, порожня пам\'ять)', async () => {
    const before = await import('@/features/recalc/useProjectRecalcProgress');
    before.rememberRecalcTotal('P9', 3);

    // «Перезавантаження»: свіжі модулі — карта в пам'яті порожня, лишається лише сховище вкладки.
    vi.resetModules();
    serve('Queued');
    await show();

    expect(
      await screen.findByText('⟦recalcProgress.queuedDocuments (project=LANDUI2026, total=3, documentCount=⟦recalcProgress.documentCount.other (count=3)⟧)⟧'),
    ).toBeTruthy();
  });
});
