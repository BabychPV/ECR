import type {
  RegistryDefinitionDto,
  RegistryFieldSaveDto,
  RegistryRuleDto,
  RegistryRuleSaveDto,
  SaveRegistryDefinitionDto,
} from '@/api/types';

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
 * ⛔ Поля йдуть **без змін** — конструктор їх показує, а не править. Надіслати
 * менше полів, ніж є, означало б попросити сервер їх видалити, а він
 * відмовляє: `dic.RegistryValue` посилається на поле зовнішнім ключем, і
 * видалення поля стерло б значення.
 *
 * @param definition Опис, отриманий із сервера.
 * @param rules Правила після правки.
 * @param reason Причина зміни; сервер вимагає її непорожньою.
 * @param language Мова, якою введено текст порушення.
 */
export function buildSaveRequest(
  definition: RegistryDefinitionDto,
  rules: readonly RuleDraft[],
  reason: string,
  language: string,
): SaveRegistryDefinitionDto {
  const fields: RegistryFieldSaveDto[] = definition.fields.map((field) => ({
    id: field.id,
    code: field.code,
    nameL10n: field.nameL10n,
    dataType: field.dataType,
    ordinal: definition.fields.indexOf(field) + 1,
    isRequired: field.isRequired,

    // ⚠ `isScopeField` і є ключовістю: сервер віддає його під цим іменем, бо
    // саме ключові поля бере `RoleAssignment.ScopeJson`. Надіслати сюди
    // `false` означало б попросити прибрати бізнес-ключ довідника.
    isKey: field.isScopeField,
    lookupRegistryDefId: field.lookupRegistryDefId,
    unitId: field.unitId,
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

  return { fields, rules: saved, reason: reason.trim() };
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

