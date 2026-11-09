import { useState, type JSX } from 'react';
import { Alert, Anchor, Button, Group, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryEntryDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { DateOnlyInput } from '@/features/registries/rc816/DateOnlyInput';
import { LookupEntriesStaleTimeMs } from '@/features/registries/api';
import { entryLabelsOf } from '@/features/registries/lookupEntryLabels';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { problemText } from '@/shared/ui/problemText';
import { dottedDate, permitGapOf } from './splitModel';
import { useAddHeaderPermit, useAddRowPermit } from './usePermitHistory';

/** A row's permit cell or a header permit field. */
export type PermitAddTarget =
  | { readonly kind: 'row'; readonly tableInstanceId: number; readonly rowKey: string; readonly columnCode: string }
  | { readonly kind: 'header'; readonly fieldCode: string };

/** Options shown at once (the registry may hold thousands; search goes over all of them). */
const OptionsLimit = 50;

export interface AddRowPermitFormProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Whose permit: a row's permit cell (j1) or a header field (jh1, Ship 2). */
  readonly target: PermitAddTarget;
  /** Code of the permit registry the column points to. */
  readonly registryCode: string;
  /** The permit the row holds now (registry entry id); it is not offered again. */
  readonly currentEntryId: number | null;
  readonly onDone: (validFrom: string) => void;
  readonly onCancel: () => void;
}

/** The next day of `yyyy-MM-dd`. */
function nextDay(date: string): string {
  const next = new Date(`${date.slice(0, 10)}T00:00:00Z`);
  next.setUTCDate(next.getUTCDate() + 1);

  return next.toISOString().slice(0, 10);
}

/** Default «valid from»: the day after the current permit ends inside the period, else the 1st of the period month. */
export function defaultValidFrom(periodKey: number, currentValidTo: string | null | undefined): string {
  const month = `${String(Math.floor(periodKey / 100))}-${String(periodKey % 100).padStart(2, '0')}`;
  if (typeof currentValidTo === 'string' && currentValidTo.slice(0, 7) === month) return nextDay(currentValidTo);

  return `${month}-01`;
}

/**
 * Feature (j) j1a: «Add permit from date…» on a row's permit cell — pick the new permit and the first day it is valid
 * for THIS row. No new document, no new row; the month of the change then has to be split (j2).
 *
 * j4: a gap between the permits is refused with the days named and two fixes — move the start date to the day the
 * server suggests (re-submits at once), or add another permit for those days (the form stays open).
 */
export function AddRowPermitForm(props: AddRowPermitFormProps): JSX.Element {
  const { documentId, periodKey, registryCode, currentEntryId } = props;
  const permitsUrl = `/api/v1/registries/${encodeURIComponent(registryCode)}/entries`;
  // ⚠ The same key and window as the header permit picker (`DocumentHeaderPanel`): permits valid in the project window.
  const entries = useQuery({
    queryKey: [...queryKeys.registries.entries(registryCode), 'document', documentId],
    queryFn: () => apiFetch<RegistryEntryDto[]>(`${permitsUrl}?documentId=${String(documentId)}`),
    staleTime: LookupEntriesStaleTimeMs,
  });
  const header = props.target.kind === 'header';
  const addRow = useAddRowPermit(documentId);
  const addHeader = useAddHeaderPermit(documentId);
  const add = header ? addHeader : addRow;
  const addLoading = usePendingLoading(add.isPending);

  const current = entries.data?.find((entry) => entry.id === currentEntryId);
  const [entryId, setEntryId] = useState<string | null>(null);
  const [validFrom, setValidFrom] = useState<string | null>(null);
  const from = validFrom ?? defaultValidFrom(periodKey, current?.validTo);

  const list = (entries.data ?? []).filter((entry) => entry.id !== currentEntryId);
  const labels = entryLabelsOf(list);
  const options = list.map((entry) => ({ value: String(entry.id), label: labels.get(entry.id) ?? entry.display }));
  const chosen = list.find((entry) => String(entry.id) === entryId);

  const submit = (date: string): void => {
    if (chosen === undefined || date.length === 0) return;
    const target = props.target;
    const onSuccess = (): void => props.onDone(date);
    if (target.kind === 'header') {
      addHeader.mutate({ fieldCode: target.fieldCode, registryEntryId: chosen.id, validFrom: date, periodKey }, { onSuccess });
    } else {
      addRow.mutate(
        {
          tableInstanceId: target.tableInstanceId,
          rowKey: target.rowKey,
          columnCode: target.columnCode,
          registryEntryId: chosen.id,
          validFrom: date,
        },
        { onSuccess },
      );
    }
  };

  const gap = permitGapOf(add.error);
  const currentLabel = current === undefined ? null : (current.display || current.code);
  const chosenLabel = chosen === undefined ? null : (labels.get(chosen.id) ?? chosen.display);

  return (
    <form
      aria-label={header ? t('permits.header.addTitle') : t('permits.history.addTitle')}
      data-add-row-permit=""
      onSubmit={(event) => {
        event.preventDefault();
        submit(from);
      }}
    >
      <Stack gap="xs">
        <Select
          label={t('permits.history.permit')}
          searchable
          limit={OptionsLimit}
          data={options}
          value={entryId}
          onChange={(next) => {
            setEntryId(next);
            if (add.error !== null) add.reset();
          }}
          nothingFoundMessage={entries.isPending ? t('permits.history.loading') : t('permits.history.noPermits')}
          data-autofocus
        />
        <DateOnlyInput
          label={header ? t('permits.header.validFrom') : t('permits.history.validFrom')}
          value={from}
          onChange={(next) => {
            setValidFrom(next);
            if (add.error !== null) add.reset();
          }}
        />
        {/* F13 p220 j7: no «— from 01.10» preview before a permit is picked. */}
        {chosenLabel !== null && (
          <Text size="xs" c="dimmed" data-add-keeps="">
            {t(header ? 'permits.header.addKeeps' : 'permits.history.addKeeps', {
              current: currentLabel ?? '—',
              next: chosenLabel,
              until: dottedDate(previousDay(from)),
              from: dottedDate(from),
            })}
          </Text>
        )}
        {!from.endsWith('-01') && (
          <Text size="xs" c="statusWarning" data-add-split-hint="">
            {header ? t('permits.header.addSplitHint') : t('permits.history.addSplitHint')}
          </Text>
        )}

        {gap !== null ? (
          <Alert color="statusError" role="alert" data-permit-gap="" styles={{ body: { minWidth: 0 } }}>
            {/* Fresh eyes 09.11: the Inspector is narrow — the text wraps and the fixes stack, nothing is cut off. */}
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
              {problemText(add.error).detail ?? problemText(add.error).title}
            </Text>
            <Stack gap="xs" mt="xs" align="flex-start" data-gap-actions="">
              {gap.suggestedFrom !== null && (
                <Anchor
                  component="button"
                  type="button"
                  size="sm"
                  ta="left"
                  data-gap-move=""
                  onClick={() => {
                    const date = gap.suggestedFrom ?? from;
                    setValidFrom(date);
                    submit(date);
                  }}
                >
                  {t('permits.history.gapMove', { permit: gap.next, date: dottedDate(gap.suggestedFrom) })}
                </Anchor>
              )}
              <Anchor
                component="button"
                type="button"
                size="sm"
                ta="left"
                data-gap-add=""
                onClick={() => {
                  add.reset();
                  setEntryId(null);
                }}
              >
                {t('permits.history.gapAdd', { days: gap.days })}
              </Anchor>
            </Stack>
          </Alert>
        ) : (
          <ErrorAlert error={add.error} />
        )}

        <Group justify="flex-end" gap="xs" wrap="wrap">
          <Button size="xs" variant="default" onClick={props.onCancel}>
            {t('common.cancel')}
          </Button>
          <Button size="xs" type="submit" loading={addLoading} disabled={chosen === undefined}>
            {t('permits.history.addSubmit')}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/** The day before `yyyy-MM-dd`. */
function previousDay(date: string): string {
  const previous = new Date(`${date.slice(0, 10)}T00:00:00Z`);
  previous.setUTCDate(previous.getUTCDate() - 1);

  return previous.toISOString().slice(0, 10);
}
