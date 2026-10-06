import type { JSX } from 'react';
import type { TemplateVersionSummary } from '@/api/types';
import { formatTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import './ctor.css';

type TemplateVersionStatus = TemplateVersionSummary['status'];

const Stages: readonly TemplateVersionStatus[] = ['Draft', 'Published', 'Deprecated'];

/**
 * Контекстний рядок шапки конструктора (`UI-36`, макет `editor-head` → `eh-ctx`):
 * стан версії, `Stepper` Draft → Published → Deprecated і стан збереження словами.
 *
 * ⚠ «Saved HH:MM» — час останнього УСПІШНОГО запису в цьому сеансі, а не
 * вигаданий: автозбереження в конструкторі немає (кожна форма пишеться своєю
 * кнопкою), а часу останньої зміни версії сервер у переліку не віддає. До
 * першого запису рядка немає зовсім.
 */
export function VersionStateLine({
  status,
  presentationRevision,
  savedAt,
}: {
  readonly status: TemplateVersionStatus | undefined;
  readonly presentationRevision: number | undefined;
  readonly savedAt: Date | null;
}): JSX.Element | null {
  if (status === undefined && presentationRevision === undefined && savedAt === null) return null;
  const current = status === undefined ? -1 : Stages.indexOf(status);

  return (
    <div className="ecr-ctor-ctx" data-testid="ctor-state">
      {status !== undefined && (
        <>
          <StatusBadge kind="version" state={status} />
          <span
            className="ecr-ctor-stepper"
            role="img"
            aria-label={t('ctor.stepper', {
              state: t(statusKey('version', status)),
              no: current + 1,
              total: Stages.length,
            })}
          >
            {Stages.map((stage, index) => (
              <i key={stage} data-done={index <= current} />
            ))}
          </span>
        </>
      )}
      {/* ⚠ Лічильник правок презентаційного шару (кнопка «Appearance» на колонці:
          підпис, порядок, формат, видимість — `ФВ-7.2`). Раніше тут стояло голе `r0`. */}
      {presentationRevision !== undefined && (
        <span data-presentation-revision>{t('version.presentationRevision', { revision: presentationRevision })}</span>
      )}
      {savedAt !== null && (
        <span role="status" data-testid="ctor-saved">
          {t('ctor.saved', { time: formatTime(savedAt, { hour: '2-digit', minute: '2-digit' }) })}
        </span>
      )}
    </div>
  );
}
