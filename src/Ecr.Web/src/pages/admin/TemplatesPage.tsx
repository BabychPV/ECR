import type { JSX } from 'react';
import { Badge, Loader, Table } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

interface TemplateSummary {
  id: number;
  code: string;
  versionCount: number;
}

interface TemplateVersionSummary {
  id: number;
  templateId: number;
  version: string;
  status: string;
  presentationRevision: number;
}

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
    queryFn: () => apiFetch<{ items: TemplateSummary[] }>('/api/v1/templates?limit=100'),
  });

  const versions = useQuery({
    queryKey: ['template-versions'],
    queryFn: () => apiFetch<TemplateVersionSummary[]>('/api/v1/template-versions'),
    enabled: templates.isSuccess,
  });

  return (
    <>
      <PageHeader title={t('templates.title')} />
      <ErrorAlert error={templates.error ?? versions.error} />

      {templates.isPending ? (
        <Loader />
      ) : (
        <Table striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('templates.code')}</Table.Th>
              <Table.Th>{t('templates.versions')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(templates.data?.items ?? []).map((template) => (
              <Table.Tr key={template.id}>
                <Table.Td>{template.code}</Table.Td>
                <Table.Td>
                  {(versions.data ?? [])
                    .filter((version) => version.templateId === template.id)
                    .map((version) => (
                      <Badge
                        key={version.id}
                        mr={4}
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
    </>
  );
}
