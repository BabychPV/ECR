import { useEffect, useState, type JSX } from 'react';
import { Button, Checkbox, Chip, Group, List, Pill, Select, Stack, Switch, Text, TextInput, Textarea } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useQuery } from '@tanstack/react-query';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Wizard, type WizardApi, type WizardStep } from '@/shared/ui/Wizard';
import { MinCandidateQuery, candidatesKey, editorSetsKey, getCandidates, getEditorSets } from './api';
import type { Candidate, EditorSet, RecipientVisibility } from './types';

/** Дані майстра; живуть лише в `Wizard` (стан кроків — локальний, не в адресі). */
export interface AssignWizardData {
  readonly months: readonly number[];
  readonly sheetCode: string | null;
  readonly setId: string | null;
  readonly users: readonly Candidate[];
  readonly saveAsSet: boolean;
  readonly setName: string;
  readonly note: string;
  /** Галочка «дозволити й іншим за ролями»: знімає ексклюзивність (D1). */
  readonly alsoOthers: boolean;
}

export const emptyAssignData: AssignWizardData = {
  months: [],
  sheetCode: null,
  setId: null,
  users: [],
  saveAsSet: false,
  setName: '',
  note: '',
  alsoOthers: false,
};

const Months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12] as const;

export interface AssignWizardProps {
  readonly documentId: number;
  readonly sheets: readonly { readonly code: string; readonly name: string }[];
  readonly initialData: AssignWizardData;
  /** Місяці, що вже передані: майстер попереджає, що їх склад буде замінено. */
  readonly takenMonths: readonly number[];
  readonly isApplying: boolean;
  readonly onApply: (data: AssignWizardData, api: WizardApi) => void;
  readonly onClose: () => void;
}

function visibilityLabel(v: RecipientVisibility): string {
  switch (v) {
    case 'Visible':
      return t('editAssign.visibility.Visible');
    case 'NotVisible':
      return t('editAssign.visibility.NotVisible');
    default:
      return t('editAssign.visibility.Unknown');
  }
}

/** Склад виконавців з урахуванням набору: набір + вручну додані, без дублів. */
export function resolveUsers(data: AssignWizardData, sets: readonly EditorSet[]): readonly Candidate[] {
  const set = sets.find((s) => String(s.id) === data.setId);
  const fromSet: Candidate[] = (set?.members ?? []).map((m) => ({
    userId: m.userId,
    displayName: m.displayName,
    visibility: m.visibility ?? 'Unknown',
  }));
  const seen = new Set<number>();

  return [...fromSet, ...data.users].filter((u) => !seen.has(u.userId) && seen.add(u.userId));
}

function WhatStep({
  data,
  update,
  sheets,
  takenMonths,
}: {
  readonly data: AssignWizardData;
  readonly update: (patch: Partial<AssignWizardData>) => void;
  readonly sheets: AssignWizardProps['sheets'];
  readonly takenMonths: readonly number[];
}): JSX.Element {
  const replaced = data.months.filter((m) => takenMonths.includes(m));

  return (
    <Stack gap="sm">
      <Text size="sm" id="ea-months-label">
        {t('editAssign.wizard.months')}
      </Text>
      <Group gap="xs" role="group" aria-labelledby="ea-months-label">
        {Months.map((month) => {
          const on = data.months.includes(month);

          return (
            <Chip
              key={month}
              checked={on}
              variant="outline"
              onChange={() =>
                update({ months: on ? data.months.filter((m) => m !== month) : [...data.months, month].sort((a, b) => a - b) })
              }
            >
              {formatMonthName(month)}
            </Chip>
          );
        })}
      </Group>
      {replaced.length > 0 ? (
        <Text size="xs" c="dimmed" role="note">
          {t('editAssign.wizard.replaces', { months: replaced.map(formatMonthName).join(', ') })}
        </Text>
      ) : null}
      {sheets.length > 0 ? (
        <Select
          label={t('editAssign.wizard.onlySheet')}
          description={t('editAssign.wizard.onlySheetHint')}
          data={sheets.map((s) => ({ value: s.code, label: `${s.name} (${s.code})` }))}
          value={data.sheetCode}
          onChange={(value) => update({ sheetCode: value })}
          clearable
        />
      ) : null}
    </Stack>
  );
}

function WhoStep({
  documentId,
  data,
  update,
}: {
  readonly documentId: number;
  readonly data: AssignWizardData;
  readonly update: (patch: Partial<AssignWizardData>) => void;
}): JSX.Element {
  const [query, setQuery] = useState('');
  const [debounced] = useDebouncedValue(query.trim(), 250);
  const sets = useQuery({ queryKey: editorSetsKey, queryFn: getEditorSets });
  const search = useQuery({
    queryKey: candidatesKey(documentId, debounced),
    queryFn: () => getCandidates(documentId, debounced),
    enabled: debounced.length >= MinCandidateQuery,
  });

  const picked = resolveUsers(data, sets.data ?? []);
  const found = (search.data ?? []).filter((c) => !picked.some((p) => p.userId === c.userId));

  return (
    <Stack gap="sm">
      <Select
        label={t('editAssign.wizard.set')}
        placeholder={t('editAssign.wizard.setNone')}
        data={(sets.data ?? []).map((s) => ({ value: String(s.id), label: s.name }))}
        value={data.setId}
        onChange={(value) => update({ setId: value })}
        clearable
      />
      <TextInput
        label={t('editAssign.wizard.search')}
        description={t('editAssign.wizard.searchHint')}
        value={query}
        onChange={(event) => setQuery(event.currentTarget.value)}
      />
      {debounced.length >= MinCandidateQuery && found.length > 0 ? (
        <Group gap="xs" role="group" aria-label={t('editAssign.wizard.found')}>
          {found.map((c) => (
            <Button
              key={c.userId}
              size="xs"
              variant="default"
              onClick={() => update({ users: [...data.users, c] })}
            >
              {c.displayName}
            </Button>
          ))}
        </Group>
      ) : null}
      {picked.length > 0 ? (
        <Pill.Group aria-label={t('editAssign.wizard.picked')}>
          {picked.map((u) => {
            const manual = data.users.some((m) => m.userId === u.userId);

            return (
              <Pill
                key={u.userId}
                withRemoveButton={manual}
                removeButtonProps={{ 'aria-label': t('editAssign.wizard.removeUser', { name: u.displayName }) }}
                onRemove={() => update({ users: data.users.filter((m) => m.userId !== u.userId) })}
              >
                {u.displayName} · {visibilityLabel(u.visibility)}
              </Pill>
            );
          })}
        </Pill.Group>
      ) : null}
      {picked.some((u) => u.visibility === 'NotVisible') ? (
        <Text size="xs" c="dimmed" role="note">
          {t('editAssign.visibilityHint')}
        </Text>
      ) : null}
      <Switch
        label={t('editAssign.wizard.saveAsSet')}
        checked={data.saveAsSet}
        onChange={(event) => update({ saveAsSet: event.currentTarget.checked })}
      />
      {data.saveAsSet ? (
        <TextInput
          label={t('editAssign.wizard.setName')}
          value={data.setName}
          maxLength={100}
          onChange={(event) => update({ setName: event.currentTarget.value })}
          data-autofocus
        />
      ) : null}
      <Textarea
        label={t('editAssign.wizard.note')}
        value={data.note}
        maxLength={500}
        autosize
        minRows={2}
        onChange={(event) => update({ note: event.currentTarget.value })}
      />
    </Stack>
  );
}

/**
 * Майстер «Передати місяць» (3 кроки: що → кому → підсумок), `Wizard` додає крок підсумку сам.
 *
 * ⛔ Склад виконавців з набору — ЗНІМОК на момент застосування (дизайн §4): сервер приймає
 * `recipientUserIds`, а `appliedSetId` лише позначає походження.
 */
export function AssignWizard({
  documentId,
  sheets,
  initialData,
  takenMonths,
  isApplying,
  onApply,
  onClose,
}: AssignWizardProps): JSX.Element {
  const sets = useQuery({ queryKey: editorSetsKey, queryFn: getEditorSets });

  // Галочка підсумку живе тут, а не в даних `Wizard`: `summary` не отримує `update`.
  const [alsoOthers, setAlsoOthers] = useState(initialData.alsoOthers);

  // ⛔ T2-06 (як у `CreateDocumentModal`): `Wizard` має ПЕРЕЙТИ в `opened`, інакше Mantine не
  // запам'ятає відкривач і фокус після Esc падає на BODY. Спершу закритий, наступним рендером відкритий.
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    setArmed(true);
  }, []);

  const steps: readonly WizardStep<AssignWizardData>[] = [
    {
      id: 'what',
      label: t('editAssign.wizard.step.what'),
      canNext: (d) => d.months.length > 0,
      render: ({ data, update }) => <WhatStep data={data} update={update} sheets={sheets} takenMonths={takenMonths} />,
    },
    {
      id: 'who',
      label: t('editAssign.wizard.step.who'),
      validate: (d) => {
        if (resolveUsers(d, sets.data ?? []).length === 0) {
          return { message: t('editAssign.wizard.noRecipients'), focus: 'input' };
        }
        if (d.saveAsSet && d.setName.trim() === '') {
          return { message: t('editAssign.wizard.noSetName'), focus: 'input[data-autofocus]' };
        }

        return null;
      },
      render: ({ data, update }) => <WhoStep documentId={documentId} data={data} update={update} />,
    },
  ];

  return (
    <Wizard<AssignWizardData>
      opened={armed}
      title={t('editAssign.wizard.title')}
      initialData={initialData}
      steps={steps}
      labels={{ back: t('wizard.back'), next: t('wizard.next'), review: t('wizard.review') }}
      reviewText={t('editAssign.wizard.reviewText')}
      applyLabel={t('editAssign.wizard.apply')}
      isApplying={isApplying}
      summary={(d) => {
        const users = resolveUsers(d, sets.data ?? []);
        const setName = (sets.data ?? []).find((s) => String(s.id) === d.setId)?.name;

        return (
          <Stack gap="xs">
            <KeyValue
              items={[
                { label: t('editAssign.wizard.summary.months'), value: d.months.map(formatMonthName).join(', ') },
                { label: t('editAssign.wizard.summary.sheet'), value: d.sheetCode },
                {
                  label: t('editAssign.col.executors'),
                  value: users.map((u) => u.displayName).join(', '),
                  ...(setName === undefined ? {} : { hint: t('editAssign.wizard.summary.fromSet', { name: setName }) }),
                },
              ]}
            />
            <List size="sm" aria-label={t('editAssign.wizard.summary.visibility')}>
              {users.map((u) => (
                <List.Item key={u.userId}>
                  {u.displayName}: {visibilityLabel(u.visibility)}
                </List.Item>
              ))}
            </List>
            <Text size="sm">
              {alsoOthers ? t('editAssign.wizard.summary.shared') : t('editAssign.wizard.summary.exclusive')}
            </Text>
            <Checkbox
              label={t('editAssign.wizard.alsoOthers')}
              checked={alsoOthers}
              onChange={(event) => setAlsoOthers(event.currentTarget.checked)}
            />
          </Stack>
        );
      }}
      onApply={(d, api) => onApply({ ...d, alsoOthers }, api)}
      onClose={onClose}
      onExitUnsaved={() => false}
    />
  );
}
