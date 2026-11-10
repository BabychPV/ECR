import type { JSX } from 'react';
import { Timestamp } from '@/shared/ui/Timestamp';
import { siteMomentText } from '@/shared/siteMoment';

/**
 * Час запису журналу в поясі МАЙДАНЧИКА, коли пояс відомий; інакше — звичайний `Timestamp` (пояс читача). Точне
 * значення — у `dateTime`/`title`.
 *
 * ✎ debt-1109 F13 (P3 A): журнал «Reopen» (пояс — із конверта причини). ✎ debt-1110: журнал безпеки (пояс — з рядка
 * сервера, `siteTimeZoneId`).
 * ⚠ Тут, а не пропом `Timestamp`: той у спільному чанку всіх маршрутів, а бюджет `DocumentPage` — на межі.
 */
export function SiteChangedAt({ value, zone }: { value: string; zone: string | null | undefined }): JSX.Element {
  const inSite = zone === null || zone === undefined || zone === '' ? null : siteMomentText(value, zone);
  if (inSite === null) return <Timestamp value={value} />;

  return (
    <time dateTime={value} title={value} data-timestamp="ok">
      {inSite}
    </time>
  );
}
