import type { JSX, ReactNode } from 'react';
import { Box, Stack } from '@mantine/core';
import { PageHeader, type PageHeaderProps } from '@/shared/ui/PageHeader';
import { DetailDrawer, type DetailDrawerProps } from '@/shared/ui/DetailDrawer';
import { StatStrip, type StatStripProps } from '@/shared/ui/StatStrip';
import { hasContent } from '@/shared/ui/TwoLine';

/**
 * `ListPage` — ЄДИНИЙ шаблон сторінки-переліку (`KIT.md` §1.5 і §3, директива
 * №15 §2, шар 3: «`PageHeader + StatStrip? + FilterBar + DataTable +
 * DetailDrawer` — 12 із 20 екранів збираються з нього»).
 *
 * ⛔ **`FilterBar` і `DataTable` сюди НЕ імпортуються — вони приходять
 * пропами.** Дві причини, і обидві самостійні:
 *
 *  1. Обидва компоненти пишуться ПАРАЛЕЛЬНОЮ гілкою того ж кроку `UI-06` і в
 *     `main` їх ще немає. Імпорт зробив би цей PR незбираним до мержу
 *     сусіднього — тобто дві гілки з перетином у колонці «Файли для запису»
 *     (CLAUDE.md §1 забороняє саме це).
 *  2. Так правильніше й по суті: шаблон КОМПОНУЄ, а не знає конкретні
 *     реалізації. Перелік усередині вкладки (`screens-ops.js:177`) ставить під
 *     смугою не `DataTable`, а власний вміст; сторінка з двома таблицями теж
 *     збирається цим самим шаблоном. Прибивши типи `DataTableProps` у
 *     сигнатуру, ми зробили б ці випадки неможливими без правки шаблону.
 *
 * ⚠ `PageHeader`, `StatStrip` і `DetailDrawer` — навпаки, імпортуються:
 * перший і третій уже в `main`, другий — у цьому ж PR. Різниця не в смаку:
 * шаблон ВІДПОВІДАЄ за те, що шапка рівно одна, смуга не малюється без
 * показників (`D15-06`), а шухляда закрита за замовчуванням (`L2`) — цього не
 * можна пообіцяти, приймаючи готові вузли.
 */
export interface ListPageProps {
  /** Шапка сторінки. Рівно одна `primary`-дія — це стереже `L1` на екрані. */
  readonly header: PageHeaderProps;

  /**
   * Показники над переліком. Немає — смуги немає (`D15-06`).
   *
   * ⚠ ПРОПСИ `StatStrip`, а не готовий вузол: інакше сторінка могла б
   * поставити сюди що завгодно, і межа `L4` («≤ 4 цифри») перестала б
   * перевірятися типом шаблону.
   */
  readonly stats?: StatStripProps | undefined;

  /**
   * Рядок фільтрів — вузол (`FilterBar` сусідньої гілки).
   *
   * ⛔ `D15-06`: порожній вузол не отримує ні обгортки, ні відступу.
   */
  readonly filters?: ReactNode;

  /** Перелік: таблиця або інше подання разом зі своїми станами. */
  readonly table: ReactNode;

  /**
   * Шухляда подробиць рядка.
   *
   * ⛔ `L2`: вона ЗАКРИТА за замовчуванням, і це не налаштування — цього
   * пропа просто немає, щоб її відкрити. Відкритість визначає `?panel=` в
   * адресі (`DetailDrawer`), тобто після звичайного рендера сторінки
   * подробиць на екрані немає.
   */
  readonly detail?: DetailDrawerProps | undefined;

  /** Додатковий вміст під переліком (пагінація, підсумок, примітка). */
  readonly children?: ReactNode;
}

/**
 * Шаблон сторінки-переліку.
 *
 * ⛔ Власного `<main>` тут немає: його малює `AppLayout` (`AppShell.Main`,
 * `id=MainContentId`, туди ж веде skip-link). Другий `main` на сторінці —
 * порушення `landmark-no-duplicate-main`, тобто червоний `a11y` в обох темах.
 */
export function ListPage({
  header,
  stats,
  filters,
  table,
  detail,
  children,
}: ListPageProps): JSX.Element {
  return (
    <Stack gap="md" data-list-page="">
      <PageHeader {...header} />

      {/* `D15-06`: немає показників — немає смуги. */}
      {stats !== undefined && <StatStrip {...stats} />}

      {/* `D15-06`: немає фільтрів — немає й порожнього рядка над таблицею. */}
      {hasContent(filters) && <Box data-list-filters="">{filters}</Box>}

      <Box data-list-table="">{table}</Box>

      {hasContent(children) && <Box data-list-footer="">{children}</Box>}

      {/*
       * ⛔ Шухляда — ОСТАННЯ в розмітці й закрита: `DetailDrawer` малює вміст
       * лише за збігу `?panel=` з `panelId`. Без параметра в адресі
       * `queryByRole('complementary')` (як і `role="dialog"`) дає `null` —
       * саме те, чого вимагає `L2`.
       */}
      {detail !== undefined && <DetailDrawer {...detail} />}
    </Stack>
  );
}
