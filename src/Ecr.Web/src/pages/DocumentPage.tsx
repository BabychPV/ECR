import type { JSX } from 'react';
import { Badge, Button, Group, NumberInput, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type {
  DocumentPeriodRequest,
  DocumentSummary,
  DocumentTableDto,
  ValidationMessageDto,
} from '@/api/types';
import { DocumentGrid } from '@/features/grid/DocumentGrid';
import { ExportButton } from '@/features/export/ExportButton';
import { ImportPanel } from '@/features/import/ImportPanel';
import { SheetActions, isEditable } from '@/features/workflow/SheetActions';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Екран документа: вибір періоду, вкладки аркушів, таблиці, робочий процес.
 *
 * ⚠ Період — не фільтр показу, а **частина адреси даних**: екземпляри таблиць
 * і стан затвердження існують окремо на кожен період (R-A6). Тому зміна
 * періоду перечитує все, а не ховає рядки.
 *
 * ⛔ Таблиці беруться з `GET /api/v1/documents/{id}/tables`. До аудиту
 * (`A7-05`) екран чекав, що `GET /api/v1/documents/{id}` віддасть аркуші з
 * `tableInstanceId`, а той віддає `DocumentSummary` — бізнес-ключ і зведений
 * стан. Grid отримував `undefined` замість екземпляра таблиці.
 */
export function DocumentPage(): JSX.Element {
  const { id } = useParams();
  const documentId = Number(id);
  const session = useSession();

  // ⛔ Період і аркуш — в адресі (`ФВ-14.29`). Посилання на документ без них
  // відкриває інший період і інший аркуш, ніж той, про який ішлося.
  const [urlPeriod, setUrlPeriod] = useUrlNumber('periodKey');
  const [sheet, setSheet] = useUrlState('sheet');
  const periodKey = urlPeriod ?? currentPeriodKey();
  const setPeriodKey = setUrlPeriod;

  const summary = useQuery({
    queryKey: ['document', documentId, periodKey],
    queryFn: () =>
      apiFetch<DocumentSummary>(`/api/v1/documents/${documentId}?periodKey=${periodKey}`),
  });

  const tables = useQuery({
    queryKey: ['document-tables', documentId, periodKey],
    queryFn: () =>
      apiFetch<DocumentTableDto[]>(
        `/api/v1/documents/${documentId}/tables?periodKey=${periodKey}`,
      ),
  });

  const validate = useMutation({
    mutationFn: () =>
      apiFetch<{ messages: ValidationMessageDto[] }>(
        `/api/v1/documents/${documentId}/validate`,
        {
          method: 'POST',
          // ⚠ Період — у ТІЛІ. До `A7-28` сервер читав його з рядка запиту, і
          // валідація мовчки йшла по періоду 0, відповідаючи «помилок немає».
          body: JSON.stringify({ periodKey } satisfies DocumentPeriodRequest),
        },
      ),
    onSuccess: (result) => {
      const errors = result.messages.filter((message) => message.severity === 'Error');

      notifications.show({
        color: errors.length === 0 ? 'green' : 'red',
        message:
          errors.length === 0
            ? t('document.validationClean')
            : t('document.validationErrors', { count: errors.length }),
      });
    },
  });

  const sheets = groupBySheet(tables.data ?? []);
  const active = sheets.find((s) => s.code === sheet) ?? sheets[0];

  // ⚠ Стан береться з `SheetStates` документа за КОДОМ аркуша: скалярного
  // статусу документа не існує (D-93) — аркуші за один період бувають у
  // різних станах одночасно.
  const state =
    active === undefined || summary.data === undefined
      ? ''
      : (summary.data.sheetStates[active.code] ?? 'Draft');
  // ⛔ Редагованість — питання ОДНОГО стану аркуша, і відповідає на нього
  // таблиця переходів (`features/workflow/transitions.ts`), а не порівняння
  // рядків тут. Порівняння на місці — це друга копія машини станів домену, і
  // розійшлася б вона мовчки: `Rejected` виглядає як «не Draft», але
  // редагувати відхилений аркуш і треба, інакше виправити зауваження нічим.
  const readOnly = !isEditable(state);

  return (
    /*
     * ⛔ Обгортка навколо ВСЬОГО екрана: заголовок — це бізнес-ключ документа,
     * тобто теж дані. Помилки обох запитів зведені разом, бо екран без
     * таблиць — це не документ, а його назва.
     *
     * ⚠ Документ БЕЗ аркушів за обраний період — окремий стан, не помилка:
     * період міг ще не відкритися. Порожні вкладки без пояснення виглядали б
     * як несправність (`A7-30` показав, що так і буває насправді).
     */
    <AsyncBoundary<DocumentSummary>
      isPending={summary.isPending || tables.isPending}
      error={summary.error ?? tables.error}
      data={summary.data}
      isEmpty={() => sheets.length === 0}
      emptyTitle={t('document.noSheets')}
      emptyHint={t('document.noSheetsHint')}
      skeleton="table"
      onRetry={() => {
        void summary.refetch();
        void tables.refetch();
      }}
    >
      {(document) => (
    <Stack>
      <PageHeader
        title={document.businessKey}
        actions={
          <Group gap="xs">
            <NumberInput
              size="xs"
              miw={110}
              label={t('documents.period')}
              value={periodKey}
              onChange={(value) => setPeriodKey(typeof value === 'number' ? value : periodKey)}
            />
            <Button size="xs" variant="default" loading={validate.isPending} onClick={() => validate.mutate()}>
              {t('document.validate')}
            </Button>

            {/* ⛔ Імпорт лише туди, куди можна писати. Кнопка над поданим
                аркушем обіцяла б заміну чисел, яку сервер відхилить: подане
                редагується лише після повернення в роботу (`ФВ-5.20a`). */}
            {can(session.data, 'Document.Import') && !readOnly && (
              <ImportPanel documentId={documentId} periodKey={periodKey} />
            )}

            {can(session.data, 'Document.Export') && (
              <ExportButton
                documentId={documentId}
                periodKey={periodKey}
                language={session.data?.language ?? 'en'}
              />
            )}

            {/* ⛔ Увесь робочий процес аркуша — в одному компоненті. До аудиту
                тут була сама лише кнопка «Подати», і на ній процес
                закінчувався: затвердити документ через інтерфейс було
                неможливо (`A7-39`). */}
            {active !== undefined && (
              <SheetActions
                documentId={documentId}
                sheetDefId={active.sheetDefId}
                periodKey={periodKey}
                state={state}
              />
            )}
          </Group>
        }
      />

      <Tabs value={active?.code ?? null} onChange={setSheet}>
        <Tabs.List>
          {sheets.map((s) => (
            <Tabs.Tab key={s.code} value={s.code}>
              {s.name}{' '}
              <Badge size="xs" variant="light">
                {document.sheetStates[s.code] ?? 'Draft'}
              </Badge>
            </Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>

      {active?.tables.map((table) => (
        <Stack key={table.tableInstanceId} gap="xs">
          <Text fw={600}>{localized(table.tableNameL10n)}</Text>
          <DocumentGrid
            documentId={documentId}
            tableInstanceId={table.tableInstanceId}
            periodKey={periodKey}
            readOnly={readOnly}
            allowsDynamicRows={table.allowsDynamicRows}
            maxDynamicRows={table.maxDynamicRows}
          />
        </Stack>
      ))}
    </Stack>
      )}
    </AsyncBoundary>
  );
}

/** Аркуш із його таблицями. */
interface SheetGroup {
  sheetDefId: number;
  code: string;
  name: string;
  ordinal: number;
  tables: DocumentTableDto[];
}

/**
 * Групує таблиці за аркушами.
 *
 * ⚠ Сервер віддає плоский перелік екземплярів: так його можна віддати одним
 * запитом і не вигадувати вкладену структуру, яка все одно розбирається на
 * клієнті.
 */
function groupBySheet(tables: DocumentTableDto[]): SheetGroup[] {
  const sheets = new Map<string, SheetGroup>();

  for (const table of tables) {
    const group = sheets.get(table.sheetCode) ?? {
      sheetDefId: table.sheetDefId,
      code: table.sheetCode,
      name: localized(table.sheetNameL10n) || table.sheetCode,
      ordinal: table.sheetOrdinal,
      tables: [],
    };

    group.tables.push(table);
    sheets.set(table.sheetCode, group);
  }

  return [...sheets.values()].sort((a, b) => a.ordinal - b.ordinal);
}

/**
 * Поточний період як `Year*100 + Sequence` (R-A6).
 *
 * ⚠ Це лише **початкове значення поля**, а не бізнес-правило: справжній
 * поточний період задає календар проєкту, і в квартальному проєкті номер
 * місяця йому не дорівнює. Користувач бачить число і може його змінити.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}
