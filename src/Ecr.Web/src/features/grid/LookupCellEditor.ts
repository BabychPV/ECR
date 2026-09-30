import type { RegistryEntryDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { createListCellEditor, type ListOption } from './listCellEditor';

/**
 * Редактор комірки `CellDataType.Lookup` — пошук і вибір запису довідника
 * (директива `docs/build/directive-registry-lookup-and-cell-style.md`, PR A4;
 * `X-13`).
 *
 * ✎ `X-13`: тут стояв `<select>` зі збереженням на `onChange` — перша стрілка
 * вниз одразу зберігала сусідній запис і закривала редактор, а пошуку серед
 * тисяч записів не було. Тепер це спільний редактор-список
 * (`listCellEditor.ts`): стрілки лише переміщують, Enter чи клік підтверджує,
 * друк шукає за назвою й кодом.
 *
 * ⚠ Записи довідника НЕ запитуються тут: `DocumentGrid.tsx` уже завантажує їх
 * (`useQueries`, кеш за кодом довідника) і передає готовий масив. `null` —
 * довідник ще їде: редактор показує «завантаження», а не порожній перелік,
 * який читався б як «довідник не наповнили».
 *
 * ⚠ У `save` іде РЯДОК ідентифікатора запису (`'5'`), порожній — очищення;
 * числом його робить `coerce(raw, 'Lookup')` (`edits.ts`), тобто той самий
 * шлях, що й решта правок.
 */
export function createLookupCellEditor(entries: readonly RegistryEntryDto[] | null) {
  return createListCellEditor(
    () => ({ options: entries === null ? null : lookupOptionsOf(entries) }),
    t('grid.lookupEditorLabel'),
  );
}

/**
 * Варіанти вибору довідника: «очистити» плюс по одному на запис.
 *
 * ⚠ Мемоїзація — за самим масивом записів (`WeakMap`): довідник на 50 000
 * записів не перебудовується у варіанти на кожне відкриття редактора.
 */
export function lookupOptionsOf(entries: readonly RegistryEntryDto[]): readonly ListOption[] {
  const known = optionsCache.get(entries);
  if (known !== undefined) return known;

  const built: ListOption[] = [
    { value: null, label: t('grid.listClear') },
    ...entries.map((entry) => ({ value: String(entry.id), label: entry.display, hint: entry.code })),
  ];
  optionsCache.set(entries, built);

  return built;
}

const optionsCache = new WeakMap<readonly RegistryEntryDto[], readonly ListOption[]>();

/**
 * Ідентифікатор запису за кодом чи назвою — для вставки з Excel, де в комірці
 * стоїть `KZ` чи «Казахстан», а не номер запису.
 *
 * ⚠ Лише ТОЧНИЙ збіг (без урахування регістру): вгадувати «схожий» запис при
 * вставці означало б мовчки підставити інший. Немає збігу — `null`, і
 * значення їде як є: сервер відповість `422`, комірка лишиться позначеною.
 */
export function lookupIdOfText(text: string, entries: readonly RegistryEntryDto[]): number | null {
  const wanted = text.trim().toLowerCase();
  if (wanted.length === 0) return null;

  return (
    entries.find((entry) => entry.code.toLowerCase() === wanted)?.id ??
    entries.find((entry) => entry.display.toLowerCase() === wanted)?.id ??
    null
  );
}

/**
 * Поточний обраний запис комірки — число (`ValueRegistryEntryId`) або
 * рядкове представлення того самого числа (RevoGrid не гарантує, яким саме
 * типом прийде `value`).
 */
function currentEntryId(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string' && value.trim().length > 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

/**
 * Показ значення `Lookup`-комірки поза редагуванням: `entry.Display`
 * (`DisplayL10n` мовою інтерфейсу), а не сирий `ValueRegistryEntryId`.
 *
 * ⚠ Запис, якого немає в переліку (видалили довідник, застарілий кеш) —
 * фолбек на сам ідентифікатор ТЕКСТОМ, а не порожній рядок і не помилка:
 * дані в комірці є, і ховати це від користувача означало б показувати
 * порожню комірку, у якій насправді щось лежить.
 */
export function lookupCellDisplay(value: unknown, entries: readonly RegistryEntryDto[]): string {
  const id = currentEntryId(value);
  if (id === null) return '';

  return displayIndexOf(entries).get(id) ?? String(id);
}

/**
 * Показ записів довідника за ідентифікатором (`CL-03`).
 *
 * ⛔ Тут стояв `entries.find(...)` — на КОЖНУ `Lookup`-комірку кожного
 * перемальовування. Довідник замовника — це тисячі записів, тобто колонка на
 * 500 рядків коштувала мільйони порівнянь за кадр.
 */
const displayCache = new WeakMap<readonly RegistryEntryDto[], ReadonlyMap<number, string>>();

function displayIndexOf(entries: readonly RegistryEntryDto[]): ReadonlyMap<number, string> {
  const known = displayCache.get(entries);
  if (known !== undefined) return known;

  const index = new Map<number, string>();

  // ⚠ Перший запис виграє — та сама поведінка, що й у `find()`.
  for (const entry of entries) {
    if (!index.has(entry.id)) index.set(entry.id, entry.display);
  }

  displayCache.set(entries, index);

  return index;
}
