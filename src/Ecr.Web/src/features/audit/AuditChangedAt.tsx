import type { JSX } from 'react';
import { formatDateTime24 } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { zoneClock } from '@/shared/siteMoment';
import { zoneCityName } from './zoneCityName';

/**
 * Підказка до часу запису: точне значення ISO, а коли пояс майданчика відомий — ще й час майданчика
 * («Site time (Atyrau): Nov 24, 2026, 21:12»). Пояс не відомий або рядок не розібрався — лише ISO.
 */
export function auditTimeTitle(value: string, zone: string | null | undefined): string {
  if (zone === null || zone === undefined || zone === '') return value;

  // ⚠ Пояс приходить із СЕРВЕРА: невідомий `Intl` пояс → UTC (`zoneClock`), а не `RangeError` посеред рендера.
  const formattable = zoneClock(zone).resolvedOptions().timeZone;
  const site = formatDateTime24(value, formattable);
  if (site === '') return value;

  return `${value}\n${t('audit.siteTime', { zone: zoneCityName(zone), time: site })}`;
}

/**
 * Час запису журналу аудиту — у поясі ЧИТАЧА, одним рядком, 24-годинним записом (✎ P-1124-4, слово людини 24.11:
 * «показуй у місцевому часі»).
 *
 * ⛔ Що було (debt-1110, fd11fb13b): журнали безпеки й «Reopen» друкували час у поясі МАЙДАНЧИКА без жодної позначки
 * — дія людини о 18:12 за Києвом стояла як «9:12 PM», а дата переносилася на три рядки. Тепер видно місцевий час, а
 * пояс майданчика лишився в підказці (`title`), поруч із точним ISO: журнал — доказ, і нічого не губиться.
 *
 * ⚠ Лише журнали аудиту. Строки, межі періодів і дати блокування й далі в поясі майданчика (`siteMoment`).
 */
export function AuditChangedAt({ value, zone }: { value: string; zone?: string | null | undefined }): JSX.Element {
  const shown = formatDateTime24(value);

  // Нерозібраний рядок — як є (той самий принцип, що в `Timestamp`): зіпсоване не ховається за тире.
  if (shown === '') {
    return (
      <span data-timestamp="unparsed" data-audit-time="">
        {value}
      </span>
    );
  }

  return (
    <time
      dateTime={value}
      title={auditTimeTitle(value, zone)}
      data-timestamp="ok"
      data-audit-time=""
      data-site-zone={zone ?? undefined}
    >
      {shown}
    </time>
  );
}
