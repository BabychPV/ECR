import { Fragment, type JSX, type ReactNode } from 'react';
import { Button, Group, Menu } from '@mantine/core';
import { t } from '@/shared/i18n';
import './documentToolbar.css';

/**
 * Рядок дій сторінки документа: щоденні дії — групами ліворуч, рідкісні й
 * небезпечні — у меню «More» праворуч.
 *
 * ⛔ Знімок людини (1290 px): усі дії стояли ОДНИМ пласким рядком разом із
 * полем періоду в шапці, і при нестачі місця flex переносив їх по одній —
 * «Delete document» опинявся сам на другому рядку під полем періоду, окремо
 * від решти. Найнебезпечніша дія документа там, де її ніхто не чекає.
 *
 * ⚠ Три рішення, кожне закриває свою частину того знімка:
 * - поле періоду лишилося в шапці поруч із заголовком, а дії — ОКРЕМИЙ рядок:
 *   висока колонка періоду (підпис + опис + поле) більше не задає висоту
 *   рядка кнопок;
 * - щоденні дії — `ActionGroup`-и з `wrap="nowrap"`: при нестачі місця
 *   переноситься ціла група, а не одна кнопка з середини;
 * - меню стоїть у ЗОВНІШНЬОМУ рядку з `wrap="nowrap"`, поза групами, що
 *   переносяться, — воно завжди праворуч у першому рядку й не може «випасти».
 */
export function DocumentToolbar({
  children,
  more,
}: {
  /** Групи щоденних дій (`ActionGroup`, `ExportButton`). */
  readonly children: ReactNode;
  /** Пункти меню «More»; `null` — дії немає (права/стан). */
  readonly more: readonly (JSX.Element | null)[];
}): JSX.Element {
  const items = more.filter((item): item is JSX.Element => item !== null);

  return (
    <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md" data-testid="document-toolbar">
      <Group
        gap="md"
        data-testid="document-actions"
        style={{ flex: 1, minWidth: 0, rowGap: 'var(--mantine-spacing-xs)' }}
      >
        {children}
      </Group>

      {/* ⛔ Меню без пунктів не малюється: оператор без права видалення й
          зміни ключа не мусить відкривати порожній список, щоб це дізнатися. */}
      {items.length > 0 && (
        <Menu shadow="md" position="bottom-end" withinPortal>
          <Menu.Target>
            <Button
              size="xs"
              variant="default"
              leftSection={<span aria-hidden="true">⋯</span>}
              data-testid="document-more"
              style={{ flexShrink: 0 }}
            >
              {t('document.moreActions')}
            </Button>
          </Menu.Target>

          <Menu.Dropdown>
            {/* ⚠ Порядок пунктів сталий (задає сторінка), тож індекс — чесний ключ. */}
            {items.map((item, index) => (
              <Fragment key={index}>{item}</Fragment>
            ))}
          </Menu.Dropdown>
        </Menu>
      )}
    </Group>
  );
}

/**
 * Група щоденних дій, що переноситься ЦІЛОЮ.
 *
 * ⚠ Порожня група (усі кнопки сховані правами чи станом) ховається CSS
 * (`:empty`, `documentToolbar.css`): порожній flex-елемент займав би проміжок,
 * а при переносі — окремий порожній рядок.
 */
export function ActionGroup({
  name,
  children,
}: {
  readonly name: string;
  readonly children: ReactNode;
}): JSX.Element {
  return (
    <Group gap="xs" wrap="nowrap" data-action-group={name} className="ecr-action-group">
      {children}
    </Group>
  );
}
