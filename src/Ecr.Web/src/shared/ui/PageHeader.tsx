import { useEffect, useRef, type JSX, type ReactNode } from 'react';
import { Anchor, Button, Group, Menu, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { announceRoute } from './RouteAnnouncer';

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

/** Скільки `secondary`-кнопок лишається на видноті (директива №15, §2). */
const MaxInlineSecondary = 2;

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

  /** Другорядні дії. Понад `MaxInlineSecondary` — усі йдуть у меню. */
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

  /*
   * ⛔ Межа саме тут: ДВІ другорядні кнопки лишаються на видноті, ТРЕТЯ
   * забирає з видноти всі три. Не «третя їде в меню, а дві лишаються» —
   * інакше поруч стояли б два способи дістатися до сусідніх за змістом дій,
   * і користувач мусив би пам'ятати, які з них де.
   */
  const inline = secondary ?? [];
  const overflow = inline.length > MaxInlineSecondary;
  const menuItems = [...(overflow ? inline : []), ...(more ?? [])];

  const rightNodes: ReactNode[] = [];

  if (!overflow) {
    for (const action of inline) rightNodes.push(actionButton(action, 'default'));
  }

  if (primary !== undefined) {
    rightNodes.push(actionButton(primary, 'filled'));
  }

  if (menuItems.length > 0) {
    rightNodes.push(
      <Menu key="ecr-header-more" shadow="md" position="bottom-end" withinPortal>
        <Menu.Target>
          <Button variant="default">{moreLabel}</Button>
        </Menu.Target>

        <Menu.Dropdown>
          {menuItems.map((action) =>
            action.href === undefined ? (
              <Menu.Item
                key={action.label}
                disabled={action.disabled === true}
                onClick={action.onClick}
              >
                {action.label}
              </Menu.Item>
            ) : (
              <Menu.Item
                key={action.label}
                component={Link}
                to={action.href}
                disabled={action.disabled === true}
              >
                {action.label}
              </Menu.Item>
            ),
          )}
        </Menu.Dropdown>
      </Menu>,
    );
  }

  const headingNode = (
    /*
     * ⚠ `tabIndex={-1}`: заголовок приймає фокус програмно, але НЕ стає
     * зупинкою при обході табом. Інакше кожен екран додавав би користувачеві
     * зайве натискання на шляху до першого поля.
     */
    <Title order={3} ref={heading} tabIndex={-1}>
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

  const right =
    rightNodes.length === 0 ? (
      actions
    ) : (
      <Group gap="xs" wrap="nowrap">
        {actions}
        {rightNodes}
      </Group>
    );

  return (
    <Group justify="space-between" mb="md">
      {left}
      {right}
    </Group>
  );
}

/**
 * Кнопка дії.
 *
 * ⚠ `variant` заданий ЯВНО, а не лишений на дефолт Mantine: правило `L1`
 * (одна головна дія на екран) перевіряється тестом екрана по `data-variant`,
 * і атрибут з'являється лише тоді, коли проп переданий.
 *
 * ⚠ Фіксованої ширини немає (`ФВ-14.30`): підписи приходять трьома мовами.
 */
function actionButton(action: HeaderAction, variant: 'filled' | 'default'): JSX.Element {
  if (action.href !== undefined) {
    return (
      <Button
        key={action.label}
        component={Link}
        to={action.href}
        variant={variant}
        disabled={action.disabled === true}
      >
        {action.label}
      </Button>
    );
  }

  return (
    <Button
      key={action.label}
      variant={variant}
      onClick={action.onClick}
      disabled={action.disabled === true}
    >
      {action.label}
    </Button>
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
