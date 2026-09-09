import { useMemo, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Paper, Stack, Table, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { TableRelationDto, TemplateStructureDto } from '@/api/types';
import {
  deleteTableRelation,
  saveTableRelation,
  tableRelations,
} from '@/features/tables/api';
import { RelationForm, kindLabel, onSourceChangeLabel } from '@/features/tables/RelationForm';
import {
  draftOf,
  emptyDraft,
  tableOptions,
  type RelationDraft,
} from '@/features/tables/relation';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';

/**
 * Редактор зв'язків між таблицями версії (`ФВ-2.12`, `ФВ-2.13`).
 *
 * ⛔ Екран існує рівно тому, що `ФВ-2.13` вимагає налаштовувати зв'язки **у
 * веб-інтерфейсі, а не в конфігах чи коді**. Доти `cfg.TableRelationDef` мала
 * таблицю, сутність і жодного шляху, яким людина могла б завести зв'язок:
 * єдиним способом лишався `INSERT` руками — тобто рівно те, що вимога
 * забороняє.
 *
 * ⛔ Правиться лише **чернетка**. Зв'язок — структура: від нього залежить,
 * звідки в таблиці беруться числа, і зміна в опублікованій версії тихо
 * змінила б уже подані форми (`ФВ-7.1`). Заборону тримає домен, а не ця
 * форма; тут лише показано, чому кнопки немає.
 *
 * ⚠ Таблиці для вибору беруться зі структури версії (`GET …/structure`), а не
 * з окремого маршруту: другий перелік тих самих таблиць розійшовся б із
 * першим на першій же зміні структури.
 */
export function TableRelationsPage(): JSX.Element {
  const params = useParams();
  const versionId = Number(params['versionId']);
  const known = Number.isFinite(versionId);

  const queryClient = useQueryClient();
  const session = useSession();
  const mayEdit = can(session.data, 'Template.Edit');

  const [draft, setDraft] = useState<RelationDraft | null>(null);

  const relations = useQuery({
    queryKey: ['table-relations', versionId],
    queryFn: () => tableRelations(versionId),
    enabled: known,
  });

  const structure = useQuery({
    queryKey: ['template-version', versionId],
    queryFn: () =>
      apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${String(versionId)}/structure`),
    enabled: known,
  });

  const tables = useMemo(() => tableOptions(structure.data), [structure.data]);

  // ⛔ Чи заморожена версія, каже СЕРВЕР — і каже це в КОНВЕРТІ, а не в
  // кожному зв'язку. Версія без жодного зв'язку — найчастіший випадок
  // (механізм опційний), і поелементна відповідь на ньому мовчала б: кнопка
  // «новий зв'язок» стояла б на опублікованій версії, де сервер однаково
  // відмовить (`ECR-TMPL-0409`). Показана й непрацездатна кнопка гірша за
  // відсутню.
  const frozen = relations.data !== undefined && !relations.data.isEditable;
  const editable = mayEdit && !frozen;

  const save = useMutation({
    mutationFn: (next: RelationDraft) => saveTableRelation(versionId, next),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['table-relations', versionId] });
      setDraft(null);
      showDone(t('tables.relationSaved'));
    },
    onError: showApiError,
  });

  const remove = useMutation({
    mutationFn: (code: string) => deleteTableRelation(versionId, code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['table-relations', versionId] });
      showDone(t('tables.relationDeleted'));
    },
    onError: showApiError,
  });

  return (
    <Stack gap="md">
      <PageHeader
        title={t('tables.relationsTitle')}
        actions={
          editable && (
            <Button variant="default" onClick={() => setDraft(emptyDraft())}>
              {t('tables.newRelation')}
            </Button>
          )
        }
      />

      {/* ⚠ Пояснення стоїть на екрані, а не в довідці: порожній перелік тут
          означає «таблиці незалежні», а не «ще не налаштували», і без цього
          рядка перший читач шукав би, чого бракує. */}
      <Alert color="blue">{t('tables.optionalHint')}</Alert>

      {frozen && <Alert color="yellow" title={t('tables.readOnly')}>{t('tables.readOnlyHint')}</Alert>}

      <AsyncBoundary<TableRelationDto[]>
        isPending={relations.isPending && known}
        error={relations.error}
        data={known ? relations.data?.relations : []}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('tables.noRelations')}
        emptyHint={t('tables.noRelationsHint')}
        skeleton="table"
        onRetry={() => void relations.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('tables.relationCode')}</Table.Th>
                <Table.Th>{t('tables.relationKind')}</Table.Th>
                <Table.Th>{t('tables.sourceTable')}</Table.Th>
                <Table.Th>{t('tables.targetTable')}</Table.Th>
                <Table.Th>{t('tables.onSourceChange')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((relation) => (
                <Table.Tr key={relation.id}>
                  <Table.Td>
                    <Group gap="xs">
                      <Text>{relation.code}</Text>
                      {!relation.isActive && (
                        <Badge variant="light" color="gray">
                          {t('tables.inactive')}
                        </Badge>
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>{kindLabel(relation.relationKind)}</Table.Td>
                  <Table.Td>{relation.sourceTableCode}</Table.Td>
                  <Table.Td>{relation.targetTableCode}</Table.Td>
                  <Table.Td>{onSourceChangeLabel(relation.onSourceChange)}</Table.Td>
                  <Table.Td>
                    {editable && (
                      <Group gap="xs">
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          onClick={() => setDraft(draftOf(relation))}
                        >
                          {t('tables.editRelation')}
                        </Button>
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          color="statusError"
                          loading={remove.isPending}
                          onClick={() => remove.mutate(relation.code)}
                        >
                          {t('tables.deleteRelation')}
                        </Button>
                      </Group>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      {draft !== null && (
        <Paper withBorder p="md">
          <Stack gap="sm">
            <Text fw={600}>{t('tables.relationForm')}</Text>

            <RelationForm
              draft={draft}
              tables={tables}
              disabled={!editable}
              saving={save.isPending}
              onChange={setDraft}
              onSubmit={() => save.mutate(draft)}
            />
          </Stack>
        </Paper>
      )}
    </Stack>
  );
}
