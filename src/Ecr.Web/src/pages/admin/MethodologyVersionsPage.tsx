import { useMemo, useState, type JSX } from 'react';
import {
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import type {
  MethodologyDraftVersionDto,
  MethodologyFormulaDto,
  UnitRef,
} from '@/api/types';
import { apiFetch } from '@/api/client';
import { ExpressionEditor } from '@/features/expressions/ExpressionEditor';
import type { ExpressionPlacement } from '@/features/expressions/api';
import {
  createMethodologyVersion,
  deleteMethodologyFormula,
  methodologyFormulas,
  methodologyVersions,
  saveMethodologyFormula,
} from '@/features/methodologies/api';
import {
  defaultVersion,
  mayEditContent,
  type FormulaDraft,
} from '@/features/methodologies/draft';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';

/**
 * Конфігуратор версії методології: формули чернетки (`ФВ-9.15`).
 *
 * ⛔ Екран існує заради одного правила, і воно ж робить його небезпечним:
 * **чернетку правлять, опубліковану — ні** (`ФВ-9.1`, `ФВ-13.2`). Опублікована
 * версія рахує числа, які вже подані регуляторові; правка в ній не має ні
 * diff-у, ні публікації, ні сліду — вона просто змінює минуле. Тому екран
 * показує опубліковану версію **тільки на читання**, а «змінити» пропонує
 * єдиним чинним способом — клоном у нову чернетку.
 *
 * ⚠ Заборону тримає не ця форма. Сервер відхиляє правку опублікованої версії
 * доменом (`MethodologyVersion.EditFormula`, `ECR-CALC-0409`); тут лише
 * показано, чому кнопки немає. Форма, яка була б єдиною перевіркою, впала б
 * від першого прямого запиту.
 *
 * ⚠ Редактор виразів — той самий компонент, що й на сторінці виразів
 * (`ФВ-9.15a`, `D-113`): діалект у нього параметр. Другий редактор означав би
 * дві розбіжні відповіді на питання «що тут можна написати».
 */
export function MethodologyVersionsPage(): JSX.Element {
  const params = useParams();
  const methodologyId = Number(params['id']);
  const known = Number.isFinite(methodologyId);

  const queryClient = useQueryClient();
  const session = useSession();
  const mayEdit = can(session.data, 'Calculation.EditFormula');

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newVersion, setNewVersion] = useState('');
  const [copyFrom, setCopyFrom] = useState<string | null>(null);
  const [editing, setEditing] = useState<FormulaDraft | null>(null);

  const versions = useQuery({
    queryKey: ['methodology-versions', methodologyId],
    queryFn: () => methodologyVersions(methodologyId),
    enabled: known,
  });

  const all = useMemo(() => versions.data ?? [], [versions.data]);

  // ⚠ Обрана версія — стан, але за замовчуванням береться ЧЕРНЕТКА, а не
  // перша в переліку: екран існує заради редагування, і відкривати його на
  // версії, яку не можна правити, означало б щоразу починати з глухого кута.
  const selected = useMemo(() => defaultVersion(all, selectedId), [all, selectedId]);

  // ⚠ Дві умови разом: стан версії каже сервер, право — профіль. Кожна окремо
  // веде користувача у відмову — `ECR-CALC-0409` або `403`.
  const editable = mayEditContent(selected, mayEdit);

  const formulas = useQuery({
    queryKey: ['methodology-formulas', selected?.id],
    queryFn: () => methodologyFormulas(methodologyId, selected?.id ?? 0),
    enabled: known && selected !== undefined,
  });

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: 60 * 60 * 1000,
  });

  const create = useMutation({
    mutationFn: () =>
      createMethodologyVersion(methodologyId, {
        versionNumber: newVersion,
        copyFromVersionId: copyFrom === null ? null : Number(copyFrom),

        // ⚠ Рівень має значення лише для ПОРОЖНЬОЇ чернетки: клон бере його з
        // джерела. Тому вибору рівня тут немає — він був би питанням без
        // наслідку у вісімдесяти відсотках випадків.
        level: 'Configuration',
      }),
    onSuccess: async (draft) => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-versions', methodologyId] });
      setSelectedId(String(draft.id));
      setCreating(false);
      setNewVersion('');
      showDone(t('methodologies.versionCreated'));
    },
    onError: showApiError,
  });

  const save = useMutation({
    mutationFn: (draft: FormulaDraft) => saveMethodologyFormula(methodologyId, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-formulas'] });
      setEditing(null);
      showDone(t('methodologies.formulaSaved'));
    },
    onError: showApiError,
  });

  const remove = useMutation({
    mutationFn: (target: { versionId: number; code: string }) =>
      deleteMethodologyFormula(methodologyId, target.versionId, target.code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodology-formulas'] });
      showDone(t('methodologies.formulaDeleted'));
    },
    onError: showApiError,
  });

  const placement = useMemo<ExpressionPlacement>(
    () => ({ methodologyVersionId: editing?.versionId }),
    [editing?.versionId],
  );

  return (
    <Stack gap="md">
      <PageHeader
        title={t('methodologies.versionsTitle')}
        actions={
          mayEdit && (
            <Button variant="default" onClick={() => setCreating(true)}>
              {t('methodologies.newVersion')}
            </Button>
          )
        }
      />

      <AsyncBoundary<MethodologyDraftVersionDto[]>
        isPending={versions.isPending && known}
        error={versions.error}
        data={known ? versions.data : []}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noVersions')}
        emptyHint={t('methodologies.noVersionsHint')}
        skeleton="table"
        onRetry={() => void versions.refetch()}
      >
        {(list) => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.version')}</Table.Th>
                <Table.Th>{t('methodologies.status')}</Table.Th>
                <Table.Th>{t('methodologies.modes')}</Table.Th>
                <Table.Th>{t('methodologies.effectiveFrom')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((version) => (
                <Table.Tr key={version.id}>
                  <Table.Td>{version.versionNumber}</Table.Td>
                  <Table.Td>
                    <Badge variant={version.isEditable ? 'light' : 'filled'}>{version.status}</Badge>
                  </Table.Td>
                  <Table.Td>
                    {/* ⚠ Режими стоять поруч зі статусом, а не в налаштуваннях:
                        саме вони визначають числа (`ФВ-9.9`, `ФВ-16.11`), і саме
                        їх клон переносить незмінними. */}
                    <Text size="sm" c="dimmed">
                      {version.numericMode} · {version.calendarMode} · {version.traceLevel}
                    </Text>
                  </Table.Td>
                  <Table.Td>{version.effectiveFrom ?? '—'}</Table.Td>
                  <Table.Td>
                    <Button
                      size="compact-xs"
                      variant={selected?.id === version.id ? 'filled' : 'subtle'}
                      onClick={() => setSelectedId(String(version.id))}
                    >
                      {t('methodologies.openVersion')}
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      {selected !== undefined && !selected.isEditable && (
        <Alert color="yellow" title={t('methodologies.readOnly')}>
          {t('methodologies.readOnlyHint')}
        </Alert>
      )}

      {selected !== undefined && (
        <>
          <Group justify="space-between">
            <Text fw={600}>{t('methodologies.formulas')}</Text>

            {editable && (
              <Button
                size="compact-sm"
                variant="default"
                onClick={() =>
                  setEditing({
                    versionId: selected.id,
                    code: '',
                    expression: '',
                    resultType: 'Number',
                    outputUnitId: null,
                    isNew: true,
                  })
                }
              >
                {t('methodologies.addFormula')}
              </Button>
            )}
          </Group>

          <AsyncBoundary<MethodologyFormulaDto[]>
            isPending={formulas.isPending}
            error={formulas.error}
            data={formulas.data}
            isEmpty={(list) => list.length === 0}
            emptyTitle={t('methodologies.noFormulas')}
            emptyHint={t('methodologies.noFormulasHint')}
            skeleton="table"
            onRetry={() => void formulas.refetch()}
          >
            {(list) => (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.formulaCode')}</Table.Th>
                    <Table.Th>{t('methodologies.expression')}</Table.Th>
                    <Table.Th>{t('methodologies.resultType')}</Table.Th>
                    <Table.Th>{t('methodologies.evaluationOrder')}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {list.map((formula) => (
                    <Table.Tr key={formula.id}>
                      <Table.Td>{formula.code}</Table.Td>
                      <Table.Td>
                        <Text size="sm" ff="monospace">
                          {formula.expression}
                        </Text>
                      </Table.Td>
                      <Table.Td>{formula.resultType}</Table.Td>
                      {/* ⚠ Порядок ПОКАЗУЄТЬСЯ і не редагується: він
                          топологічний і рахується при публікації (`ФВ-9.4`).
                          Поле вводу тут дозволило б людині зсунути обчислення
                          так, що помилка стала б числом у звіті. */}
                      <Table.Td>{formula.evaluationOrder}</Table.Td>
                      <Table.Td>
                        {editable && (
                          <Group gap="xs">
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              onClick={() =>
                                setEditing({
                                  versionId: selected.id,
                                  code: formula.code,
                                  expression: formula.expression,
                                  resultType: formula.resultType,
                                  outputUnitId: formula.outputUnitId,
                                  isNew: false,
                                })
                              }
                            >
                              {t('methodologies.editFormula')}
                            </Button>
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              color="red"
                              loading={remove.isPending}
                              onClick={() =>
                                remove.mutate({ versionId: selected.id, code: formula.code })
                              }
                            >
                              {t('methodologies.deleteFormula')}
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
        </>
      )}

      <Modal
        opened={creating}
        onClose={() => setCreating(false)}
        title={t('methodologies.newVersionTitle')}
      >
        <Stack gap="sm">
          {/* ⛔ Пояснення стоїть у діалозі, а не в довідці: клон — це не
              «зробити копію», а єдиний спосіб змінити опубліковану версію
              (`ФВ-9.1`), і людина має розуміти, чому інакше не можна. */}
          <Alert color="blue">{t('methodologies.cloneHint')}</Alert>

          <TextInput
            label={t('methodologies.versionNumber')}
            description={t('methodologies.versionNumberHint')}
            value={newVersion}
            onChange={(event) => setNewVersion(event.currentTarget.value)}
            data-autofocus
          />

          <Select
            label={t('methodologies.copyFrom')}
            description={t('methodologies.copyFromHint')}
            placeholder={t('methodologies.emptyDraft')}
            clearable
            value={copyFrom}
            data={all.map((version) => ({
              value: String(version.id),
              label: `${version.versionNumber} · ${version.status}`,
            }))}
            onChange={setCopyFrom}
          />

          <Button
            disabled={newVersion.trim().length === 0}
            loading={create.isPending}
            onClick={() => create.mutate()}
          >
            {t('methodologies.createVersion')}
          </Button>
        </Stack>
      </Modal>

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={t('methodologies.formulaTitle')}
        size="lg"
      >
        {editing !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('methodologies.formulaCode')}
              description={t('methodologies.formulaCodeHint')}
              value={editing.code}
              disabled={!editing.isNew}
              onChange={(event) =>
                setEditing({ ...editing, code: event.currentTarget.value })
              }
            />

            <ExpressionEditor
              value={editing.expression}
              onChange={(value) => setEditing({ ...editing, expression: value })}
              dialect="Methodology"
              placement={placement}
              ariaLabel={t('methodologies.expression')}
              height="140px"
            />

            <Select
              label={t('methodologies.resultType')}
              description={t('methodologies.resultTypeHint')}
              allowDeselect={false}
              value={editing.resultType}
              data={[
                { value: 'Number', label: t('methodologies.resultNumber') },
                { value: 'Text', label: t('methodologies.resultText') },
              ]}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  resultType: value === 'Text' ? 'Text' : 'Number',

                  // ⛔ Текстовий результат не має одиниці: вимір — властивість
                  // числа (`ФВ-16.6`). Лишити її означало б відправити запит,
                  // який сервер відхилить, — і показати відмову там, де вибір
                  // уже зроблено правильно.
                  outputUnitId: value === 'Text' ? null : editing.outputUnitId,
                })
              }
            />

            {editing.resultType === 'Number' && (
              <Select
                label={t('methodologies.outputUnit')}
                description={t('methodologies.outputUnitHint')}
                placeholder={t('methodologies.noUnit')}
                clearable
                searchable
                value={editing.outputUnitId === null ? null : String(editing.outputUnitId)}
                data={(units.data ?? []).map((unit) => ({
                  value: String(unit.id),
                  label: unit.code,
                }))}
                onChange={(value) =>
                  setEditing({
                    ...editing,
                    outputUnitId: value === null ? null : Number(value),
                  })
                }
              />
            )}

            <Button
              disabled={editing.code.trim().length === 0 || editing.expression.trim().length === 0}
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.saveFormula')}
            </Button>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}
