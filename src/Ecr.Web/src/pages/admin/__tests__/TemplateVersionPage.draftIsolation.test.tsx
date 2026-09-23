import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import type { ComponentProps, JSX } from 'react';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Живий дефект (2026-09-23, скрин людини): друк у діалозі «Add sheet» на
 * версії з 91 таблицею — секунди на символ. Причина: чернетка діалогу була
 * станом СТОРІНКИ (`useState` у `TemplateVersionPage` + `onChange={setSheetDraft}`),
 * тож кожне натискання клавіші перерендерювало все дерево структури. Замір
 * на стенді: ~212 тис. волокон React на символ, медіана 6.4 с (dev) / 2.0 с
 * (prod) від натискання до кадру; після — 317 волокон і ~31 мс.
 *
 * ⛔ Шпигун — на корені дерева структури (`Accordion` аркушів), а не на
 * сторінці: твердження саме про те, що друк не чіпає дерева.
 *
 * ⛔ Мутаційний доказ: повернення чернетки «Add sheet» на сторінку
 * (`onChange={setSheetDraft}` замість локального `LocalDraft`) дає тут
 * червоний тест — лічильник рендерів дерева росте на кожен символ.
 */

const renders = vi.hoisted(() => ({ tree: 0 }));

vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();
  const Real = actual.Accordion;

  function SpyAccordion(props: ComponentProps<typeof Real>): JSX.Element {
    renders.tree += 1;
    return <Real {...props} />;
  }

  return { ...actual, Accordion: Object.assign(SpyAccordion, Real) };
});

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'sheets.add': 'Add sheet',
  'sheets.code': 'Sheet code',
  'sheets.name': 'Sheet name',
  'sheets.save': 'Save sheet',
  'tableDef.add': 'Add table',
  'tableDef.code': 'Table code',
  'tableDef.name': 'Table name',
  'tableDef.save': 'Save table',
};

function structureDto(): unknown {
  return {
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
            code: 'TBL',
            nameL10n: { values: { en: 'Table' } },
            layoutKind: 'Static',
            maxDynamicRows: null,
            ordinal: 1,
            rowMode: 'Dynamic',
            rows: [],
            columns: [
              {
                id: 42,
                code: 'C1',
                dataType: 'Decimal',
                displayFormat: null,
                headerL10n: { values: { en: 'Column one' } },
                isHidden: false,
                isReadOnly: false,
                isRequired: false,
                ordinal: 1,
                unitSymbol: null,
                formulaExpression: null,
                formulaDialect: null,
              },
            ],
          },
        ],
      },
    ],
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

let fetchMock: ReturnType<typeof vi.fn>;

function stubFetch(): void {
  fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (url.includes('/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
    }
    if (url.includes('/me')) {
      return json({
        userId: 0,
        userName: 'test',
        language: 'en',
        permissions: ['Template.View', 'Template.Edit', 'Template.Publish'],
        isSimulation: false,
      });
    }
    if (url.includes('/api/v1/languages')) {
      return json([{ code: 'en', nameNative: 'English', ordinal: 1, isDefault: true }]);
    }
    if (init?.method === 'PUT') {
      return json({});
    }
    if (url.includes('/structure')) {
      return json(structureDto());
    }
    if (url.includes('/versions?limit=')) {
      return json({ items: [], nextCursor: null, totalCount: null });
    }

    return json([]);
  });
  vi.stubGlobal('fetch', fetchMock);
}

beforeEach(async () => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  stubFetch();
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
  renders.tree = 0;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPage(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Набирає текст посимвольно — кожен символ окремою подією, як людина. */
function typeInto(input: HTMLElement, text: string): void {
  let value = '';
  for (const ch of text) {
    value += ch;
    fireEvent.change(input, { target: { value } });
  }
}

function putBody(pathPart: string): unknown {
  const call = fetchMock.mock.calls.find(
    ([input, init]) => String(input).includes(pathPart) && (init as RequestInit | undefined)?.method === 'PUT',
  );
  expect(call, `PUT на ${pathPart}`).toBeDefined();
  return JSON.parse(String((call?.[1] as RequestInit).body));
}

describe('TemplateVersionPage — друк у діалозі не перерендерює дерево структури', () => {
  it('«Add sheet»: набір коду й назви не рендерить дерево, а збереження везе введене', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Add sheet' }));
    const dialog = await screen.findByRole('dialog');
    const code = await within(dialog).findByRole('textbox', { name: /Sheet code/ });
    const name = await within(dialog).findByRole('textbox', { name: /Sheet name · English/ });

    const before = renders.tree;
    typeInto(code, 'NEWSHEET');
    typeInto(name, 'New sheet');

    // Форма справді оновлюється від кожного символу — інакше «нуль рендерів
    // дерева» було б правдою й для зламаного поля.
    expect((code as HTMLInputElement).value).toBe('NEWSHEET');
    expect((name as HTMLInputElement).value).toBe('New sheet');
    expect(renders.tree - before).toBe(0);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save sheet' }));

    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/sheets/NEWSHEET'))).toBe(true);
    });
    expect(putBody('/sheets/NEWSHEET')).toMatchObject({ nameL10n: { en: 'New sheet' } });
  });

  it('«Add table»: те саме для сусіднього діалогу', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Add table' }));
    const dialog = await screen.findByRole('dialog');
    const code = await within(dialog).findByRole('textbox', { name: /Table code/ });
    const name = await within(dialog).findByRole('textbox', { name: /Table name · English/ });

    const before = renders.tree;
    typeInto(code, 'NEWTBL');
    typeInto(name, 'New table');

    expect((code as HTMLInputElement).value).toBe('NEWTBL');
    expect(renders.tree - before).toBe(0);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save table' }));

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(([input]) => String(input).includes('/sheets/SHEET/tables/NEWTBL')),
      ).toBe(true);
    });
    expect(putBody('/sheets/SHEET/tables/NEWTBL')).toMatchObject({ nameL10n: { en: 'New table' } });
  });
});
