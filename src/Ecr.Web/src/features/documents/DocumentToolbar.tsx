import { Fragment, type JSX, type ReactNode } from 'react';
import { Button, Group, Menu } from '@mantine/core';
import { t } from '@/shared/i18n';
import './documentToolbar.css';

/**
 * Рядок дій сторінки документа (UI-14; макет `docs/design/hybrid/screen-document.js`,
 * `renderActions` + `renderSave`; KIT §1 п.2 «одна головна дія, ≤ 2 другорядні,
 * решта — у меню More»).
 *
 * Ліворуч — стан (чип аркуша, прогрес, стан збереження словами); праворуч — дії
 * в порядку макета: другорядні → «More» → головна.
 *
 * ⛔ Знімок людини (1290 px): усі дії стояли ОДНИМ пласким рядком разом із
 * полем періоду в шапці, і при нестачі місця flex переносив їх по одній —
 * «Delete document» опинявся сам на другому рядку під полем періоду, окремо
 * від решти. Звідси два правила, що лишаються:
 * - поле періоду — у шапці поруч із заголовком, а дії — ОКРЕМИЙ рядок;
 * - дії праворуч — ОДНА група з `wrap="nowrap"`: при нестачі місця переноситься
 *   вся група цілою (під стан ліворуч), а не одна кнопка з середини. Їх тепер
 *   щонайбільше чотири (дві другорядні, «More», головна), тож група вміщається.
 */
export function DocumentToolbar({
  status,
  children,
  more,
  primary,
}: {
  /** Стан документа й аркуша ліворуч (чип, прогрес, збереження). */
  readonly status?: ReactNode;
  /** Другорядні дії (≤ 2) перед меню. */
  readonly children?: ReactNode;
  /** Пункти меню «More»; `null` — дії немає (права/стан). */
  readonly more: readonly (JSX.Element | null)[];
  /** Головна дія екрана (одна `primary`, `L1`). */
  readonly primary?: ReactNode;
}): JSX.Element {
  const items = more.filter((item): item is JSX.Element => item !== null);

  return (
    <Group
      justify="space-between"
      align="center"
      gap="md"
      data-testid="document-toolbar"
      style={{ rowGap: 'var(--mantine-spacing-xs)' }}
    >
      <Group gap="md" data-testid="document-status" style={{ minWidth: 0, rowGap: 'var(--mantine-spacing-xs)' }}>
        {status}
      </Group>

      <Group gap="xs" wrap="nowrap" data-testid="document-actions" ml="auto">
        {children}

        {/* ⛔ Меню без пунктів не малюється: оператор без жодної рідкісної дії
            не мусить відкривати порожній список, щоб це дізнатися. */}
        {items.length > 0 && (
          <Menu shadow="md" position="bottom-end" withinPortal>
            <Menu.Target>
              <Button
                variant="default"
                rightSection={<span aria-hidden="true">▾</span>}
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

        {primary}
      </Group>
    </Group>
  );
}

/**
 * Група елементів, що переноситься ЦІЛОЮ.
 *
 * ⚠ Порожня група (усе сховано правами чи станом) ховається CSS
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
