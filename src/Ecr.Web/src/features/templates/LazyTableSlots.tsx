import { memo, useCallback, useEffect, useRef, useState, type JSX, type ReactNode } from 'react';
import { Box, Text } from '@mantine/core';

/**
 * Таблиці аркуша в дереві структури версії — ОДНИМ списком, вміст кожної
 * монтується за прокруткою.
 *
 * ⛔ ЩО БУЛО. `TemplateVersionPage` малював УСІ таблиці всіх аркушів одразу:
 * `Accordion.Panel` (Mantine `Collapse`) тримає дітей змонтованими й у
 * згорнутому стані. На версії чинного розміру (92 таблиці, ~2986 колонок,
 * у кожної колонки — 2–5 кнопок) це тисячі компонентів ще до першого кліку.
 * Замір на продакшн-збірці (медіана з 3, `vite preview`, стенд `EcrUx`):
 * відкриття v2 (чернетка) — 1478 мс до «settled», із них 1020 мс довгих
 * задач; розгортання аркуша з 91 таблиці — 1556 мс.
 *
 * ⛔ ЧОГО НЕ РОБИМО. «Монтувати вміст аркуша лише при першому розгортанні»
 * вже пробували: блок просто переїхав на клік (3772 мс). Причина та сама —
 * 91 таблиця за раз; змінився лише момент.
 *
 * ⛔ ЩО СТАЛО. Той самий механізм, що й сітки документа (`#299`,
 * `features/grid/SheetTables.tsx`): слот кожної таблиці спершу — легкий
 * заповнювач (заголовок + зарезервована висота), а повний вміст монтується,
 * щойно слот наближається до видимої області (`IntersectionObserver`,
 * запас `TemplateTableMountAhead`). Згорнутий аркуш не перетинає екрана
 * ніколи — тож при відкритті сторінки не монтується ЖОДНА таблиця.
 *
 * ⚠ Змонтоване лишається змонтованим (множина тільки росте) — як і в `#299`:
 * повторна прокрутка не має платити вдруге, а згортання аркуша не повинно
 * губити фокус чи відкритий стан кнопок.
 *
 * ⚠ ВИСОТА ЗАПОВНЮВАЧА — не косметика. Нульова висота зібрала б усі 91 слот
 * в один екран, спостерігач побачив би їх усі — і змонтувалось би знову все.
 * Тому заповнювач тримає ОЦІНКУ висоти справжньої таблиці
 * (`estimateTemplateTableHeight`) — прокрутка не стрибає, смуга прокрутки
 * одразу правдива.
 *
 * ⚠ ПЕРШІ `eager` таблиць відкритого аркуша монтуються без спостерігача: вони
 * гарантовано нагорі щойно розгорнутої панелі, і чекати на них подію
 * видимості означало б показати людині заповнювач там, де вміст уже можна
 * було намалювати. Це ж робить поведінку визначеною там, де розкладки немає
 * взагалі (jsdom: заглушка `IntersectionObserver` у `src/test/setup.ts`
 * інертна НАВМИСНО, див. її коментар).
 *
 * ⚠ КЛАВІАТУРА. Заповнювач не є зупинкою Tab (91 зайва зупинка — шум). Щоб
 * Tab не «перестрибував» незмонтовані таблиці, фокус усередині таблиці N
 * монтує таблицю N+1 — до того, як людина до неї дійде.
 */
export interface LazyTableSlotsProps<T extends { readonly id: number }> {
  readonly items: readonly T[];
  /** Аркуш розгорнутий — лише тоді діє `eager`. */
  readonly active: boolean;
  /** Скільки перших таблиць монтувати без події видимості. */
  readonly eager?: number;
  /** Доступна назва таблиці — і для заповнювача, і для змонтованого слота. */
  readonly nameOf: (item: T) => string;
  /** Заголовок заповнювача — той самий текст, що й у справжньому заголовку. */
  readonly titleOf: (item: T) => ReactNode;
  readonly heightOf: (item: T) => number;
  readonly render: (item: T) => ReactNode;
}

/** Атрибут слота: несе `id` таблиці (читається з DOM, як у `#299`). */
export const TemplateTableSlotAttribute = 'data-template-table-slot';

/**
 * Запас монтування. Більший, ніж у сітки документа (`200px`): тут немає
 * запиту зрізу, лише рендер, і таблиця коштує кілька мілісекунд — дешевше
 * змонтувати трохи раніше, ніж показати заповнювач при швидкій прокрутці.
 */
export const TemplateTableMountAhead = '600px 0px';

/**
 * Геометрія таблиці структури (Mantine `Table striped withTableBorder`,
 * кнопки `compact-xs`), у пікселях. Зміряно на стенді `EcrUx`, продакшн-збірка,
 * вікно 1400×900, 90 таблиць обох версій: рядок таблиці — 31.5 (тобто
 * `1636 − 313 = 1323` на `51 − 9 = 42` рядки), шапка — 29.5; слот понад саму
 * таблицю колонок — 72 (v1, лише перегляд) / 75 (v2, з кнопками правки) для
 * фіксованої таблиці без рядків. Оцінка за цими числами розходиться зі
 * справжньою висотою на 0–3 px.
 *
 * ⚠ Секцію НЕпорожніх рядків на стенді зміряти не було на чому (у всіх 92
 * таблиць `rows = 0`), тож її заголовок — оцінка, а не замір. Промах тут
 * коштує лише стрибка смуги прокрутки, не монтування зайвого: слот
 * монтується із запасом `TemplateTableMountAhead`.
 */
const Geometry = {
  /** Заголовок таблиці (`Group mt="sm"`) + `mt="xs"` таблиці колонок. */
  header: 36 + 10,
  /** Шапка таблиці (`thead`). */
  tableHead: 29.5,
  /** Рядок таблиці — колонка чи рядок шаблону. */
  row: 31.5,
  /** Секція рядків фіксованої таблиці, коли рядків немає («Рядків немає»). */
  rowsEmpty: 27,
  /** Заголовок секції рядків + `mt="xs"` таблиці рядків (оцінка). */
  rowsHeader: 32 + 10,
} as const;

export function estimateTemplateTableHeight(table: {
  readonly rowMode: string;
  readonly columns: readonly unknown[];
  readonly rows: readonly unknown[];
}): number {
  const columns = Geometry.header + Geometry.tableHead + table.columns.length * Geometry.row;
  if (table.rowMode === 'Dynamic') return columns;

  const rows =
    table.rows.length === 0
      ? Geometry.rowsEmpty
      : Geometry.rowsHeader + Geometry.tableHead + table.rows.length * Geometry.row;

  return Math.round(columns + rows);
}

export function LazyTableSlots<T extends { readonly id: number }>({
  items,
  active,
  eager = 1,
  nameOf,
  titleOf,
  heightOf,
  render,
}: LazyTableSlotsProps<T>): JSX.Element {
  const [mounted, setMounted] = useState<ReadonlySet<number>>(() => new Set<number>());
  const slots = useRef(new Map<number, HTMLDivElement>());

  const add = useCallback((ids: readonly number[]): void => {
    if (ids.length === 0) return;
    setMounted((previous) => {
      const next = new Set(previous);
      for (const id of ids) next.add(id);
      // ⚠ Та сама множина, якщо нічого не додалося — інакше кожне спрацювання
      // спостерігача давало б новий рендер і нового спостерігача (`#299`).
      return next.size === previous.size ? previous : next;
    });
  }, []);

  const isMounted = (item: T, index: number): boolean =>
    mounted.has(item.id) || (active && index < eager);

  // Закріпити «eager» у множині: згортання аркуша не повинно їх розмонтувати.
  useEffect(() => {
    if (!active) return;
    add(items.slice(0, eager).map((item) => item.id));
  }, [active, eager, items, add]);

  useEffect(() => {
    // ⚠ Немає `IntersectionObserver` — монтуємо все: деградація до попередньої,
    // робочої поведінки, а не до порожнього екрана.
    if (typeof IntersectionObserver === 'undefined') {
      add(items.map((item) => item.id));
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        const appeared: number[] = [];
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          const raw = entry.target.getAttribute(TemplateTableSlotAttribute);
          if (raw !== null) appeared.push(Number(raw));
        }
        add(appeared);
      },
      { rootMargin: TemplateTableMountAhead },
    );

    // ⚠ Новий спостерігач на кожну зміну `mounted` — самовідновлення, як у
    // `#299`: після монтування розмітка зсувається, і перший такт нового
    // спостерігача добирає слоти, що через цей зсув доїхали до екрана.
    for (const item of items) {
      if (mounted.has(item.id)) continue;
      const node = slots.current.get(item.id);
      if (node !== undefined) observer.observe(node);
    }

    return () => observer.disconnect();
  }, [items, mounted, add]);

  /** Стабільна між рендерами списку — інакше `memo` слота нижче марний. */
  const register = useCallback((id: number, node: HTMLDivElement | null): void => {
    if (node === null) slots.current.delete(id);
    else slots.current.set(id, node);
  }, []);

  return (
    <>
      {items.map((item, index) => (
        <Slot
          key={item.id}
          item={item}
          live={isMounted(item, index)}
          nextId={items[index + 1]?.id}
          register={register}
          add={add}
          nameOf={nameOf}
          titleOf={titleOf}
          heightOf={heightOf}
          render={render}
        />
      ))}
    </>
  );
}

interface SlotProps<T extends { readonly id: number }> {
  readonly item: T;
  readonly live: boolean;
  readonly nextId: number | undefined;
  readonly register: (id: number, node: HTMLDivElement | null) => void;
  readonly add: (ids: readonly number[]) => void;
  readonly nameOf: (item: T) => string;
  readonly titleOf: (item: T) => ReactNode;
  readonly heightOf: (item: T) => number;
  readonly render: (item: T) => ReactNode;
}

/**
 * Один слот таблиці.
 *
 * ⛔ `memo` — НЕ косметика, а половина всього виграшу, і куплена заміром.
 * Без нього монтування ОДНІЄЇ таблиці (зміна `mounted`) перерендерювало
 * ВСІ вже змонтовані: `render(item)` дає нові елементи, і React звіряв
 * заново кожне піддерево. Ціна росла з кожною змонтованою таблицею —
 * O(n²) на прокрутку аркуша. Замір (v2, 91 таблиця, прокрутка кроками по
 * 400 px до кінця): 37.7 с довгих задач сумарно, найдовша — 956 мс. Слот
 * перерендерюється лише тоді, коли змінилося щось ЙОГО: таблиця, стан
 * «змонтована», або сторінка дала новий `render` (її власний стан змінився —
 * тоді перемалювати таблиці справді треба).
 */
const Slot = memo(function Slot<T extends { readonly id: number }>({
  item,
  live,
  nextId,
  register,
  add,
  nameOf,
  titleOf,
  heightOf,
  render,
}: SlotProps<T>): JSX.Element {
  const id = item.id;

  return (
    <div
      ref={(node: HTMLDivElement | null) => register(id, node)}
      data-template-table-slot={id}
      data-template-table-mounted={live}
      onFocus={nextId === undefined ? undefined : () => add([nextId])}
    >
      {live ? (
        render(item)
      ) : (
        <Box
          role="group"
          aria-label={nameOf(item)}
          aria-busy="true"
          style={{ height: heightOf(item) }}
        >
          <Text fw={600} mt="sm">
            {titleOf(item)}
          </Text>
        </Box>
      )}
    </div>
  );
}) as <T extends { readonly id: number }>(props: SlotProps<T>) => JSX.Element;
