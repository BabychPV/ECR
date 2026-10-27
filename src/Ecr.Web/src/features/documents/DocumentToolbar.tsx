import { Fragment, type JSX, type ReactNode } from 'react';
import { Button, Group, Menu, Skeleton, VisuallyHidden } from '@mantine/core';
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
  pending = false,
}: {
  /** Стан документа й аркуша ліворуч (чип, прогрес, збереження). */
  readonly status?: ReactNode;
  /** Другорядні дії (≤ 2) перед меню. */
  readonly children?: ReactNode;
  /** Пункти меню «More»; `null` — дії немає (права/стан). */
  readonly more: readonly (JSX.Element | null)[];
  /** Головна дія екрана (одна `primary`, `L1`). */
  readonly primary?: ReactNode;
  /**
   * ✎ D-441 CLS: дії ще не в остаточній формі (сервер не сказав, чи й який період подавати) — замість них скелет.
   * Див. `ActionsPlaceholder`.
   */
  readonly pending?: boolean;
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

      {/* ⚠ Контейнер дій — той самий вузол і в скелеті, і в остаточній формі: замінюється лише вміст. */}
      <Group gap="xs" wrap="nowrap" data-testid="document-actions" ml="auto" className="ecr-doc-actions">
        {pending ? <ActionsPlaceholder /> : <ActionsContent items={items} primary={primary}>{children}</ActionsContent>}
      </Group>
    </Group>
  );
}

/**
 * Скелет рядка дій, доки його остаточна форма невідома (✎ D-441 CLS).
 *
 * ⛔ Чому скелет ЗАМІСТЬ дій, а не лише на місці «Подати». Головна дія стоїть праворуч від «More», а група дій
 * притиснута до правого краю: будь-що, що з'являється праворуч від «More», зсуває її ліворуч, а ширше за рядок —
 * переносить усю групу вниз. Ширину остаточної «Подати» наперед не знати: вона називає період, який вибрав
 * сервер («Submit: September 2026» ≠ «Submit: May 2026»), а поруч бувають ▾ і «Next to submit: …» (D-441). Тож
 * заглушка «Подати» сталої ширини лише зменшила б стрибок, а не прибрала б. Пізня «Подати» зсувала «More» на
 * ~200 px між натиском і відпуском миші, і клік губився (e2e-stand 39994116932, 40023308269). Тепер «More» (як і
 * решта дій) з'являється одразу на остаточному місці й більше не рухається; скелет займає приблизно те саме
 * місце, тож рядок під ним не стрибає.
 *
 * ⚠ Не кнопки: жодної дії, яку можна натиснути «наосліп» до відповіді (вона подала б не той період). Для
 * читалки — прихований напис «завантаження» в `role="status"`, як в `AsyncBoundary` (`ФВ-14.25`): порожні
 * прямокутники самі собою не чутні. Форма — «Validate · More · Submit» (макет UI-14, чернетка).
 */
function ActionsPlaceholder(): JSX.Element {
  return (
    <Group gap="xs" wrap="nowrap" role="status" aria-busy="true" data-testid="document-actions-pending">
      <VisuallyHidden>{t('common.loading')}</VisuallyHidden>
      <Skeleton height={36} width={88} radius="sm" />
      <Skeleton height={36} width={96} radius="sm" />
      <Skeleton height={36} width={176} radius="sm" data-testid="document-submit-placeholder" />
    </Group>
  );
}

/** Остаточні дії: другорядні → «More» → головна. */
function ActionsContent({
  children,
  items,
  primary,
}: {
  readonly children?: ReactNode;
  readonly items: readonly JSX.Element[];
  readonly primary?: ReactNode;
}): JSX.Element {
  return (
    <>
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
    </>
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
