import { useState, type JSX } from 'react';
import { Anchor, Group, List, Paper, Stack, Text } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { AddRowPermitForm } from './AddRowPermitForm';
import { dottedDate } from './splitModel';
import { usePermitHistory, useUndoHeaderPermit } from './usePermitHistory';

export interface HeaderPermitSectionProps {
  readonly documentId: number;
  /** The period the user is in (`YYYYMM`): the reporting window and the rows the change makes split. */
  readonly periodKey: number;
  /** The header permit field. */
  readonly fieldCode: string;
  /** Code of the permit registry the field points to. */
  readonly registryCode: string;
  /** The permit the header holds now (registry entry id). */
  readonly currentEntryId: number | null;
  readonly canEdit: boolean;
}

/**
 * Feature (j) Ship 2, jh1 (mockup light-1335-jh1): under the header permit field — its history («KZ…A until 14.02.2026 /
 * KZ…B from 15.02.2026», who added it), «+ Add permit from date…» and «Undo last». A change mid-month keeps ONE header
 * field and one document; the tables that rely on the header permit then need their month split (jh2/jh3).
 */
export function HeaderPermitSection(props: HeaderPermitSectionProps): JSX.Element | null {
  const { documentId, periodKey, fieldCode, registryCode, currentEntryId, canEdit } = props;
  const history = usePermitHistory(documentId, periodKey, currentEntryId !== null);
  const item = history.data?.header.find((candidate) => candidate.fieldCode === fieldCode);
  const permits = item?.permits ?? [];
  const canUndo = permits.some((permit) => permit.segmentId !== null);

  const undo = useUndoHeaderPermit(documentId, periodKey);
  const undoLoading = usePendingLoading(undo.isPending);
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState<string | null>(null);

  if (currentEntryId === null) return null;

  return (
    <Stack gap="xs" data-header-permit-history={fieldCode}>
      {permits.length > 1 && (
        <List listStyleType="none" aria-label={t('permits.header.historyTitle')}>
          {permits.map((permit) => (
            <List.Item key={`${String(permit.registryEntryId)}:${permit.from ?? ''}`} data-header-permit-item="">
              <Text size="xs" component="span" ff="monospace">
                {permit.permit}
              </Text>{' '}
              <Text size="xs" component="span" c="dimmed">
                {permit.from === null
                  ? t('permits.cell.until', { date: permit.until === null ? '—' : dottedDate(permit.until) })
                  : t('permits.cell.from', { date: dottedDate(permit.from) })}
                {permit.addedBy !== null && permit.addedAt !== null
                  ? ` · ${t('permits.history.addedBy', { user: permit.addedBy, time: formatDateTime(new Date(permit.addedAt)) })}`
                  : ''}
              </Text>
            </List.Item>
          ))}
        </List>
      )}

      {added !== null && (
        <Text size="xs" role="status" data-header-permit-added="">
          {/* F13 p223 (4): a change on the 1st shares no month — nothing to split, no «split the month» note. */}
          {added.endsWith('-01')
            ? t('permits.header.addedWhole', { date: dottedDate(added) })
            : t('permits.header.added', { date: dottedDate(added) })}
        </Text>
      )}
      <ErrorAlert error={undo.error} />

      {canEdit &&
        (adding ? (
          <Paper withBorder p="sm" radius="sm">
            <AddRowPermitForm
              documentId={documentId}
              periodKey={periodKey}
              target={{ kind: 'header', fieldCode }}
              registryCode={registryCode}
              currentEntryId={currentEntryId}
              onDone={(date) => {
                setAdding(false);
                setAdded(date);
              }}
              onCancel={() => setAdding(false)}
            />
          </Paper>
        ) : (
          <Group gap="sm">
            <Anchor component="button" type="button" size="sm" data-header-permit-add="" onClick={() => setAdding(true)}>
              {`+ ${t('permits.history.add')}`}
            </Anchor>
            {canUndo && (
              <Anchor
                component="button"
                type="button"
                size="sm"
                c="dimmed"
                disabled={undoLoading}
                data-header-permit-undo=""
                onClick={() => {
                  setAdded(null);
                  undo.mutate({ fieldCode });
                }}
              >
                {t('permits.history.undo')}
              </Anchor>
            )}
          </Group>
        ))}
    </Stack>
  );
}
