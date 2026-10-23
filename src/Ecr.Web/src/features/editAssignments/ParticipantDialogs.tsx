import { useState, type JSX } from 'react';
import { Button, Group, Modal, SegmentedControl, Select, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useQuery } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { MinCandidateQuery, getCandidates } from './api';
import { levelLabel } from './model';
import { AssignmentLevels, type AssignmentLevel, type Candidate } from './types';

type LocalizedText = components['schemas']['LocalizedText'];

/** Аркуш документа з його таблицями (для вибору області учасника). */
export interface ParticipantSheet {
  readonly code: string;
  readonly name: string;
  readonly tables?: readonly { readonly tableCode: string; readonly tableNameL10n?: LocalizedText | null }[];
}

/** Що вибрано в діалозі учасника. */
export interface ParticipantChoice {
  readonly user: Candidate;
  readonly level: AssignmentLevel;
  readonly sheetCode: string | null;
  readonly tableCode: string | null;
}

/** Пошук одного користувача серед кандидатів документа (≥ 2 символи; сервер повертає ≤ 20, без email/SID). */
function UserPicker({
  documentId,
  picked,
  onPick,
}: {
  readonly documentId: number;
  readonly picked: Candidate | null;
  readonly onPick: (user: Candidate | null) => void;
}): JSX.Element {
  const [query, setQuery] = useState('');
  const [debounced] = useDebouncedValue(query.trim(), 250);
  const search = useQuery({
    queryKey: queryKeys.editAssignments.candidates(documentId, debounced),
    queryFn: () => getCandidates(documentId, debounced),
    enabled: debounced.length >= MinCandidateQuery,
  });

  if (picked !== null) {
    return (
      <Group gap="xs" data-testid="participant-picked">
        <Text size="sm" fw={500}>
          {picked.displayName}
        </Text>
        <Button size="xs" variant="subtle" onClick={() => onPick(null)}>
          {t('editAssign.participant.change')}
        </Button>
      </Group>
    );
  }

  return (
    <Stack gap="xs">
      <TextInput
        label={t('editAssign.participant.who')}
        description={t('editAssign.wizard.searchHint')}
        value={query}
        onChange={(event) => setQuery(event.currentTarget.value)}
        data-autofocus
      />
      {debounced.length >= MinCandidateQuery && (search.data ?? []).length > 0 ? (
        <Group gap="xs" role="group" aria-label={t('editAssign.wizard.found')}>
          {(search.data ?? []).map((c) => (
            <Button key={c.userId} size="xs" variant="default" onClick={() => onPick(c)}>
              {c.displayName}
            </Button>
          ))}
        </Group>
      ) : null}
    </Stack>
  );
}

/**
 * D-443: «Додати учасника» — користувач, рівень (`Read`/`Write`/`Submit`/`Approve`) і область (документ, аркуш,
 * таблиця). Клієнт прав не вирішує: «не вище власного рівня» і заборони ролей перевіряє сервер (403/422 — у
 * діалозі текстом сервера).
 */
export function ParticipantModal({
  opened,
  documentId,
  sheets,
  isPending,
  error,
  onSubmit,
  onClose,
}: {
  readonly opened: boolean;
  readonly documentId: number;
  readonly sheets: readonly ParticipantSheet[];
  readonly isPending: boolean;
  readonly error: unknown;
  readonly onSubmit: (choice: ParticipantChoice) => void;
  readonly onClose: () => void;
}): JSX.Element {
  const [user, setUser] = useState<Candidate | null>(null);
  const [level, setLevel] = useState<AssignmentLevel>('Read');
  const [sheetCode, setSheetCode] = useState<string | null>(null);
  const [tableCode, setTableCode] = useState<string | null>(null);
  const loading = usePendingLoading(isPending);

  const tables = sheets.find((s) => s.code === sheetCode)?.tables ?? [];

  return (
    <Modal opened={opened} onClose={onClose} title={t('editAssign.participant.title')}>
      <Stack gap="sm" data-testid="participant-modal">
        <Text size="sm" c="dimmed">
          {t('editAssign.participant.hint')}
        </Text>
        <UserPicker documentId={documentId} picked={user} onPick={setUser} />
        <Stack gap="xs">
          <Text size="sm" id="participant-level-label">
            {t('editAssign.participant.level')}
          </Text>
          <SegmentedControl
            aria-labelledby="participant-level-label"
            value={level}
            onChange={(value) => setLevel(value as AssignmentLevel)}
            data={AssignmentLevels.map((value) => ({ value, label: levelLabel(value) }))}
          />
        </Stack>
        <Select
          label={t('editAssign.participant.sheet')}
          placeholder={t('editAssign.participant.sheetAll')}
          data={sheets.map((s) => ({ value: s.code, label: `${s.name} (${s.code})` }))}
          value={sheetCode}
          onChange={(value) => {
            setSheetCode(value);
            setTableCode(null);
          }}
          clearable
        />
        {sheetCode !== null && tables.length > 0 ? (
          <Select
            label={t('editAssign.participant.table')}
            placeholder={t('editAssign.participant.tableAll')}
            data={tables.map((table) => ({
              value: table.tableCode,
              label: `${localized(table.tableNameL10n) || table.tableCode} (${table.tableCode})`,
            }))}
            value={tableCode}
            onChange={setTableCode}
            clearable
          />
        ) : null}
        {error === null || error === undefined ? null : <ErrorAlert error={error} />}
        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={user === null}
            loading={loading}
            onClick={() => {
              if (user !== null) onSubmit({ user, level, sheetCode, tableCode });
            }}
          >
            {t('editAssign.participant.grant')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** D-443: «Передати власність» — новий власник (сервер вимагає від нього права запису в проєкті). */
export function OwnerTransferModal({
  opened,
  documentId,
  isPending,
  error,
  onSubmit,
  onClose,
}: {
  readonly opened: boolean;
  readonly documentId: number;
  readonly isPending: boolean;
  readonly error: unknown;
  readonly onSubmit: (user: Candidate) => void;
  readonly onClose: () => void;
}): JSX.Element {
  const [user, setUser] = useState<Candidate | null>(null);
  const loading = usePendingLoading(isPending);

  return (
    <Modal opened={opened} onClose={onClose} title={t('editAssign.owner.title')}>
      <Stack gap="sm" data-testid="owner-transfer-modal">
        <Text size="sm" c="dimmed">
          {t('editAssign.owner.text')}
        </Text>
        <UserPicker documentId={documentId} picked={user} onPick={setUser} />
        {error === null || error === undefined ? null : <ErrorAlert error={error} />}
        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={user === null}
            loading={loading}
            onClick={() => {
              if (user !== null) onSubmit(user);
            }}
          >
            {t('editAssign.owner.confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
