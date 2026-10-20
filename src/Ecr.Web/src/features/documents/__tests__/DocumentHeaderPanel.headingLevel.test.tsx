import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import axe from 'axe-core';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { testTheme } from '@/test/render';

/**
 * P3-N8 (TESTER-PASS-B-RC19, axe `heading-order`, moderate): на сторінці документа
 * після `h1` (PageHeader) одразу йшов `h4` «Шапка документа» — пропуск h2/h3. Шапка
 * малюється з рівнями 2 (панель) і 3 (секція контракту); візуальний розмір лишився
 * `h4`/`h5` через `size`. Тест обгортає панель в `h1`, як робить сторінка.
 */
const DocumentId = 77;

function field(code: string, en: string, headerFieldDefId: number): unknown {
  return { code, dataType: 'String', headerFieldDefId, isRequired: false, label: { values: { en } }, value: null };
}

const Fields = [field('PERMIT_NUMBER', 'Permit Number', 1), field('AREA', 'Area', 2), field('REMARK', 'Remark', 3)];

function show(collapsible: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input).split('?')[0] ?? '';
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`)) return json({ fields: Fields, version: 'V1' });
      if (url.endsWith('/api/v1/registries')) return json([]);
      throw new Error(`неочікуваний запит: ${url}`);
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <main>
          <h1>Document</h1>
          <DocumentHeaderPanel documentId={DocumentId} canEdit collapsible={collapsible} />
        </main>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe.each([true, false])('DocumentHeaderPanel: рівні заголовків (collapsible=%s)', (collapsible) => {
  it('панель — h2, секція контракту — h3, h4 немає', async () => {
    show(collapsible);

    await screen.findByTestId('document-header-contract');

    // `hidden: true`: згорнута шапка ховає секцію контракту від дерева доступності, але
    // рівні в розмітці мають бути правильні й у розгорнутому стані.
    expect(screen.getByRole('heading', { level: 2, hidden: true })).toBeTruthy();
    expect(screen.getByRole('heading', { level: 3, hidden: true })).toBeTruthy();
    expect(screen.queryAllByRole('heading', { level: 4, hidden: true })).toHaveLength(0);
    expect(screen.queryAllByRole('heading', { level: 5, hidden: true })).toHaveLength(0);
  });

  it('axe heading-order: без порушень під h1', async () => {
    show(collapsible);

    await screen.findByTestId('document-header-contract');

    const results = await axe.run(document.body, { runOnly: { type: 'rule', values: ['heading-order'] }, resultTypes: ['violations'] });

    expect(results.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.html).join(' | ')}`)).toEqual([]);
  });
});
