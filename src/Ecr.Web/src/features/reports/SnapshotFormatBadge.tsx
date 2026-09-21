import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import { toneFills, type StatusTone } from '@/shared/ui/StatusBadge';
import { t } from '@/shared/i18n';

/**
 * Формат чисел зрізу, як його віддає перелік (`ReportSnapshotSummary.hashFormat`).
 *
 * Значення — константи сервера `VerifyReportSnapshotHandler.FormatCurrent`/
 * `FormatLegacy`/`FormatUnknown` (`ReportSnapshotHandlers.cs`). У колонці
 * `rpt.ReportSnapshot.HashFormat` лежать лише `current`/`legacy` (перевірка
 * `CK_ReportSnapshot_HashFormat`), `NULL` перелік проєктує в `unknown`.
 */
export type SnapshotHashFormat = 'current' | 'legacy' | 'unknown';

/**
 * Рядок сервера → формат.
 *
 * ⚠ У `schema.d.ts` поле — необов'язковий `string`, а не union, тож компілятор
 * не скаже про нове значення. Відсутнє поле і незнайомий рядок — `unknown`:
 * формат справді невідомий, і сказати «новий» (не малювати нічого) означало б
 * твердити те, чого клієнт не знає.
 */
export function snapshotHashFormat(value: string | undefined): SnapshotHashFormat {
  return value === 'current' || value === 'legacy' ? value : 'unknown';
}

/**
 * Вигляд кожного формату. `null` — нічого не малюється.
 *
 * ⛔ `legacy` — `info`, не `warning` і не `danger`: старий зріз не має дефекту й
 * не вимагає дії (рішення замовника 2026-09-21 — подані зрізи не
 * перебудовуються). Позначка має пояснити різницю в кількості знаків, а не
 * послати шукати помилку.
 *
 * ⚠ `current` не малюється: позначка «звичайний формат» на кожному рядку —
 * шум, який привчив би не дивитися на цю колонку.
 */
const Looks: Readonly<
  Record<SnapshotHashFormat, { tone: StatusTone; label: string; hint: string } | null>
> = {
  current: null,
  legacy: { tone: 'info', label: 'snapshots.formatLegacy', hint: 'snapshots.formatLegacyHint' },
  unknown: {
    tone: 'neutral',
    label: 'snapshots.formatUnknown',
    hint: 'snapshots.formatUnknownHint',
  },
};

/**
 * Позначка формату зрізу поруч зі статусом.
 *
 * ⚠ Локальний бейдж, а не `StatusBadge`: формат — не стан зрізу, і в таблиці
 * набору для нього різновиду немає. Кольори — ті самі пари токенів набору
 * (`toneFills`), варіант `default` з тієї ж причини, що й у `StatusBadge`
 * (`light` без `color` бере фірмовий відтінок).
 *
 * ⚠ Підказка — `title`, той самий прийом, що й `snapshots.exportHint` на цій
 * сторінці. Підпис сам несе зміст («ранній формат»), підказка лише пояснює.
 */
export function SnapshotFormatBadge(props: { format: string | undefined }): JSX.Element | null {
  const format = snapshotHashFormat(props.format);
  const look = Looks[format];

  if (look === null) return null;

  const fill = toneFills[look.tone];

  return (
    <Badge
      size="sm"
      miw="fit-content"
      variant="default"
      bg={fill.bg}
      c={fill.text}
      title={t(look.hint)}
      data-hash-format={format}
      data-format-tone={look.tone}
    >
      {t(look.label)}
    </Badge>
  );
}
