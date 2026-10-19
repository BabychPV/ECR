import type { CreatePermitRequest, PermitTypeListItem } from '../api/types';

/**
 * Чиста модель майстра «New permit» (PM-07; макет `new-permit`): дані кроків, початкові дати, перевірка кроку
 * «Details», тіло запиту. Нічого з React: усе перевіряється без рендера.
 *
 * ⛔ Перевіряється лише те, що сервер теж відхилить (`numberRequired`, `validityOrder`): клієнт показує
 * відмову ДО натискання, але не вигадує власних правил (довжини, формату номера, обов'язковості розташування).
 */
export interface NewPermitData {
  /** Код обраного типу або порожньо. */
  readonly type: string;
  readonly number: string;
  readonly location: string;
  /** `YYYY-MM-DD`: перший чинний день. */
  readonly validFrom: string;
  /** `YYYY-MM-DD`: перший НЕчинний день. */
  readonly validTo: string;
}

export type NewPermitProblem = 'typeRequired' | 'numberRequired' | 'datesRequired' | 'validityOrder';

const Iso = /^\d{4}-\d{2}-\d{2}$/;

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

/** `YYYY-MM-DD` за ЛОКАЛЬНИМИ компонентами дати (не UTC: людина вводить свій календарний день). */
export function isoDate(date: Date): string {
  return `${String(date.getFullYear())}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Початкова чинність: від сьогодні рівно на рік (так само, як поновлення за замовчуванням на сервері). */
export function defaultValidity(today: Date): { validFrom: string; validTo: string } {
  const next = new Date(today.getFullYear() + 1, today.getMonth(), today.getDate());

  return { validFrom: isoDate(today), validTo: isoDate(next) };
}

export function initialData(today: Date, types: readonly PermitTypeListItem[]): NewPermitData {
  return { type: publishedTypes(types)[0]?.code ?? '', number: '', location: '', ...defaultValidity(today) };
}

/** Типи, за якими можна заводити дозволи: лише опубліковані (`status === 'Published'`). */
export function publishedTypes(types: readonly PermitTypeListItem[]): PermitTypeListItem[] {
  return types.filter((item) => item.status === 'Published');
}

/** Перевірка кроку «Type». */
export function checkType(data: NewPermitData, types: readonly PermitTypeListItem[]): NewPermitProblem | null {
  return publishedTypes(types).some((item) => item.code === data.type) ? null : 'typeRequired';
}

/** Перевірка кроку «Details»: номер, обидві дати, кінець після початку. */
export function checkDetails(data: NewPermitData): NewPermitProblem | null {
  if (data.number.trim().length === 0) return 'numberRequired';
  if (!Iso.test(data.validFrom) || !Iso.test(data.validTo)) return 'datesRequired';
  if (data.validTo <= data.validFrom) return 'validityOrder';

  return null;
}

/** Тіло `POST /api/v1/permits`: порожнє розташування — `null`, а не порожній рядок. */
export function toRequest(data: NewPermitData): CreatePermitRequest {
  const location = data.location.trim();

  return {
    typeCode: data.type,
    number: data.number.trim(),
    location: location.length === 0 ? null : location,
    issueDate: null,
    validFrom: data.validFrom,
    validTo: data.validTo,
    reportingFlags: null,
  };
}
