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
 * Клієнтська перевірка: код, хоч одна мова назви й обов'язкові поля довідника.
 *
 * ⚠ Решту (унікальність коду, порядок рівнів, типи значень) знає лише сервер: його відмова показується
 * біля поля (`serverFieldProblem`) або сповіщенням, а не вгадується тут.
 */
export function validateNode(
  fields: readonly Pick<RegistryField, 'code' | 'isRequired'>[],
  form: NodeFormState,
  codeAutomatic = false,
): NodeFormProblems {
  const values: Record<string, 'required'> = {};
  for (const field of fields) {
    // ⚠ Порожній «район» - це «-» (вузол без району), тож обов'язковим він лишається, а порожнім не вважається.
    if (field.code === NaeAreaField) continue;
    if (field.isRequired && (form.values[field.code] ?? '').trim().length === 0) values[field.code] = 'required';
  }

  const named = Object.values(form.display).some((text) => text.trim().length > 0);

  return {
    ...(!codeAutomatic && form.code.trim().length === 0 ? { code: 'required' as const } : {}),
    ...(named ? {} : { display: 'required' as const }),
    values,
  };
}

/** Поля ділового ключа NAE (`NaeTreeSchema`): код NAE, регіон, район (`-`, якщо району немає). */
export const NaeCodeField = 'NaeCode';
export const NaeRegionField = 'RegionCode';
export const NaeAreaField = 'AreaCode';
export const NaeNoArea = '-';

/**
 * Значення нового вузла, які успадковуються від батька: регіон завжди, район - крім вузла-району.
 * ⚠ Лише підказка в порожніх полях; людина може змінити, а сервер перевірить ключ.
 */
export function inheritedValues(
  parentValues: Readonly<Record<string, string>>,
  levelCode: string,
): Readonly<Record<string, string>> {
  const result: Record<string, string> = {};
  const region = (parentValues[NaeRegionField] ?? '').trim();
  const area = (parentValues[NaeAreaField] ?? '').trim();
  if (levelCode !== 'REGION' && region !== '') result[NaeRegionField] = region;
  if (levelCode !== 'REGION' && levelCode !== 'AREA' && area !== '' && area !== NaeNoArea) result[NaeAreaField] = area;

  return result;
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
 * відсутня лишається, порожня прибирається). Значення полів - лише змінені; стерте поле - `null`.
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
    // Новий вузол везе всі непорожні значення (в тому числі успадковані від батька), правка - лише змінені.
    const before = target.mode === 'create' ? '' : (initial.values[field.code] ?? '');
    let after = current.values[field.code] ?? '';
    if (field.code === NaeAreaField && after.trim().length === 0) after = NaeNoArea;
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
export type ServerFieldProblem = 'codeTaken' | 'codeAutomatic' | 'keyTaken';

const CodeTakenKey = 'err.ECR-REG-0409.entryCodeTaken';
const CodeAutomaticKey = 'err.ECR-REG-0422.entryCodeAutomatic';
/** Ділового ключа (код NAE + регіон + район) вже хтось займає. */
const KeyTakenKeys: readonly string[] = ['err.ECR-REG-4092.keyTaken', 'err.ECR-REG-4092.keyWindowOverlap'];

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

/**
 * Чи відмова про поле коду: «код зайнятий» (409) і «код довідника автоматичний» (422).
 * Решта відмов (порядок рівнів, право, збій) - сповіщення, поле не підсвічується.
 */
export function serverFieldProblem(error: unknown): ServerFieldProblem | null {
  const key = serverMessageKey(error);
  if (key === CodeTakenKey) return 'codeTaken';
  if (key === CodeAutomaticKey) return 'codeAutomatic';
  if (key !== null && KeyTakenKeys.includes(key)) return 'keyTaken';

  return null;
}
