import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';
import {
  EditableFieldDataTypes,
  type FieldDataType,
  type FieldDraft,
  type RuleDraft,
} from './definition';

/** Чернетка опису довідника (`BE-24` крок 2). */
export type RegistryDraftDto = components['schemas']['RegistryDefinitionDraftDto'];

/** Стан чернетки: опублікована версія + сама чернетка або `null`. */
export type RegistryDraftState = components['schemas']['RegistryDefinitionDraftStateResponse'];

/** Тіло `PUT …/definition/draft`. */
export type SaveRegistryDraftRequest = components['schemas']['SaveRegistryDefinitionDraftRequest'];

/** Тіло прямого `PUT …/definition` — те саме, що й вміст чернетки. */
export type SaveRegistryDefinition = components['schemas']['SaveRegistryDefinitionDto'];

/** Відповідь публікації: нова версія опублікованого опису. */
export type RegistryDefinitionVersion = components['schemas']['RegistryDefinitionVersionResponse'];

/**
 * Ключ запиту чернетки.
 *
 * ⚠ Починається з `'registries'` навмисно: `queryKeys.registries.all()` —
 * префікс, і публікація, яка інвалідовує весь домен, зносить і чернетку теж.
 * Власний перший елемент лишив би чернетку в кеші після того, як її вже
 * опублікували, — тобто банер «є незбережена чернетка» висів би над описом,
 * у якому вона вже НЕ чернетка.
 *
 * @param code Код довідника.
 */
export function registryDraftKey(code: string): readonly ['registries', 'definitionDraft', string] {
  return ['registries', 'definitionDraft', code] as const;
}

/*
 * ⛔ Адреса записана повністю в КОЖНІЙ функції — той самий аргумент, що і в
 * `api.ts`: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає
 * літерал `/api/v1/…` разом із методом у вікні одразу після нього.
 */

/**
 * Читає чернетку опису; `draft === null` — чернетки немає.
 *
 * @param code Код довідника.
 */
export function getRegistryDraft(code: string): Promise<RegistryDraftState> {
  return apiFetch<RegistryDraftState>(
    `/api/v1/registries/${encodeURIComponent(code)}/definition/draft`,
  );
}

/**
 * Зберігає чернетку. Опублікований опис не змінюється.
 *
 * @param code Код довідника.
 * @param body Повний стан форми разом із причиною і версією чернетки.
 */
export function saveRegistryDraft(
  code: string,
  body: SaveRegistryDraftRequest,
): Promise<RegistryDraftDto> {
  return apiFetch<RegistryDraftDto>(
    `/api/v1/registries/${encodeURIComponent(code)}/definition/draft`,
    { method: 'PUT', body: JSON.stringify(body) },
  );
}

/**
 * Зберігає опис і одразу публікує його, повз чернетку.
 *
 * ⛔ Це ПРЯМИЙ `PUT …/definition` — той самий, яким конструктор користувався
 * до кроку 2. Він нікуди не дівся, але тепер вимагає ОБОХ прав
 * (`Registry.EditDefinition` разом із `Registry.Publish`), і саме тому лишився
 * окремою дією, а не «швидким збереженням»: одна дія, що змінює опублікований
 * опис негайно, доступна лише тому, хто й так має право публікувати.
 *
 * ⚠ Основний шлях конструктора — чернетка. Ця дія існує для випадку, коли
 * чернетки немає й заводити її нема сенсу: автор описав зміну і сам-таки має
 * право її випустити.
 *
 * @param code Код довідника.
 * @param body Повний стан форми разом із причиною.
 */
export function saveAndPublishRegistryDefinition(
  code: string,
  body: SaveRegistryDefinition,
): Promise<RegistryDefinitionVersion> {
  return apiFetch<RegistryDefinitionVersion>(
    `/api/v1/registries/${encodeURIComponent(code)}/definition`,
    { method: 'PUT', body: JSON.stringify(body) },
  );
}

/**
 * Публікує чернетку — право `Registry.Publish`, окреме від права правити.
 *
 * @param code Код довідника.
 * @param rowVersion Версія чернетки, яку публікують.
 */
export function publishRegistryDefinition(
  code: string,
  rowVersion: string,
): Promise<RegistryDefinitionVersion> {
  return apiFetch<RegistryDefinitionVersion>(
    `/api/v1/registries/${encodeURIComponent(code)}/definition/publish`,
    { method: 'POST', body: JSON.stringify({ rowVersion }) },
  );
}

/**
 * Скасовує чернетку без публікації.
 *
 * ⛔ `rowVersion` іде через `encodeURIComponent`, і це не косметика: версія —
 * base64, у ній трапляється `+`, а в рядку запиту `+` означає ПРОБІЛ. Сервер
 * порівнює версію дослівно (`RegistryDraft.RequireVersion`), тож чернетка з
 * плюсом у версії відповідала б `409` на кожне скасування — і причина
 * («версія чужа») називала б не те, що сталося насправді.
 *
 * @param code Код довідника.
 * @param rowVersion Версія чернетки, яку скасовують.
 */
export function discardRegistryDraft(code: string, rowVersion: string): Promise<void> {
  return apiFetch<void>(
    `/api/v1/registries/${encodeURIComponent(code)}/definition/draft?rowVersion=${encodeURIComponent(rowVersion)}`,
    { method: 'DELETE' },
  );
}

const DraftChangedKey = 'err.ECR-REG-0409.definitionDraftChanged';
const DraftStaleKey = 'err.ECR-REG-0409.definitionDraftStale';
const DraftMissingKey = 'err.ECR-REG-0404.definitionDraft';

/** Відмова, яку цей екран пояснює сам, а не віддає в загальну плашку. */
export type DraftConflict =
  | { readonly kind: 'changed' }

  /** Опис змінили ПОВЗ чернетку: чернетка лишається на екрані. */
  | { readonly kind: 'stale'; readonly baseVersion: string; readonly currentVersion: string }

  /** Чернетки вже немає — її опублікували або скасували з іншої вкладки. */
  | { readonly kind: 'missing' };

function messageKeyOf(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const key = error.problem.extensions2?.['messageKey'];
  return typeof key === 'string' ? key : null;
}

function textOf(error: EcrApiError, name: string): string {
  const value = error.problem.extensions2?.[name];
  return typeof value === 'string' ? value : '';
}

/**
 * Розбирає відмову чернетки; `null` — відмова не про чернетку.
 *
 * ⛔ Розрізнення за `messageKey`, а не за `errorCode`: під `ECR-REG-0409`
 * живуть і `definitionDraftChanged` (версія чужа — правку треба перечитати),
 * і `definitionDraftStale` (опис змінили повз чернетку — рішення за людиною).
 * Це ДВІ різні наступні дії, і звести їх до одного «конфлікту» означало б
 * запропонувати не ту з них.
 *
 * @param error Відмова мутації.
 */
export function draftConflictOf(error: unknown): DraftConflict | null {
  const key = messageKeyOf(error);
  if (key === null || !(error instanceof EcrApiError)) return null;

  if (key === DraftChangedKey) return { kind: 'changed' };
  if (key === DraftMissingKey) return { kind: 'missing' };

  if (key === DraftStaleKey) {
    return {
      kind: 'stale',
      baseVersion: textOf(error, 'baseVersion'),
      currentVersion: textOf(error, 'currentVersion'),
    };
  }

  return null;
}

type LocalizedText = components['schemas']['LocalizedText'];

function localizedText(text: LocalizedText | undefined, language: string): string {
  return text?.values?.[language] ?? '';
}

function fieldDataType(value: string): FieldDataType {
  return (EditableFieldDataTypes as readonly string[]).includes(value)
    ? (value as FieldDataType)
    : 'String';
}

/**
 * Правила чернетки у формі, придатній для правки.
 *
 * ⚠ Береться ВЕСЬ перелік, а не лише нові правила: чернетка зберігає повний
 * стан форми (`SaveRegistryDefinitionDraftRequest`), і показати поверх неї
 * правила опублікованої версії означало б стерти правку, яку людина вже
 * зберегла.
 *
 * @param draft Чернетка з сервера.
 * @param language Мова, підпис якої показує форма.
 */
export function draftRules(draft: RegistryDraftDto, language: string): RuleDraft[] {
  return draft.rules.map((rule) => ({
    id: rule.id,
    code: rule.code,
    ruleKind: rule.ruleKind,
    expression: rule.expression,
    severity: rule.severity,
    message: localizedText(rule.messageL10n, language),
    isActive: rule.isActive,
  }));
}

/**
 * Нові поля чернетки — ті, у яких `id === null`.
 *
 * ⛔ Наявні поля відфільтровані навмисно: форма їх не править (`D2-202`), а
 * показує з опублікованого опису. Пустити їх у чернетку нових полів означало б
 * надіслати їх при наступному збереженні ще раз, уже під `id === null`, тобто
 * як дублікати.
 *
 * @param draft Чернетка з сервера.
 * @param language Мова, якою введено назву поля.
 */
export function draftNewFields(draft: RegistryDraftDto, language: string): FieldDraft[] {
  return draft.fields
    .filter((field) => field.id === null)
    .map((field) => ({
      code: field.code,
      name: localizedText(field.nameL10n, language),
      dataType: fieldDataType(field.dataType),
      isKey: field.isKey,
      lookupRegistryDefId: field.lookupRegistryDefId,
      unitId: field.unitId,
    }));
}
