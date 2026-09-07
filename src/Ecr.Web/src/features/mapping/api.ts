import { apiFetch } from '@/api/client';
import type { MappedFieldPreview, MappingPreview } from '@/api/types';

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
