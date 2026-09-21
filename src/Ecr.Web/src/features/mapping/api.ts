import { apiFetch, EcrApiError } from '@/api/client';
import type {
  CreateEntityFieldMapRequest,
  EntityFieldMapDto,
  MappedFieldPreview,
  MappingPreview,
} from '@/api/types';

/**
 * Ширина вікна перегляду за замовчуванням, у днях.
 *
 * ⚠ Те саме число, що й на сервері. Дублювання свідоме і безпечне: сервер
 * лишається джерелом істини (він підставить своє, якщо межі не надіслані), а
 * тут воно потрібне, щоб показати людині, ЯКИЙ проміжок вона дивиться.
 */
export const WindowDays = 7;

/** Межі вікна перегляду від заданого моменту назад. */
export function windowFrom(now: Date): { fromUtc: string; toUtc: string } {
  const from = new Date(now.getTime() - WindowDays * 24 * 60 * 60 * 1000);

  return { fromUtc: from.toISOString(), toUtc: now.toISOString() };
}

/**
 * Читає перегляд мапінгу.
 *
 * ⚠ Межі вікна передаються ЯВНО, хоча сервер має свої за замовчуванням: без
 * них два послідовні запити дали б різні вікна, і людина бачила б, як число
 * «саме змінилося» між оновленнями сторінки.
 */
export function fetchMappingPreview(
  sourceEntityId: number,
  window: { fromUtc: string; toUtc: string },
): Promise<MappingPreview> {
  const query = new URLSearchParams({ fromUtc: window.fromUtc, toUtc: window.toUtc });

  return apiFetch<MappingPreview>(
    `/api/v1/sources/${sourceEntityId}/mapping/preview?${query.toString()}`,
  );
}

/**
 * Мапінги, які нічого не покладуть у документ.
 *
 * ⛔ Функція чиста і винесена з подання навмисно: це і є визначення розриву з
 * боку мапінгу, і воно має перевірятися тестом, а не оглядом екрана.
 * `RawOnly` сюди НЕ входить — «точки лишаються сирими для звірки» це свідомий
 * вибір людини (`D-118`), а не дефект.
 */
export function brokenMaps(fields: readonly MappedFieldPreview[]): MappedFieldPreview[] {
  return fields.filter(
    (field) => field.outcome === 'NoData' || field.outcome === 'TargetMissing',
  );
}

/** Чи перегляд не знайшов жодного розриву. */
export function hasNoGaps(preview: MappingPreview): boolean {
  return (
    brokenMaps(preview.fields).length === 0 &&
    preview.unmappedSourceFields.length === 0 &&
    preview.uncoveredColumns.length === 0
  );
}

/**
 * Заводить мапінг поля джерела (Прогалина 1 директиви паритету зі старою
 * системою).
 *
 * ⛔ До цієї функції жоден екран не мав способу завести мапінг для
 * щойно доданої колонки чи щойно побаченого в переліку розривів поля
 * джерела — єдиним шляхом лишався ручний SQL.
 */
export function createEntityFieldMap(body: CreateEntityFieldMapRequest): Promise<EntityFieldMapDto> {
  return apiFetch<EntityFieldMapDto>('/api/v1/entity-field-maps', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Призупиняє мапінг: збір за ним більше не пише значень (`BE-27`).
 *
 * ⛔ Не видалення. Мапінг лишається разом з одиницями й адресою рядка — саме
 * тому пауза і є виходом для мапінгу, за яким уже зібрано дані: він пояснює
 * ці дані, а видалення лишило б їх без пояснення.
 */
export function pauseEntityFieldMap(fieldMapId: number): Promise<EntityFieldMapDto> {
  return apiFetch<EntityFieldMapDto>(`/api/v1/entity-field-maps/${fieldMapId}/pause`, {
    method: 'POST',
  });
}

/** Повертає призупинений мапінг у збір (`BE-27`). */
export function resumeEntityFieldMap(fieldMapId: number): Promise<EntityFieldMapDto> {
  return apiFetch<EntityFieldMapDto>(`/api/v1/entity-field-maps/${fieldMapId}/resume`, {
    method: 'POST',
  });
}

/**
 * Приймає нову одиницю джерела замість оголошеної (`ФВ-16.9`).
 *
 * ⛔ Зміна одиниці в джерелі ЗУПИНЯЄ збір (`ECR-INT-0422`) і не приймається
 * кодом: мовчазна конверсія дає правдоподібні числа, помилку в яких знаходять
 * на звірці через місяць. Це рішення людини, і сервер пише його в журнал
 * безпеки разом з обома одиницями.
 */
export function acceptSourceUnitChange(
  fieldMapId: number,
  sourceUnitId: number,
): Promise<EntityFieldMapDto> {
  return apiFetch<EntityFieldMapDto>(`/api/v1/entity-field-maps/${fieldMapId}/accept-unit-change`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sourceUnitId }),
  });
}

/**
 * Видаляє мапінг (`BE-27`).
 *
 * ⚠ Сервер відмовляє з `409 ECR-INT-0409`, якщо за мапінгом уже зібрано дані,
 * і кладе у відмову `collectedPoints`. Правильна відповідь на неї — пауза, а
 * не повтор запиту; `isMappingInUse` відрізняє цей випадок від решти відмов.
 */
export function deleteEntityFieldMap(fieldMapId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/entity-field-maps/${fieldMapId}`, { method: 'DELETE' });
}

/** Код відмови «мапінг не в тому стані» — той самий, що в `ErrorCodes.cs`. */
export const MappingStateConflictCode = 'ECR-INT-0409';

/** Ключ каталогу, яким сервер позначає саме «за мапінгом уже зібрано дані». */
export const MappingHasCollectedDataKey = 'err.ECR-INT-0409.mappingHasCollectedData';

/**
 * Скільки точок уже зібрано, якщо відмова саме про це; інакше `null`.
 *
 * ⛔ Перевіряється `messageKey`, а не сам код: `ECR-INT-0409` покриває чотири
 * стани (повторна пауза, відновлення непризупиненого, та сама одиниця, зібрані
 * дані), і пропонувати паузу у відповідь на «уже призупинено» — це порада
 * зробити те, що вже зроблено.
 *
 * ⚠ Повертається саме ЧИСЛО, а не `true`: діалог мусить сказати «зібрано 4 812
 * точок», інакше людина обирає між паузою й видаленням наосліп — той самий
 * випадок, що й «на запис посилаються N комірок» у довіднику.
 */
export function collectedPointsBlockingDelete(error: unknown): number | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== MappingStateConflictCode) {
    return null;
  }

  const extensions = error.problem.extensions2 ?? {};

  if (extensions['messageKey'] !== MappingHasCollectedDataKey) {
    return null;
  }

  const points = extensions['collectedPoints'];

  return typeof points === 'number' ? points : null;
}
