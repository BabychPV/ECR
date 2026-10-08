import type { components } from '@/api/schema';
import { localized } from '@/shared/i18n/localized';

type DocumentHeaderField = components['schemas']['DocumentHeaderFieldDto'];

/**
 * Секція «Contract» шапки документа — порядок полів як в Excel-формі
 * (звіт розбіжностей Contract-вкладки, RC15-A).
 *
 * ⛔ Визначення полів шапки заводить версія шаблону (серверна сторона), тож поле
 * впізнається за нормалізованим КОДОМ або англійським підписом (`Area`, `Filled in by`…):
 * нерозпізнані поля йдуть після секції звичайним переліком і нічого не губляться.
 */
export const ContractOrder = [
  { key: 'area', names: ['area'] },
  { key: 'contractor', names: ['contractor'] },
  { key: 'region', names: ['region'] },
  { key: 'location', names: ['location', 'facility', 'locationfacility'] },
  { key: 'onshoreOffshore', names: ['onshoreoffshore', 'onshore', 'offshore'] },
  { key: 'filledInBy', names: ['filledinby', 'filledby'] },
  { key: 'contractHolder', names: ['contractholder'] },
  { key: 'contractNumber', names: ['contractnumber', 'contractno'] },
  { key: 'activityType', names: ['typeofactivity', 'activitytype'] },
  { key: 'processedOn', names: ['processedon'] },
  // File Number — це BusinessKey документа, а не поле шапки: показується read-only.
  { key: 'fileNumber', names: ['filenumber', 'businesskey'] },
  { key: 'permitNumber', names: ['permitnumber', 'permit'] },
  { key: 'version', names: ['version'] },
] as const;

export type ContractKey = (typeof ContractOrder)[number]['key'];

/** Поля, які користувач не редагує: File Number (BusinessKey) і Version. */
const ReadOnlyKeys: ReadonlySet<ContractKey> = new Set<ContractKey>(['fileNumber', 'version']);

const normalize = (text: string): string => text.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');

/** Ключ секції для поля шапки або `null` — поле не з «Contract». */
export function contractKeyOf(field: DocumentHeaderField): ContractKey | null {
  const candidates = [normalize(field.code), normalize(field.label.values?.en ?? '')];

  for (const entry of ContractOrder) {
    if (candidates.some((candidate) => (entry.names as readonly string[]).includes(candidate))) return entry.key;
  }

  return null;
}

export function isContractReadOnly(key: ContractKey | null): boolean {
  return key !== null && ReadOnlyKeys.has(key);
}

/** Підпис «EN — RU»; якщо одної з мов немає чи вони збігаються — звичайний локалізований. */
export function bilingualLabel(field: DocumentHeaderField): string {
  const en = field.label.values?.en?.trim() ?? '';
  const ru = field.label.values?.ru?.trim() ?? '';

  return en.length > 0 && ru.length > 0 && en !== ru ? `${en} — ${ru}` : localized(field.label);
}

/** Поля секції у порядку Excel та решта (у порядку відповіді). Дубль ключа — у «решті». */
export function splitContractFields(fields: readonly DocumentHeaderField[]): {
  readonly contract: readonly (readonly [ContractKey, DocumentHeaderField])[];
  readonly other: readonly DocumentHeaderField[];
} {
  const byKey = new Map<ContractKey, DocumentHeaderField>();
  const other: DocumentHeaderField[] = [];

  for (const field of fields) {
    const key = contractKeyOf(field);

    if (key === null || byKey.has(key)) other.push(field);
    else byKey.set(key, field);
  }

  const contract = ContractOrder.flatMap((entry): [ContractKey, DocumentHeaderField][] => {
    const field = byKey.get(entry.key);

    return field === undefined ? [] : [[entry.key, field]];
  });

  return { contract, other };
}
