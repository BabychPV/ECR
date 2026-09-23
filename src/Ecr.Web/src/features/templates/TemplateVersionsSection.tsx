import { useState, type JSX } from 'react';
import { Anchor, Button, Group, Stack, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateVersionPage, TemplateVersionSummary } from '@/api/types';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { NewTemplateVersionModal } from './NewTemplateVersionModal';

/** Адреса сторінки структури версії — та сама, що й у переліку шаблонів. */
export function templateVersionHref(templateId: number, versionId: number): string {
  return `/admin/templates/${String(templateId)}/versions/${String(versionId)}`;
}

/**
 * Версії шаблону на його картці (`U-19`).
 *
 * ⛔ Сторінка шаблону знала МЕНШЕ, ніж рядок переліку, з якого на неї
 * прийшли: у переліку — версії з посиланнями і «New version», на картці —
 * лише код і число залежних. Єдиний шлях до редактора структури був назад
 * через перелік.
 *
 * ⚠ Запит — ТОЙ САМИЙ, що в `TemplatesPage` (ключ `versionsOf`, адреса,
 * `?limit=100`): перехід із переліку на картку бере версії з кешу, а
 * створення версії звідси оновлює обидва екрани однією інвалідацією.
 *
 * ⛔ «New version» — лише з `editable`, і це та сама перевірка
 * `can(session, 'Template.Edit')`, що й у переліку; вирішує її сторінка, а не
 * цей компонент, щоб правило жило в одному місці на екран.
 *
 * ⚠ Кнопка `variant="default"`: головна дія картки — «Rename template» у
 * шапці, а `L1` дозволяє на екрані рівно одну `filled`.
 */
export function TemplateVersionsSection({
  templateId,
  editable,
}: {
  templateId: number;
  editable: boolean;
}): JSX.Element {
  const [creating, setCreating] = useState(false);

  const versions = useQuery({
    queryKey: queryKeys.templates.versionsOf(templateId),
    queryFn: () =>
      apiFetch<TemplateVersionPage>(`/api/v1/templates/${String(templateId)}/versions?limit=100`),
  });

  const items = versions.data?.items;

  /** Остання версія — від неї клонується наступна (правило `TemplatesPage`). */
  const latest = items === undefined || items.length === 0 ? null : (items[items.length - 1]?.id ?? null);

  const columns: readonly DataTableColumn<TemplateVersionSummary>[] = [
    {
      key: 'version',
      label: t('templates.versionNumber'),
      render: (version) => (
        <Anchor component={Link} size="sm" to={templateVersionHref(templateId, version.id)}>
          {version.version} · r{version.presentationRevision}
        </Anchor>
      ),
      sortValue: (version) => version.version,
    },
    {
      key: 'status',
      label: t('templates.versionStatus'),
      render: (version) => <StatusBadge kind="version" state={version.status} quiet />,
      sortable: false,
    },
    {
      key: 'publishedAt',
      label: t('templates.versionPublishedAt'),
      render: (version) => <Timestamp value={version.publishedAt} />,
      sortValue: (version) => version.publishedAt ?? '',
    },
  ];

  return (
    <Stack gap="xs" data-template-versions>
      <Group justify="space-between">
        <Title order={4}>{t('templates.versions')}</Title>

        {editable && (
          <Button size="xs" variant="default" onClick={() => setCreating(true)}>
            {t('templates.newVersion')}
          </Button>
        )}
      </Group>

      <DataTable<TemplateVersionSummary>
        columns={columns}
        rows={items}
        rowKey={(version) => String(version.id)}
        isPending={versions.isPending}
        error={versions.error}
        emptyTitle={t('templates.versionsEmpty')}
        emptyHint={t('templates.versionsEmptyHint')}
        onRetry={() => void versions.refetch()}
      />

      <NewTemplateVersionModal
        templateId={creating ? templateId : null}
        cloneFrom={latest}
        onClose={() => setCreating(false)}
      />
    </Stack>
  );
}
