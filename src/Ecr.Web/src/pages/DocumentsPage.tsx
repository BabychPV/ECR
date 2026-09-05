import { useState, type JSX } from 'react';
import { Badge, Button, Group, NumberInput, Table } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { DocumentPage } from '@/api/types';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
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
  // ⛔ Період і курсор — в АДРЕСІ (`ФВ-14.29`). Перелік документів за
  // конкретний період — це те, що надсилають колезі; у локальному стані таке
  // посилання вело б на порожній екран із проханням обрати період наново.
  const [periodKey, setPeriodKey] = useUrlNumber('periodKey');
  const [cursor, setCursor] = useUrlState('cursor');
  const [creating, setCreating] = useState(false);
  const session = useSession();

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
          <Group gap="xs" align="end">
            <NumberInput
              size="xs"
              miw={120}
              label={t('documents.period')}
              value={periodKey ?? ''}
              onChange={(value) => {
                setPeriodKey(typeof value === 'number' ? value : null);
                setCursor(null);
              }}
            />

            {/* ⛔ Створення документа не мало в інтерфейсі жодної кнопки
                (`A7-42`): система, уся суть якої — заповнення документів,
                не давала створити перший. */}
            {can(session.data, 'Document.Create') && (
              <Button size="xs" onClick={() => setCreating(true)}>
                {t('documents.create')}
              </Button>
            )}
          </Group>
        }
      />

      {/*
       * ⛔ Через `<AsyncBoundary>`, а не через `ErrorAlert` плюс `?? []`.
       * Стара форма показувала невдалий запит і порожній перелік ОДНОЧАСНО:
       * зверху червона смуга, під нею таблиця з заголовками і жодним рядком —
       * тобто «даних немає» там, де сервер відмовив (`ФВ-14.22`).
       */}
      <AsyncBoundary<DocumentPage>
        isPending={query.isPending}
        error={query.error}
        data={query.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('documents.empty')}
        emptyHint={t('documents.emptyHint')}
        skeleton="table"
        onRetry={() => void query.refetch()}
      >
        {(page) => (
          <>
            <Table striped highlightOnHover className="ecr-sticky-head">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('documents.key')}</Table.Th>
                  <Table.Th>{t('documents.project')}</Table.Th>
                  <Table.Th>{t('documents.sheets')}</Table.Th>
                  <Table.Th>{t('documents.state')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((document) => (
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
            {page.nextCursor !== null && (
              <Button mt="md" variant="default" onClick={() => setCursor(page.nextCursor)}>
                {t('documents.more')}
              </Button>
            )}
          </>
        )}
      </AsyncBoundary>

      <CreateDocumentModal opened={creating} onClose={() => setCreating(false)} />
    </>
  );
}
