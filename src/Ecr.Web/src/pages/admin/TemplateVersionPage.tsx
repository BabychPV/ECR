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

  // ⚠ Структура не несе статусу версії: його віддає перелік версій шаблону.
  // Тому кнопка публікації тут показується за правом, а сервер лишається
  // єдиним, хто вирішує, чи можна публікувати саме цю версію.
  const editable = true;

  return (
    /*
     * ⛔ Обгортка навколо ВСЬОГО екрана, включно із заголовком: заголовок
     * містить номер версії, тобто теж дані. Показати шапку з порожнім місцем
     * замість номера — це той самий стан «наче все гаразд» на недоступних
     * даних (`ФВ-14.22`).
     *
     * ⚠ Версія без аркушів — окремий стан: опублікувати таку не можна, і
     * дізнатися про це з відмови публікації гірше, ніж побачити на екрані.
     */
    <AsyncBoundary<TemplateStructureDto>
      isPending={structure.isPending}
      error={structure.error}
      data={structure.data}
      isEmpty={(version) => version.sheets.length === 0}
      emptyTitle={t('version.empty')}
      emptyHint={t('version.emptyHint')}
      skeleton="form"
      onRetry={() => void structure.refetch()}
    >
      {(version) => (
      <>
      <PageHeader
        title={`${t('version.title')} ${version.templateVersionId}`}
        actions={
          <Group gap="xs">
            <Text size="xs" c="dimmed">
              r{version.presentationRevision}
            </Text>
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
              </Accordion.Control>
              <Accordion.Panel>
                {sheet.tables.map((table) => (
                  <div key={table.id}>
                    <Text fw={600} mt="sm">
                      {table.code} · {table.rowMode}
                    </Text>
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
      </>
      )}
    </AsyncBoundary>
  );
}
