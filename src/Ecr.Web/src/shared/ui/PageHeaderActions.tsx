import type { JSX, ReactNode } from 'react';
import { Button, Group, Menu } from '@mantine/core';
import { Link } from 'react-router-dom';
import type { HeaderAction } from './PageHeader';

/**
 * Кластер дій у шапці сторінки — **окремий модуль рівно заради бюджету**
 * (`D-132`: 250 КБ gzip на маршрут), а не заради декомпозиції.
 *
 * ⛔ Чому саме він. `PageHeader` стоїть на КОЖНІЙ сторінці, тож усе, що
 * лежить у ньому статично, лягає в усі 24 маршрутні чанки. Розширення шапки
 * додало туди +0.6 КБ gzip (виміряно: `TemplateVersionPage` 249.8 → 250.4 без
 * решти змін), і два маршрути, що стояли за пів кілобайта від межі,
 * перевалили за неї. Кластер дій — найбільша частина цього приросту і єдина,
 * яку можна винести БЕЗ наслідків для доступності.
 *
 * ⛔ Лівий блок (назва, `badge`, `count`, `meta`, `back`) винести не можна, і
 * це не забудькуватість. У ньому живе `<Title ref>`, який приймає фокус
 * (`ФВ-14.19`). Лінивий лівий блок дав би один із двох поганих кінців:
 * `fallback` із копією заголовка → заголовок ПЕРЕМОНТОВУЄТЬСЯ, коли модуль
 * приїде, `ref` переїжджає на новий вузол, а ефект фокуса вже відпрацював —
 * фокус утрачено назавжди; `fallback={null}` → шапка порожня до приїзду
 * чанка, тобто оголошення маршруту читалці спізнюється на цілий мережевий
 * обмін. Обидва — регрес того, що цей PR зобов'язаний був не зачепити.
 *
 * ⚠ Тут же — чому `fallback` цього `Suspense` саме `actions`, а не `null`:
 * 24 чинні сторінки передають дії САМЕ цим пропом, і вони мусять лишитися на
 * екрані незмінно. Нові ж кнопки з'являються на кадр пізніше — та сама
 * оборудка, що вже прийнята для `DocumentGrid` (`lazy()`, коментар
 * `scripts/check-bundle-budget.mjs`).
 *
 * ⚠ `import type { HeaderAction }` назад у `PageHeader` циклу НЕ створює:
 * під `verbatimModuleSyntax` імпорт типу стирається компілятором цілком.
 */

/** Скільки `secondary`-кнопок лишається на видноті (директива №15, §2). */
const MaxInlineSecondary = 2;

export default function PageHeaderActions({
  actions,
  primary,
  secondary,
  more,
  moreLabel,
}: {
  readonly actions?: ReactNode;
  readonly primary?: HeaderAction | undefined;
  readonly secondary?: readonly HeaderAction[] | undefined;
  readonly more?: readonly HeaderAction[] | undefined;
  readonly moreLabel: string;
}): JSX.Element {
  /*
   * ⛔ Межа саме тут: ДВІ другорядні кнопки лишаються на видноті, ТРЕТЯ
   * забирає з видноти всі три. Не «третя їде в меню, а дві лишаються» —
   * інакше поруч стояли б два способи дістатися до сусідніх за змістом дій,
   * і користувач мусив би пам'ятати, які з них де.
   */
  const inline = secondary ?? [];
  const overflow = inline.length > MaxInlineSecondary;
  const menuItems = [...(overflow ? inline : []), ...(more ?? [])];

  return (
    <Group gap="xs" wrap="nowrap">
      {actions}

      {!overflow && inline.map((action) => actionButton(action, 'default'))}

      {primary !== undefined && actionButton(primary, 'filled')}

      {menuItems.length > 0 && (
        <Menu shadow="md" position="bottom-end" withinPortal>
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
        </Menu>
      )}
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
