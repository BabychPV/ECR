import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DependencyGraphPage, monthsOfMask } from '@/pages/admin/DependencyGraphPage';

const notify = vi.hoisted(() => ({ showDone: vi.fn(), showApiError: vi.fn() }));
vi.mock('@/shared/ui/notify', () => notify);

/**
 * `B179 L5`: екран «Граф залежностей» — рівні з доказами, цикли, перемикачі зв'язки джерела (стан-ворота, «читати з
 * документа», «перераховувати залежних») через підтвердження, стани «помилка / порожньо / немає права».
 *
 * Тексти — позначені ключі (`⟦key⟧`): каталог у тестах порожній.
 */
const graph = {
  revision: 'r1',
  truncated: false,
  nodes: [
    { kind: 'template', id: 1, code: 'HSE400' },
    { kind: 'template', id: 2, code: 'LAND' },
    { kind: 'registry', id: 10, code: 'COMP' },
  ],
  edges: [
    {
      kind: 'fills', fromKind: 'template', fromId: 1, toKind: 'registry', toId: 10, scope: 'all', monthMask: 0, evidenceTotal: 1,
      evidence: [{ source: 'sourceBinding', id: 5, versionId: null, detail: 'Any' }],
    },
    {
      kind: 'templateFormula', fromKind: 'registry', fromId: 10, toKind: 'template', toId: 2, scope: 'month', monthMask: 0, evidenceTotal: 1,
      evidence: [{ source: 'ast', id: 7, versionId: 70, detail: 'T1.CNT' }],
    },
  ],
  templateDependencies: [
    {
      sourceTemplateId: 1, consumerTemplateId: 2, bindingId: 5, registryDefId: 10, scope: 'month', monthMask: 0,
      viaTemplateFormula: true, viaMethodology: false,
    },
  ],
  levels: [
    { level: 0, templateIds: [1] },
    { level: 1, templateIds: [2] },
  ],
  cycles: [],
  diagnostics: [],
};

const binding = {
  id: 5,
  code: 'HSE400_LAND',
  sourceTemplateCode: 'HSE400',
  registryCode: 'COMP',
  sourceState: 'any',
  policy: 'ConfirmWithReason',
  readFromDocument: true,
  recalculateDependents: true,
  readFromDocumentEffective: true,
  readFromDocumentSource: 'binding',
  consumerTemplateCodes: ['LAND'],
  updatedAt: null,
  updatedByUserId: null,
};

interface Served {
  readonly puts: { url: string; body: unknown }[];
}

function serve(options: { graph?: unknown; graphStatus?: number; bindings?: unknown[] } = {}): Served {
  const served: Served = { puts: [] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': status === 200 ? 'application/json' : 'application/problem+json' },
        });

      if (init?.method === 'PUT') {
        served.puts.push({ url, body: JSON.parse(String(init.body)) });

        return Promise.resolve(json({ binding, changed: true, queuedYears: [] }));
      }

      if (url.includes('/api/v1/dependency-graph')) {
        const status = options.graphStatus ?? 200;

        return Promise.resolve(
          status === 200
            ? json(options.graph ?? graph)
            : json({ title: 'x', status, detail: 'x', errorCode: 'ECR-AUTH-0403', correlationId: 'c1' }, status),
        );
      }

      return Promise.resolve(json(options.bindings ?? [binding]));
    }),
  );

  return served;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DependencyGraphPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('B179 L5: екран «Граф залежностей»', () => {
  it('рівні за порядком; шаблон-джерело з перемикачами, споживач із доказами', async () => {
    serve();
    show();

    expect(await screen.findByRole('heading', { level: 2, name: '⟦dependencyGraph.level (level=1)⟧' })).toBeDefined();
    expect(screen.getByRole('heading', { level: 2, name: '⟦dependencyGraph.level (level=2)⟧' })).toBeDefined();

    const source = screen.getByTestId('template-HSE400');
    expect(within(source).getByRole('heading', { level: 3, name: 'HSE400' })).toBeDefined();
    expect(within(source).getByText(/HSE400_LAND/)).toBeDefined();
    expect(within(source).getByText(/⟦dependencyGraph.fills.consumers/)).toBeDefined();

    const consumer = screen.getByTestId('template-LAND');
    expect(within(consumer).getByText(/⟦dependencyGraph.reads.from/)).toBeDefined();
    expect(within(consumer).getByText('⟦dependencyGraph.scope.month⟧')).toBeDefined();
    expect(within(consumer).getByText('⟦dependencyGraph.via.formula⟧')).toBeDefined();
    expect(within(consumer).queryByText('⟦dependencyGraph.via.methodology⟧')).toBeNull();
    expect(within(consumer).getByText(/T1\.CNT/)).toBeDefined();
  });

  it('вимкнення «перераховувати залежних» — підтвердження, потім PUT /flags лише з цим бітом', async () => {
    const served = serve();
    show();
    const user = userEvent.setup();

    const sw = await screen.findByRole('switch', { name: /dependencyGraph.flag.recalculateDependents/ });
    await user.click(sw);

    // Підтвердження з наслідком, PUT ще не пішов.
    expect(await screen.findByText('⟦dependencyGraph.confirm.recalcOff⟧')).toBeDefined();
    expect(served.puts).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: '⟦dependencyGraph.confirm.verb⟧' }));

    await waitFor(() => expect(served.puts).toHaveLength(1));
    expect(served.puts[0]).toEqual({
      url: '/api/v1/document-source-bindings/5/flags',
      body: { recalculateDependents: false },
    });
    await waitFor(() => expect(notify.showDone).toHaveBeenCalledWith('⟦dependencyGraph.saved⟧'));
  });

  it('скасування підтвердження нічого не надсилає', async () => {
    const served = serve();
    show();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('switch', { name: /dependencyGraph.flag.readFromDocument/ }));
    expect(await screen.findByText('⟦dependencyGraph.confirm.readOff⟧')).toBeDefined();

    await user.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));

    await waitFor(() => expect(screen.queryByText('⟦dependencyGraph.confirm.readOff⟧')).toBeNull());
    expect(served.puts).toHaveLength(0);
  });

  it('стан-ворота «лише затверджені» — PUT /source-state з approvedOnly', async () => {
    const served = serve();
    show();
    const user = userEvent.setup();

    await user.click(await screen.findByText('⟦dependencyGraph.state.approvedOnly⟧'));
    expect(await screen.findByText('⟦dependencyGraph.confirm.approvedOnly⟧')).toBeDefined();
    await user.click(screen.getByRole('button', { name: '⟦dependencyGraph.confirm.verb⟧' }));

    await waitFor(() => expect(served.puts).toHaveLength(1));
    expect(served.puts[0]).toEqual({
      url: '/api/v1/document-source-bindings/5/source-state',
      body: { sourceState: 'approvedOnly' },
    });
  });

  it('цикл і зауваження показуються смугою й переліком; урізання — окремою смугою', async () => {
    serve({
      graph: {
        ...graph,
        truncated: true,
        cycles: [{ templateIds: [1, 2] }],
        diagnostics: [{ key: 'astFallback', detail: 'template version 7' }],
      },
    });
    show();

    expect(await screen.findByText('⟦dependencyGraph.cycles.title⟧')).toBeDefined();
    expect(screen.getByText('HSE400 ⇄ LAND')).toBeDefined();
    expect(screen.getByTestId('dependency-graph-truncated')).toBeDefined();
    expect(screen.getByTestId('dependency-graph-diagnostics').textContent).toContain('template version 7');
    expect(screen.getAllByText('⟦dependencyGraph.onCycle⟧').length).toBe(2);
  });

  it('порожній граф — порожній стан із поясненням, без рівнів', async () => {
    serve({ graph: { ...graph, nodes: [], edges: [], templateDependencies: [], levels: [] } });
    show();

    expect(await screen.findByText('⟦dependencyGraph.empty.title⟧')).toBeDefined();
    expect(screen.getByText('⟦dependencyGraph.empty.hint⟧')).toBeDefined();
    expect(screen.queryByRole('heading', { name: /dependencyGraph.level/ })).toBeNull();
  });

  it('403 — окремий стан «немає права», перемикачів немає', async () => {
    serve({ graphStatus: 403 });
    show();

    await waitFor(() => expect(screen.queryByRole('switch')).toBeNull());
    expect(await screen.findByRole('alert')).toBeDefined();
    expect(document.body.textContent).toContain('ECR-AUTH-0403');
    // Повтор запиту права не дасть: кнопки повтору немає (на відміну від збою 500).
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('відмова сервера 500 — помилка з кнопкою повтору, а не порожній екран', async () => {
    serve({ graphStatus: 500 });
    show();

    expect(await screen.findByRole('alert')).toBeDefined();
    expect(screen.queryByRole('switch')).toBeNull();
    expect(screen.getByRole('button')).toBeDefined();
  });
});

describe('monthsOfMask', () => {
  it('біти 1…12 → перелік місяців', () => {
    expect(monthsOfMask(0)).toBe('');
    expect(monthsOfMask((1 << 1) | (1 << 10))).toBe('1, 10');
  });
});
