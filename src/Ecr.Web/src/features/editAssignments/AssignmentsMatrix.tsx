import type { JSX } from 'react';
import { ActionIcon, Group, Menu, Stack, Table, Text, VisuallyHidden } from '@mantine/core';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { AssignmentBadge } from './AssignmentBadge';
import { compareAssignments, nextMonthSlice, sliceLabel } from './model';
import type { EditAssignment, RecipientStatus } from './types';

export type MatrixView = 'months' | 'people';

export interface MatrixActions {
  readonly onValidate: (assignment: EditAssignment) => void;
  readonly onReturn: (assignment: EditAssignment) => void;
  readonly onEdit: (assignment: EditAssignment) => void;
  readonly onRepeat: (assignment: EditAssignment) => void;
  readonly onRemove: (assignment: EditAssignment) => void;
}

const Months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12] as const;

/** Матриця «місяць × виконавець × статус»: подання «Months» — рядок = передача. */
function MonthsTable({
  assignments,
  actions,
}: {
  readonly assignments: readonly EditAssignment[];
  readonly actions: MatrixActions | undefined;
}): JSX.Element {
  return (
    <Table.ScrollContainer minWidth={320}>
      <Table data-testid="edit-assignments-months">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t('editAssign.col.slice')}</Table.Th>
            <Table.Th scope="col">{t('editAssign.col.executors')}</Table.Th>
            <Table.Th scope="col">{t('editAssign.col.status')}</Table.Th>
            <Table.Th scope="col">
              <VisuallyHidden>{t('editAssign.col.actions')}</VisuallyHidden>
            </Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {[...assignments].sort(compareAssignments).map((a) => (
            <Table.Tr key={a.id} data-assignment-id={a.id}>
              <Table.Th scope="row" fw={500}>
                {sliceLabel(a.slice)}
              </Table.Th>
              <Table.Td>
                <Stack gap="xs">
                  {a.recipients.length === 0 ? (
                    <Text size="sm" c="dimmed">
                      {t('editAssign.ownerOnly')}
                    </Text>
                  ) : (
                    a.recipients.map((r) => (
                      <Group key={r.userId} gap="xs" wrap="nowrap">
                        <Text size="sm">{r.displayName}</Text>
                        {r.isActive ? null : (
                          <Text size="xs" c="dimmed">
                            {t('editAssign.inactive')}
                          </Text>
                        )}
                        <AssignmentBadge state={r.status} />
                      </Group>
                    ))
                  )}
                </Stack>
                {a.recipients.some((r) => r.status === 'Returned' && r.returnReason) ? (
                  <Text size="xs" c="dimmed" mt="xs">
                    {t('editAssign.returnedWith', {
                      reason: a.recipients.find((r) => r.status === 'Returned' && r.returnReason)?.returnReason ?? '',
                    })}
                  </Text>
                ) : null}
              </Table.Td>
              <Table.Td>
                <AssignmentBadge state={a.status} />
              </Table.Td>
              <Table.Td>{actions === undefined ? null : <RowMenu assignment={a} actions={actions} />}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function RowMenu({ assignment, actions }: { readonly assignment: EditAssignment; readonly actions: MatrixActions }): JSX.Element {
  const label = sliceLabel(assignment.slice);
  const canRepeat = nextMonthSlice(assignment.slice) !== null;

  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon variant="subtle" color="gray" aria-label={t('editAssign.rowActions', { slice: label })}>
          <span aria-hidden="true">⋯</span>
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        {assignment.status === 'Filled' ? (
          <Menu.Item onClick={() => actions.onValidate(assignment)}>{t('editAssign.validate')}</Menu.Item>
        ) : null}
        {assignment.status === 'Filled' || assignment.status === 'Validated' ? (
          <Menu.Item onClick={() => actions.onReturn(assignment)}>{t('editAssign.return')}</Menu.Item>
        ) : null}
        <Menu.Item onClick={() => actions.onEdit(assignment)}>{t('editAssign.edit')}</Menu.Item>
        {canRepeat ? <Menu.Item onClick={() => actions.onRepeat(assignment)}>{t('editAssign.repeatNext')}</Menu.Item> : null}
        <Menu.Item color="statusError" onClick={() => actions.onRemove(assignment)}>
          {t('editAssign.remove')}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

/** Коротка позначка статусу виконавця в клітинці «людина × місяць»; повне слово — для читалки. */
function CellMark({ status }: { readonly status: RecipientStatus }): JSX.Element {
  const glyph = status === 'Filled' ? '●' : status === 'Returned' ? '↩' : '◔';
  const word =
    status === 'Filled' ? t('editAssign.status.Filled') : status === 'Returned' ? t('editAssign.status.Returned') : t('editAssign.status.Assigned');

  return (
    <span title={word}>
      <span aria-hidden="true">{glyph}</span>
      <VisuallyHidden>{word}</VisuallyHidden>
    </span>
  );
}

/** Подання «People»: виконавець × місяці (січень…грудень), реальна таблиця із `scope`. */
function PeopleTable({ assignments }: { readonly assignments: readonly EditAssignment[] }): JSX.Element {
  const people = new Map<number, string>();
  for (const a of assignments) for (const r of a.recipients) people.set(r.userId, r.displayName);

  const cell = (userId: number, month: number): RecipientStatus | null => {
    const match = assignments.find((a) => a.slice.monthNumber === month && a.recipients.some((r) => r.userId === userId));

    return match?.recipients.find((r) => r.userId === userId)?.status ?? null;
  };

  return (
    <Table.ScrollContainer minWidth={420}>
      <Table data-testid="edit-assignments-people">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">{t('editAssign.col.person')}</Table.Th>
            {Months.map((m) => (
              <Table.Th key={m} scope="col" title={formatMonthName(m)}>
                {formatMonthName(m).slice(0, 3)}
              </Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {[...people.entries()].map(([userId, name]) => (
            <Table.Tr key={userId}>
              <Table.Th scope="row" fw={500}>
                {name}
              </Table.Th>
              {Months.map((m) => {
                const status = cell(userId, m);

                return <Table.Td key={m}>{status === null ? null : <CellMark status={status} />}</Table.Td>;
              })}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

export function AssignmentsMatrix({
  view,
  assignments,
  actions,
}: {
  readonly view: MatrixView;
  readonly assignments: readonly EditAssignment[];
  /** `undefined` - лише перегляд (не керуючий, симуляція): стовпець дій порожній. */
  readonly actions: MatrixActions | undefined;
}): JSX.Element {
  return view === 'months' ? <MonthsTable assignments={assignments} actions={actions} /> : <PeopleTable assignments={assignments} />;
}
