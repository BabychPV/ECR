import { memo, type JSX, type ReactNode } from 'react';
import { Badge, Box, Group, Table, Text, VisuallyHidden } from '@mantine/core';
import type { CellChangePage } from '@/api/types';
import { authorName } from '@/features/audit/authorOptions';
import { decimalDelta } from '@/features/audit/changeDelta';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { formatDate, formatDecimal, formatPeriodKey } from '@/shared/format';

/**
 * Таблиця журналу змін комірок у формі макета (`UI-38`, `docs/design/hybrid/screens-ops.js`
 * `/admin/audit`, вкладка «Data changes»): WHEN · WHO · DOCUMENT · CELL · WAS → BECOMES · ORIGIN · LATE.
 *
 * ⚠ Журнал читається як історія, а не як дамп: старе значення закреслене, нове — поруч за стрілкою,
 * для числа — Δ зі знаком (`screen-document.js`, колонка «Δ»); перше значення комірки позначене «new».
 * Джерело — піктограмою І словом (піктограма сама не читається ні оком новачка, ні читалкою).
 *
 * ⛔ Закреслення читалка не озвучує, тож «Was»/«Becomes» стоять у прихованому для ока тексті:
 * інакше «12 15» у комірці не відрізнити від «15 12».
 */

export type CellChange = CellChangePage['items'][number];

/** Типи колонок, чиє значення — число (`U-05`: одне правило подачі числа). */
const NumericTypes: readonly string[] = ['Decimal', 'Int', 'Formula', 'Calculated'];

/**
 * Значення журналу — за правилом показу, а не у форматі сховища (`X-35`).
 *
 * ⛔ Журнал показував «Was 53.1771000000000000»: `aud.CellChange` зберігає
 * число з повним масштабом колонки сховища (`decimal(34,16)`, `D-148`).
 * Людина набирала `53.1771` і шукає в журналі саме його. Хвостові нулі
 * прибирає той самий `formatDecimal`, що й сітка (`U-05`/`U-24`), розряди —
 * мовою інтерфейсу.
 *
 * ⚠ Нерозібране значення показується ЯК Є: журнал — доказ, і сховати дивне
 * значення за «—» означало б сховати саму розбіжність.
 */
export function auditValueText(value: string | null | undefined, dataType: string | null | undefined): string {
  if (value === null || value === undefined) return '—';
  if (dataType === null || dataType === undefined) return value;

  if (NumericTypes.includes(dataType)) return formatDecimal(value) ?? value;

  if (dataType === 'Date') {
    const day = /^\d{4}-\d{2}-\d{2}/.exec(value)?.[0];
    const shown = day === undefined ? '' : formatDate(day);

    return shown.length > 0 ? shown : value;
  }

  return value;
}

/**
 * Δ для показу: зі знаком «+» для зростання, мовою інтерфейсу; `null` — не число або немає «було».
 * Нульова різниця теж показується («0»): переписане те саме значення — факт, який журнал і фіксує.
 */
export function auditDeltaText(change: Pick<CellChange, 'oldValue' | 'newValue' | 'columnDataType'>): string | null {
  const type = change.columnDataType;
  if (type === null || type === undefined || !NumericTypes.includes(type)) return null;

  const delta = decimalDelta(change.oldValue, change.newValue);
  if (delta === null) return null;

  const shown = formatDecimal(delta) ?? delta;

  return delta.startsWith('-') || delta === '0' ? shown : `+${shown}`;
}

/** Документ: людська назва → бізнес-ключ → номер (документа вже немає). */
function documentText(change: CellChange): string {
  const name = localized(change.documentNameL10n);

  if (name.length > 0) return name;
  if (change.documentBusinessKey !== null && change.documentBusinessKey !== undefined) {
    return change.documentBusinessKey;
  }

  return t('audit.documentGone', { id: change.documentId });
}

/** Колонка: заголовок мовою інтерфейсу, код — поруч; без запису — номер. */
function columnText(change: CellChange): string {
  const header = localized(change.columnHeaderL10n);
  const code = change.columnCode ?? null;

  if (header.length > 0) return code === null ? header : `${header} (${code})`;

  return code ?? t('audit.columnGone', { id: change.columnDefId });
}

/** Походження зміни: піктограма + слово макета (`ORIGIN_LABEL`/`ORIGIN_ICON`). */
const OriginIcons: Readonly<Record<string, string>> = {
  UserEdit: 'M4 20l4-1L19 8l-3-3L5 16z',
  Import: 'M12 16V4M7 9l5-5 5 5M4 20h16',
  Recalculation: 'M18 7V5H6l7 7-7 7h12v-2',
  Migration:
    'M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3zM4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3',
};

/** Назва походження мовою інтерфейсу; невідоме серверу значення показується як є (журнал — доказ). */
export function originLabel(origin: string): string {
  return origin in OriginIcons ? t(`audit.originLabel.${origin}`) : origin;
}

const ClockPath = 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3 2';
const ArrowPath = 'M5 12h14M13 6l6 6-6 6';

function Glyph({ d, size = 12 }: { readonly d: string; readonly size?: number }): JSX.Element {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={d} />
    </svg>
  );
}

/** «Було → стало · Δ» (`ops-arrow` макета: моноширинно, старе закреслене й приглушене). */
function WasBecomes({ change }: { readonly change: CellChange }): JSX.Element {
  const first = change.oldValue === null || change.oldValue === undefined;
  const delta = auditDeltaText(change);

  return (
    <Group gap="xs" wrap="nowrap" ff="monospace" fz="sm" data-audit-was-becomes="">
      <VisuallyHidden>{t('import.was')}</VisuallyHidden>
      <Text
        span
        inherit
        c={first ? 'var(--ecr-faint)' : 'var(--ecr-muted)'}
        td={first ? undefined : 'line-through'}
        data-audit-was=""
      >
        {auditValueText(change.oldValue, change.columnDataType)}
      </Text>
      <Box component="span" c="var(--ecr-faint)" display="inline-flex">
        <Glyph d={ArrowPath} />
      </Box>
      <VisuallyHidden>{t('import.becomes')}</VisuallyHidden>
      <Text span inherit fw={500} data-audit-becomes="">
        {auditValueText(change.newValue, change.columnDataType)}
      </Text>
      {first ? (
        <Badge size="xs" variant="light" color="brand" data-audit-new="">
          {t('audit.newValue')}
        </Badge>
      ) : (
        delta !== null && (
          <Text span inherit fz="xs" c="var(--ecr-muted)" title={t('audit.delta')} data-audit-delta="">
            <VisuallyHidden>{t('audit.delta')}</VisuallyHidden>
            {delta}
          </Text>
        )
      )}
    </Group>
  );
}

function Origin({ origin }: { readonly origin: string }): JSX.Element {
  const icon = OriginIcons[origin];

  return (
    <Group gap="xs" wrap="nowrap" c="var(--ecr-muted)" fz="xs" title={origin} data-audit-origin={origin}>
      {icon !== undefined && <Glyph d={icon} />}
      <span>{originLabel(origin)}</span>
    </Group>
  );
}

function Late({ change }: { readonly change: CellChange }): ReactNode {
  return (
    <Group gap="xs" wrap="nowrap">
      {/* ⚠ Пізня правка — у пільговому строку після кінця періоду (`D-70`); пояснювати доводиться саме її. */}
      {change.isLateEdit && (
        <Group gap="xs" wrap="nowrap" c="var(--ecr-warning)" fz="sm" fw={500} title={t('audit.lateHint')} data-audit-late="">
          <Glyph d={ClockPath} />
          <span>{t('audit.lateMark')}</span>
        </Group>
      )}
      {/* ФВ-2.16 / D-239: правка за політикою Warn поза вікном доступу. */}
      {change.isOutOfWindow && (
        <Badge size="xs" color="statusWarning" variant="outline">
          {t('audit.outOfWindow')}
        </Badge>
      )}
    </Group>
  );
}

/**
 * ⛔ Окремий `memo`-компонент: набір у полях фільтра перемальовує сторінку на кожну клавішу, і 100
 * рядків таблиці разом із нею давали затримку друку до 117 мс (`R-18`). Сторінка відповіді від React
 * Query стабільна за посиланням — таблиця малюється лише тоді, коли приходять нові дані.
 */
export const CellChangesTable = memo(function CellChangesTable({
  items,
}: {
  readonly items: readonly CellChange[];
}): JSX.Element {
  return (
    <Table striped className="ecr-sticky-head" aria-label={t('audit.title')}>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t('audit.when')}</Table.Th>
          <Table.Th>{t('audit.who')}</Table.Th>
          <Table.Th>{t('audit.documentCell')}</Table.Th>
          <Table.Th>{t('audit.wasBecomes')}</Table.Th>
          <Table.Th>{t('audit.origin')}</Table.Th>
          <Table.Th>{t('audit.lateMark')}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {items.map((change, index) => (
          <Table.Tr key={`${change.documentId}:${change.rowKey}:${change.columnDefId}:${index}`}>
            <Table.Td>
              {/* ⛔ Саме `Timestamp`, а не сирий рядок: точне значення лишається в `dateTime`/`title`. */}
              <Text span ff="monospace" fz="sm">
                <Timestamp value={change.changedAt} />
              </Text>
            </Table.Td>
            {/* ⛔ `R-18`: імена з сервера, а не «user 3». Номер — підказкою: фільтри стоять на ньому. */}
            <Table.Td title={`#${String(change.changedByUserId)}`}>{authorName(change)}</Table.Td>
            <Table.Td>
              {/* Макет: верхній рядок — комірка, нижній — документ (там «· sheet › table»: серверу
                  бракує назв аркуша/таблиці, TODO-контракт BE-16 у листі UI-38). */}
              <TwoLine
                title={`#${String(change.documentId)} · #${String(change.columnDefId)}`}
                primary={`${change.rowKey} · ${columnText(change)}`}
                secondary={
                  <>
                    <span>{documentText(change)}</span>
                    {' · '}
                    <span title={String(change.periodKey)}>{formatPeriodKey(change.periodKey) || String(change.periodKey)}</span>
                  </>
                }
              />
            </Table.Td>
            <Table.Td>
              <WasBecomes change={change} />
            </Table.Td>
            <Table.Td>
              <Origin origin={change.origin} />
            </Table.Td>
            <Table.Td>
              <Late change={change} />
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
});
