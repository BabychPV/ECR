import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { emptyColumnDraft, type ColumnDraft } from '../column';

/**
 * Директива registry-lookup, PR A3: колонку `Lookup` конфігурували сирим
 * числовим `RegistryDefId` — автор шаблону мав знати ідентифікатор
 * напам'ять, узятий десь поза цим екраном. Тепер це вибір зі списку
 * довідників за назвою й кодом.
 *
 * ⛔ `Select`/`MultiSelect` (`@mantine/core`) під jsdom «зависають» —
 * відтворюваний факт, уже задокументований тричі в цьому репозиторії
 * (`approval-route-editor.test.tsx`, `user-access-editor.test.tsx`,
 * `GrantsPanel.resourceName.test.tsx`): жоден із них не рендерить `Select`
 * напряму саме тому. Емпірично відтворено й тут (окремим ізольованим
 * `_debug_bareselect`-тестом — голий `<Select searchable>` без жодного
 * зв'язку з `ColumnEditor` чи `useQuery` зависав так само, до власного
 * ліміту 30000мс). Обхід — той самий, що й у трьох попередніх місцях:
 * заглушуємо `Select` легким `<select>` з тими самими проп-ами
 * (`data`/`value`/`onChange`/`label`), керованим звичайним
 * `fireEvent.change`, без порталу й без floating-ui.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  type StubOption = { value: string; label: string };
  type StubSelectProps = {
    data?: (string | StubOption)[];
    value?: string | null;
    onChange?: (value: string | null) => void;
    label?: string;
    placeholder?: string;
    'aria-label'?: string;
  };

  function StubSelect(props: StubSelectProps): JSX.Element {
    const options = (props.data ?? []).map((item) =>
      typeof item === 'string' ? { value: item, label: item } : item,
    );

    return (
      <select
        aria-label={props['aria-label'] ?? props.label ?? props.placeholder}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.target.value === '' ? null : event.target.value)}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

const SeededStrings: Record<string, string> = {
  'columns.lookupRegistryDefId': 'Registry',
  'columns.lookupRegistryDefIdHint': 'The registry this column looks values up from.',
  'columns.lookupRegistryDefIdEmpty': 'No registries found',
  'common.cancel': 'Cancel',
  'columns.save': 'Save',
};

const registries = [
  { id: 7, code: 'PERMITS', nameL10n: { en: 'Permits' }, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master' },
  { id: 12, code: 'UNITS', nameL10n: { en: 'Measurement units' }, fields: [], isHierarchical: false, isTemporal: false, sourceKind: 'Master' },
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
    <MantineProvider>
      <QueryClientProvider client={client}>
        <ColumnEditor
          draft={draft}
          disabled={false}
          saving={false}
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

    const select = await screen.findByLabelText('Registry');
    await waitFor(() => {
      expect((select as HTMLSelectElement).querySelectorAll('option').length).toBeGreaterThan(1);
    });

    fireEvent.change(select, { target: { value: '7' } });

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ lookupRegistryDefId: 7 }));
  }, 60000);

  it('наявна колонка з уже заданим (сирим) ідентифікатором показує правильно вибраний довідник', async () => {
    const draft: ColumnDraft = { ...emptyColumnDraft(1), dataType: 'Lookup', lookupRegistryDefId: 12 };

    await show(draft, () => {});

    const select = await screen.findByLabelText('Registry');
    await waitFor(() => {
      expect((select as HTMLSelectElement).value).toBe('12');
    });
  }, 60000);
});
