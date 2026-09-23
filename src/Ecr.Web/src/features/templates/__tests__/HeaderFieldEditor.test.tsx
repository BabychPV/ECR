import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { renderWithQuery, testTheme } from '@/test/render';
import { emptyHeaderFieldDraft, type HeaderFieldDraft } from '../headerField';

/**
 * Форма поля шапки документа — за зразком `ColumnEditor.registryLookup.test.tsx`
 * і `ColumnEditor.silentEmpty.test.tsx`: вибір довідника лише для типу
 * Lookup (мутаційний доказ вимоги контракту) і причина, з якою «Зберегти»
 * недоступне, видна на екрані.
 */

const SeededStrings: Record<string, string> = {
  'headerFields.code': 'Code',
  'headerFields.codeHint': 'The address of the field in the API.',
  'headerFields.label': 'Label',
  'headerFields.labelHint': 'Shown to the person filling in the form.',
  'headerFields.dataType': 'Data type',
  'headerFields.dataTypeHint': 'Fixed once the field is created.',
  'headerFields.ordinal': 'Order',
  'headerFields.ordinalHint': 'Display order only.',
  'headerFields.required': 'Required',
  'headerFields.lookupRegistryDefId': 'Registry',
  'headerFields.lookupRegistryDefIdHint': 'The registry this field looks values up from.',
  'headerFields.lookupRegistryDefIdEmpty': 'No registries found',
  'headerFields.save': 'Save field',
  'headerFields.errCode': 'Give the field a code.',
  'headerFields.errCodeInvalid': 'The code can contain only Latin letters, digits, and underscores.',
  'headerFields.errLabel': 'Give the field a label in at least one language.',
  'headerFields.errLookupRequired': 'Pick a registry for a Lookup field.',
  'common.cancel': 'Cancel',
};

const registries = [
  { id: 7, code: 'PERMITS', nameL10n: { values: { en: 'Permits' } }, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master' },
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

async function show(
  draft: HeaderFieldDraft,
  onChange: (next: HeaderFieldDraft) => void = () => {},
  onSubmit: () => void = () => {},
): Promise<void> {
  mockFetch();
  await loadCatalog('en', 'private');

  const { HeaderFieldEditor } = await import('../HeaderFieldEditor');

  renderWithQuery(
    <HeaderFieldEditor
      draft={draft}
      disabled={false}
      saving={false}
      onChange={onChange}
      onSubmit={onSubmit}
      onCancel={() => {}}
    />,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('HeaderFieldEditor: вибір довідника лише для типу Lookup', () => {
  it('тип Lookup — поле довідника на екрані', async () => {
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Region',
      labelL10n: { en: 'Region' },
      dataType: 'Lookup',
    };

    await show(draft);

    expect(await screen.findByLabelText('Registry')).not.toBeNull();
  });

  it('тип НЕ Lookup (String) — поле довідника відсутнє взагалі, навіть якщо чернетка несе стале значення', async () => {
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Region',
      labelL10n: { en: 'Region' },
      dataType: 'String',
      lookupRegistryDefId: 7,
    };

    await show(draft);

    // ⚠ Спершу дочекатися стабілізації форми (напис підказки поля коду
    // точно на екрані), і лише тоді стверджувати відсутність — інакше
    // «ще не встигло домалюватись» видавалося б за «правильно сховано».
    await screen.findByLabelText('Code');
    expect(screen.queryByLabelText('Registry')).toBeNull();
  });

  it('вибір довідника зі списку виставляє числовий lookupRegistryDefId', async () => {
    const onChange = vi.fn();
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Region',
      labelL10n: { en: 'Region' },
      dataType: 'Lookup',
    };

    await show(draft, onChange);

    fireEvent.click(await screen.findByLabelText('Registry'));
    fireEvent.click(await screen.findByRole('option', { name: 'Permits (PERMITS)' }));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ lookupRegistryDefId: 7 }));
  }, 60000);

  /**
   * ⚠ Довідники з попереднього рендеру ВЖЕ в кеші того самого `QueryClient`
   * (`enabled: isLookup` контролює лише НОВИЙ запит, не читання кеша) — тому
   * `registries.isPending` тут `false` ще ДО будь-якого мережевого виклику
   * для String-поля. Якщо прибрати `isLookup &&` перед `<Select>`
   * (`HeaderFieldEditor.tsx`), цей тест — і лише він — упіймає це: два тести
   * вище проходять і без цієї умови, бо для НОВОГО типу запит іще не
   * розв'язаний (`isPending: true`) з іншої причини.
   */
  it('перемикання типу з Lookup на String ховає поле довідника, навіть коли довідники вже в кеші', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { HeaderFieldEditor } = await import('../HeaderFieldEditor');

    const lookupDraft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Region',
      labelL10n: { en: 'Region' },
      dataType: 'Lookup',
    };

    const { rerender } = render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <HeaderFieldEditor
            draft={lookupDraft}
            disabled={false}
            saving={false}
            onChange={() => {}}
            onSubmit={() => {}}
            onCancel={() => {}}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    // Довідники доїхали й лягли в кеш цього самого клієнта.
    await screen.findByLabelText('Registry');

    const stringDraft: HeaderFieldDraft = { ...lookupDraft, dataType: 'String', lookupRegistryDefId: 7 };

    rerender(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <HeaderFieldEditor
            draft={stringDraft}
            disabled={false}
            saving={false}
            onChange={() => {}}
            onSubmit={() => {}}
            onCancel={() => {}}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByLabelText('Code');
    expect(screen.queryByLabelText('Registry')).toBeNull();
  }, 60000);
});

describe('HeaderFieldEditor: валідація блокує збереження', () => {
  it('тип Lookup без обраного довідника — причина на екрані, «Зберегти» вимкнено', async () => {
    const onSubmit = vi.fn();
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Region',
      labelL10n: { en: 'Region' },
      dataType: 'Lookup',
      lookupRegistryDefId: null,
    };

    await show(draft, () => {}, onSubmit);

    await waitFor(() => {
      expect(screen.getByText('Pick a registry for a Lookup field.')).not.toBeNull();
    });

    const save = screen.getByRole('button', { name: 'Save field' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.click(save);
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('порожній підпис — причина на екрані, «Зберегти» вимкнено', async () => {
    const draft: HeaderFieldDraft = { ...emptyHeaderFieldDraft(0), code: 'Region', labelL10n: {} };

    await show(draft);

    expect(
      await screen.findByText('Give the field a label in at least one language.'),
    ).not.toBeNull();

    const save = screen.getByRole('button', { name: 'Save field' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
  });

  it('коректна чернетка — жодної причини, «Зберегти» доступне', async () => {
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Region',
      labelL10n: { en: 'Region' },
    };

    await show(draft);

    await screen.findByLabelText('Code');
    expect(screen.queryByRole('alert')).toBeNull();

    const save = screen.getByRole('button', { name: 'Save field' }) as HTMLButtonElement;
    expect(save.disabled).toBe(false);
  });
});
