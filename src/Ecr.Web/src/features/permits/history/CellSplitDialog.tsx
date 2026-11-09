import { useState, type JSX } from 'react';
import { Alert, Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { decimalTextOf } from '@/features/grid/rounding';
import { formatDecimal } from '@/shared/format/number';
import { formatMonthYear } from '@/shared/format/period';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import {
  dayCount,
  dayMonth,
  dottedDate,
  initialParts,
  monthOf,
  totalOfParts,
  type SplitCellMark,
} from './splitModel';
import type { SplitTarget } from './splitRequest';
import { useSaveCellSplits } from './usePermitHistory';

export interface CellSplitDialogProps {
  readonly target: SplitTarget;
  /** The split cell; `null` — the history has no split for it (nothing to split, or still loading). */
  readonly mark: SplitCellMark | null;
  readonly historyPending: boolean;
  readonly rowLabel: string;
  readonly unit: string | null;
  /** The row version the grid holds (as `PATCH …/cells`); `null` — unknown, the server checks nothing then. */
  readonly baseVersion: string | null;
  readonly onClose: () => void;
}

const PartLetters = 'ABCDEFGH';

function letterOf(partNo: number): string {
  return PartLetters[partNo - 1] ?? String(partNo);
}

function shown(value: string): string {
  return formatDecimal(value) ?? value;
}

/**
 * Feature (j) j2dialog: «Split February 2026 — Sea Water Intake».
 *
 * One line says why there are parts (the permit changed on …); one input per permit with its days and unit; part A
 * starts with the old single value when the month was not split yet; the total A + B updates live. ⛔ ECR never divides
 * the value by days — nothing here computes a part. Save sends the parts (a part may stay empty: partial save).
 */
export function CellSplitDialog({ target, mark, historyPending, rowLabel, unit, baseVersion, onClose }: CellSplitDialogProps): JSX.Element {
  const save = useSaveCellSplits(target.documentId);
  const saveLoading = usePendingLoading(save.isPending);
  const [texts, setTexts] = useState<readonly string[] | null>(null);

  const parts = mark?.month.parts ?? [];
  const initial = mark === null ? [] : initialParts(mark);
  const values = texts ?? initial.map((value) => (value === null ? '' : value));
  const parsed = values.map((text) => (text.trim().length === 0 ? null : decimalTextOf(text)));
  const invalid = values.map((text, index) => text.trim().length > 0 && parsed[index] === null);
  const total = totalOfParts(parsed);

  const { year, month } = mark === null ? { year: 0, month: 0 } : monthOf(mark.month.month);
  const monthLabel = formatMonthYear(year, month);
  const changedOn = parts[1]?.dayFrom;
  const wasSingle = mark !== null && !mark.isSplit && (mark.cell?.parts ?? []).every((part) => part === null) ? initial[0] : null;

  const submit = (): void => {
    if (mark === null || invalid.some(Boolean)) return;
    save.mutate(
      {
        periodKey: target.periodKey,
        items: [
          {
            tableInstanceId: target.tableInstanceId,
            rowKey: target.rowKey,
            columnCode: target.columnCode,
            baseVersion: baseVersion ?? '',
            parts: parsed,
          },
        ],
      },
      { onSuccess: onClose },
    );
  };

  return (
    <Modal
      opened
      onClose={onClose}
      title={mark === null ? t('permits.split.titleEmpty') : t('permits.split.title', { month: monthLabel, row: rowLabel })}
      data-split-dialog=""
    >
      {mark === null ? (
        <Text size="sm" role="status">
          {historyPending ? t('permits.split.loading') : t('permits.split.nothing')}
        </Text>
      ) : (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <Stack gap="sm">
            {changedOn !== undefined && (
              <Alert color="blue" variant="light" data-split-why="">
                {t('permits.split.why', { date: dottedDate(changedOn), month: monthLabel })}
              </Alert>
            )}
            {parts.map((part, index) => (
              <TextInput
                key={part.partNo}
                label={`${t('permits.split.part', { part: letterOf(part.partNo) })} · ${part.permit}`}
                description={t('permits.split.partDays', {
                  days: `${dayMonth(part.dayFrom)}–${dottedDate(part.dayTo)}`,
                  count: dayCount(part.dayFrom, part.dayTo),
                })}
                inputMode="decimal"
                value={values[index] ?? ''}
                onChange={(event) => {
                  const next = [...values];
                  next[index] = event.currentTarget.value;
                  setTexts(next);
                  if (save.error !== null) save.reset();
                }}
                rightSection={unit === null ? undefined : <Text size="xs" c="dimmed">{unit}</Text>}
                rightSectionWidth={unit === null ? undefined : 90}
                error={invalid[index] === true ? t('permits.split.notNumber') : undefined}
                data-autofocus={index === 0 ? true : undefined}
                data-split-part={part.partNo}
              />
            ))}
            {wasSingle !== null && wasSingle !== undefined && (
              <Text size="xs" c="dimmed" data-split-was="">
                {t('permits.split.was', { value: shown(wasSingle) })}
              </Text>
            )}
            <Text size="sm" fw={600} role="status" aria-live="polite" data-split-total="">
              {total === null
                ? t('permits.split.totalPending', { month: monthLabel })
                : t('permits.split.total', {
                    month: monthLabel,
                    parts: parsed.map((value) => shown(value ?? '0')).join(' + '),
                    total: shown(total),
                    unit: unit ?? '',
                  })}
            </Text>
            <ErrorAlert error={save.error} />
            <Text size="xs" c="dimmed" data-split-note="">
              {t('permits.split.ownLimit')} {t('permits.split.limitsOnTotal')}
            </Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={onClose}>
                {t('common.cancel')}
              </Button>
              <Button type="submit" loading={saveLoading} disabled={invalid.some(Boolean)}>
                {t('permits.split.save')}
              </Button>
            </Group>
          </Stack>
        </form>
      )}
    </Modal>
  );
}
