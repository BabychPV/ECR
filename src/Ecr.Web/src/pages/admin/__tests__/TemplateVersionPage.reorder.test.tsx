import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Конструктор шаблону (`ФВ-2.6`): перестановка колонок на сторінці версії.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): у сторінці передати
 * `reorderColumnsMutation.mutate` позиції навпаки (`to, from`) — червоніє
 * тіло патча; прибрати `disabled` з `ReorderCell` рядків — червоніє «рядки
 * вимкнені»; прибрати гейт `can(…, 'Template.Edit')` з колонки порядку —
 * червоніє «без права».
 */

const column = (id: number, code: string, ordinal: number): unknown => ({
  id,
  code,
  dataType: 'Decimal',
  displayFormat: null,
  headerL10n: { values: { en: code } },
  isHidden: false,
  isReadOnly: false,
  isRequired: false,
  ordinal,
  unitSymbol: null,
  formulaExpression: null,
  formulaDialect: null,
});

const row = (rowKey: string, ordinal: number): unknown => ({
  rowKey,
  label: rowKey,
  ordinal,
  rowKind: 'Item',
  parentRowKey: null,
  isReadOnly: false,
  formulaExpression: null,
  formulaDialect: null,
});

const structure = {
  isEditable: true,
  presentationRevision: 0,
  groupRules: [],
  templateVersionId: 1,
  sheets: [
    {
      id: 1,
      code: 'SHEET',
      nameL10n: { values: { en: 'Sheet' } },
      isMandatory: true,
      isVisible: true,
      ordinal: 1,
      sheetGroup: null,
      tables: [
        {
          id: 10,
          code: 'T10',
          nameL10n: { values: { en: 'Table 10' } },
          layoutKind: 'Static',
          maxDynamicRows: null,
          ordinal: 1,
          rowMode: 'Fixed',
          columns: [column(100, 'AAA', 0), column(101, 'BBB', 1), column(102, 'CCC', 2)],
          rows: [row('R1', 0), row('R2', 1)],
        },
      ],
    },
  ],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

let patches: unknown[] = [];

function stubFetch(permissions: readonly string[]): void {
  patches = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/presentation') && init?.method === 'PATCH') {
        patches.push(JSON.parse(String(init.body)) as unknown);
        return json({ presentationRevision: 1 });
      }
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: {} });
      if (url.includes('/me')) {
        return json({ userId: 0, userName: 'test', language: 'en', permissions, isSimulation: false });
      }
      if (url.includes('/structure')) return json(structure);
      if (url.includes('/versions?limit=')) {
        return json({
          items: [{ id: 1, version: '1.0.0.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null }],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );
}

async function renderPage(permissions: readonly string[]): Promise<void> {
  stubFetch(permissions);
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
  await screen.findByText('(AAA)');
  // ⚠ Панель `Accordion` розкривається анімацією; доки вона не завершилась,
  // вміст для ролей прихований.
  await waitFor(() => expect(screen.queryAllByRole('table').length).toBeGreaterThan(0));
}

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateVersionPage — перестановка колонок (ФВ-2.6)', () => {
  it('«нижче» на першій колонці шле один PATCH презентаційного шару', async () => {
    await renderPage(['Template.View', 'Template.Edit']);

    fireEvent.click(screen.getByRole('button', { name: /reorder\.moveDown.*AAA/ }));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]).toEqual([
      { entityType: 'ColumnDef', entityId: 101, field: 'Ordinal', value: '0' },
      { entityType: 'ColumnDef', entityId: 100, field: 'Ordinal', value: '1' },
    ]);
  });

  it('перетягування третьої колонки на першу', async () => {
    await renderPage(['Template.View', 'Template.Edit']);

    const handle = (code: string): Element => {
      const cell = screen.getByText(`(${code})`).closest('tr');
      const node = cell?.querySelector('[data-reorder-handle]');
      if (node == null || cell == null) throw new Error(`немає ручки ${code}`);
      return node;
    };
    const transfer = { setData: vi.fn(), dropEffect: 'none', effectAllowed: 'all' };

    fireEvent.dragStart(handle('CCC'), { dataTransfer: transfer });
    const target = screen.getByText('(AAA)').closest('tr');
    if (target === null) throw new Error('немає рядка AAA');
    fireEvent.dragOver(target, { dataTransfer: transfer });
    fireEvent.drop(target, { dataTransfer: transfer });

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]).toEqual([
      { entityType: 'ColumnDef', entityId: 102, field: 'Ordinal', value: '0' },
      { entityType: 'ColumnDef', entityId: 100, field: 'Ordinal', value: '1' },
      { entityType: 'ColumnDef', entityId: 101, field: 'Ordinal', value: '2' },
    ]);
  });

  it('рядки: кнопки порядку вимкнені й пояснені', async () => {
    await renderPage(['Template.View', 'Template.Edit']);

    const down = screen.getByRole('button', { name: /reorder\.moveDown.*R1/ }) as HTMLButtonElement;
    expect(down.disabled).toBe(true);
    const hint = document.getElementById(down.getAttribute('aria-describedby') ?? '');
    expect(hint?.textContent).toMatch(/reorder\.rowsUnavailable/);
  });

  it('без права Template.Edit — жодних кнопок порядку', async () => {
    await renderPage(['Template.View']);

    expect(screen.queryByRole('button', { name: /reorder\.moveDown/ })).toBeNull();
    expect(document.querySelector('[data-reorder-handle]')).toBeNull();
  });
});
