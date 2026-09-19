import { lazy, Suspense, useEffect, useRef, type JSX, type ReactNode } from 'react';
import { Anchor, Group, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { announceRoute } from './RouteAnnouncer';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';

/**
 * ⛔ Кластер дій — ЗА `import()`, і це вимога бюджету (`D-132`), а не смак.
 *
 * `PageHeader` стоїть на кожній сторінці, тож усе статичне в ньому лягає в усі
 * 24 маршрутні чанки. Виміряно на `TemplateVersionPage` (межа 250.0 КБ gzip):
 * розширення шапки коштувало +0.6 КБ КОЖНОМУ маршруту, і два маршрути, що
 * стояли за пів кілобайта від межі, перевалили за неї. Кнопки, меню й
 * `actionButton` — найбільша частина цього приросту; підстави, чому лівий
 * блок лишається тут, а не їде слідом, описані в `PageHeaderActions.tsx`.
 *
 * ⚠ `fallback={actions}`, а не `null`: 24 чинні сторінки передають дії саме
 * пропом `actions`, і вони мусять бути на екрані з першого кадру. Нові кнопки
 * з'являються на кадр пізніше — і платять за це лише ті сторінки, які їх
 * замовили.
 */
const PageHeaderActions = lazy(() => import('./PageHeaderActions'));

/**
 * Дія в шапці сторінки (`KIT.md` §6.4: `{label, icon, onClick|href, id}`).
 *
 * ⛔ Саме ОПИС дії, а не готовий вузол. Готовий `ReactNode` не можна перекласти
 * в пункт меню: правило «понад 2 `secondary` — у меню» (директива №15, §2,
 * Шар 2) вимагає знати ПІДПИС і ДІЮ окремо, а з довільного вузла їх не
 * дістати. Наявний проп `actions` (вузол) лишається і нічого не втрачає — він
 * просто не бере участі в переливанні в меню.
 */
export interface HeaderAction {
  /** Підпис кнопки або пункту меню. Приходить пропом, а не з каталогу: шапка
   *  не знає, який саме рядок потрібен цьому екрану. */
  readonly label: string;

  /** Дія. Або вона, або `href` — не обидва. */
  readonly onClick?: (() => void) | undefined;

  /** Перехід замість дії; маршрут застосунку, не зовнішня адреса. */
  readonly href?: string | undefined;

  /** Недоступна дія лишається ВИДИМОЮ і поясненою — її ховає викликач,
   *  коли права немає взагалі. */
  readonly disabled?: boolean | undefined;
}

export interface PageHeaderProps {
  /** Назва екрана: вона ж отримує фокус, вона ж оголошується. */
  readonly title: string;

  /** Довільні дії праворуч — чинний проп, поведінка не змінилася. */
  readonly actions?: ReactNode;

  /** Значок стану поруч із назвою (`StatusBadge` тощо). */
  readonly badge?: ReactNode;

  /** Скільки об'єктів на екрані. */
  readonly count?: number | undefined;

  /** Рядок пояснення під назвою — людською мовою, не кодом. */
  readonly meta?: ReactNode;

  /** «← Back to …» для вкладеної сторінки. `label` — ВЕСЬ напис: слова
   *  «Back to» у каталозі немає, і вигадувати ключ тут не можна. */
  readonly back?: { readonly label: string; readonly href: string } | undefined;

  /** Головна дія екрана. Рівно одна (`L1`), і саме вона — `filled`. */
  readonly primary?: HeaderAction | undefined;

  /** Другорядні дії. Понад дві — усі йдуть у меню (`PageHeaderActions.tsx`). */
  readonly secondary?: readonly HeaderAction[] | undefined;

  /** Дії, яким місце лише в меню. */
  readonly more?: readonly HeaderAction[] | undefined;

  /** Підпис кнопки меню. Літерал за замовчуванням із тієї ж причини, що й
   *  `notificationCloseButtonProps` у `notify.ts`: ключа в каталозі
   *  (`09-seed.sql`) ще немає, а `t()` на неіснуючий ключ показав би читалці
   *  `⟦…⟧` замість імені кнопки. */
  readonly moreLabel?: string | undefined;
}

/**
 * Заголовок сторінки з місцем для дій праворуч.
 *
 * ⛔ Заголовок ще й **приймає фокус** при відкритті екрана (`ФВ-14.19`).
 * Користувач клавіатури після переходу опиняється «ніде»: фокус лишається на
 * пункті меню попереднього екрана, і `Tab` веде його по навігації заново —
 * тобто кожен перехід коштує йому десятка натискань.
 *
 * ⛔ Розширення (директива №15, §2, Шар 2) НЕ торкається ні фокуса, ні
 * оголошення: обидва ефекти лишилися дослівно тими самими, а весь новий вміст
 * малюється навколо того самого `<Title order={3} ref tabIndex={-1}>`. Тест
 * `pages/__tests__/ChangePasswordPage.pageHeader.test.tsx` (фокус, `tabindex`,
 * `aria-live`, РІВНО один заголовок) лишився без жодної правки — це й було
 * критерієм приймання.
 *
 * ⛔ `D15-06`: елемент без даних не малюється. Жодної обгортки «про запас» —
 * без `badge`/`count`/`meta`/`back`/дій розмітка дослівно та сама, що була до
 * розширення. Порожній `<div>` коштує не нічого: він з'їдає відступ і збиває
 * `justify="space-between"`.
 */
export function PageHeader({
  title,
  actions,
  badge,
  count,
  meta,
  back,
  primary,
  secondary,
  more,
  moreLabel = 'More',
}: PageHeaderProps): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef(false);

  useEffect(() => {
    // ⛔ Рівно ОДИН раз за монтування. Заголовок сторінки документа
    // уточнюється після завантаження даних, і фокус за кожною зміною назви
    // висмикував би курсор із поля, у якому користувач уже пише.
    if (focused.current) return;

    focused.current = true;
    heading.current?.focus();
  }, []);

  useEffect(() => {
    announceRoute(title);
  }, [title]);

  const headingNode = (
    /*
     * ⚠ `tabIndex={-1}`: заголовок приймає фокус програмно, але НЕ стає
     * зупинкою при обході табом. Інакше кожен екран додавав би користувачеві
     * зайве натискання на шляху до першого поля.
     */
    <Title order={3} ref={heading} tabIndex={-1} className={RouteHeadingClass}>
      {title}
    </Title>
  );

  const titleRow =
    shown(badge) || count !== undefined ? (
      <Group gap="xs" wrap="nowrap">
        {headingNode}
        {badge}
        {count !== undefined && (
          <Text size="sm" c="dimmed">
            {String(count)}
          </Text>
        )}
      </Group>
    ) : (
      headingNode
    );

  const left =
    back === undefined && !shown(meta) ? (
      titleRow
    ) : (
      <Stack gap="xs">
        {back !== undefined && (
          <Anchor component={Link} to={back.href} size="sm">
            {`← ${back.label}`}
          </Anchor>
        )}
        {titleRow}
        {shown(meta) && (
          <Text size="sm" c="dimmed">
            {meta}
          </Text>
        )}
      </Stack>
    );

  /*
   * ⛔ Перевірка саме ТУТ, а не всередині лінивого модуля: якби `import()`
   * стояв беззастережно, його замовляли б усі 24 маршрути і винесення не дало
   * б нічого. Сторінка, що нових дій не просить, іде рівно тим шляхом, що й до
   * розширення.
   */
  const wantsActions =
    primary !== undefined || (secondary?.length ?? 0) > 0 || (more?.length ?? 0) > 0;

  const right = wantsActions ? (
    <Suspense fallback={actions}>
      <PageHeaderActions
        actions={actions}
        primary={primary}
        secondary={secondary}
        more={more}
        moreLabel={moreLabel}
      />
    </Suspense>
  ) : (
    actions
  );

  return (
    <Group justify="space-between" mb="md">
      {left}
      {right}
    </Group>
  );
}

/**
 * Чи є в цьому вузлі що малювати (`D15-06`).
 *
 * ⚠ Перевірка не зводиться до `!== undefined`: `null`, `false` і порожній
 * рядок — звичайні значення умовного рендера у викликача
 * (`badge={isDraft && <StatusBadge …/>}`), і кожне з них дало б порожню
 * обгортку з відступом навколо нічого.
 */
function shown(node: ReactNode): boolean {
  return node !== undefined && node !== null && node !== false && node !== '';
}
