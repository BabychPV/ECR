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
  'registries.permitHeaderOption': 'Permits: {name} ({code})',
  'registries.lookupGroup.permits': 'Permits',
  'registries.lookupGroup.dictionaries': 'Dictionaries',
  'registries.lookupGroup.unlisted': 'Current value',
  'registries.lookupTargetUnlisted': 'Not in the list (registry #{id})',
};

const registries = [
  { id: 7, code: 'PERMITS', nameL10n: { values: { en: 'Permits' } }, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master' },
];

// Вид цілі - ознака СЕРВЕРА (`kind`), а не префікс коду: `PRM_AIR` без `kind` лишився б довідником.
const permitHeaders = [
  { id: 77, code: 'PRM_AIR', nameL10n: { values: { en: 'Air permit' } }, fields: [], isHierarchical: false, isTemporal: true, sourceKind: 'Master', kind: 'PermitHeader' },
  {
    id: 91,
    code: 'PRMG_WATER',
    nameL10n: { values: { en: 'Water permits' } },
    descriptionL10n: { values: { en: 'All water permits of the site' } },
    fields: [],
    isHierarchical: false,
    isTemporal: true,
    sourceKind: 'Master',
    kind: 'PermitGroup',
  },
  // Довідник, який випадково зветься як група: за `kind` він лишається в «Dictionaries».
  { id: 8, code: 'PRMG_LEGACY', nameL10n: { values: { en: 'Legacy dictionary' } }, fields: [], isHierarchical: false, isTemporal: false, sourceKind: 'Master', kind: 'Dictionary' },
];

const registryRequests: string[] = [];

function mockFetch(): void {
  registryRequests.length = 0;
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
        registryRequests.push(url);

        // Шапки дозволів віддаються лише за явною згодою (звичайний перелік їх ховає).
        const listed = url.includes('includePermitHeaders=true') ? [...registries, ...permitHeaders] : registries;

        return new Response(JSON.stringify(listed), {
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

  // ✎ Permits (lookup-prm). Мутаційний доказ: поверни запит без `includePermitHeaders=true` (або старий ключ списку) —
  // `PRM_AIR` зникне з варіантів і тест почервоніє.
  it('шапка типу дозволу PRM_* пропонується як ціль Lookup з підписом «Permits: …» і кодом', async () => {
    const onChange = vi.fn();
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Permit',
      labelL10n: { en: 'Permit' },
      dataType: 'Lookup',
    };

    await show(draft, onChange);

    fireEvent.click(await screen.findByLabelText('Registry'));
    fireEvent.click(await screen.findByRole('option', { name: 'Permits: Air permit (PRM_AIR)' }));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ lookupRegistryDefId: 77 }));
    expect(registryRequests.every((url) => url.endsWith('/api/v1/registries?includePermitHeaders=true'))).toBe(true);
  }, 60000);

  // ✎ Permits (template-picker): група «Permits» стоїть ПЕРШОЮ, перед «Dictionaries» - у довгому списку (Land: 54+
  // довідників) плаский хвіст `PRM_*` не потрапляв людині на очі. Мутаційний доказ: поверни плаский список
  // (`registries.data.map(...)`) - груп не буде і тест почервоніє.
  it('шапки дозволів - окрема група «Permits» перед «Dictionaries»', async () => {
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Permit',
      labelL10n: { en: 'Permit' },
      dataType: 'Lookup',
    };

    await show(draft, vi.fn());

    fireEvent.click(await screen.findByLabelText('Registry'));
    const permitsGroup = await screen.findByText('Permits');
    const dictionariesGroup = await screen.findByText('Dictionaries');
    const air = await screen.findByRole('option', { name: 'Permits: Air permit (PRM_AIR)' });
    const plain = await screen.findByRole('option', { name: 'Permits (PERMITS)' });

    const before = (a: Element, b: Element): boolean => Boolean(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
    expect(before(permitsGroup, air)).toBe(true);
    expect(before(air, dictionariesGroup)).toBe(true);
    expect(before(dictionariesGroup, plain)).toBe(true);
  }, 60000);

  // ✎ Групи типів дозволів (PRMG). Вид цілі - `kind` СЕРВЕРА. Мутаційний доказ: поверни розпізнавання за префіксом
  // (`code.startsWith('PRM')`) - довідник `PRMG_LEGACY` потрапить у «Permits», а тест нижче почервоніє; прибери `PermitGroup`
  // з `isPermitTarget` - група впаде в «Dictionaries».
  it('група PRMG_* - у «Permits» (перед «Dictionaries»), довідник з тим самим префіксом - у «Dictionaries»', async () => {
    const draft: HeaderFieldDraft = { ...emptyHeaderFieldDraft(0), code: 'Permit', labelL10n: { en: 'Permit' }, dataType: 'Lookup' };

    await show(draft, vi.fn());

    fireEvent.click(await screen.findByLabelText('Registry'));
    const permitsGroup = await screen.findByText('Permits');
    const dictionariesGroup = await screen.findByText('Dictionaries');
    const water = await screen.findByRole('option', { name: /Water permits \(PRMG_WATER\)/ });
    const legacy = await screen.findByRole('option', { name: 'Legacy dictionary (PRMG_LEGACY)' });

    const before = (a: Element, b: Element): boolean => Boolean(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
    expect(before(permitsGroup, water)).toBe(true);
    expect(before(water, dictionariesGroup)).toBe(true);
    expect(before(dictionariesGroup, legacy)).toBe(true);
  }, 60000);

  it('опція групи двохрядкова: назва і приглушений опис; у звичайної цілі опису немає', async () => {
    const draft: HeaderFieldDraft = { ...emptyHeaderFieldDraft(0), code: 'Permit', labelL10n: { en: 'Permit' }, dataType: 'Lookup' };

    await show(draft, vi.fn());

    fireEvent.click(await screen.findByLabelText('Registry'));
    const water = await screen.findByRole('option', { name: /Water permits \(PRMG_WATER\)/ });
    expect(water.querySelector('[data-lookup-target-description]')?.textContent).toBe('All water permits of the site');
    expect((await screen.findByRole('option', { name: 'Permits: Air permit (PRM_AIR)' })).querySelector('[data-lookup-target-description]')).toBeNull();
    expect((await screen.findByRole('option', { name: 'Permits (PERMITS)' })).querySelector('[data-lookup-target-description]')).toBeNull();
  }, 60000);

  it('пошук знаходить групу по опису, регістр не важить; група без збігів зникає', async () => {
    const draft: HeaderFieldDraft = { ...emptyHeaderFieldDraft(0), code: 'Permit', labelL10n: { en: 'Permit' }, dataType: 'Lookup' };

    await show(draft, vi.fn());

    const input = await screen.findByLabelText('Registry');
    fireEvent.click(input);
    fireEvent.change(input, { target: { value: 'OF THE SITE' } });

    expect(await screen.findByRole('option', { name: /Water permits \(PRMG_WATER\)/ })).not.toBeNull();
    await waitFor(() => {
      expect(screen.queryByRole('option', { name: 'Permits (PERMITS)' })).toBeNull();
    });
    expect(screen.queryByText('Dictionaries')).toBeNull();
  }, 60000);

  // P3-2: поточної цілі немає в переліку (групу деактивовано / права немає) - порожнє значення вводило б в оману.
  // Мутаційний доказ: прибери `currentId` у `lookupTargetSelectData` - у полі буде порожньо, тест почервоніє.
  it('поточна ціль, якої немає в переліку, лишається видимою окремою опцією «unlisted»', async () => {
    const onChange = vi.fn();
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Permit',
      labelL10n: { en: 'Permit' },
      dataType: 'Lookup',
      lookupRegistryDefId: 555,
    };

    await show(draft, onChange);

    const input = (await screen.findByLabelText('Registry')) as HTMLInputElement;
    expect(input.value).toBe('Not in the list (registry #555)');
    fireEvent.click(input);
    expect(await screen.findByText('Current value')).not.toBeNull();
    expect(onChange).not.toHaveBeenCalled();
  }, 60000);

  it('поточна ціль, що Є в переліку, не дає зайвої опції «unlisted»', async () => {
    const draft: HeaderFieldDraft = {
      ...emptyHeaderFieldDraft(0),
      code: 'Permit',
      labelL10n: { en: 'Permit' },
      dataType: 'Lookup',
      lookupRegistryDefId: 7,
    };

    await show(draft, vi.fn());

    const input = (await screen.findByLabelText('Registry')) as HTMLInputElement;
    expect(input.value).toBe('Permits (PERMITS)');
    fireEvent.click(input);
    await screen.findByText('Dictionaries');
    expect(screen.queryByText('Current value')).toBeNull();
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
