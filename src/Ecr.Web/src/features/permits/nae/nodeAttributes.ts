import { EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';
import type { TreeLevel, TreeNode } from '@/shared/ui/TreeEditor';

/** Код довідника структури НВ (NAE plan N2: сід `NAE_TREE` - у серверній лінії N2). */
export const NaeRegistryCode = 'NAE_TREE';

export type NodeDetail = components['schemas']['RegistryEntryDetailDto'];
export type NodeUpsertBody = components['schemas']['RegistryEntryUpsertDto'];
type RegistryField = components['schemas']['RegistryFieldDto'];

/** Куди форма веде: новий вузол під батьком на рівні або правка наявного. */
export type NodeFormTarget =
  | { readonly mode: 'create'; readonly parent: TreeNode | null; readonly level: TreeLevel }
  | { readonly mode: 'edit'; readonly node: TreeNode };

/** Значення форми: код, назва мовами каталогу, атрибути довідника текстом (сервер знає типи й відхилить зайве). */
export interface NodeFormState {
  readonly code: string;
  readonly display: Readonly<Record<string, string>>;
  readonly values: Readonly<Record<string, string>>;
}

export const EmptyNodeForm: NodeFormState = { code: '', display: {}, values: {} };

/** Поля ділового ключа NAE (`NaeTreeSchema`): номер NAE; регіон і район сервер виводить з предків. */
export const NaeCodeField = 'NaeCode';
export const NaeRegionField = 'RegionCode';
export const NaeAreaField = 'AreaCode';

/** Шлях AF: сервер зберігає його для синку з AF, але в картці вузла його немає (людина 04.11: «навіщо показувати Path AF»). */
export const NaeAfPathField = 'AfPath';

/** Мова, назва якою обов'язкова на кожному вузлі дерева NAE (Q1, 03.11); `ru` і `kz` - за бажанням. */
export const RequiredNameLanguage = 'en';

/** Код рівня вузла, для якого відкрита форма: новий - вибраний, наявний - власний (`null` - рівня немає). */
export function targetLevelCode(target: NodeFormTarget): string | null {
  return target.mode === 'create' ? target.level.code : (target.node.levelCode ?? null);
}

/**
 * Поля вузла його типу (рівня): власні й поля всіх рівнів (`levelCode == null`). Вузол без рівня бачить лише поля всіх рівнів.
 * ⛔ Фільтр потрібен і сервер його знає: непорожнє значення поля чужого рівня - `422 fieldLevelMismatch`.
 */
export function fieldsForLevel(fields: readonly RegistryField[], levelCode: string | null): RegistryField[] {
  return fields.filter(
    (field) => field.code !== NaeAfPathField && (field.levelCode === undefined || field.levelCode === null || field.levelCode === levelCode),
  );
}

/**
 * Поле веде сервер (регіон і район - з предків, шлях AF - із завантаження): форма його лише показує.
 * ⚠ Виняток: `RegionCode` пишеться на вузлі REGION, `AreaCode` - на вузлі AREA (це їхній код); на інших рівнях обидва
 * виводяться з предків і лишаються лише для читання, хоч би що казала ознака поля.
 */
export function isServerField(field: Pick<RegistryField, 'isReadOnly'> & { readonly code?: string }, levelCode: string | null = null): boolean {
  if (field.code === NaeRegionField) return levelCode !== 'REGION';
  if (field.code === NaeAreaField) return levelCode !== 'AREA';

  return field.isReadOnly === true;
}

/** Рівень джерела NAE: лише на ньому сервер зберігає виведені регіон і район. */
export const NaeSourceLevel = 'SRC';

/**
 * Поле сервера показується в картці цього рівня. Виведені регіон і район сервер зберігає лише на джерелах (SRC): на
 * SITE/EQUIP (і району на REGION, регіону на AREA) там завжди «—», що читалося як збій (AN-161), тож рядка немає.
 */
export function showsServerField(field: { readonly code?: string }, levelCode: string | null): boolean {
  if (field.code === NaeRegionField || field.code === NaeAreaField) return levelCode === NaeSourceLevel;

  return true;
}

/** Стан форми з повного запису. `null` із сервера - поле не заповнене, у формі це порожній рядок. */
export function formOfDetail(detail: NodeDetail): NodeFormState {
  const values: Record<string, string> = {};
  for (const [field, value] of Object.entries(detail.values)) values[field] = value === null || value === undefined ? '' : String(value);

  return { code: detail.code, display: { ...(detail.displayL10n.values ?? {}) }, values };
}

/** Помилки полів, які видно ДО запиту. Порожній об'єкт - можна зберігати. */
export interface NodeFormProblems {
  readonly code?: 'required';
  readonly display?: 'required';
  readonly values: Readonly<Record<string, 'required'>>;
}

/**
 * Клієнтська перевірка: код, назва англійською й обов'язкові поля свого типу.
 *
 * ⚠ `fields` - лише поля, які людина вводить (свого рівня, без полів сервера). Решту (унікальність номера NAE, порядок
 * рівнів, типи значень) знає лише сервер: його відмова показується біля поля (`serverFieldProblem`) або сповіщенням.
 */
export function validateNode(
  fields: readonly Pick<RegistryField, 'code' | 'isRequired'>[],
  form: NodeFormState,
  codeAutomatic = false,
): NodeFormProblems {
  const values: Record<string, 'required'> = {};
  for (const field of fields) {
    if (field.isRequired && (form.values[field.code] ?? '').trim().length === 0) values[field.code] = 'required';
  }

  const named = (form.display[RequiredNameLanguage] ?? '').trim().length > 0;

  return {
    ...(!codeAutomatic && form.code.trim().length === 0 ? { code: 'required' as const } : {}),
    ...(named ? {} : { display: 'required' as const }),
    values,
  };
}

export function hasProblems(problems: NodeFormProblems): boolean {
  return problems.code !== undefined || problems.display !== undefined || Object.keys(problems.values).length > 0;
}

/**
 * Тіло `POST …/entries` для вузла.
 *
 * ⛔ Створення: `id: null`, батько й рівень - з місця, куди людина натиснула «Add child»; сервер вимагає рівень для
 * нового запису ієрархічного довідника. Правка: `levelCode: null` (рівень лишається; змінити його - це «Move to…»),
 * батько - той, що був (правка атрибутів не переносить вузол).
 *
 * ⚠ Назва везе всі мови форми, а мову, яку людина стерла, - порожнім рядком (сервер зливає назву з наявною:
 * відсутня лишається, порожня прибирається). Значення - лише полів, які людина вводить (`fields`: свого рівня, без
 * полів сервера), і лише змінені; стерте поле - `null`.
 */
export function buildNodeBody(input: {
  readonly registryId: number;
  readonly fields: readonly Pick<RegistryField, 'code'>[];
  readonly target: NodeFormTarget;
  readonly parentEntryId: number | null;
  readonly initial: NodeFormState;
  readonly current: NodeFormState;
  /** Код видає система: для нового вузла іде порожнім (сервер відмовить, якщо його назвати). */
  readonly codeAutomatic?: boolean;
}): NodeUpsertBody {
  const { target, initial, current } = input;
  const display: Record<string, string> = { ...current.display };
  for (const language of Object.keys(initial.display)) {
    if (!(language in current.display)) display[language] = '';
  }

  const values: Record<string, string | null> = {};
  for (const field of input.fields) {
    // Новий вузол везе всі непорожні значення, правка - лише змінені.
    const before = target.mode === 'create' ? '' : (initial.values[field.code] ?? '');
    const after = current.values[field.code] ?? '';
    if (after !== before) values[field.code] = after.trim().length === 0 ? null : after;
  }

  return {
    id: target.mode === 'edit' ? Number(target.node.id) : null,
    registryDefId: input.registryId,
    code: input.codeAutomatic === true && target.mode === 'create' ? '' : current.code.trim(),
    display: { values: display },
    parentEntryId: input.parentEntryId,
    levelCode: target.mode === 'create' ? target.level.code : null,
    values,
  };
}

/** Відмови сервера, які належать конкретному полю форми. */
export type ServerFieldProblem = 'codeTaken' | 'codeAutomatic' | 'keyTaken' | 'naeCodeTaken' | 'nameEnRequired';

const CodeTakenKey = 'err.ECR-REG-0409.entryCodeTaken';
const CodeAutomaticKey = 'err.ECR-REG-0422.entryCodeAutomatic';
/** Ділового ключа (код NAE + регіон + район) вже хтось займає. */
const KeyTakenKeys: readonly string[] = ['err.ECR-REG-4092.keyTaken', 'err.ECR-REG-4092.keyWindowOverlap'];
/** Номер NAE зайнятий у своєму регіоні й на вибраному On/Offshore (Q3, 03.11). */
const NaeCodeTakenKey = 'err.ECR-REG-4094.naeCodeTaken';
const NameEnRequiredKey = 'err.ECR-REG-0422.naeNameEnRequired';

/** Ключ каталогу відмови сервера (`extensions2.messageKey`), якщо він є. */
export function serverMessageKey(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const key = error.problem.extensions2?.['messageKey'];

  return typeof key === 'string' ? key : null;
}

/**
 * Код запису, що вже займає діловий ключ (`entryCode` у тілі відмови `ECR-REG-4092`), якщо сервер його назвав.
 * ⚠ Це технічний код запису: людині він потрібен лише як «ось хто тримає ключ».
 */
export function serverKeyHolder(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const holder = error.problem.extensions2?.['entryCode'];

  return typeof holder === 'string' && holder !== '' ? holder : null;
}

/** Хто тримає номер NAE (`409 ECR-REG-4094`): номер у нормалізованому вигляді, область і запис-тримач. */
export interface NaeCodeHolder {
  readonly naeCode: string;
  readonly region: string;
  readonly area: string;
  readonly entryCode: string;
  /** Запис-тримач, до якого веде посилання; `null` - сервер його не назвав. */
  readonly entryId: number | null;
}

export function serverNaeCodeHolder(error: unknown): NaeCodeHolder | null {
  if (!(error instanceof EcrApiError) || serverMessageKey(error) !== NaeCodeTakenKey) return null;
  const read = (key: string): string => {
    const value = error.problem.extensions2?.[key];

    return typeof value === 'string' || typeof value === 'number' ? String(value) : '';
  };
  const id = Number(read('entryId'));

  return {
    naeCode: read('naeCode'),
    region: read('regionCode'),
    area: read('areaCode'),
    entryCode: read('entryCode'),
    entryId: Number.isInteger(id) && id > 0 ? id : null,
  };
}

/**
 * Чи відмова про поле форми: «код зайнятий» (409), «код автоматичний» (422), «номер NAE зайнятий» (409), «англійська назва
 * обов'язкова» (422). Решта відмов (порядок рівнів, право, збій) - сповіщення, поле не підсвічується.
 */
export function serverFieldProblem(error: unknown): ServerFieldProblem | null {
  const key = serverMessageKey(error);
  if (key === CodeTakenKey) return 'codeTaken';
  if (key === CodeAutomaticKey) return 'codeAutomatic';
  if (key === NaeCodeTakenKey) return 'naeCodeTaken';
  if (key === NameEnRequiredKey) return 'nameEnRequired';
  if (key !== null && KeyTakenKeys.includes(key)) return 'keyTaken';

  return null;
}
