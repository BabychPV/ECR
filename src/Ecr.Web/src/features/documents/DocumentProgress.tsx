import type { JSX } from 'react';
import { Anchor, Group, Text } from '@mantine/core';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { formatCount } from '@/shared/format/plural';
import { t } from '@/shared/i18n';
import { SheetFillSummary } from './SheetFillSummary';

/** Аркуш документа для смужки станів. */
export interface ProgressSheet {
  readonly code: string;
  readonly name: string;
  readonly state: string;
}

/** Зауваження останньої перевірки; `null` — документ ще не перевіряли або прочитати не вдалося. */
export interface ProgressIssues {
  readonly errors: number;
  readonly total: number;
}

interface DocumentProgressProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly sheets: readonly ProgressSheet[];
  readonly activeCode: string;
  readonly issues: ProgressIssues | null;
  /** Перейти до панелі зауважень. */
  readonly onShowIssues: () => void;
}

/**
 * Прогрес документа одним рядком (UI-15; макет `docs/design/hybrid/screen-document.js`:
 * `renderState` — чип «Sheet: Draft», `E.SegmentBar` аркушів, `renderProgress` —
 * «68 of 91 tables filled · 7 issues»).
 *
 * ⛔ Число зауважень — лише коли перевірку запускали й її прочитано
 * (`issues === null` → нічого, не «0»): «не знаємо» і «порушень немає» —
 * різні відповіді (`A7-28`, `summarize().hasErrors === null`).
 *
 * ⚠ Колір — лише у проблеми (KIT §1 п.3): посилання червоне, коли є помилки,
 * жовте — лише попередження; «No issues» — нейтральне.
 */
export function DocumentProgress({
  documentId,
  periodKey,
  sheets,
  activeCode,
  issues,
  onShowIssues,
}: DocumentProgressProps): JSX.Element {
  const active = sheets.find((sheet) => sheet.code === activeCode);

  return (
    <Group gap="sm" wrap="wrap" data-testid="document-progress" style={{ rowGap: 'var(--mantine-spacing-xs)' }}>
      {active !== undefined && (
        <Group gap="xs" wrap="nowrap">
          <Text size="xs" c="dimmed">
            {t('document.sheetStateLabel')}
          </Text>
          <StatusBadge kind="sheet" state={active.state} />
        </Group>
      )}

      {/* TODO(UI-15): `SegmentBar` аркушів — зі `shared/ui/SegmentBar` лінії групи 3
          (UI-19), щойно її коміт буде в `dev/integration`; власної копії тут не
          робимо (один компонент на дві лінії). */}

      <SheetFillSummary documentId={documentId} periodKey={periodKey} />

      {issues !== null &&
        (issues.total === 0 ? (
          <Text size="xs" c="dimmed" data-testid="document-issues-link">
            {t('document.noIssues')}
          </Text>
        ) : (
          <Anchor
            component="button"
            type="button"
            size="xs"
            fw={500}
            c={issues.errors > 0 ? 'statusError' : 'statusWarning'}
            onClick={onShowIssues}
            data-testid="document-issues-link"
          >
            {formatCount(issues.total, 'document.issuesCount')}
          </Anchor>
        ))}
    </Group>
  );
}
