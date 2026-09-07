import type { JSX } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { RegistryDefinitionDto, RegistryHistoryEntryDto } from '@/api/types';
import {
  RegistryFields,
  RegistryHistory,
  RegistryMappings,
  RegistryRelations,
  RegistryRules,
} from '@/features/registries/RegistryConstructor';
import {
  RuleKinds,
  buildSaveRequest,
  emptyRule,
  isComplete,
  toDraft,
} from '@/features/registries/definition';

/**
 * Конструктор довідника (`ФВ-8.12`).
 *
 * ⛔ Перевіряються **компоненти**, а не сторінка цілком — з тієї самої
 * причини, що й у перегляді мапінгу (`D1-12`): рендер сторінки з `Tabs` і
 * запитами в jsdom іде хвилинами. Сторінка як ціле лишається під
 * a11y-прогоном, який платить цю ціну один раз.
 *
 * ⚠ Каталог рядків тут не завантажений, тому підписи приходять ключами в
 * `⟦…⟧`. Перевіряються **дані**: код поля, вид зв'язку, вид правила, шлях
 * тега — тобто рівно те, заради чого екран існує.
 */
const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: true,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 11,
  fields: [
    {
      id: 41,
      code: 'Number',
      nameL10n: { values: { en: 'Permit number' } },
      dataType: 'String',
      isRequired: true,
      isScopeField: true,
      lookupRegistryDefId: null,
      unitId: null,
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
    {
      id: 44,
      code: 'Limit',
      nameL10n: { values: { en: 'Annual limit' } },
      dataType: 'Decimal',
      isRequired: false,
      isScopeField: false,
      lookupRegistryDefId: null,
      unitId: 7,
    },
  ],
  relations: [
    {
      kind: 'Cascade',
      fieldCode: 'Substance',
      targetRegistryDefId: 5,
      targetRegistryCode: 'SUBSTANCE',
      linkKind: null,
      linkCount: null,
    },
    {
      kind: 'Association',
      fieldCode: null,
      targetRegistryDefId: null,
      targetRegistryCode: null,
      linkKind: 'permit-water-body',
      linkCount: 12,
    },
  ],
  rules: [
    {
      id: 101,
      code: 'IndicatorRequired',
      ruleKind: 'RequiredWhen',
      expression: "[Type] = 'Emission'",
      severity: 'Error',
      messageL10n: { values: { en: 'Indicator is required' } },
      parametersJson: null,
      isActive: true,
    },
    {
      id: 102,
      code: 'NumberUnique',
      ruleKind: 'UniqueWithin',
      expression: '[Number]',
      severity: 'Error',
      messageL10n: { values: { en: 'Duplicate number' } },
      parametersJson: null,
      isActive: false,
    },
  ],
  mappings: [
    {
      fieldMapId: 7,
      fieldCode: 'Limit',
      sourceCode: 'PI_PERMITS',
      sourceField: 'Permit_Limit',
      transformCode: 'Last',
      sourceUnitCode: 'm3',
      targetUnitCode: 'km3',
      isActive: true,
    },
  ],
};

const History: RegistryHistoryEntryDto[] = [
  {
    changedAt: '2026-05-01T10:00:00Z',
    entityType: 'cfg.RegistryDef',
    operation: 'SaveDefinition',
    oldJson: '{}',
    newJson: '{}',
    changeReason: 'ліміт перенесено з Configuration!J3',
    changedByUserId: 9,
  },
];

function show(node: JSX.Element): void {
  render(<MantineProvider>{node}</MantineProvider>);
}

describe('Конструктор довідника', () => {
  it('ФВ-8.12: показує поля довідника разом із типом і ознакою ключа', () => {
    // ⛔ Ознака «ключове» саме в рядку поля: за ключовими полями звужується
    // доступ (`RoleAssignment.ScopeJson`), і пошук по всьому екрану лишався б
    // зеленим, навіть якби колонку прибрали цілком.
    show(<RegistryFields definition={Definition} />);

    const row = screen.getByText('Number').closest('tr');
    expect(row).not.toBeNull();
    expect(within(row!).getByText('String')).toBeDefined();
    expect(within(row!).getAllByText(/registries\.yes/).length).toBe(2);

    const lookup = screen.getByText('Substance').closest('tr');
    expect(within(lookup!).getByText('Lookup')).toBeDefined();
    expect(within(lookup!).getByText('5')).toBeDefined();
  });

  it('ФВ-8.12: показує зв\'язки — і каскад через поле, і M:N із даних', () => {
    // ⚠ Зв'язки обчислені, а не оголошені (`Q-027`): каскад бачать через поле,
    // M:N — через кількість зв'язків у записах. Один без одного лишає
    // половину `ФВ-8.4` невидимою.
    show(<RegistryRelations definition={Definition} />);

    const cascade = screen.getByText('Cascade').closest('tr');
    expect(within(cascade!).getByText('Substance')).toBeDefined();
    expect(within(cascade!).getByText('SUBSTANCE')).toBeDefined();

    const association = screen.getByText('Association').closest('tr');
    expect(within(association!).getByText('permit-water-body')).toBeDefined();
    expect(within(association!).getByText('12')).toBeDefined();
  });

  it('ФВ-8.12: показує правила довідника, включно з вимкненим', () => {
    // ⚠ Вимкнене правило лишається у списку: правило, що колись діяло, —
    // єдине пояснення того, чому наявні записи виглядають саме так.
    show(
      <RegistryRules
        rules={Definition.rules.map((rule) => toDraft(rule, 'en'))}
        canEdit={false}
        onChange={vi.fn()}
        onAdd={vi.fn()}
      />,
    );

    expect(screen.getByDisplayValue("[Type] = 'Emission'")).toBeDefined();
    expect(screen.getByText('RequiredWhen')).toBeDefined();

    const disabled = screen.getByDisplayValue('[Number]');
    expect(disabled).toBeDefined();
    expect(screen.getByText('UniqueWithin')).toBeDefined();

    // Прапорець «діє» знятий саме у вимкненого правила.
    const checkboxes = screen.getAllByRole('checkbox');
    expect(checkboxes.map((box) => (box as HTMLInputElement).checked)).toEqual([true, false]);
  });

  it('ФВ-8.12: у виборі виду правила рівно чотири види і серед них немає ValidityWindow', () => {
    // ⛔ Директива №06 `H-10`. П'ятий вид, `ValidityWindow`, стоїть у
    // `reference/design/07` і виглядає правдоподібно — але вікно чинності це
    // ПОЛЯ запису (`ФВ-8.5`), і правило-дублер дало б два джерела істини про
    // чинність, які розійшлися б мовчки на межі вікна.
    show(
      <RegistryRules
        rules={[emptyRule('Expression')]}
        canEdit
        onChange={vi.fn()}
        onAdd={vi.fn()}
      />,
    );

    const kinds = screen.getByRole('combobox', { name: /registries\.ruleKind/ });
    const options = within(kinds).getAllByRole('option').map((option) => option.textContent);

    expect(options).toEqual(['RequiredWhen', 'UniqueWithin', 'Expression', 'CrossRegistry']);
    expect(options).not.toContain('ValidityWindow');
    expect(RuleKinds.length).toBe(4);
  });

  it('ФВ-8.12: показує мапінг поля разом з обома одиницями', () => {
    // ⚠ Розбіжність між одиницею джерела і цільовою — «найчастіше джерело
    // мовчазних розбіжностей у числах» (`ФВ-16.9`), і побачити її можна лише
    // тоді, коли на екрані стоять обидві.
    show(<RegistryMappings definition={Definition} />);

    const row = screen.getByText('Permit_Limit').closest('tr');
    expect(within(row!).getByText('Limit')).toBeDefined();
    expect(within(row!).getByText('PI_PERMITS')).toBeDefined();
    expect(within(row!).getByText(/m3\s*→\s*km3/)).toBeDefined();
  });

  it('ФВ-8.12: історія називає причину зміни, а не лише її факт', () => {
    // ⛔ Питання, на яке цей екран відповідає, — «чому тут з'явилося це поле».
    // Дата й операція без причини на нього не відповідають.
    show(<RegistryHistory entries={History} />);

    const row = screen.getByText('SaveDefinition').closest('tr');
    expect(within(row!).getByText('ліміт перенесено з Configuration!J3')).toBeDefined();
    expect(within(row!).getByText('9')).toBeDefined();
  });

  it('порожній перелік у кожній області каже про це прямо, а не мовчить', () => {
    // ⚠ `ФВ-14.22`: порожнеча без слів читається як «не відпрацювало».
    const bare = { ...Definition, relations: [], rules: [], mappings: [] };

    show(<RegistryRelations definition={bare} />);
    expect(screen.getByText(/registries\.noRelations/)).toBeDefined();

    show(<RegistryMappings definition={bare} />);
    expect(screen.getByText(/registries\.noMappings/)).toBeDefined();

    show(<RegistryHistory entries={[]} />);
    expect(screen.getByText(/registries\.noHistory/)).toBeDefined();
  });
});

describe('Збереження опису довідника', () => {
  it('надсилає поля незмінними — інакше сервер прочитає це як прохання їх видалити', () => {
    // ⛔ `dic.RegistryValue` посилається на поле зовнішнім ключем. Поле,
    // яке не потрапило в запит, сервер відхиляє (`ECR-REG-0422`), і мовчазна
    // втрата одного поля у формі означала б, що збереження не проходить
    // ніколи — без жодної підказки, чому саме.
    const request = buildSaveRequest(
      Definition,
      Definition.rules.map((rule) => toDraft(rule, 'en')),
      '  причина  ',
      'en',
    );

    expect(request.fields.map((field) => field.id)).toEqual([41, 42, 44]);

    // ⛔ Ключовість переноситься з `isScopeField`: саме ключові поля складають
    // бізнес-ключ, і надіслати сюди `false` означало б попросити його прибрати.
    expect(request.fields.filter((field) => field.isKey).map((f) => f.code)).toEqual(['Number']);

    // Причина обрізається: сервер вимагає її непорожньою, і рядок із пробілів
    // виглядав би заповненим.
    expect(request.reason).toBe('причина');
  });

  it('нове правило надсилається без ідентифікатора, наявне — зі своїм', () => {
    // ⚠ Сервер розрізняє створення і правку саме за `id`. Вигаданий
    // ідентифікатор означав би правку ЧУЖОГО правила.
    const rules = [
      ...Definition.rules.map((rule) => toDraft(rule, 'en')),
      { ...emptyRule('CrossRegistry'), code: 'SubstanceExists', expression: '[Substance]' },
    ];

    const request = buildSaveRequest(Definition, rules, 'нове правило', 'en');

    expect(request.rules.map((rule) => rule.id)).toEqual([101, 102, null]);
    expect(request.rules[2]?.ruleKind).toBe('CrossRegistry');
  });

  it('правило без умови не вважається готовим до збереження', () => {
    // ⛔ Порожній предикат — не «правило без умови», а правило, яке не
    // спрацює ніколи і виглядатиме при цьому налаштованим.
    expect(isComplete(emptyRule('Expression'))).toBe(false);
    expect(isComplete({ ...emptyRule('Expression'), code: 'A' })).toBe(false);
    expect(isComplete({ ...emptyRule('Expression'), code: 'A', expression: '[X] > 0' })).toBe(true);

    // Вид поза переліком із чотирьох не проходить теж: `H-10`.
    expect(
      isComplete({
        ...emptyRule('Expression'),
        code: 'A',
        expression: '[X] > 0',
        ruleKind: 'ValidityWindow',
      }),
    ).toBe(false);
  });
});

