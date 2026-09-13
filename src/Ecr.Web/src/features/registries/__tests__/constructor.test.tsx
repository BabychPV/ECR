import type { JSX } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
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
  emptyField,
  emptyRule,
  isComplete,
  isFieldComplete,
  toDraft,
  type FieldDraft,
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
    show(
      <RegistryFields
        definition={Definition}
        canEdit={false}
        newFields={[]}
        registryOptions={[]}
        onAddField={vi.fn()}
        onChangeField={vi.fn()}
        onRemoveField={vi.fn()}
      />,
    );

    const row = screen.getByText('Number').closest('tr');
    expect(row).not.toBeNull();
    expect(within(row!).getByText('String')).toBeDefined();
    expect(within(row!).getAllByText(/registries\.yes/).length).toBe(2);

    const lookup = screen.getByText('Substance').closest('tr');
    expect(within(lookup!).getByText('Lookup')).toBeDefined();
    expect(within(lookup!).getByText('5')).toBeDefined();
  });

  it('Q-lane4: кнопка «додати поле» стоїть лише коли можна редагувати опис', () => {
    // ⛔ Це і є фікс дефекту: до нього на вкладці «Fields» не було ЖОДНОГО
    // контролю, що додавав поле, — на відміну від сусідньої вкладки «Rules»
    // (`registries.addRule`), у якої кнопка стоїть завжди, навіть при нулі
    // правил. Без права `Registry.EditDefinition` кнопка не показується —
    // так само, як «Save definition» на сторінці-контейнері.
    const { rerender } = render(
      <MantineProvider>
        <RegistryFields
          definition={Definition}
          canEdit={false}
          newFields={[]}
          registryOptions={[]}
          onAddField={vi.fn()}
          onChangeField={vi.fn()}
          onRemoveField={vi.fn()}
        />
      </MantineProvider>,
    );

    expect(screen.queryByRole('button', { name: /registries\.addField/ })).toBeNull();

    rerender(
      <MantineProvider>
        <RegistryFields
          definition={Definition}
          canEdit
          newFields={[]}
          registryOptions={[]}
          onAddField={vi.fn()}
          onChangeField={vi.fn()}
          onRemoveField={vi.fn()}
        />
      </MantineProvider>,
    );

    expect(screen.getByRole('button', { name: /registries\.addField/ })).toBeDefined();
  });

  it('Q-lane4: клік на «додати поле» повідомляє сторінку — компонент не тримає власного стану чернетки', () => {
    // ⚠ Так само, як `RegistryRules`/`onAdd`: список нових полів живе на
    // сторінці (`RegistryConstructorPage.tsx`), а не в цьому компоненті —
    // інакше він загубився б при перемиканні вкладок (`keepMounted={false}`).
    const onAddField = vi.fn();
    show(
      <RegistryFields
        definition={Definition}
        canEdit
        newFields={[]}
        registryOptions={[]}
        onAddField={onAddField}
        onChangeField={vi.fn()}
        onRemoveField={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /registries\.addField/ }));
    expect(onAddField).toHaveBeenCalledTimes(1);
  });

  it('Q-lane4: рядок нового поля редагується — код, назва, тип і ключовість долітають у onChangeField', () => {
    // ⛔ Мутаційна проба: якщо форма перестане передавати введене в
    // `onChangeField`, цей тест провалиться на КОЖНОМУ полі окремо — не лише
    // на першому натиску клавіші.
    //
    // ⚠ `fireEvent.change` (одна подія з готовим значенням), а не
    // посимвольний `userEvent.type`: чернетка тут — СТАТИЧНИЙ проп (мок
    // `onChangeField` не оновлює `newFields` назад), тож контрольований інпут
    // після кожного натиску відкочувався б до порожнього значення і на
    // виході лишалася б тільки остання літера.
    const onChangeField = vi.fn();
    const draft = emptyField();

    show(
      <RegistryFields
        definition={Definition}
        canEdit
        newFields={[draft]}
        registryOptions={[{ value: '5', label: 'Substance (SUBSTANCE)' }]}
        onAddField={vi.fn()}
        onChangeField={onChangeField}
        onRemoveField={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText(/registries\.code/), { target: { value: 'Owner' } });
    expect(onChangeField).toHaveBeenLastCalledWith(0, { ...draft, code: 'Owner' });

    fireEvent.change(screen.getByLabelText(/registries\.name/), { target: { value: 'Owner' } });
    expect(onChangeField).toHaveBeenLastCalledWith(0, { ...draft, name: 'Owner' });

    fireEvent.click(screen.getByRole('checkbox', { name: /registries\.keyField/ }));
    expect(onChangeField).toHaveBeenLastCalledWith(0, { ...draft, isKey: true });

    // Перемикання типу на `Lookup` показує вибір цілі — а не просто змінює
    // клітинку тексту на select без наслідків.
    fireEvent.change(screen.getByRole('combobox', { name: /registries\.dataType/ }), {
      target: { value: 'Lookup' },
    });
    expect(onChangeField).toHaveBeenLastCalledWith(0, { ...draft, dataType: 'Lookup' });
  });

  it('Q-lane4: кнопка «прибрати» стосується лише НЕЗБЕРЕЖЕНОГО чернеткового поля', () => {
    // ⛔ Наявне поле («Number», «Substance», «Limit» — уже в `Definition.fields`)
    // не має власної кнопки видалення взагалі: D2-202 забороняє прибирати
    // наявне поле навіть побічно. Кнопка стоїть лише в рядку чернетки.
    const onRemoveField = vi.fn();
    show(
      <RegistryFields
        definition={Definition}
        canEdit
        newFields={[emptyField(), emptyField()]}
        registryOptions={[]}
        onAddField={vi.fn()}
        onChangeField={vi.fn()}
        onRemoveField={onRemoveField}
      />,
    );

    const removeButtons = screen.getAllByRole('button', { name: /registries\.removeField/ });
    expect(removeButtons.length).toBe(2);

    fireEvent.click(removeButtons[1]!);
    expect(onRemoveField).toHaveBeenCalledWith(1);

    // Наявні поля з `Definition` не отримали кнопки видалення.
    const savedRow = screen.getByText('Number').closest('tr');
    expect(within(savedRow!).queryByRole('button')).toBeNull();
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
    //
    // ⚠ Запит переліку користувачів тут ще НЕ завершився (`usersResolved`
    // хибний, мапа порожня) — саме тому автор показаний голим ідентифікатором
    // БЕЗ бейджа «нерозв'язано»: цей тест перевіряє причину, а не резолюцію
    // автора (для неї — окремі тести нижче).
    show(<RegistryHistory entries={History} userNames={new Map()} usersResolved={false} />);

    const row = screen.getByText('SaveDefinition').closest('tr');
    expect(within(row!).getByText('ліміт перенесено з Configuration!J3')).toBeDefined();
    expect(within(row!).getByText('9')).toBeDefined();
  });

  it('ФВ-8.12/Q-296: автор історії показаний ІМ\'ЯМ, знайденим у переліку користувачів', () => {
    // ⛔ Голий `changedByUserId` не відповідає нікому, крім того, хто тримає в
    // голові таблицю користувачів (сиблінг пропущеного в `AuditPage.tsx`).
    // Мутаційна проба: повернення на голий `entry.changedByUserId` без мапи
    // провалює саме цю перевірку.
    show(
      <RegistryHistory
        entries={History}
        userNames={new Map([[9, 'Ada Lovelace']])}
        usersResolved
      />,
    );

    const row = screen.getByText('SaveDefinition').closest('tr');
    expect(within(row!).getByText('Ada Lovelace')).toBeDefined();
    // Голий ідентифікатор більше не єдине, що видно в клітинці автора.
    expect(within(row!).queryByText('9')).toBeNull();
  });

  it('ФВ-8.12/Q-296: автор без права/видалений користувач — голий ідентифікатор і бейдж, решта історії лишається на екрані', () => {
    // ⛔ Право читати перелік користувачів (`Security.ManageUsers`) — ІНШЕ за
    // право дивитись історію довідника. Відмова на ньому (403) чи видалений
    // користувач (немає в першій сторінці переліку) не повинні ховати рядок
    // історії — лише ім'я автора в ньому.
    show(<RegistryHistory entries={History} userNames={new Map()} usersResolved />);

    // Решта історії видно й далі: операція та причина рендерені як завжди.
    const row = screen.getByText('SaveDefinition').closest('tr');
    expect(within(row!).getByText('ліміт перенесено з Configuration!J3')).toBeDefined();

    // Автор — голий ідентифікатор і бейдж «нерозв'язано», а не порожньо й не
    // виняток, що зупинив би рендер усього компонента.
    expect(within(row!).getByText('9')).toBeDefined();
    expect(within(row!).getByText(/registries\.userUnresolved/)).toBeDefined();
  });

  it('порожній перелік у кожній області каже про це прямо, а не мовчить', () => {
    // ⚠ `ФВ-14.22`: порожнеча без слів читається як «не відпрацювало».
    const bare = { ...Definition, relations: [], rules: [], mappings: [] };

    show(<RegistryRelations definition={bare} />);
    expect(screen.getByText(/registries\.noRelations/)).toBeDefined();

    show(<RegistryMappings definition={bare} />);
    expect(screen.getByText(/registries\.noMappings/)).toBeDefined();

    show(<RegistryHistory entries={[]} userNames={new Map()} usersResolved />);
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
      [],
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

    const request = buildSaveRequest(Definition, rules, [], 'нове правило', 'en');

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

  it('Q-lane4: нове поле надсилається без ідентифікатора, з порядком ПІСЛЯ наявних і без обов\'язковості', () => {
    // ⛔ Це і є фікс: до нього форма не мала звідки взяти запит на НОВЕ поле
    // взагалі. Сервер розрізняє «нове» від «правки наявного» за `id === null`
    // (`RegistryFieldSaveDto`) — той самий принцип, що вже стоїть для правил.
    const draft: FieldDraft = {
      ...emptyField('Decimal'),
      code: 'AnnualFee',
      name: 'Annual fee',
      unitId: 9,
    };

    const request = buildSaveRequest(Definition, [], [draft], 'нове поле', 'en');

    expect(request.fields.map((field) => field.id)).toEqual([41, 42, 44, null]);

    const added = request.fields.at(-1)!;
    expect(added.code).toBe('AnnualFee');
    expect(added.nameL10n).toEqual({ values: { en: 'Annual fee' } });
    expect(added.dataType).toBe('Decimal');

    // Продовжує порядок наявних полів (їх три), а не починає заново з 1 —
    // інакше нове поле посперечалося б за позицію з наявним.
    expect(added.ordinal).toBe(4);

    // ⛔ Мутаційна проба: нове поле НІКОЛИ не йде обов'язковим — сервер це й
    // так відхилив би (`ECR-REG-0422`, наявні записи його ще не мають), і
    // якщо колись хтось почне брати `isRequired` із чогось іншого, ніж
    // жорстке `false`, саме це поле в цій самій перевірці й зловить регрес.
    expect(added.isRequired).toBe(false);

    expect(added.unitId).toBe(9);

    // Наявні поля лишаються НЕЗМІННИМИ поруч із новим — `D2-202` не порушено.
    expect(request.fields.slice(0, 3).map((field) => field.id)).toEqual([41, 42, 44]);
  });

  it('Q-lane4: ключовість і ціль лукапа нового поля переносяться в запит', () => {
    const draft: FieldDraft = {
      ...emptyField('Lookup'),
      code: 'Substance2',
      name: 'Second substance',
      isKey: true,
      lookupRegistryDefId: 5,
    };

    const request = buildSaveRequest(Definition, [], [draft], 'нове поле', 'en');
    const added = request.fields.at(-1)!;

    expect(added.isKey).toBe(true);
    expect(added.lookupRegistryDefId).toBe(5);
  });

  it('Q-lane4: ціль лукапа не надсилається для типу, що нею не користується', () => {
    // ⛔ Форма ховає вибір цілі для не-`Lookup` типів (див. `RegistryFields`),
    // але якщо ціль усе одно лишилась у чернетці (перемкнули тип і назад),
    // запит не повинен нести чужий для цього типу `lookupRegistryDefId` —
    // сервер прочитав би це як поле-посилання, яким воно не є.
    const draft: FieldDraft = {
      ...emptyField('String'),
      code: 'Note',
      name: 'Note',
      lookupRegistryDefId: 5,
    };

    const request = buildSaveRequest(Definition, [], [draft], 'нове поле', 'en');
    expect(request.fields.at(-1)!.lookupRegistryDefId).toBeNull();
  });

  it('Q-lane4: нове поле без коду, без назви або лукап без цілі не готове до збереження', () => {
    // ⛔ Мутаційна проба: кожна з трьох умов перевіряється ОКРЕМО — тест, що
    // просто перевірив би "порожнє поле не готове", пропустив би регрес, який
    // ламає лише ОДНУ з них (наприклад, забув перевірити ціль лукапа).
    expect(isFieldComplete(emptyField())).toBe(false);
    expect(isFieldComplete({ ...emptyField(), code: 'A' })).toBe(false);
    expect(isFieldComplete({ ...emptyField(), code: 'A', name: 'A' })).toBe(true);

    expect(isFieldComplete({ ...emptyField('Lookup'), code: 'A', name: 'A' })).toBe(false);
    expect(
      isFieldComplete({ ...emptyField('Lookup'), code: 'A', name: 'A', lookupRegistryDefId: 5 }),
    ).toBe(true);
  });
});

