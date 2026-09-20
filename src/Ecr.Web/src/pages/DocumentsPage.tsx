import { useState, type JSX } from 'react';
import { Button, Code, Group, NumberInput, Skeleton, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { DocumentPage, PagedProjects } from '@/api/types';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { DocumentListSummaryStrip } from '@/features/documents/DocumentListSummaryStrip';
import { formatNumber } from '@/shared/format';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlNumber, useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
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
  const [periodKey] = useUrlNumber('periodKey');
  const [cursor] = useUrlState('cursor');
  const setUrlParams = useUrlParamsSetter();
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

  // ⛔ Аудит-пас 5: колонка «Project» показувала голий числовий `projectId`
  // — та сама сутність, чий код уже видно в діалозі «New document» одним
  // кліком поруч (`CreateDocumentModal`).
  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
  });

  /**
   * ⛔ Директива D15 §0, правило L10. Тут стояло
   * `…?.code ?? String(projectId)`, тож відмова `GET /api/v1/projects`
   * повертала колонку рівно в той стан, який прибрав аудит-пас 5: голий
   * числовий ідентифікатор у переліку на десятки рядків. Оператор відкриває
   * не той документ — і ніщо не каже, що підпис просто не прочитався.
   *
   * ⚠ Число з екрана не ховається: без коду воно єдине, що лишається від
   * адреси документа. Змінюється ФОРМА — `<Code>` замість тексту, тобто
   * «це ідентифікатор», а не «це назва проєкту», плюс видима причина над
   * таблицею.
   */
  const projectCodeOf = (projectId: number): JSX.Element | string =>
    projects.data?.items.find((project) => project.id === projectId)?.code ?? (
      <Code>{projectId}</Code>
    );

  /**
   * ⛔ UI-walkthrough F3: посилання було зібране як `/documents/${id}` — без
   * періоду. `DocumentPage.tsx` бере `urlPeriod ?? currentPeriodKey()`, тобто
   * без параметра відкриває ПОТОЧНИЙ місяць, а не той період, який людина
   * щойно обрала тут і про який ішлося. Вимога записана в самому
   * `DocumentPage.tsx`: «⛔ Період і аркуш — в адресі (`ФВ-14.29`). Посилання
   * на документ без них відкриває інший період і інший аркуш, ніж той, про
   * який ішлося.»
   *
   * ⚠ Сьогодні наслідок НЕВИДИМИЙ: період стенда (`202609`) випадково
   * дорівнює поточному місяцю, тож підстановка дає ту саму цифру. Тому тест
   * на цю поведінку мусить задавати період ЯВНО і ВІДМІННИЙ від поточного —
   * інакше він зелений і без фіксу.
   */
  const documentHref = (documentId: number): string =>
    `/documents/${documentId}` + (periodKey === null ? '' : `?periodKey=${periodKey}`);

  return (
    <>
      <PageHeader
        title={t('documents.title')}
        actions={
          <Group gap="xs" align="end">
            {/* ⛔ UI-аудит, lane 3: `setPeriodKey(...)` одразу за ним
                `setCursor(null)` — два ОКРЕМІ виклики сеттера `useUrlState`
                в одному обробнику — не компонувались: `setSearchParams`,
                викликаний двічі синхронно в тому самому тіку, губив ОБИДВІ
                зміни (підтверджено ізольованим тестом на голому
                `useSearchParams`). Поле «Period» виглядало інтерактивним
                (некерований DOM встигав показати введене), але жоден запит
                ніколи не бачив `periodKey` в адресі. `useUrlParamsSetter`
                оновлює обидва параметри ОДНИМ переходом. */}
            <NumberInput
              size="xs"
              miw={120}
              label={t('documents.period')}
              value={periodKey ?? ''}
              onChange={(value) => {
                setUrlParams({ periodKey: typeof value === 'number' ? value : null, cursor: null });
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
            {/* ⛔ Відмова переліку проєктів — окрема від відмови переліку
                документів (та під власною межею вище): сама таблиця приїхала,
                не прочиталися лише підписи проєктів. Тому банер тут, а не
                замість таблиці. */}
            {projects.error !== null && (
              <ErrorAlert error={projects.error} onRetry={() => void projects.refetch()} />
            )}

            <DocumentListSummaryStrip periodKey={periodKey} />

            <Table striped highlightOnHover className="ecr-sticky-head">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('documents.key')}</Table.Th>
                  <Table.Th>{t('documents.project')}</Table.Th>
                  <Table.Th>{t('documents.sheets')}</Table.Th>
                  <Table.Th>{t('documents.state')}</Table.Th>
                  <Table.Th>{t('documents.errors')}</Table.Th>
                  <Table.Th>{t('documents.modified')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((document) => {
                  const name = localized(document.nameL10n);

                  return (
                  <Table.Tr key={document.id}>
                    <Table.Td>
                      {/* ⛔ Директива "людське ім'я документа": показуємо
                          ім'я ПОРУЧ із бізнес-ключем, а не замість нього —
                          ключ бере участь в експортах і аудиті, і має
                          лишатися видимим завжди. */}
                      {name.length > 0 ? (
                        <Stack gap="xs">
                          <Link to={documentHref(document.id)}>{name}</Link>
                          <Text size="xs" c="dimmed">
                            {document.businessKey}
                          </Text>
                        </Stack>
                      ) : (
                        <Link to={documentHref(document.id)}>{document.businessKey}</Link>
                      )}
                    </Table.Td>
                    <Table.Td>
                      {/* ⚠ Доки перелік у дорозі, місце тримає скелет, а не
                          число: інакше на кожному відкритті сторінки колонка
                          на мить показувала б ідентифікатори. */}
                      {projects.isPending ? (
                        <Skeleton height={12} width={60} radius="sm" data-projects="pending" />
                      ) : (
                        projectCodeOf(document.projectId)
                      )}
                    </Table.Td>
                    <Table.Td>{document.sheetCount}</Table.Td>
                    <Table.Td>
                      {/* ⛔ UI-walkthrough F6: за період, якого немає в
                          календарі (`/?periodKey=190001`), клітинка була
                          ПОРОЖНЯ — і ніщо не відрізняло «за цей період станів
                          немає» від «не завантажилося». Порожнеча в таблиці
                          читається двояко; видима позначка відсутності —
                          читається однозначно.

                          ⚠ Символ, а не рядок каталогу, навмисно: «—» однакове
                          в усіх мовах і не потребує перекладу, тож позначка
                          не залежить від рядка, якого в каталозі ще немає. */}
                      {Object.keys(document.sheetStates).length === 0 ? (
                        <Text c="dimmed">—</Text>
                      ) : (
                        /*
                         * ⛔ UI-06: тут стояв власний `<Badge variant="light">`
                         * із `{sheet}: {state}` — п'ятий спосіб показу статусу,
                         * названий у шапці `StatusBadge.tsx` поіменно. Він був
                         * гірший за решту чотирьох одразу двічі: друкував КОД
                         * СЕРВЕРА як текст інтерфейсу і не фарбував НІЧОГО —
                         * `Rejected` («аркуш повернено, робота стоїть») виглядав
                         * рівно так само, як `Approved`. Колір тут не окраса:
                         * перелік документів — екран, з якого починають день, і
                         * єдине, заради чого в ньому є колонка стану, — побачити,
                         * де саме щось не так, не відкриваючи кожен документ.
                         *
                         * ⚠ Код аркуша лишається видимим ПОРУЧ із бейджем:
                         * `StatusBadge` малює лише перекладений стан, а аркушів у
                         * документі кілька, і без коду незрозуміло, ЧИЙ це стан.
                         * Пара «код + бейдж» загорнута у власний `wrap="nowrap"`
                         * саме тому, що перенос рядка всередині пари відірвав би
                         * стан від аркуша й дав би читати його як чужий.
                         *
                         * ⚠ Зовнішній проміжок БІЛЬШИЙ за внутрішній (`md` проти
                         * `xs`, обидва зі шкали теми — `ФВ-14.12`): саме різниця
                         * проміжків і робить пару «код + стан» однією річчю. За
                         * однакових проміжків чотири аркуші читалися б як вісім
                         * незалежних написів.
                         */
                        <Group gap="md">
                          {Object.entries(document.sheetStates).map(([sheet, state]) => (
                            <Group key={sheet} gap="xs" wrap="nowrap">
                              <Text size="xs" c="dimmed">
                                {sheet}
                              </Text>
                              <StatusBadge kind="sheet" state={state} />
                            </Group>
                          ))}
                        </Group>
                      )}
                    </Table.Td>
                    <Table.Td data-document-errors={document.errorCount ?? 'none'}>
                      {/* ⛔ BE-09: `null` — документ за цей період НЕ ПЕРЕВІРЯЛИ, і
                          це «—», а не «0»: зелений нуль під неперевіреним
                          документом — та сама неправда, що `A7-28`. */}
                      {document.errorCount === null || document.errorCount === undefined ? (
                        <Text c="dimmed">—</Text>
                      ) : (
                        formatNumber(document.errorCount)
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Stack gap="xs">
                        <Timestamp value={document.modifiedAt} />
                        {typeof document.modifiedByDisplayName === 'string' && (
                          <Text size="xs" c="dimmed">
                            {document.modifiedByDisplayName}
                          </Text>
                        )}
                      </Stack>
                    </Table.Td>
                  </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>

            {/* ⚠ Курсорна пагінація, а не offset: за місяць у проєкті тисячі
                документів, і сторінка 200 через OFFSET сканує все, що до неї. */}
            {page.nextCursor !== null && (
              <Button mt="md" variant="default" onClick={() => setUrlParams({ cursor: page.nextCursor })}>
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
