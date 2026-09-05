import type { JSX } from 'react';
import { Badge, Table } from '@mantine/core';
import { useQueries, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { TemplatePage, TemplateVersionSummary } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
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

  return (
    <>
      <PageHeader title={t('templates.title')} />
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
                    {(versionQueries[index]?.data ?? []).map((version) => (
                      <Badge
                        key={version.id}
                        mr="xs"
                        variant={version.status === 'Published' ? 'filled' : 'light'}
                        component={Link}
                        to={`/admin/templates/${template.id}/versions/${version.id}`}
                        style={{ cursor: 'pointer' }}
                      >
                        {version.version} · {version.status} · r{version.presentationRevision}
                      </Badge>
                    ))}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </>
  );
}
