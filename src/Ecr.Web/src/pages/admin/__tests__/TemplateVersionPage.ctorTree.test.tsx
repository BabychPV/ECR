import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { JSX } from 'react';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Конструктор версії шаблону (`UI-36`): дерево «Structure» + ОДНА таблиця в
 * робочій області (макет `docs/design/hybrid/screens-templates.js`, `ctor-*`).
 *
 * ⛔ Що тут тримається закритим. Версія чинного розміру — 92 таблиці, ~2986
 * колонок. До `LazyTableSlots` стос згорток монтував усі таблиці (~1 с довгих
 * задач на відкритті). Тепер монтується лише вибрана таблиця — перша перевірка
 * нижче тримає саме це, а не вигляд.
 *
 * ⛔ Мутаційні докази (локально, не в коміті): `parseNode` без запасного вузла
 * (повертає `root` для зниклої таблиці) — червона «зниклий вузол»; фільтр дерева
 * без збігу за назвою таблиці — червона «пошук»; `onSelect` не пише `?node=` —
 * червона «вибір у дереві».
 */

let editable = true;

/** Поточна адреса — щоб перевіряти, що вибір вузла пишеться в `?node=`. */
let location = '';
function LocationProbe(): JSX.Element | null {
  const current = useLocation();
  location = current.pathname + current.search;
  return null;
}

const TableIds = [10, 11, 12, 13, 14] as const;

function table(id: number): unknown {
  return {
    id,
    code: `T${String(id)}`,
    nameL10n: { values: { en: `Table ${String(id)}` } },
    layoutKind: 'Static',
    maxDynamicRows: null,
    ordinal: id,
    rowMode: 'Fixed',
    rows: [],
    columns: [
      {
        id: id * 100,
        code: `COL${String(id)}`,
        dataType: 'Decimal',
        displayFormat: null,
        headerL10n: { values: { en: `Column ${String(id)}` } },
        isHidden: false,
        isReadOnly: false,
        isRequired: false,
        ordinal: 1,
        unitSymbol: null,
        formulaExpression: null,
        formulaDialect: null,
      },
    ],
  };
}

function structureDto(): unknown {
  return {
    isEditable: editable,
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
        tables: TableIds.map(table),
      },
      {
        id: 2,
        code: 'WATER',
        nameL10n: { values: { en: 'Water' } },
        isMandatory: true,
        isVisible: false,
        ordinal: 2,
        sheetGroup: null,
        tables: [table(20)],
      },
    ],
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }
      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.Publish', 'Template.Edit'],
          isSimulation: false,
        });
      }
      if (url.includes('/structure')) return json(structureDto());
      if (url.includes('/versions?limit=')) {
        return json({
          items: [{ id: 1, version: '1.0.0.0', status: editable ? 'Draft' : 'Published', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null }],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );
}

function renderPage(entry = '/admin/templates/1/versions/1'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <LocationProbe />
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Вміст таблиці змонтовано — видно її колонку. */
function columnShown(id: number): boolean {
  return screen.queryByText(`(COL${String(id)})`) !== null;
}

function treeItem(name: RegExp): HTMLElement {
  return within(screen.getByRole('tree')).getByRole('treeitem', { name });
}

beforeEach(async () => {
  editable = true;
  stubFetch();
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TemplateVersionPage: дерево конструктора й одна таблиця в робочій області (UI-36)', () => {
  it('за замовчуванням відкрита перша таблиця першого аркуша — і змонтована лише вона', async () => {
    renderPage();

    await waitFor(() => expect(columnShown(10)).toBe(true));
    for (const id of [11, 12, 13, 14, 20]) expect(columnShown(id)).toBe(false);
    expect(treeItem(/Table 10/).getAttribute('aria-selected')).toBe('true');
  });

  it('вибір у дереві: монтує вибрану таблицю замість попередньої й пише її в адресу', async () => {
    renderPage();
    await waitFor(() => expect(columnShown(10)).toBe(true));

    fireEvent.click(treeItem(/Table 13/));

    await waitFor(() => expect(columnShown(13)).toBe(true));
    expect(columnShown(10)).toBe(false);
    expect(location).toContain('node=t%3A13');
  });

  it('адреса відкриває таблицю іншого аркуша й розгортає його в дереві', async () => {
    renderPage('/admin/templates/1/versions/1?node=t:20');

    await waitFor(() => expect(columnShown(20)).toBe(true));
    expect(treeItem(/Water/).getAttribute('aria-expanded')).toBe('true');
    // Прапорець прихованого аркуша — із самої структури версії.
    expect(within(treeItem(/Water/)).getByText('⟦version.hidden⟧')).toBeDefined();
  });

  it('зниклий вузол в адресі (таблицю видалили) — перша таблиця, а не порожня сторінка', async () => {
    renderPage('/admin/templates/1/versions/1?node=t:999');

    await waitFor(() => expect(columnShown(10)).toBe(true));
  });

  it('корінь дерева — огляд версії з аркушами; клац по аркушу відкриває його огляд', async () => {
    renderPage('/admin/templates/1/versions/1?node=root');

    const root = await screen.findByTestId('ctor-root');
    expect(columnShown(10)).toBe(false);
    fireEvent.click(within(root).getByRole('button', { name: 'Sheet' }));

    const sheet = await screen.findByTestId('ctor-sheet');
    expect(sheet.getAttribute('data-sheet-code')).toBe('SHEET');
    // Огляд аркуша — таблиці в порядку оператора з номерами «аркуш.таблиця».
    expect(within(sheet).getByText('1.4')).toBeDefined();
  });

  it('пошук «Find a table» лишає лише збіги; без збігів — порожній стан із очищенням', async () => {
    renderPage();
    const search = await screen.findByRole('searchbox', { name: '⟦ctor.findTable⟧' });

    fireEvent.change(search, { target: { value: 'Table 12' } });
    const items = within(screen.getByRole('tree')).getAllByRole('treeitem');
    expect(items.map((item) => item.textContent)).toEqual(['⟦ctor.versionNode (version=1.0.0.0)⟧', 'Sheet5', '1.3Table 12']);

    fireEvent.change(search, { target: { value: 'nothing like this' } });
    expect(screen.getByTestId('ctor-tree-empty')).toBeDefined();
    fireEvent.click(within(screen.getByTestId('ctor-tree-empty')).getByRole('button'));
    expect(within(screen.getByRole('tree')).getAllByRole('treeitem').length).toBeGreaterThan(3);
  });

  it('клавіатура: ↓ переводить фокус, Enter вибирає таблицю', async () => {
    renderPage();
    await waitFor(() => expect(columnShown(10)).toBe(true));

    const selected = treeItem(/Table 10/);
    expect(selected.tabIndex).toBe(0);
    selected.focus();
    fireEvent.keyDown(selected, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(treeItem(/Table 11/));

    fireEvent.keyDown(document.activeElement as Element, { key: 'Enter' });
    await waitFor(() => expect(columnShown(11)).toBe(true));
  });

  it('опублікована версія: банер заморозки з дією «Clone to new draft» відкриває клон', async () => {
    editable = false;
    renderPage();

    const banner = await screen.findByTestId('ctor-frozen');
    fireEvent.click(within(banner).getByRole('button', { name: '⟦ctor.cloneToDraft⟧' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('textbox')).toBeDefined();
  });
});
