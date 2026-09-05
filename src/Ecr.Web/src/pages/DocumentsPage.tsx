import { useState, type JSX } from 'react';
import { Badge, Button, Group, Loader, NumberInput, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { DocumentPage } from '@/api/types';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Перелік документів.
 *
 * ⚠ Період обов'язковий для показу стану: без нього «стан документа» не
 * визначений — аркуші за різні періоди бувають у різних станах одночасно
 * (D-93). Тому колонка стану порожня, доки період не обрано, а не показує
 * бадж «Draft», якому ніхто не зможе довіряти.
 */
export function DocumentsPage(): JSX.Element {
  const [periodKey, setPeriodKey] = useState<number | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);

  const query = useQuery({
    queryKey: ['documents', periodKey, cursor],
    queryFn: () =>
      apiFetch<DocumentPage>(
        `/api/v1/documents?limit=50` +
          (periodKey === null ? '' : `&periodKey=${periodKey}`) +
          (cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`),
      ),
  });

  return (
    <>
      <PageHeader
        title={t('documents.title')}
        actions={
          <NumberInput
            size="xs"
            w={150}
            placeholder={t('documents.period')}
            value={periodKey ?? ''}
            onChange={(value) => {
              setPeriodKey(typeof value === 'number' ? value : null);
              setCursor(null);
            }}
          />
        }
      />

      <ErrorAlert error={query.error} />

      {query.isPending ? (
        <Loader />
      ) : (
        <>
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('documents.key')}</Table.Th>
                <Table.Th>{t('documents.project')}</Table.Th>
                <Table.Th>{t('documents.sheets')}</Table.Th>
                <Table.Th>{t('documents.state')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(query.data?.items ?? []).map((document) => (
                <Table.Tr key={document.id}>
                  <Table.Td>
                    <Link to={`/documents/${document.id}`}>{document.businessKey}</Link>
                  </Table.Td>
                  <Table.Td>{document.projectId}</Table.Td>
                  <Table.Td>{document.sheetCount}</Table.Td>
                  <Table.Td>
                    <Group gap="xs">
                      {Object.entries(document.sheetStates).map(([sheet, state]) => (
                        <Badge key={sheet} size="sm" variant="light">
                          {sheet}: {state}
                        </Badge>
                      ))}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>

          {/* ⚠ Курсорна пагінація, а не offset: за місяць у проєкті тисячі
              документів, і сторінка 200 через OFFSET сканує все, що до неї. */}
          {query.data?.nextCursor !== null && query.data !== undefined && (
            <Button mt="md" variant="default" onClick={() => setCursor(query.data.nextCursor)}>
              {t('documents.more')}
            </Button>
          )}

          {(query.data?.items.length ?? 0) === 0 && <Text c="dimmed">{t('documents.empty')}</Text>}
        </>
      )}
    </>
  );
}
