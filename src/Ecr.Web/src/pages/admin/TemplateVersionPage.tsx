import { useState, type JSX } from 'react';
import { Accordion, Badge, Button, Group, Modal, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type {
  CloneVersionRequest,
  DeprecateVersionRequest,
  TemplateColumnDto,
  TemplateStructureDto,
  VersionIdResponse,
} from '@/api/types';
import { AccessMatrix } from '@/features/templates/AccessMatrix';
import { PresentationEditor } from '@/features/templates/PresentationEditor';
import { SheetEditor } from '@/features/templates/SheetEditor';
import { deleteSheet, saveSheet } from '@/features/templates/sheetApi';
import { draftOf, emptyDraft, type SheetDraft } from '@/features/templates/sheet';
import { TableEditor } from '@/features/templates/TableEditor';
import { deleteTable, saveTable } from '@/features/templates/tableApi';
import {
  draftOf as tableDraftOf,
  emptyDraft as emptyTableDraft,
  type TableDraft,
} from '@/features/templates/table';
import { VersionDiff } from '@/features/templates/VersionDiff';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Редактор структури версії.
 *
 * ⛔ Опублікована версія структурно незмінна — це тримає тригер у базі, а не
 * лише інтерфейс. Кнопка публікації ховається не «щоб не заплутати», а тому
 * що сервер однаково відхилить: показана й непрацездатна кнопка гірша за
 * відсутню.
 *
 * ⛔ Шапка (`PageHeader`) рендериться ЗАВЖДИ, включно з версією без жодного
 * аркуша: до цього вона стояла всередині `AsyncBoundary`, і порожня версія
 * лишалася без жодної кнопки взагалі — глухий кут (`S-03`). `AsyncBoundary`
 * тепер стосується лише переліку аркушів і таблиць нижче.
 */
export function TemplateVersionPage(): JSX.Element {
  const { id: templateId, versionId } = useParams();
  const id = Number(versionId);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const session = useSession();

  const [cloning, setCloning] = useState(false);
  const [newVersion, setNewVersion] = useState('');
  const [deprecating, setDeprecating] = useState(false);
  const [editing, setEditing] = useState<TemplateColumnDto | null>(null);
  const [sheetDraft, setSheetDraft] = useState<SheetDraft | null>(null);

  // ⚠ Чернетка таблиці несе код аркуша окремо від самого `TableDraft`
  // (W5.1): таблиця адресується ДВОМА кодами (`sheets/{sheetCode}/tables/{code}`),
  // а форма керує лише другим — код аркуша задає контекст, у якому її
  // відкрили, і сам не редагується.
  const [tableDraft, setTableDraft] = useState<{ sheetCode: string; draft: TableDraft } | null>(
    null,
  );

  const structure = useQuery({
    queryKey: ['template-version', id],
    queryFn: () => apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${id}/structure`),
  });

  const publish = useMutation({
    mutationFn: () => apiFetch(`/api/v1/template-versions/${id}/publish`, { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-version', id] });
      showDone(t('version.published'));
    },

    // ⚠ Публікація падає з переліком проблем структури: показуємо саме його,
    // а не «не вдалося опублікувати».
    onError: showApiError,
  });

  /**
   * Клон версії (`ФВ-2.8`).
   *
   * ⛔ Єдиний спосіб внести СТРУКТУРНУ зміну в опубліковану версію: вона
   * заморожена тригером у базі, і кнопки «розморозити» не існує навмисно.
   * До аудиту клону не було в інтерфейсі зовсім (`A7-39`), тобто після першої
   * ж публікації шаблон ставав незмінним назавжди.
   *
   * ⚠ `Code` і `RowKey` зберігаються при клонуванні — інакше формули клону
   * посилалися б у порожнечу.
   */
  const clone = useMutation({
    mutationFn: () =>
      apiFetch<VersionIdResponse>(`/api/v1/template-versions/${id}/clone`, {
        method: 'POST',
        body: JSON.stringify({ newVersion: newVersion.trim() } satisfies CloneVersionRequest),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['template-versions'] });
      setCloning(false);
      setNewVersion('');
      showDone(t('version.cloned'));

      // Одразу відкриваємо клон: інакше користувач лишається на замороженій
      // версії й шукає нову в переліку.
      await navigate(`/admin/templates/${templateId ?? ''}/versions/${result.versionId}`);
    },
    onError: showApiError,
  });

  /**
   * Виведення версії з обігу (`ФВ-7.8`).
   *
   * ⛔ Це відкат БЕЗ видалення: на версію посилаються проєкти, подані
   * форми, зрізи звітності й аудит. Стан `Deprecated` існував від Етапу 1
   * і був недосяжний — перевести версію в нього не міг ніхто, тобто
   * єдиним «відкатом» лишалося видалення.
   *
   * ⚠ Проєкти, прив'язані до цієї версії, працюють далі: інакше відкат
   * зупинив би заповнення форм посеред періоду (`ФВ-1.2`).
   */
  const deprecate = useMutation({
    mutationFn: (reason: string) =>
      apiFetch(`/api/v1/template-versions/${id}/deprecate`, {
        method: 'POST',
        body: JSON.stringify({ reason } satisfies DeprecateVersionRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-versions'] });
      setDeprecating(false);
      showDone(t('version.deprecated'));
    },
    onError: showApiError,
  });

  /**
   * Запис аркуша (`ФВ-2.1`) — перший вертикальний зріз авторства структури
   * шаблону через API.
   */
  const saveSheetMutation = useMutation({
    mutationFn: (draft: SheetDraft) => saveSheet(id, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-version', id] });
      setSheetDraft(null);
      showDone(t('sheets.saved'));
    },
    onError: showApiError,
  });

  /** Видалення аркуша — м'яко, `ФВ-7.6`. */
  const deleteSheetMutation = useMutation({
    mutationFn: (code: string) => deleteSheet(id, code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-version', id] });
      showDone(t('sheets.deleted'));
    },
    onError: showApiError,
  });

  /**
   * Запис таблиці (`W5.1`) — другий вертикальний зріз авторства структури
   * шаблону через API, той самий патерн, що й аркуш вище.
   */
  const saveTableMutation = useMutation({
    mutationFn: (args: { sheetCode: string; draft: TableDraft }) =>
      saveTable(id, args.sheetCode, args.draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-version', id] });
      setTableDraft(null);
      showDone(t('tableDef.saved'));
    },
    onError: showApiError,
  });

  /** Видалення таблиці — м'яко, `ФВ-7.6`. */
  const deleteTableMutation = useMutation({
    mutationFn: (args: { sheetCode: string; code: string }) =>
      deleteTable(id, args.sheetCode, args.code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-version', id] });
      showDone(t('tableDef.deleted'));
    },
    onError: showApiError,
  });

  // ⚠ Структура не несе статусу версії: його віддає перелік версій шаблону.
  // Тому кнопка публікації тут показується за правом, а сервер лишається
  // єдиним, хто вирішує, чи можна публікувати саме цю версію.
  const editable = true;

  // ⛔ Форма аркуша, на відміну від кнопки публікації вище, ХОВАЄТЬСЯ на
  // опублікованій версії: `isEditable` рахує СЕРВЕР (той самий прапорець,
  // що й `TableRelationsDto.isEditable` на сторінці зв'язків) — інакше
  // показана форма означала б обіцянку, яку `ECR-TMPL-0409` однаково не
  // виконає.
  const canEditSheets = can(session.data, 'Template.Edit') && (structure.data?.isEditable ?? false);

  const nextOrdinal = (() => {
    const sheets = structure.data?.sheets ?? [];
    return sheets.length === 0 ? 0 : Math.max(...sheets.map((s) => s.ordinal)) + 1;
  })();

  return (
    <>
      <PageHeader
        title={`${t('version.title')} ${String(id)}`}
        actions={
          <Group gap="xs">
            {structure.data !== undefined && (
              <Text size="xs" c="dimmed">
                r{structure.data.presentationRevision}
              </Text>
            )}
            {/* ⚠ Порівняння версій доступне за правом ПЕРЕГЛЯДУ: питання
                «що зміниться» законне й для того, хто нічого не править —
                саме з нього починається рішення про міграцію. */}
            {can(session.data, 'Template.View') && <VersionDiff templateVersionId={id} />}

            {/* ⛔ Матриця доступу (`ФВ-2.18`) — теж право ПЕРЕГЛЯДУ: питання
                «які періоди відкриті на цьому аркуші» законне для всіх, і
                саме воно найчастіше й з'ясовується постфактум, коли форму
                вже не заповнити. */}
            {can(session.data, 'Template.View') && <AccessMatrix templateVersionId={id} />}

            {/* ⛔ Зв'язки таблиць (`ФВ-2.12`, `ФВ-2.13`) — окрема сторінка, і
                вхід у неї стоїть саме тут: питання «звідки в цій таблиці
                числа» ставлять, дивлячись на структуру. Без цього посилання
                редактор існував би лише за адресою, яку треба знати. */}
            {can(session.data, 'Template.View') && (
              <Button
                size="xs"
                variant="default"
                onClick={() =>
                  void navigate(
                    `/admin/templates/${templateId ?? ''}/versions/${String(id)}/relations`,
                  )
                }
              >
                {t('version.relations')}
              </Button>
            )}

            {/* ⛔ Клон — єдиний спосіб змінити структуру після публікації
                (`ФВ-7.1`). Кнопка є завжди, коли є право правити шаблони:
                клонувати чернетку теж законно. */}
            {can(session.data, 'Template.Edit') && (
              <Button size="xs" variant="default" onClick={() => setCloning(true)}>
                {t('version.clone')}
              </Button>
            )}

            {editable && can(session.data, 'Template.Publish') && (
              <>
                <Button size="xs" loading={publish.isPending} onClick={() => publish.mutate()}>
                  {t('version.publish')}
                </Button>

                {/* ⚠ Право те саме, що на публікацію: вивести з обігу —
                    рішення тієї самої ваги, що й випустити. Сервер
                    відмовить, якщо версія ще чернетка. */}
                <Button
                  size="xs"
                  variant="default"
                  color="red"
                  onClick={() => setDeprecating(true)}
                >
                  {t('version.deprecate')}
                </Button>
              </>
            )}
          </Group>
        }
      />

      {/*
       * ⚠ Версія без аркушів — окремий стан: опублікувати таку не можна, і
       * дізнатися про це з відмови публікації гірше, ніж побачити на екрані.
       * `emptyAction` дає ЗІ ВХОДУ вихід із цього стану: кнопка «додати
       * аркуш» показана й тут — інакше порожня версія лишалася б глухим
       * кутом (`S-03`), бо самого переліку немає, чого показувати.
       */}
      <AsyncBoundary<TemplateStructureDto>
        isPending={structure.isPending}
        error={structure.error}
        data={structure.data}
        isEmpty={(version) => version.sheets.length === 0}
        emptyTitle={t('version.empty')}
        emptyHint={t('version.emptyHint')}
        emptyAction={
          canEditSheets && (
            <Button onClick={() => setSheetDraft(emptyDraft(0))}>{t('sheets.add')}</Button>
          )
        }
        skeleton="form"
        onRetry={() => void structure.refetch()}
      >
        {(version) => (
          <>
            {canEditSheets && (
              <Group justify="flex-end" mb="xs">
                <Button
                  variant="default"
                  onClick={() => setSheetDraft(emptyDraft(nextOrdinal))}
                >
                  {t('sheets.add')}
                </Button>
              </Group>
            )}

            <Accordion multiple>
              {[...version.sheets]
                .sort((a, b) => a.ordinal - b.ordinal)
                .map((sheet) => (
                  <Accordion.Item key={sheet.id} value={sheet.code}>
                    <Accordion.Control>
                      {localized(sheet.nameL10n)}{' '}
                      <Text span c="dimmed">
                        ({sheet.code})
                      </Text>
                      {!sheet.isVisible && (
                        <Badge ml="xs" size="xs" variant="outline">
                          {t('version.hidden')}
                        </Badge>
                      )}
                    </Accordion.Control>
                    <Accordion.Panel>
                      {canEditSheets && (
                        <Group gap="xs" mb="sm">
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            onClick={() => setSheetDraft(draftOf(sheet))}
                          >
                            {t('sheets.edit')}
                          </Button>
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            color="red"
                            loading={deleteSheetMutation.isPending}
                            onClick={() => deleteSheetMutation.mutate(sheet.code)}
                          >
                            {t('sheets.delete')}
                          </Button>
                        </Group>
                      )}

                      {canEditSheets && (
                        <Group justify="flex-end" mb="xs">
                          <Button
                            size="compact-xs"
                            variant="default"
                            onClick={() =>
                              setTableDraft({
                                sheetCode: sheet.code,
                                draft: emptyTableDraft(
                                  sheet.tables.length === 0
                                    ? 0
                                    : Math.max(...sheet.tables.map((t2) => t2.ordinal)) + 1,
                                ),
                              })
                            }
                          >
                            {t('tableDef.add')}
                          </Button>
                        </Group>
                      )}

                      {sheet.tables.map((table) => (
                        <div key={table.id}>
                          <Group gap="xs" mt="sm">
                            <Text fw={600}>
                              {localized(table.nameL10n) || table.code}{' '}
                              <Text span c="dimmed">
                                ({table.code}) · {table.rowMode}
                              </Text>
                            </Text>
                            {canEditSheets && (
                              <>
                                <Button
                                  size="compact-xs"
                                  variant="subtle"
                                  onClick={() =>
                                    setTableDraft({
                                      sheetCode: sheet.code,
                                      draft: tableDraftOf(table),
                                    })
                                  }
                                >
                                  {t('tableDef.edit')}
                                </Button>
                                <Button
                                  size="compact-xs"
                                  variant="subtle"
                                  color="red"
                                  loading={
                                    deleteTableMutation.isPending &&
                                    deleteTableMutation.variables?.code === table.code
                                  }
                                  onClick={() =>
                                    deleteTableMutation.mutate({
                                      sheetCode: sheet.code,
                                      code: table.code,
                                    })
                                  }
                                >
                                  {t('tableDef.delete')}
                                </Button>
                              </>
                            )}
                          </Group>
                          <Table striped withTableBorder mt="xs">
                            <Table.Thead>
                              <Table.Tr>
                                <Table.Th>{t('version.column')}</Table.Th>
                                <Table.Th>{t('version.type')}</Table.Th>
                                <Table.Th>{t('version.unit')}</Table.Th>
                                <Table.Th />
                              </Table.Tr>
                            </Table.Thead>
                            <Table.Tbody>
                              {table.columns.map((column) => (
                                <Table.Tr key={column.id}>
                                  <Table.Td>
                                    {localized(column.headerL10n) || column.code}{' '}
                                    <Text span c="dimmed">
                                      ({column.code})
                                    </Text>
                                    {column.isHidden && (
                                      <Badge ml="xs" size="xs" variant="outline">
                                        {t('version.hidden')}
                                      </Badge>
                                    )}
                                  </Table.Td>
                                  <Table.Td>
                                    {column.dataType}
                                    {column.isReadOnly && (
                                      <Badge ml="xs" size="xs" variant="light">
                                        {t('version.readOnly')}
                                      </Badge>
                                    )}
                                  </Table.Td>
                                  <Table.Td>{column.unitSymbol ?? '—'}</Table.Td>
                                  <Table.Td>
                                    {/* ⚠ Правка тут не потребує нової версії: підпис,
                                        порядок, формат і видимість — презентаційний
                                        шар, і його дозволено міняти в опублікованій
                                        версії (`ФВ-7.2`). */}
                                    {can(session.data, 'Template.Edit') && (
                                      <Button
                                        size="compact-xs"
                                        variant="subtle"
                                        onClick={() => setEditing(column)}
                                      >
                                        {t('version.presentation')}
                                      </Button>
                                    )}
                                  </Table.Td>
                                </Table.Tr>
                              ))}
                            </Table.Tbody>
                          </Table>
                        </div>
                      ))}
                    </Accordion.Panel>
                  </Accordion.Item>
                ))}
            </Accordion>
          </>
        )}
      </AsyncBoundary>

      <PresentationEditor
        templateVersionId={id}
        column={editing}
        onClose={() => setEditing(null)}
      />

      <ReasonModal
        opened={deprecating}
        title={t('version.deprecate')}
        label={t('workflow.reason')}
        description={t('version.deprecateHint')}
        confirmLabel={t('version.deprecate')}
        isPending={deprecate.isPending}
        onConfirm={(reason) => deprecate.mutate(reason)}
        onClose={() => setDeprecating(false)}
      />

      <Modal opened={cloning} onClose={() => setCloning(false)} title={t('version.clone')}>
        <TextInput
          label={t('templates.versionNumber')}
          description={t('version.cloneHint')}
          value={newVersion}
          onChange={(event) => setNewVersion(event.currentTarget.value)}
          data-autofocus
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCloning(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={newVersion.trim().length === 0}
            loading={clone.isPending}
            onClick={() => clone.mutate()}
          >
            {t('version.clone')}
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={sheetDraft !== null}
        onClose={() => setSheetDraft(null)}
        title={sheetDraft?.isNew === true ? t('sheets.add') : t('sheets.edit')}
      >
        {sheetDraft !== null && (
          <SheetEditor
            draft={sheetDraft}
            disabled={!canEditSheets}
            saving={saveSheetMutation.isPending}
            onChange={setSheetDraft}
            onSubmit={() => saveSheetMutation.mutate(sheetDraft)}
            onCancel={() => setSheetDraft(null)}
          />
        )}
      </Modal>

      <Modal
        opened={tableDraft !== null}
        onClose={() => setTableDraft(null)}
        title={tableDraft?.draft.isNew === true ? t('tableDef.add') : t('tableDef.edit')}
      >
        {tableDraft !== null && (
          <TableEditor
            draft={tableDraft.draft}
            disabled={!canEditSheets}
            saving={saveTableMutation.isPending}
            onChange={(draft) => setTableDraft({ sheetCode: tableDraft.sheetCode, draft })}
            onSubmit={() => saveTableMutation.mutate(tableDraft)}
            onCancel={() => setTableDraft(null)}
          />
        )}
      </Modal>
    </>
  );
}
