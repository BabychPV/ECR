import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Table, TextInput } from '@mantine/core';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type {
  CreateTemplateRequest,
  CreateTemplateVersionRequest,
  TemplatePage,
  TemplateVersionSummary,
} from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Перелік шаблонів і їхніх версій.
 *
 * ⚠ Опублікована версія структурно **незмінна**: правка вимагає нової версії,
 * а презентаційна зміна лише піднімає `PresentationRevision` (D-16). Тому в
 * переліку видно і статус, і ревізію: без другої незрозуміло, чому кеш
 * оновився без нової версії.
 */
export function TemplatesPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  const [creating, setCreating] = useState(false);
  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});

  // Для якого шаблону заводимо версію; `null` — діалог закритий.
  const [versioning, setVersioning] = useState<number | null>(null);
  const [versionNumber, setVersionNumber] = useState('');

  const templates = useQuery({
    queryKey: ['templates'],
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
  });

  const items = templates.data?.items ?? [];

  // ⚠ Версії читаються ПО ШАБЛОНУ: маршрут контракту —
  // `GET /api/v1/templates/{id}/versions`. До аудиту сторінка била в
  // `/api/v1/template-versions`, якого не існує, і перелік версій був
  // порожній завжди (`A7-03`).
  const versionQueries = useQueries({
    queries: items.map((template) => ({
      queryKey: ['template-versions', template.id],
      queryFn: () =>
        apiFetch<TemplateVersionSummary[]>(`/api/v1/templates/${template.id}/versions`),
    })),
  });

  const versionsError = versionQueries.find((query) => query.error)?.error ?? null;

  /**
   * Створення шаблону (`ФВ-2.1`).
   *
   * ⛔ Дії не було в інтерфейсі зовсім: сторож вважав `POST /templates`
   * досяжним лише тому, що клієнт читає `GET /templates` тією ж адресою
   * (`A7-42`). Тобто перший шаблон системи неможливо було завести інакше, як
   * запитом повз інтерфейс.
   */
  const create = useMutation({
    mutationFn: () =>
      apiFetch<{ templateId: number }>('/api/v1/templates', {
        method: 'POST',
        body: JSON.stringify({ code: code.trim(), nameL10n: name } satisfies CreateTemplateRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['templates'] });
      setCreating(false);
      setCode('');
      setName({});
      showDone(t('templates.created'));
    },
    onError: showApiError,
  });

  /**
   * Створення версії шаблону.
   *
   * ⚠ Клон робиться з ОСТАННЬОЇ версії, якщо вона є: порожня версія поруч із
   * наявною структурою — майже завжди помилка, а не намір. Номер задає
   * людина: `Major.Minor.Patch.Build` несе сенс (`ФВ-2.8`), і вигадувати його
   * за користувача означало б вигадувати клас зміни.
   */
  const createVersion = useMutation({
    mutationFn: (target: { templateId: number; cloneFrom: number | null }) =>
      apiFetch<{ versionId: number }>(`/api/v1/templates/${target.templateId}/versions`, {
        method: 'POST',
        body: JSON.stringify({
          versionNumber: versionNumber.trim(),
          cloneFromVersionId: target.cloneFrom,
        } satisfies CreateTemplateVersionRequest),
      }),
    onSuccess: async (_result, target) => {
      await queryClient.invalidateQueries({ queryKey: ['template-versions', target.templateId] });
      await queryClient.invalidateQueries({ queryKey: ['templates'] });
      setVersioning(null);
      setVersionNumber('');
      showDone(t('templates.versionCreated'));
    },
    onError: showApiError,
  });

  /** Остання версія шаблону — від неї клонується наступна. */
  const latestVersionOf = (templateId: number): number | null => {
    const index = items.findIndex((template) => template.id === templateId);
    const versions = index < 0 ? [] : (versionQueries[index]?.data ?? []);

    return versions.length === 0 ? null : (versions[versions.length - 1]?.id ?? null);
  };

  const editable = can(session.data, 'Template.Edit');

  return (
    <>
      <PageHeader
        title={t('templates.title')}
        actions={
          editable && (
            <Button size="xs" onClick={() => setCreating(true)}>
              {t('templates.create')}
            </Button>
          )
        }
      />
      {/*
       * ⛔ Помилка версій підмішана до помилки переліку навмисно. Інакше
       * шаблони показувалися б, а колонка версій була б порожньою — тобто
       * «версій немає» замість «версії не завантажилися» (ФВ-14.22).
       */}
      <AsyncBoundary<TemplatePage>
        isPending={templates.isPending}
        error={templates.error ?? versionsError}
        data={templates.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('templates.empty')}
        emptyHint={t('templates.emptyHint')}
        skeleton="table"
        onRetry={() => void templates.refetch()}
      >
        {(page) => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('templates.code')}</Table.Th>
                <Table.Th>{t('templates.versions')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {page.items.map((template, index) => (
                <Table.Tr key={template.id}>
                  <Table.Td>{template.code}</Table.Td>
                  <Table.Td>
                    <Group gap="xs">
                      {(versionQueries[index]?.data ?? []).map((version) => (
                        <Badge
                          key={version.id}
                          variant={version.status === 'Published' ? 'filled' : 'light'}
                          component={Link}
                          to={`/admin/templates/${template.id}/versions/${version.id}`}
                          style={{ cursor: 'pointer' }}
                        >
                          {version.version} · {version.status} · r{version.presentationRevision}
                        </Badge>
                      ))}

                      {/* ⛔ Кнопка стоїть у рядку шаблону, а не на окремому
                          екрані: версія завжди належить шаблону, і питання
                          «якому саме» не має виникати. */}
                      {editable && (
                        <Button
                          size="compact-xs"
                          variant="default"
                          onClick={() => setVersioning(template.id)}
                        >
                          {t('templates.newVersion')}
                        </Button>
                      )}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal opened={creating} onClose={() => setCreating(false)} title={t('templates.create')}>
        {/* ⚠ Код — це бізнес-ключ шаблону: за ним на нього посилаються
            проєкти, і змінити його потім не можна. */}
        <TextInput
          label={t('templates.code')}
          description={t('templates.codeHint')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />

        <LocalizedInput
          label={t('templates.name')}
          description={t('templates.nameHint')}
          value={name}
          onChange={setName}
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCreating(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code.trim().length === 0 || !hasAnyText(name)}
            loading={create.isPending}
            onClick={() => create.mutate()}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={versioning !== null}
        onClose={() => setVersioning(null)}
        title={t('templates.newVersion')}
      >
        <TextInput
          label={t('templates.versionNumber')}
          description={t('templates.versionNumberHint')}
          value={versionNumber}
          onChange={(event) => setVersionNumber(event.currentTarget.value)}
          data-autofocus
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setVersioning(null)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={versionNumber.trim().length === 0}
            loading={createVersion.isPending}
            onClick={() => {
              if (versioning !== null) {
                createVersion.mutate({
                  templateId: versioning,
                  cloneFrom: latestVersionOf(versioning),
                });
              }
            }}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
