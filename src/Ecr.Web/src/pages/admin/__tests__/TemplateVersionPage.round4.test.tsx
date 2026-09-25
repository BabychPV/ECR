import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { testTheme } from '@/test/render';

/**
 * Четвертий раунд UX, лінія D — редактор структури версії:
 *
 * - X-02 (critical): правка колонки стартує з ПОВНОЇ відповіді
 *   `GET …/columns/{code}`, і збереження без правок везе назад одиницю,
 *   точність, значення за замовчуванням — а не `null`, як доти;
 * - R-06/X-01: видалення колонки й правила валідації — лише після
 *   підтвердження;
 * - X-15: правило валідації видаляється з ПЕРЕЛІКУ, а не введеним кодом;
 * - X-33: після публікації «Withdraw from use» з'являється без reload;
 * - R-21: у діалозі правил доступу немає двох сиблінгів з однаковим `key`.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`, і саме за ключами тест шукає
 * кнопки — так він не залежить від англійського тексту.
 */

interface Call {
  readonly method: string;
  readonly url: string;
  readonly body: unknown;
}

const Column = {
  id: 42,
  code: 'COL',
  dataType: 'Decimal',
  displayFormat: null,
  headerL10n: { values: { en: 'Limit' } },
  isHidden: false,
  isReadOnly: false,
  isRequired: false,
  ordinal: 1,
  unitSymbol: 't',
  formulaExpression: null,
  formulaDialect: null,
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
            rowMode: 'Fixed',
            rows: [],
            columns: [Column],
          },
        ],
      },
    ],
  };
}

/** Повний склад колонки з `GET …/columns/COL` — те, чого структура не несе. */
const FullColumn = {
  id: 42,
  code: 'COL',
  headerL10n: { values: { en: 'Limit', ru: 'Лимит' } },
  ordinal: 1,
  dataType: 'Decimal',
  isRequired: false,
  isReadOnly: false,
  isHidden: false,
  precision: 18,
  scale: 4,
  defaultValue: '0',
  displayFormat: null,
  styleId: null,
  lookupRegistryDefId: null,
  lookupFilter: null,
  unitId: 12,
};

function json(body: unknown, status = 200): Response {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function stubFetch(): { calls: Call[]; publish: () => void } {
  const calls: Call[] = [];
  let status: 'Draft' | 'Published' = 'Draft';

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({ method, url, body: init?.body === undefined ? undefined : JSON.parse(String(init.body)) });

      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.View', 'Template.Edit', 'Template.Publish'],
          isSimulation: false,
        });
      }
      if (url.endsWith('/api/v1/languages')) {
        return json([
          { code: 'en', nameNative: 'English', isDefault: true },
          { code: 'ru', nameNative: 'Русский', isDefault: false },
        ]);
      }
      if (url.endsWith('/api/v1/units')) {
        return json([{ id: 12, code: 't', dimensionCode: 'Mass', dimensionId: 1, factorToBase: '1000', offsetToBase: '0' }]);
      }
      if (url.endsWith('/tables/10/columns/COL') && method === 'GET') {
        return json(FullColumn);
      }
      if (url.endsWith('/tables/10/columns/COL') && method === 'PUT') {
        return json(FullColumn);
      }
      if (url.endsWith('/tables/10/columns/COL') && method === 'DELETE') {
        return json(null, 204);
      }
      if (url.endsWith('/tables/10/validation-rules') && method === 'GET') {
        return json([
          {
            id: 7,
            tableDefId: 10,
            code: 'POSITIVE',
            severity: 'Error',
            scope: 0,
            columnDefId: null,
            expression: '[COL] >= 0',
            messageL10n: { values: { en: 'Must be positive' } },
            isActive: true,
          },
        ]);
      }
      if (url.endsWith('/tables/10/validation-rules/POSITIVE') && method === 'DELETE') {
        return json(null, 204);
      }
      if (url.endsWith('/publish') && method === 'POST') {
        status = 'Published';
        return json(null, 204);
      }
      if (url.includes('/structure')) {
        return json(structureDto());
      }
      if (url.includes('/versions?limit=')) {
        return json({
          items: [
            { id: 1, version: '1.0', status, presentationRevision: 0, clonedFromVersionId: null, publishedAt: null },
          ],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );

  return {
    calls,
    publish: () => {
      status = 'Published';
    },
  };
}

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderPage(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
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

async function openSheet(): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: /Sheet \(SHEET\)/ }));
  await screen.findByText('Limit');
}

function sent(calls: Call[], method: string, suffix: string): Call[] {
  return calls.filter((call) => call.method === method && call.url.endsWith(suffix));
}

describe('TemplateVersionPage — четвертий раунд UX (лінія D)', () => {
  it('X-02: правка колонки відкривається з повної відповіді й зберігає одиницю, точність і default', async () => {
    const { calls } = stubFetch();
    renderPage();
    await openSheet();

    fireEvent.click(await screen.findByRole('button', { name: '⟦columns.edit⟧' }));
    const dialog = await screen.findByRole('dialog');

    // Форма чекає `GET …/columns/COL`, а не будує чернетку з бідної структури.
    await waitFor(() => expect(sent(calls, 'GET', '/tables/10/columns/COL')).toHaveLength(1));
    const code = await within(dialog).findByLabelText(/columns\.code⟧/);
    expect((code as HTMLInputElement).value).toBe('COL');

    // ⛔ Попередження «Saving will clear them» зникло разом із причиною.
    expect(within(dialog).queryByText(/columns\.partialDataWarning/)).toBeNull();

    const save = await within(dialog).findByRole('button', { name: '⟦columns.save⟧' });
    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(sent(calls, 'PUT', '/tables/10/columns/COL')).toHaveLength(1));
    expect(sent(calls, 'PUT', '/tables/10/columns/COL')[0]!.body).toMatchObject({
      unitId: 12,
      precision: 18,
      scale: 4,
      defaultValue: '0',
      headerL10n: { en: 'Limit', ru: 'Лимит' },
    });
  }, 30_000);

  it('R-06: видалення колонки — лише після підтвердження з назвою колонки', async () => {
    const { calls } = stubFetch();
    renderPage();
    await openSheet();

    fireEvent.click(await screen.findByRole('button', { name: '⟦columns.delete⟧' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('⟦columns.deleteTitle (name=Limit)⟧')).toBeDefined();
    expect(sent(calls, 'DELETE', '/tables/10/columns/COL')).toHaveLength(0);

    fireEvent.click(within(dialog).getByTestId('confirm-verb'));

    await waitFor(() => expect(sent(calls, 'DELETE', '/tables/10/columns/COL')).toHaveLength(1));
  });

  it('X-15: правило валідації видаляється з переліку й після підтвердження', async () => {
    const { calls } = stubFetch();
    renderPage();
    await openSheet();

    fireEvent.click(await screen.findByRole('button', { name: '⟦validationRules.title⟧' }));

    const remove = await screen.findByRole('button', { name: /validationRules\.deleteNamed/ });
    expect(screen.getByText('POSITIVE')).toBeDefined();

    // ⛔ Поле «ввести код для видалення» прибрано: код-поле лишилося одне — у
    // формі запису правила вище.
    expect(await screen.findAllByLabelText(/validationRules\.code⟧/)).toHaveLength(1);

    fireEvent.click(remove);
    expect(await screen.findByText('⟦validationRules.deleteTitle (code=POSITIVE)⟧')).toBeDefined();
    expect(sent(calls, 'DELETE', '/validation-rules/POSITIVE')).toHaveLength(0);

    fireEvent.click(screen.getByTestId('confirm-verb'));
    await waitFor(() => expect(sent(calls, 'DELETE', '/tables/10/validation-rules/POSITIVE')).toHaveLength(1));
  }, 30_000);

  it('X-33: після публікації «Withdraw from use» з\'являється без перезавантаження', async () => {
    stubFetch();
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: '⟦version.publish⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Quarterly release' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦version.publish⟧' }));

    expect(await screen.findByRole('button', { name: '⟦version.deprecate⟧' })).toBeDefined();
    await waitFor(() => expect(screen.queryByRole('button', { name: '⟦version.publish⟧' })).toBeNull());
  });

  it('R-21: діалог правил доступу не дає двох сиблінгів з однаковим key', async () => {
    stubFetch();
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: '⟦periodRules.title⟧' }));
    await screen.findByRole('button', { name: '⟦periodRules.add⟧' });

    const duplicate = errors.mock.calls.some((args) =>
      args.some((arg) => typeof arg === 'string' && arg.includes('same key')),
    );
    expect(duplicate).toBe(false);
  });
});
