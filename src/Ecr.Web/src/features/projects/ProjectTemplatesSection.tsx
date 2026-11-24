import { Fragment, useState, type JSX, type ReactNode } from 'react';
import { Badge, Button, Group, Modal, Select, Stack, Table, Text, VisuallyHidden } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { templatesListQuery, templateVersionsQuery } from '@/features/templates/templateQueries';
import { versionsOfTemplatesQuery } from '@/features/templates/versionsOfTemplates';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { CodeText } from '@/shared/ui/CodeText';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { Hint } from '@/shared/ui/Hint';
import { showDone } from '@/shared/ui/notify';
import {
  attachProjectTemplate,
  detachProjectTemplate,
  fetchProjectTemplates,
  invalidateProjectTemplates,
  projectTemplatesKey,
  type ProjectTemplateDto,
} from './projectTemplatesApi';

/**
 * Чому рядок не можна від'єднати — або `null`, якщо можна.
 *
 * ⚠ Лише очевидне з самого рядка: основний шаблон і шаблон із документами. Решту (власний маршрут погодження,
 * архівний проєкт, гонка з новим документом) каже сервер — відмовою `409 ECR-PRD-0409` з ключем причини.
 */
export function detachBlockedReason(row: ProjectTemplateDto): string | null {
  if (row.isPrimary) return t('err.ECR-PRD-0409.primaryTemplate', { templateCode: row.templateCode });
  if (row.documentCount > 0) return t('projects.templates.detachBlockedDocuments');

  return null;
}

/**
 * Початкова версія для приєднання — остання ОПУБЛІКОВАНА (за `publishedAt`, за рівності — більший `id`).
 *
 * ⚠ Свідомо не імпорт `latestPublishedVersionId` з `CreateProjectModal`: той модуль — лінивий чанк діалогу
 * створення, і статичний імпорт затягнув би його у вкладку Settings (бюджет маршруту `D-132`).
 */
function latestPublished(versions: ReadonlyArray<{ id: number; status: string; publishedAt: string | null }>): string | null {
  let best: { id: number; at: number } | null = null;
  for (const version of versions) {
    if (version.status !== 'Published') continue;
    const parsed = version.publishedAt === null ? Number.NaN : Date.parse(version.publishedAt);
    const at = Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed;
    if (best === null || at > best.at || (at === best.at && version.id > best.id)) best = { id: version.id, at };
  }

  return best === null ? null : String(best.id);
}

/**
 * ✎ 24.11 (рішення людини «Одна таблиця шаблонів»): що вкладка Templates додає до рядка шаблону — колонку
 * погодження, дію «виняток» поруч із «Detach» і розгорнутий під рядком редактор. Дані — власний запит розширення
 * (`GET /projects/{id}/template-routes`), зіставлення з рядком — за `templateId`.
 */
export interface ProjectTemplateExtension {
  /** Заголовок додаткової колонки. */
  readonly header: string;
  /** Вміст колонки для шаблону. */
  readonly cell: (row: ProjectTemplateDto) => ReactNode;
  /** Дія рядка (перед «Detach»). */
  readonly action: (row: ProjectTemplateDto) => ReactNode;
  /** Рядок під шаблоном на всю ширину таблиці; `null` — немає. */
  readonly expansion: (row: ProjectTemplateDto) => ReactNode;
}

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): налаштування проєкту → «Templates». Проєкт «рік · об'єкт» тримає
 * документи КІЛЬКОХ шаблонів: таблиця (код і назва, версія, документів, позначка «Primary»), «Add template» (шаблон
 * і його опублікована версія) і «Detach» з підтвердженням.
 *
 * ⛔ «Detach» недоступний для основного шаблону і для шаблону з документами — з підказкою чому; відмову сервера
 * (маршрут шаблону, архівний проєкт, гонка) показує банер із текстом каталогу (`messageKey`), а не мовчання.
 *
 * ⚠ `readOnly` — архівний проєкт або немає `Project.Manage` + гранта Manage (як решта дій вкладки Settings):
 * тоді лише перелік, без дій.
 */
export function ProjectTemplatesSection({
  projectId,
  readOnly,
  toolbar,
  extension,
}: {
  readonly projectId: number;
  readonly readOnly: boolean;
  /** ✎ 24.11 «Одна таблиця шаблонів»: рядок над таблицею (маршрут погодження проєкту для всіх шаблонів). */
  readonly toolbar?: ReactNode;
  /** ✎ 24.11: колонка погодження, дія «виняток» і рядок-редактор під шаблоном — у ЦІЙ таблиці, а не в другій. */
  readonly extension?: ProjectTemplateExtension | undefined;
}): JSX.Element {
  const queryClient = useQueryClient();

  const templates = useQuery({
    queryKey: projectTemplatesKey(projectId),
    queryFn: () => fetchProjectTemplates(projectId),
  });

  const [attaching, setAttaching] = useState(false);
  const [templateId, setTemplateId] = useState<string | null>(null);
  const [versionId, setVersionId] = useState<string | null>(null);
  const [versionTouched, setVersionTouched] = useState(false);

  const [detaching, setDetaching] = useState<ProjectTemplateDto | null>(null);
  const [detachError, setDetachError] = useState<unknown>(null);

  // Каталог шаблонів і версії — ті самі описи запитів, що в діалозі створення проєкту (`templateQueries.ts`).
  const catalog = useQuery({ ...templatesListQuery, enabled: attaching });
  const versions = useQuery({
    ...templateVersionsQuery(templateId === null ? undefined : Number(templateId)),
    enabled: attaching && templateId !== null,
  });

  const attachedIds = new Set((templates.data ?? []).map((row) => row.templateId));
  // ⚠ Архівний шаблон сервер не приєднає (`ECR-TMPL-0409`), а вже приєднаний — `templateAlreadyAttached`.
  const candidates = (catalog.data?.items ?? []).filter((template) => !template.isArchived && !attachedIds.has(template.id));
  // ✎ B74 UX: як у «Новому проєкті» (`CreateProjectModal`) і майстрі документа — пропонуються лише шаблони з ОПУБЛІКОВАНОЮ
  // версією: шаблон лише з чернеткою лишав обов'язкове поле «Version» порожнім без пояснення. Версії — той самий пул
  // (`versionsOfTemplatesQuery`, ключ `versionsOf(id)`), що й у діалозі створення проєкту.
  const publishedOf = useQuery({
    ...versionsOfTemplatesQuery(queryClient, candidates.map((template) => template.id)),
    enabled: attaching && candidates.length > 0,
  });
  const templateOptions = candidates
    .filter((_, index) => (publishedOf.data?.[index]?.items ?? []).some((version) => version.status === 'Published'))
    .map((template) => {
      const name = localized(template.nameL10n);

      return { value: String(template.id), label: name.length > 0 ? `${template.code} · ${name}` : template.code };
    });

  const versionItems = versions.data?.items ?? [];
  const versionOptions = versionItems
    .filter((version) => version.status === 'Published')
    .map((version) => ({ value: String(version.id), label: version.version }));
  const effectiveVersionId = versionTouched ? versionId : (versionId ?? latestPublished(versionItems));

  const resetAttach = (): void => {
    setTemplateId(null);
    setVersionId(null);
    setVersionTouched(false);
  };

  const attach = useMutation({
    // ⚠ Відмову показує сам діалог (банер нижче) — глобальна сітка тосту не додає.
    meta: { handled: true },
    mutationFn: (target: number) => attachProjectTemplate(projectId, target),
    onSuccess: async () => {
      await invalidateProjectTemplates(queryClient, projectId);
      setAttaching(false);
      resetAttach();
      showDone(t('projects.templates.attached'));
    },
  });

  // ФВ-14.26: спінер — лише якщо запит затримався (без миготіння на швидкій відповіді).
  const attachLoading = usePendingLoading(attach.isPending);

  const detach = useMutation({
    meta: { handled: true },
    mutationFn: (target: number) => detachProjectTemplate(projectId, target),
    onSuccess: async () => {
      setDetaching(null);
      await invalidateProjectTemplates(queryClient, projectId);
      showDone(t('projects.templates.detached'));
    },
    onError: (error) => {
      // Банер над таблицею: відмова каже причину (документи, маршрут, основний шаблон) текстом каталогу.
      setDetaching(null);
      setDetachError(error);
    },
  });

  // ✎ 24.11: колонка дій — «Detach» (керує проєктом) і/або дія розширення («виняток» маршруту).
  const hasActions = !readOnly || extension !== undefined;
  const columnCount = 3 + (extension === undefined ? 0 : 1) + (hasActions ? 1 : 0);

  return (
    <Stack gap="xs" data-project-templates="">
      <Text size="sm" c="dimmed">
        {t('projects.templates.hint')}
      </Text>

      {toolbar}

      {detachError !== null && <ErrorAlert error={detachError} />}

      <AsyncBoundary<ProjectTemplateDto[]>
        isPending={templates.isPending}
        error={templates.error}
        data={templates.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('projects.templates.empty')}
        skeleton="table"
        onRetry={() => void templates.refetch()}
      >
        {(all) => (
          <Table.ScrollContainer minWidth={420}>
            <Table data-testid="project-templates" verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">{t('projects.templates.template')}</Table.Th>
                  <Table.Th scope="col">{t('projects.templates.version')}</Table.Th>
                  <Table.Th scope="col">{t('projects.templates.documents')}</Table.Th>
                  {extension !== undefined && <Table.Th scope="col">{extension.header}</Table.Th>}
                  {hasActions && (
                    <Table.Th scope="col">
                      <VisuallyHidden>
                        {extension === undefined ? t('projects.templates.detach') : t('approvalRoutes.colActions')}
                      </VisuallyHidden>
                    </Table.Th>
                  )}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {all.map((row) => {
                  const name = localized(row.templateNameL10n);
                  const blocked = detachBlockedReason(row);
                  const expansion = extension?.expansion(row) ?? null;

                  return (
                    <Fragment key={row.templateId}>
                      <Table.Tr data-project-template={row.templateCode} data-primary={row.isPrimary}>
                        <Table.Td>
                          {/* ⚠ `wrap`, not `nowrap`: Mantine `Code` has `overflow: auto`, so in a nowrap flex row at 375 px it
                              shrinks below its text and becomes a scroll region without focus (axe
                              scrollable-region-focusable). Wrapping moves «Primary» under the code instead. */}
                          <Group gap="xs" wrap="wrap">
                            <CodeText>{row.templateCode}</CodeText>
                            {row.isPrimary && (
                              <Badge variant="light" data-testid="project-template-primary">
                                {t('projects.templates.primary')}
                              </Badge>
                            )}
                          </Group>
                          {name.length > 0 && (
                            <Text size="xs" c="dimmed">
                              {name}
                            </Text>
                          )}
                        </Table.Td>
                        <Table.Td>{row.version}</Table.Td>
                        <Table.Td data-project-template-documents={row.documentCount}>{row.documentCount}</Table.Td>
                        {extension !== undefined && <Table.Td>{extension.cell(row)}</Table.Td>}
                        {hasActions && (
                          <Table.Td>
                            <Group gap="xs" wrap="wrap">
                              {extension?.action(row)}
                              {!readOnly && (
                                /* ⚠ `aria-disabled`, а не `disabled`: вимкнена кнопка не отримує ні фокуса, ні наведення,
                                   тож підказка «чому ні» була б недосяжна. */
                                <Hint label={blocked ?? ''} disabled={blocked === null}>
                                  <Button
                                    size="xs"
                                    variant="default"
                                    aria-disabled={blocked !== null}
                                    data-disabled={blocked !== null}
                                    aria-label={`${t('projects.templates.detach')} ${row.templateCode}`}
                                    onClick={(event) => {
                                      if (blocked !== null) {
                                        event.preventDefault();
                                        return;
                                      }
                                      setDetachError(null);
                                      setDetaching(row);
                                    }}
                                  >
                                    {t('projects.templates.detach')}
                                  </Button>
                                </Hint>
                              )}
                            </Group>
                          </Table.Td>
                        )}
                      </Table.Tr>
                      {expansion !== null && (
                        <Table.Tr data-project-template-expansion={row.templateCode}>
                          <Table.Td colSpan={columnCount}>{expansion}</Table.Td>
                        </Table.Tr>
                      )}
                    </Fragment>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </AsyncBoundary>

      {!readOnly && (
        <Group>
          <Button
            variant="default"
            onClick={() => {
              attach.reset();
              resetAttach();
              setAttaching(true);
            }}
          >
            {t('projects.templates.attach')}
          </Button>
        </Group>
      )}

      <Modal opened={attaching} onClose={() => setAttaching(false)} title={t('projects.templates.attach')}>
        <Stack gap="xs">
          {(catalog.error ?? publishedOf.error ?? versions.error) !== null && (
            <ErrorAlert
              error={catalog.error ?? publishedOf.error ?? versions.error}
              onRetry={() => {
                void catalog.refetch();
                void publishedOf.refetch();
                if (templateId !== null) void versions.refetch();
              }}
            />
          )}

          <Select
            required
            searchable
            label={t('projects.templates.template')}
            description={t('projects.templates.pickTemplate')}
            data={templateOptions}
            value={templateId}
            onChange={(value) => {
              setTemplateId(value);
              setVersionId(null);
              setVersionTouched(false);
            }}
            data-autofocus
          />

          <Select
            required
            label={t('projects.templates.version')}
            data={versionOptions}
            value={effectiveVersionId}
            disabled={templateId === null}
            onChange={(value) => {
              setVersionTouched(true);
              setVersionId(value);
            }}
          />

          {attach.error !== null && <ErrorAlert error={attach.error} />}

          <Group justify="flex-end" mt="md">
            <Button variant="default" onClick={() => setAttaching(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              disabled={effectiveVersionId === null}
              loading={attachLoading}
              onClick={() => {
                // ⛔ B149 (KIT §6.9): кнопка закритого `Modal` (~200 мс у DOM) не має приєднувати шаблон після «Cancel».
                if (!attaching || attach.isPending || effectiveVersionId === null) return;
                attach.mutate(Number(effectiveVersionId));
              }}
            >
              {t('projects.templates.attachApply')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <ConfirmModal
        opened={detaching !== null}
        title={t('projects.templates.detach')}
        text={detaching === null ? '' : t('projects.templates.detachConfirm', { templateCode: detaching.templateCode })}
        verb={t('projects.templates.detach')}
        cancelLabel={t('common.cancel')}
        danger
        isPending={detach.isPending}
        onConfirm={() => {
          if (detaching === null || detach.isPending) return;
          detach.mutate(detaching.templateId);
        }}
        onClose={() => setDetaching(null)}
      />
    </Stack>
  );
}
