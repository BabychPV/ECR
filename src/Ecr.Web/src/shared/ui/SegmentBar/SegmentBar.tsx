import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { statusKey, statusTone, type StatusKind } from '@/shared/ui/StatusBadge';
import './segmentBar.css';

/** Сегмент смужки — один аркуш документа (або інша одиниця з власним станом). */
export interface Segment {
  /** Стабільний ключ (код аркуша). */
  readonly code: string;

  /** Людська назва — у підказці й у тексті для читалки. */
  readonly label: string;

  /** Стан, як його назвав сервер. */
  readonly state: string;
}

export interface SegmentBarProps {
  readonly segments: readonly Segment[];

  /** Словник станів; за замовчуванням — стан аркуша (`D-93`). */
  readonly kind?: StatusKind | undefined;

  /**
   * Скільки одиниць усього, якщо відомо більше, ніж прийшло сегментів
   * (`DocumentSummary.sheetCount`). За замовчуванням — кількість сегментів.
   */
  readonly total?: number | undefined;

  /** Стан, що вважається «готово» для підпису «N of M approved». */
  readonly doneState?: string | undefined;

  /** `false` — без підпису поруч (підпис стоїть деінде). */
  readonly summary?: boolean | undefined;

  /** `lg` — ширші сегменти (шапка документа). */
  readonly size?: 'sm' | 'lg' | undefined;
}

/**
 * Смужка станів аркушів документа (`KIT.md` §6.7 `SegmentBar`, з макета B
 * «Control Room»): один сегмент — один аркуш, поруч «N of M approved».
 *
 * ⛔ Колір не єдиний носій (`ФВ-14.18`): у кожного сегмента своя ФОРМА за
 * станом (`segmentBar.css`), підказка «назва — стан» і повний перелік для
 * читалки в `aria-label` смужки (`role="img"`). Підпис стану — той самий рядок
 * `status.<kind>.<state>`, що й у `StatusBadge`: одне слово на весь екран.
 *
 * ⚠ Тон сегмента — з тієї самої таблиці, що й тон бейджа (`statusTone`), а не
 * власна мапа кольорів: смужка й бейдж не можуть розійтися.
 */
export function SegmentBar({
  segments,
  kind = 'sheet',
  total,
  doneState = 'Approved',
  summary = true,
  size = 'sm',
}: SegmentBarProps): JSX.Element {
  const named = segments.map((segment) => ({
    ...segment,
    stateLabel: t(statusKey(kind, segment.state)),
    // Невідомий стан — `UnknownStateTone` («увага»): той самий, що в бейджа.
    tone: statusTone(kind, segment.state),
  }));
  const done = segments.filter((segment) => segment.state === doneState).length;
  const all = Math.max(total ?? segments.length, segments.length);

  return (
    <span className="ecr-segbar" data-size={size} data-segment-bar="">
      <span
        className="ecr-segbar__segs"
        role="img"
        aria-label={named.map((segment) => `${segment.label}: ${segment.stateLabel}`).join(', ')}
      >
        {named.map((segment) => (
          <i
            key={segment.code}
            className="ecr-segbar__sg"
            title={`${segment.label} — ${segment.stateLabel}`}
            data-segment={segment.code}
            data-state={segment.state}
            data-tone={segment.tone}
          />
        ))}
      </span>
      {summary && (
        <span className="ecr-segbar__label" data-segment-summary={`${String(done)}/${String(all)}`}>
          {t('segments.approvedOf', { done, total: all })}
        </span>
      )}
    </span>
  );
}
