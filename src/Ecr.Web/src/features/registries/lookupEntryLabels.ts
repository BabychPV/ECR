import type { RegistryEntryDto } from '@/api/types';

/**
 * Підписи записів Lookup-переліку для піклера (комірка сітки, поле шапки).
 *
 * Звичайний довідник - `entry.display`. Піклер ГРУПИ типів дозволів (`PRMG_*`) віддає записи з `typeCode`/`typeName`:
 * номер дозволу унікальний лише в межах типу, тож у переліку з кількох типів підпис - «номер — тип». Якщо й так
 * збігаються (той самий номер і тип, різний початок чинності - перевидані дозволи), до підпису додається
 * «(початок чинності)», щоб людина відрізнила їх.
 *
 * ⚠ Запис без `typeName`/`typeCode` лишається `display` як є: поведінка звичайних довідників і шапок не змінюється.
 */
function typeOf(entry: RegistryEntryDto): string {
  const name = entry.typeName?.trim() ?? '';
  if (name !== '') return name;

  return entry.typeCode?.trim() ?? '';
}

function build(entries: readonly RegistryEntryDto[]): ReadonlyMap<number, string> {
  // ОДИН прохід по записах (довідник - до 50 тис.; `rowIndex.test` лічить звернення): далі працюємо зі знімком.
  const items: { id: number; label: string; typed: boolean; validFrom: string | null }[] = [];
  const seen = new Map<string, number>();
  const result = new Map<number, string>();

  for (const entry of entries) {
    const type = typeOf(entry);
    const label = type === '' ? entry.display : `${entry.display} — ${type}`;
    items.push({ id: entry.id, label, typed: type !== '', validFrom: entry.validFrom });
    if (type !== '') seen.set(label, (seen.get(label) ?? 0) + 1);
  }

  for (const item of items) {
    // Перший запис з тим самим id виграє - як у `find()`.
    if (result.has(item.id)) continue;
    const ambiguous = item.typed && (seen.get(item.label) ?? 0) > 1 && item.validFrom !== null;
    result.set(item.id, ambiguous ? `${item.label} (${item.validFrom ?? ''})` : item.label);
  }

  return result;
}

const cache = new WeakMap<readonly RegistryEntryDto[], ReadonlyMap<number, string>>();

/** Підписи записів за id (id записів унікальні). Мемоїзовано за масивом: довідник на тисячі записів не перебудовується. */
export function entryLabelsOf(entries: readonly RegistryEntryDto[]): ReadonlyMap<number, string> {
  const known = cache.get(entries);
  if (known !== undefined) return known;

  const built = build(entries);
  cache.set(entries, built);

  return built;
}
