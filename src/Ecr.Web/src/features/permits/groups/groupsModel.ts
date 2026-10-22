import { language, t } from '@/shared/i18n';
import { EcrApiError } from '@/api/client';
import { textOf } from '../typeEditor/labels';
import type {
  CreatePermitGroupRequest,
  PermitGroupDto,
  ReplacePermitGroupTypesRequest,
  UpdatePermitGroupRequest,
} from './groupsApi';

/**
 * Чиста модель екрана «Groups» (групи типів дозволів): рядки переліку, показники, пошук, чернетка форми шторки,
 * дзеркало серверних перевірок і розбір відмов сервера на поля. Нічого з React і мережі.
 */

/** Значення `?panel=` для шторки створення (код групи починається з літери, тож з `_` збігу бути не може). */
export const NewGroupPanel = '_new';

/** Допустимий код групи (дзеркало серверного правила): літера, далі літери/цифри/`_`, до 59 символів. */
export const GroupCodePattern = /^[A-Za-z][A-Za-z0-9_]{0,58}$/;

/** Межі довжини тексту (на мову), як у контракті групи. */
export const GroupNameMax = 256;
export const GroupDescriptionMax = 1000;

/** Мови каталогу, у яких адмін вводить назву й опис (рішення `D-95`: en, ru, kz). */
export const GroupLanguages = ['en', 'ru', 'kz'] as const;

export function isValidGroupCode(code: string): boolean {
  return GroupCodePattern.test(code);
}

/** Назва групи мовою користувача (запасні мови - як `textOf`); порожню замінює код. */
export function groupName(group: Pick<PermitGroupDto, 'code' | 'nameL10n'>, lang: string = language()): string {
  const name = textOf(group.nameL10n, lang);

  return name === '' ? group.code : name;
}

/** Опис групи мовою користувача; порожній, якщо його немає. */
export function groupDescription(group: Pick<PermitGroupDto, 'descriptionL10n'>, lang: string = language()): string {
  return textOf(group.descriptionL10n, lang);
}

/** «Water supply · Wastewater»: назви типів складу в порядку показу. */
export function typesText(group: Pick<PermitGroupDto, 'types'>, lang: string = language()): string {
  return group.types.map((type) => textOf(type.nameL10n, lang) || type.code).join(' · ');
}

/** Показники смуги над переліком. */
export interface GroupsStats {
  readonly all: number;
  readonly active: number;
  readonly inactive: number;
}

export function groupsStats(groups: readonly PermitGroupDto[]): GroupsStats {
  const active = groups.filter((group) => group.isActive).length;

  return { all: groups.length, active, inactive: groups.length - active };
}

/** Показник, що фільтрує перелік (`?stat=`); невідоме значення нічого не відсіює. */
export type GroupsStat = 'active' | 'inactive';

export function groupsStatParam(value: string | null): GroupsStat | '' {
  return value === 'active' || value === 'inactive' ? value : '';
}

/** Пошук за кодом, назвою й описом будь-якою мовою та назвою типів складу; регістр не важить. */
export function matchesNeedle(group: PermitGroupDto, needle: string): boolean {
  const query = needle.trim().toLocaleLowerCase();
  if (query === '') return true;

  const haystack = [
    group.code,
    ...Object.values(group.nameL10n),
    ...Object.values(group.descriptionL10n),
    ...group.types.flatMap((type) => [type.code, ...Object.values(type.nameL10n)]),
  ]
    .join(' ')
    .toLocaleLowerCase();

  return haystack.includes(query);
}

export function filterGroups(groups: readonly PermitGroupDto[], filter: { needle: string; stat: GroupsStat | '' }): PermitGroupDto[] {
  return groups.filter(
    (group) =>
      matchesNeedle(group, filter.needle) &&
      (filter.stat === '' || (filter.stat === 'active' ? group.isActive : !group.isActive)),
  );
}

/** Чи група використовується шаблонами: тоді її лише деактивують (видалення сервер відхилить `ECR-PRM-4098`). */
export function isGroupUsed(group: Pick<PermitGroupDto, 'usedInColumns' | 'usedInTemplates'>): boolean {
  return group.usedInColumns > 0 || group.usedInTemplates > 0;
}

/** «3 templates · 5 columns» або «Not used». */
export function usageText(group: Pick<PermitGroupDto, 'usedInColumns' | 'usedInTemplates'>): string {
  return isGroupUsed(group)
    ? t('permits.groups.used', { templates: String(group.usedInTemplates), columns: String(group.usedInColumns) })
    : t('permits.groups.usedNone');
}

/** Чернетка форми шторки. */
export interface GroupDraft {
  readonly code: string;
  readonly name: Record<string, string>;
  readonly description: Record<string, string>;
  readonly isActive: boolean;
  /** Коди типів складу в порядку показу. */
  readonly typeCodes: readonly string[];
}

export function emptyDraft(): GroupDraft {
  return { code: '', name: {}, description: {}, isActive: true, typeCodes: [] };
}

export function draftOf(group: PermitGroupDto): GroupDraft {
  return {
    code: group.code,
    name: { ...group.nameL10n },
    description: { ...group.descriptionL10n },
    isActive: group.isActive,
    typeCodes: group.types.map((type) => type.code),
  };
}

/** Мови без тексту не потрапляють у запит (порожня мова - не «називається порожньо»). */
export function compact(map: Readonly<Record<string, string>>): Record<string, string> {
  return Object.fromEntries(Object.entries(map).filter(([, value]) => value.trim() !== '')) as Record<string, string>;
}

/** Що не так з чернеткою ДО відправки; ключ - поле форми. Порожній обʼєкт - можна відправляти. */
export type DraftProblems = Partial<Record<'code' | 'name' | 'description', string>>;

/**
 * Дзеркало серверних перевірок (`permitGroupCodeInvalid`, `permitGroupNameRequired`, `permitGroupTextTooLong`): людина
 * бачить причину біля поля, а не відмову після відправки. Тексти - ті самі ключі, що й у відповіді сервера.
 * ⚠ Код перевіряється лише при створенні: існуючий не змінюється.
 */
export function draftProblems(draft: GroupDraft, isNew: boolean): DraftProblems {
  const problems: { -readonly [K in keyof DraftProblems]: string } = {};

  if (isNew && !isValidGroupCode(draft.code)) problems.code = t('err.ECR-PRM-0422.permitGroupCodeInvalid', { code: draft.code });
  if (Object.values(draft.name).every((value) => value.trim() === '')) {
    problems.name = t('err.ECR-PRM-0422.permitGroupNameRequired');
  } else if (Object.values(draft.name).some((value) => value.length > GroupNameMax)) {
    problems.name = t('err.ECR-PRM-0422.permitGroupTextTooLong', { item: t('permits.groups.field.name'), max: String(GroupNameMax) });
  }
  if (Object.values(draft.description).some((value) => value.length > GroupDescriptionMax)) {
    problems.description = t('err.ECR-PRM-0422.permitGroupTextTooLong', {
      item: t('permits.groups.field.description'),
      max: String(GroupDescriptionMax),
    });
  }

  return problems;
}

/** Чи змінилися назва, опис чи активність (потрібен `PUT permit-groups/{code}`). */
export function metaChanged(draft: GroupDraft, original: GroupDraft): boolean {
  return (
    draft.isActive !== original.isActive ||
    JSON.stringify(compact(draft.name)) !== JSON.stringify(compact(original.name)) ||
    JSON.stringify(compact(draft.description)) !== JSON.stringify(compact(original.description))
  );
}

/** Чи змінився склад (потрібен `PUT permit-groups/{code}/types`); порядок теж зміна. */
export function typesChanged(draft: GroupDraft, original: GroupDraft): boolean {
  return draft.typeCodes.join('\n') !== original.typeCodes.join('\n');
}

export function createBody(draft: GroupDraft): CreatePermitGroupRequest {
  const description = compact(draft.description);

  return {
    code: draft.code.trim(),
    nameL10n: compact(draft.name),
    descriptionL10n: Object.keys(description).length === 0 ? null : description,
    typeCodes: draft.typeCodes.length === 0 ? null : [...draft.typeCodes],
  };
}

export function updateBody(draft: GroupDraft, rowVersion: string): UpdatePermitGroupRequest {
  const description = compact(draft.description);

  return {
    nameL10n: compact(draft.name),
    descriptionL10n: Object.keys(description).length === 0 ? null : description,
    isActive: draft.isActive,
    rowVersion,
  };
}

export function typesBody(draft: GroupDraft, rowVersion: string): ReplacePermitGroupTypesRequest {
  return { typeCodes: [...draft.typeCodes], rowVersion };
}

/** Коди відмов групи, які екран розрізняє. */
export const GroupErrorCodes = {
  notFound: 'ECR-PRM-0405',
  duplicate: 'ECR-PRM-4097',
  inUse: 'ECR-PRM-4098',
  conflict: 'ECR-PRM-4099',
  invalid: 'ECR-PRM-0422',
} as const;

/** Поле форми, до якого прив'язується відмова сервера. */
export type GroupField = 'code' | 'name' | 'types';

/**
 * Яке поле форми відповідає `messageKey` відмови `0422`/`4097` (інакше - відмова всієї форми, наприклад
 * `permitGroupTextTooLong`: сервер не каже, назва це чи опис).
 */
export function fieldOfMessageKey(messageKey: string): GroupField | null {
  switch (messageKey) {
    case 'err.ECR-PRM-0422.permitGroupCodeInvalid':
    case 'err.ECR-PRM-4097.code':
    case 'err.ECR-PRM-4097.registryCode':
      return 'code';
    case 'err.ECR-PRM-0422.permitGroupNameRequired':
      return 'name';
    case 'err.ECR-PRM-0422.permitGroupMemberDuplicate':
    case 'err.ECR-PRM-0422.permitGroupMemberUnknown':
    case 'err.ECR-PRM-0422.permitGroupMemberNotPublished':
      return 'types';
    default:
      return null;
  }
}

/** `messageKey` відмови та підстановки (`extensions2`) - як їх бачить `problemText`. */
export function messageKeyOf(error: unknown): { key: string; params: Record<string, string> } | null {
  if (!(error instanceof EcrApiError)) return null;
  const raw = error.problem.extensions2?.['messageKey'];
  if (typeof raw !== 'string' || raw === '') return null;

  const params: Record<string, string> = {};
  // `errorCode`/`correlationId` лежать у тілі поруч з підстановками (розширення пласкі), але в тексті не потрібні.
  const technical = new Set(['messageKey', 'errorCode', 'correlationId']);
  for (const [name, value] of Object.entries(error.problem.extensions2 ?? {})) {
    if (!technical.has(name) && (typeof value === 'string' || typeof value === 'number')) params[name] = String(value);
  }

  return { key: raw, params };
}
