import type { ColumnDataSchemaModel, EditorBase, HyperFunc, VNode } from '@revolist/revogrid';
import type { RegistryEntryDto } from '@/api/types';

/**
 * Кастомний редактор RevoGrid для комірок `CellDataType.Lookup` (директива
 * `docs/build/directive-registry-lookup-and-cell-style.md`, PR A4).
 *
 * ⛔ Перший кастомний редактор RevoGrid у проєкті — зразка в коді не було
 * (`A.0` директиви). Бібліотека приймає `column.editor` або як конструктор
 * класу (`EditorCtrConstructible`), або як просту функцію
 * (`EditorCtrCallable: (column, save, close) => EditorBase`,
 * `@revolist/revogrid/dist/types/types/selection.d.ts`). Обрано ДРУГЕ: немає
 * жодного внутрішнього стану, який виживав би довше одного відкриття
 * редактора (перелік записів приходить ГОТОВИМ, замкненням, а не запитом
 * зсередини редактора — див. нижче), тож клас із власним `constructor` не
 * додав би нічого, крім зайвого рівня непрямоти.
 *
 * ⚠ Записи довідника НЕ запитуються тут: `DocumentGrid.tsx` уже завантажує
 * їх через `useQuery`/`useQueries` (кеш TanStack Query за кодом довідника,
 * той самий кеш, що й решта застосунку) і передає готовий масив у
 * `gridColumns()` → сюди. Мережевий запит УСЕРЕДИНІ `render()` (викликається
 * на кожен рендер `RevoEdit`) означав би новий запит на кожен фрейм, поки
 * дані не прийдуть, — і `render()` мусив би повертати щось СИНХРОННО, ще до
 * відповіді сервера.
 */

/** Значення комірки в моделі рядка `DocumentGrid` — те, що приходить у `column.value`. */
type LookupCellValue = number | string | null | undefined;

/**
 * Поточний обраний запис комірки — число (`ValueRegistryEntryId`) або
 * рядкове представлення того самого числа (RevoGrid не гарантує, яким саме
 * типом прийде `value`, `A7-01`-подібний клас неоднозначності).
 */
function currentEntryId(value: LookupCellValue): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string' && value.trim().length > 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  return null;
}

/**
 * Фабрика редактора, зв'язана з КОНКРЕТНИМ переліком записів однієї колонки
 * (`gridColumns` викликає її окремо на кожну `Lookup`-колонку зі своїм
 * довідником).
 *
 * **Готово коли** (директива, PR A4): комірка показує dropdown із записами
 * довідника; вибір запису шле в `PatchCells` `entryId` (число), не текст
 * показу; комірка без вибору — порожній плейсхолдер, а не помилка (перший
 * `<option>` — завжди порожній рядок, і `save(null)` при його виборі минає
 * `coerce()` як явне очищення комірки, `edits.ts`).
 */
export function createLookupCellEditor(entries: readonly RegistryEntryDto[]) {
  return (
    column: ColumnDataSchemaModel,
    save: (value?: unknown, preventFocus?: boolean) => void,
  ): EditorBase => {
    const selectedId = currentEntryId(column.value as LookupCellValue);

    return {
      render(h: HyperFunc<VNode>): VNode {
        return h(
          'select',
          {
            class: 'ecr-lookup-cell-editor',
            style: { width: '100%', height: '100%' },
            // ⚠ Автофокус — той самий очікуваний UX, що й вбудований
            // `TextEditor` (клавіатурна навігація Excel-подібна: Enter/Tab
            // відкриває редактор і одразу дає друкувати/обирати).
            autoFocus: true,
            onChange: (event: Event) => {
              const target = event.target as HTMLSelectElement;

              // ⛔ Порожня опція — це «прибрати вибір», не «нічого не
              // сталося»: `save(null)` таки викликається, а не пропускається,
              // інакше очистити раз обрану комірку стало б неможливо з
              // клавіатури/миші (довелося б Delete окремо, поза редактором).
              save(target.value === '' ? null : Number(target.value));
            },
          },
          [
            h('option', { value: '', selected: selectedId === null }, ''),
            ...entries.map((entry) =>
              h(
                'option',
                { value: String(entry.id), selected: entry.id === selectedId },
                entry.display,
              ),
            ),
          ],
        );
      },
    };
  };
}

/**
 * Показ значення `Lookup`-комірки поза редагуванням: `entry.Display`
 * (`DisplayL10n` мовою інтерфейсу, той самий канонічний вибір поля показу,
 * що й усюди в застосунку — `A.0` директиви, "Рішення людини"), а не сирий
 * `ValueRegistryEntryId`.
 *
 * ⚠ Запис, якого немає в переліку (видалили довідник, застарілий кеш) —
 * фолбек на сам ідентифікатор ТЕКСТОМ, а не порожній рядок і не помилка:
 * дані в комірці є, і ховати це від користувача означало б показувати
 * порожню комірку, у якій насправді щось лежить.
 */
export function lookupCellDisplay(
  value: unknown,
  entries: readonly RegistryEntryDto[],
): string {
  const id = currentEntryId(value as LookupCellValue);
  if (id === null) return '';

  return displayIndexOf(entries).get(id) ?? String(id);
}

/**
 * Показ записів довідника за ідентифікатором (`CL-03`).
 *
 * ⛔ Тут стояв `entries.find(...)` — на КОЖНУ `Lookup`-комірку кожного
 * перемальовування (`cellTemplate` у `gridColumns`). Довідник замовника — це
 * тисячі записів (`RegistriesController` віддає до 50 000 однією відповіддю),
 * тобто колонка на 500 рядків коштувала мільйони порівнянь за кадр.
 *
 * ⚠ Мемоїзація — за самим масивом записів: він приходить із кеша TanStack
 * Query за кодом довідника й лишається тим самим об'єктом між рендерами, доки
 * довідник не перечитали. `WeakMap` тримає мапу рівно доти, доки живий масив.
 */
const displayCache = new WeakMap<readonly RegistryEntryDto[], ReadonlyMap<number, string>>();

function displayIndexOf(entries: readonly RegistryEntryDto[]): ReadonlyMap<number, string> {
  const known = displayCache.get(entries);
  if (known !== undefined) return known;

  const index = new Map<number, string>();

  // ⚠ Перший запис виграє — та сама поведінка, що й у `find()`: дублікат
  // ідентифікатора в довіднику неможливий, але поведінка не має мінятися
  // разом зі швидкістю.
  for (const entry of entries) {
    if (!index.has(entry.id)) index.set(entry.id, entry.display);
  }

  displayCache.set(entries, index);

  return index;
}
