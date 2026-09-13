import type {
  RegistryDefinitionDto,
  RegistryFieldSaveDto,
  RegistryRuleDto,
  RegistryRuleSaveDto,
  SaveRegistryDefinitionDto,
} from '@/api/types';

/**
 * Типи поля, які людина обирає у формі «додати поле» — за зразком
 * `templates/column.ts` `EditableDataTypes` і з тієї самої причини.
 *
 * ⛔ `Formula`/`Calculated` тут немає навмисно: їх рахує рушій методології
 * (`ColumnDef.IsComputed` / `cfg.CalculationBinding`), а не людина руками.
 * Довідникове поле, заведене через ЦЮ форму, завжди зберігає введене
 * значення — вибір одного з цих двох видів дав би поле, яке обіцяє
 * обчислення, якого насправді ніхто не робить.
 */
export const EditableFieldDataTypes = [
  'String',
  'Int',
  'Decimal',
  'Bool',
  'Date',
  'Lookup',
  'Unit',
] as const;

/** Тип значення нового поля довідника. */
export type FieldDataType = (typeof EditableFieldDataTypes)[number];

/**
 * Типи, для яких має сенс одиниця вимірювання (`RegistryFieldDto.unitId`).
 *
 * ⚠ Не `Unit` сам: поле типу `Unit` зберігає ВИБІР одиниці на рядок
 * (`ValueUnitId`, `R-A4`) — це інше, ніж підпис «в яких одиницях виміряне
 * число» для `Int`/`Decimal`.
 */
export const NumericFieldTypes: readonly FieldDataType[] = ['Int', 'Decimal'];

/**
 * Види правил довідника — **рівно чотири** (директива №06 `H-10`).
 *
 * ⛔ П'ятого — `ValidityWindow` — тут немає навмисно, хоч він і стоїть п'ятим
 * у `reference/design/07-data-model.md`. Вікно чинності запису — це його
 * **поля** `validFrom`/`validTo` (`ФВ-8.5`), а не правило: правило-дублер дало
 * б два джерела істини про чинність, і розійшлися б вони мовчки — рівно на
 * межі вікна, де ціна помилки найбільша.
 *
 * ⚠ Перелік тут — не «зручність форми». Він мусить збігатися з переліком на
 * сервері (`RegistryRuleKind`), і саме тому винесений у модуль, який
 * перевіряється тестом, а не розсипаний по `<option>` усередині розмітки.
 */
export const RuleKinds = ['RequiredWhen', 'UniqueWithin', 'Expression', 'CrossRegistry'] as const;

/** Вид правила довідника. */
export type RuleKind = (typeof RuleKinds)[number];

/** Рівні порушення правила. */
export const Severities = ['Info', 'Warning', 'Error'] as const;

/** Рівень порушення. */
export type Severity = (typeof Severities)[number];

/**
 * Правило в стані редагування.
 *
 * ⚠ `id === null` означає «нове»; сервер розрізняє створення і правку саме за
 * цим полем, і вигадати ідентифікатор на клієнті означало б надіслати правку
 * чужого правила.
 */
export interface RuleDraft {
  readonly id: number | null;
  readonly code: string;
  readonly ruleKind: string;
  readonly expression: string;
  readonly severity: string;
  readonly message: string;
  readonly isActive: boolean;
}

/**
 * Зводить правило з сервера до форми, придатної для правки.
 *
 * @param rule Правило з відповіді сервера.
 * @param language Мова, підпис якої показує форма.
 */
export function toDraft(rule: RegistryRuleDto, language: string): RuleDraft {
  return {
    id: rule.id,
    code: rule.code,
    ruleKind: rule.ruleKind,
    expression: rule.expression,
    severity: rule.severity,
    message: rule.messageL10n.values?.[language] ?? '',
    isActive: rule.isActive,
  };
}

/**
 * Чи готове правило до збереження.
 *
 * ⛔ Порожній предикат — не «правило без умови», а правило, яке не спрацює
 * ніколи і виглядатиме при цьому налаштованим. Сервер відхиляє його
 * (`ECR-REG-0422`); тут перевірка потрібна, щоб не дати натиснути «зберегти»
 * і отримати відмову на весь набір через один рядок.
 *
 * @param draft Правило в стані редагування.
 */
export function isComplete(draft: RuleDraft): boolean {
  return (
    draft.code.trim().length > 0
    && draft.expression.trim().length > 0
    && (RuleKinds as readonly string[]).includes(draft.ruleKind)
    && (Severities as readonly string[]).includes(draft.severity)
  );
}

/**
 * Складає запит на збереження опису.
 *
 * ⛔ НАЯВНІ поля йдуть **без змін** — конструктор їх показує, а не править.
 * Надіслати менше полів, ніж є, означало б попросити сервер їх видалити, а
 * він відмовляє: `dic.RegistryValue` посилається на поле зовнішнім ключем, і
 * видалення поля стерло б значення.
 *
 * ⚠ НОВІ поля (`newFields`) — окрема річ, не «правка»: сервер розрізняє їх
 * від наявних так само, як і правила, — за `id === null`
 * (`RegistryFieldSaveDto`, `SaveRegistryDefinitionHandler.ApplyFields`).
 * Порядок (`ordinal`) новим полям призначається ПІСЛЯ всіх наявних — форма
 * не дає переставляти наявні поля, тож нові лише дописуються в кінець.
 *
 * @param definition Опис, отриманий із сервера.
 * @param rules Правила після правки.
 * @param newFields Нові поля, додані в цьому сеансі (ще не збережені).
 * @param reason Причина зміни; сервер вимагає її непорожньою.
 * @param language Мова, якою введено текст порушення і назву нового поля.
 */
export function buildSaveRequest(
  definition: RegistryDefinitionDto,
  rules: readonly RuleDraft[],
  newFields: readonly FieldDraft[],
  reason: string,
  language: string,
): SaveRegistryDefinitionDto {
  const existingFields: RegistryFieldSaveDto[] = definition.fields.map((field, index) => ({
    id: field.id,
    code: field.code,
    nameL10n: field.nameL10n,
    dataType: field.dataType,
    ordinal: index + 1,
    isRequired: field.isRequired,

    // ⚠ `isScopeField` і є ключовістю: сервер віддає його під цим іменем, бо
    // саме ключові поля бере `RoleAssignment.ScopeJson`. Надіслати сюди
    // `false` означало б попросити прибрати бізнес-ключ довідника.
    isKey: field.isScopeField,
    lookupRegistryDefId: field.lookupRegistryDefId,
    unitId: field.unitId,
  }));

  const addedFields: RegistryFieldSaveDto[] = newFields.map((draft, index) => ({
    id: null,
    code: draft.code.trim(),
    nameL10n: { values: { [language]: draft.name.trim() } },
    dataType: draft.dataType,
    ordinal: existingFields.length + index + 1,

    // ⛔ Нове поле обов'язковим бути не може — сервер це й так відхилив би
    // (`ECR-REG-0422`: наявні записи його ще не мають), форма лише не показує
    // вибору там, де його немає (той самий принцип, що й `FieldDraft` без
    // `isRequired` узагалі).
    isRequired: false,
    isKey: draft.isKey,
    lookupRegistryDefId: draft.dataType === 'Lookup' ? draft.lookupRegistryDefId : null,
    unitId: NumericFieldTypes.includes(draft.dataType) ? draft.unitId : null,
  }));

  const saved: RegistryRuleSaveDto[] = rules.map((rule) => ({
    id: rule.id,
    code: rule.code.trim(),
    ruleKind: rule.ruleKind,
    expression: rule.expression.trim(),
    severity: rule.severity,
    messageL10n: { values: { [language]: rule.message } },
    parametersJson: null,
    isActive: rule.isActive,
  }));

  return { fields: [...existingFields, ...addedFields], rules: saved, reason: reason.trim() };
}

/**
 * Порожнє правило для форми «додати».
 *
 * @param ruleKind Вид правила; змінити його після створення сервер не дасть.
 */
export function emptyRule(ruleKind: RuleKind): RuleDraft {
  return {
    id: null,
    code: '',
    ruleKind,
    expression: '',
    severity: 'Error',
    message: '',
    isActive: true,
  };
}

/**
 * Нове поле довідника в стані редагування.
 *
 * ⛔ НАЯВНІ поля сюди не потрапляють — це чернетка лише для того, що
 * додається в ЦЬОМУ сеансі (`D2-202`: наявне поле показується, а не
 * правиться; ця чернетка того рішення не порушує, бо описує тільки нове).
 *
 * ⛔ `isRequired` немає навмисно: нове поле обов'язковим бути не може
 * (сервер відхиляє — `ECR-REG-0422`, наявні записи його ще не мають), і
 * форма, яка запропонувала б цей прапорець, дала б натиснути там, де
 * сервер однаково відмовить.
 *
 * ⚠ `name` — рядок ОДНІЄЮ мовою (мовою інтерфейсу), а не повний
 * `LocalizedText` — той самий спрощений підхід, що вже прийнятий тут для
 * `RuleDraft.message`. Повний багатомовний ввід (`LocalizedInput`,
 * `templates/ColumnEditor.tsx`) має сенс у окремій формі на всю сторінку;
 * тут поле додається в рядку таблиці, і мову решти назв так само вводили б
 * послідовно, одну по одній, а не всі одразу.
 */
export interface FieldDraft {
  readonly code: string;
  readonly name: string;
  readonly dataType: FieldDataType;
  readonly isKey: boolean;
  readonly lookupRegistryDefId: number | null;
  readonly unitId: number | null;
}

/** Порожнє нове поле для форми «додати». */
export function emptyField(dataType: FieldDataType = 'String'): FieldDraft {
  return {
    code: '',
    name: '',
    dataType,
    isKey: false,
    lookupRegistryDefId: null,
    unitId: null,
  };
}

/**
 * Чи готове нове поле до збереження.
 *
 * ⛔ Поле типу `Lookup` без цілі — це поле, яке нічого не пропонує обрати:
 * сервер такого не забороняє напряму (`lookupRegistryDefId` для інших типів
 * просто ігнорується), але зберегти його означало б завести колонку, яка
 * ніколи не покаже жодного варіанта.
 */
export function isFieldComplete(draft: FieldDraft): boolean {
  return (
    draft.code.trim().length > 0
    && draft.name.trim().length > 0
    && (EditableFieldDataTypes as readonly string[]).includes(draft.dataType)
    && (draft.dataType !== 'Lookup' || draft.lookupRegistryDefId !== null)
  );
}

