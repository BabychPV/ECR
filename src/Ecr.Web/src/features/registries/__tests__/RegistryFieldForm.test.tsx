import type { JSX } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { RegistryFields } from '@/features/registries/RegistryConstructor';
import { RegistryFieldForm } from '@/features/registries/RegistryFieldForm';
import { emptyField, isFieldComplete, type FieldDraft } from '@/features/registries/definition';
import { draftNewFields } from '@/features/registries/registryDraft';

/**
 * Форма нового поля довідника: кожна властивість — підписаний контрол із підказкою.
 *
 * ⚠ Каталог рядків не завантажений: підписи й підказки приходять ключами в `⟦…⟧`, тому тести
 * перевіряють наявність КЛЮЧІВ підказок (і що вони прив'язані до контролу), а не англійський текст.
 */
const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: false,
  sourceKind: 'Local',
  definitionVersion: 1,
  dataRevision: 1,
  fields: [
    {
      id: 41,
      code: 'Limit',
      nameL10n: { values: { en: 'Annual limit' } },
      dataType: 'Decimal',
      isRequired: false,
      isScopeField: false,
      lookupRegistryDefId: null,
      unitId: 7,
    },
    {
      id: 42,
      code: 'Substance',
      nameL10n: { values: { en: 'Substance name' } },
      dataType: 'Lookup',
      isRequired: false,
      isScopeField: false,
      lookupRegistryDefId: 5,
      unitId: null,
    },
  ],
  relations: [],
  rules: [],
  mappings: [],
};

const Registries = [{ value: '5', label: 'Substances (SUBSTANCE)' }];
const Units = [
  { value: '7', label: 't (Mass)' },
  { value: '9', label: 'm3 (Volume)' },
];

function show(node: JSX.Element): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

function form(draft: FieldDraft, over: Partial<Parameters<typeof RegistryFieldForm>[0]> = {}): JSX.Element {
  return (
    <RegistryFieldForm
      definition={Definition}
      draft={draft}
      index={0}
      otherNew={[draft]}
      canEdit
      mayBeRequired
      registryOptions={Registries}
      unitOptions={Units}
      onChange={vi.fn()}
      onRemove={vi.fn()}
      {...over}
    />
  );
}

describe('Форма поля довідника', () => {
  it('кожна властивість має підпис і підказку, прив\'язану до контролу', () => {
    show(form(emptyField()));

    // Підказка не просто лежить поруч, а описує саме свій контрол (`aria-describedby`).
    expect(screen.getByLabelText(/registries\.code/).getAttribute('aria-describedby')).not.toBeNull();
    expect(screen.getByText(/registries\.fieldForm\.codeHint/)).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.nameHint/)).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.type\.String/)).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.requiredHint/)).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.keyHint/)).toBeDefined();
    expect(screen.getByRole('checkbox', { name: /registries\.required/ })).toBeDefined();
    expect(screen.getByRole('checkbox', { name: /registries\.keyField/ })).toBeDefined();
  });

  it('підказка типу змінюється разом із типом', () => {
    show(form({ ...emptyField('Date') }));

    expect(screen.getByText(/registries\.fieldForm\.type\.Date/)).toBeDefined();
    expect(screen.queryByText(/registries\.fieldForm\.type\.String/)).toBeNull();
  });

  it('порожні код і назва показують помилку; без права редагувати — ні', () => {
    const { unmount } = render(<MantineProvider>{form(emptyField())}</MantineProvider>);
    expect(screen.getByText(/registries\.fieldForm\.errCode/)).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.errName/)).toBeDefined();
    unmount();

    show(form(emptyField(), { canEdit: false }));
    expect(screen.queryByText(/registries\.fieldForm\.errCode/)).toBeNull();
  });

  it('«Обов\'язкове» активне, коли записів немає, і долітає в onChange', () => {
    const onChange = vi.fn();
    const draft = emptyField();
    show(form(draft, { onChange }));

    const box = screen.getByRole('checkbox', { name: /registries\.required/ });
    expect((box as HTMLInputElement).disabled).toBe(false);

    fireEvent.click(box);
    expect(onChange).toHaveBeenLastCalledWith({ ...draft, isRequired: true });
  });

  it('«Обов\'язкове» вимкнене з поясненням, коли в довіднику вже є записи', () => {
    // ⛔ Мутаційна проба: прибрати `requiredLocked` у формі — перевірка падає.
    show(form(emptyField(), { mayBeRequired: false }));

    const box = screen.getByRole('checkbox', { name: /registries\.required/ });
    expect((box as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText(/registries\.fieldForm\.requiredLocked/)).toBeDefined();
    expect(screen.queryByText(/registries\.fieldForm\.requiredHint/)).toBeNull();
  });

  it('прапорець із чернетки при наявних записях лишається знімним і показує помилку', () => {
    const onChange = vi.fn();
    const draft = { ...emptyField(), isRequired: true };
    show(form(draft, { mayBeRequired: false, onChange }));

    const box = screen.getByRole('checkbox', { name: /registries\.required/ });
    expect((box as HTMLInputElement).disabled).toBe(false);
    expect(screen.getByText(/registries\.fieldForm\.requiredLocked/)).toBeDefined();

    fireEvent.click(box);
    expect(onChange).toHaveBeenLastCalledWith({ ...draft, isRequired: false });

    // І збереження такої чернетки заблоковане, доки прапорець не знято.
    expect(isFieldComplete({ ...draft, code: 'A', name: 'A' }, false)).toBe(false);
  });

  it('вибір довідника-цілі — за назвою, лише для Lookup', () => {
    const onChange = vi.fn();
    const draft = emptyField('Lookup');
    show(form(draft, { onChange }));

    const target = screen.getByRole('combobox', { name: /registries\.lookup/ });
    expect(within(target).getByText('Substances (SUBSTANCE)')).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.errLookup/)).toBeDefined();

    fireEvent.change(target, { target: { value: '5' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...draft, lookupRegistryDefId: 5 });

    // Одиниці для Lookup немає.
    expect(screen.queryByRole('combobox', { name: /registries\.unit/ })).toBeNull();
  });

  it('одиниця — вибір зі списку, а не голе число; лише для Int і Decimal', () => {
    const onChange = vi.fn();
    const draft = emptyField('Decimal');
    show(form(draft, { onChange }));

    const unit = screen.getByRole('combobox', { name: /registries\.unit/ });
    expect(within(unit).getByText('m3 (Volume)')).toBeDefined();
    expect(screen.queryByRole('spinbutton')).toBeNull();

    fireEvent.change(unit, { target: { value: '9' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...draft, unitId: 9 });

    fireEvent.change(unit, { target: { value: '' } });
    expect(onChange).toHaveBeenLastCalledWith({ ...draft, unitId: null });
  });

  it('перемикання типу скидає ціль і одиницю, що йому не потрібні', () => {
    const onChange = vi.fn();
    const draft: FieldDraft = { ...emptyField('Decimal'), unitId: 9 };
    show(form(draft, { onChange }));

    fireEvent.change(screen.getByRole('combobox', { name: /registries\.dataType/ }), {
      target: { value: 'Bool' },
    });
    expect(onChange).toHaveBeenLastCalledWith({ ...draft, dataType: 'Bool', unitId: null });
  });
});

describe('Таблиця полів і форма нового поля', () => {
  it('наявні поля показують ціль і одиницю назвою, а не числовим ідентифікатором', () => {
    show(
      <RegistryFields
        definition={Definition}
        canEdit={false}
        newFields={[]}
        registryOptions={Registries}
        unitOptions={Units}
        entryCount={0}
        onAddField={vi.fn()}
        onChangeField={vi.fn()}
        onRemoveField={vi.fn()}
      />,
    );

    const limit = screen.getByText('Limit').closest('tr');
    expect(within(limit!).getByText('t (Mass)')).toBeDefined();
    const substance = screen.getByText('Substance').closest('tr');
    expect(within(substance!).getByText('Substances (SUBSTANCE)')).toBeDefined();
    expect(screen.getByText(/registries\.fieldForm\.intro/)).toBeDefined();
  });

  it('entryCount: нуль — обов\'язкове дозволене; більше нуля або невідомо — ні', () => {
    const props = {
      definition: Definition,
      canEdit: true,
      newFields: [emptyField()],
      registryOptions: Registries,
      unitOptions: Units,
      onAddField: vi.fn(),
      onChangeField: vi.fn(),
      onRemoveField: vi.fn(),
    };

    const { rerender } = render(
      <MantineProvider>
        <RegistryFields {...props} entryCount={0} />
      </MantineProvider>,
    );
    const enabled = (): boolean =>
      !(screen.getByRole('checkbox', { name: /registries\.required/ }) as HTMLInputElement).disabled;
    expect(enabled()).toBe(true);

    for (const count of [3, null]) {
      rerender(
        <MantineProvider>
          <RegistryFields {...props} entryCount={count} />
        </MantineProvider>,
      );
      expect(enabled()).toBe(false);
    }
  });
});

describe('Чернетка нового поля', () => {
  it('відновлює обов\'язковість із чернетки сервера', () => {
    // ⛔ Мутаційна проба: прибрати `isRequired` у `draftNewFields` — прапорець губиться після
    // перезавантаження сторінки, і наступне збереження мовчки знімає його.
    const fields = draftNewFields(
      {
        fields: [
          {
            id: null,
            code: 'Owner',
            nameL10n: { values: { en: 'Owner' } },
            dataType: 'String',
            ordinal: 1,
            isRequired: true,
            isKey: true,
            lookupRegistryDefId: null,
            unitId: null,
          },
        ],
        rules: [],
        reason: 'r',
      } as unknown as Parameters<typeof draftNewFields>[0],
      'en',
    );

    expect(fields[0]?.isRequired).toBe(true);
    expect(fields[0]?.isKey).toBe(true);
  });
});
