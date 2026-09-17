import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { emptyColumnDraft, type ColumnDraft } from '../column';
import { testTheme } from '@/test/render';

/**
 * Директива registry-lookup, PR A3: колонку `Lookup` конфігурували сирим
 * числовим `RegistryDefId` — автор шаблону мав знати ідентифікатор
 * напам'ять, узятий десь поза цим екраном. Тепер це вибір зі списку
 * довідників за назвою й кодом.
 *
 * ✎ Тут `Select` підмінявся саморобним `<select>` — нібито тому, що
 * справжній «зависає під jsdom» (`Q-299`). Причина зависання знайдена й
 * усунена: взаємна рекурсія jsdom ↔ nwsapi на станових псевдокласах
 * (коментар у `src/test/setup.ts`). Тест працює зі справжнім `Select`.
 *
 * ⚠ Випадний список Mantine рендериться в порталі поза деревом форми, тому
 * опції шукаються через `screen`, а не через `within(...)`.
 */

const SeededStrings: Record<string, string> = {
  'columns.lookupRegistryDefId': 'Registry',
  'columns.lookupRegistryDefIdHint': 'The registry this column looks values up from.',
  'columns.lookupRegistryDefIdEmpty': 'No registries found',
  'common.cancel': 'Cancel',
  'columns.save': 'Save',
};

/**
 * ⚠ `nameL10n` — це `{ values: { <мова>: … } }`, як його віддає сервер
 * (`shared/i18n/localized.ts`), а не плаский `{ en: … }`. Поки `Select` був
 * заглушений, тест не читав підпису опції взагалі, і хибна форма фікстури
 * лишалася непоміченою: справжній компонент малював « (PERMITS)» без назви.
 */
const registries = [
  { id: 7, code: 'PERMITS', nameL10n: { values: { en: 'Permits' } }, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master' },
  { id: 12, code: 'UNITS', nameL10n: { values: { en: 'Measurement units' } }, fields: [], isHierarchical: false, isTemporal: false, sourceKind: 'Master' },
];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/registries')) {
        return new Response(JSON.stringify(registries), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

async function show(draft: ColumnDraft, onChange: (next: ColumnDraft) => void): Promise<void> {
  mockFetch();
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { ColumnEditor } = await import('../ColumnEditor');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ColumnEditor
          draft={draft}
          disabled={false}
          saving={false}
          templateVersionId={1}
          onChange={onChange}
          onSubmit={() => {}}
          onCancel={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ColumnEditor: вибір довідника за назвою (аудит-пас, PR A3)', () => {
  it('вибір довідника зі списку виставляє числовий lookupRegistryDefId', async () => {
    const onChange = vi.fn();
    const draft: ColumnDraft = { ...emptyColumnDraft(1), dataType: 'Lookup' };

    await show(draft, onChange);

    fireEvent.click(await screen.findByLabelText('Registry'));

    // Довідник обирається за назвою й кодом, а не за сирим числом — саме
    // це предмет картки.
    fireEvent.click(await screen.findByRole('option', { name: 'Permits (PERMITS)' }));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ lookupRegistryDefId: 7 }));
  }, 60000);

  it('наявна колонка з уже заданим (сирим) ідентифікатором показує правильно вибраний довідник', async () => {
    const draft: ColumnDraft = { ...emptyColumnDraft(1), dataType: 'Lookup', lookupRegistryDefId: 12 };

    await show(draft, () => {});

    const select = await screen.findByLabelText('Registry');
    await waitFor(() => {
      expect((select as HTMLInputElement).value).toBe('Measurement units (UNITS)');
    });
  }, 60000);
});
