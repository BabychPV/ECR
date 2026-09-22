import { useEffect, useMemo, useState, type JSX } from 'react';
import { Badge, Button, Group, NumberInput, Skeleton, Stack, Tabs, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import type {
  DocumentPeriodRequest,
  DocumentSummary,
  DocumentTableDto,
  ValidationResultResponse,
} from '@/api/types';
import {
  DeleteDocumentPermission,
  useDeleteDocumentAction,
} from '@/features/documents/DeleteDocumentAction';
import { SheetFillSummary } from '@/features/documents/SheetFillSummary';
import { ValidationPanel } from '@/features/documents/ValidationPanel';
import { useDocumentPending } from '@/features/grid/autosave';
import { RestoreEditsBanner } from '@/features/grid/RestoreEditsBanner';
import { ExportButton } from '@/features/export/ExportButton';
import { CalculationResultsPanel } from '@/features/methodologies/CalculationResultsPanel';
import { ImportPanel } from '@/features/import/ImportPanel';
import { SheetActions, isEditable } from '@/features/workflow/SheetActions';
import { WorkflowHistory } from '@/features/workflow/WorkflowHistory';
import { can, useSession } from '@/shared/session/useSession';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Сітка вантажиться окремим чанком.
 *
 * ⛔ Не оптимізація «про запас», а ліки, прописані самим гейтом бюджету:
 * `RevoGrid` — 79,1 % джерел чанка сторінки, і саме через нього маршрут
 * важив 259,2 КБ при межі 250 (`D-132`, `H-4`). Статичний імпорт означав,
 * що ядро сітки вантажить КОЖЕН, хто відкрив документ, — разом із тими,
 * хто дивиться його зведення і до таблиць не доходить.
 *
 * ⚠ Видимої затримки це не додає, і ось чому: сітка й до того не могла
 * намалюватися раніше за свій зріз даних — вона тягне його власним
 * запитом. Чанк вантажиться паралельно з тим самим очікуванням.
 *
 * ⛔ Чанк той самий, а от `React.lazy` і `<Suspense>` ПРИБРАНО — і це не
 * стиль, а єдине, що лікує зміряний дефект. Числа, спосіб вимірювання і
 * перебрані (та відкинуті) гіпотези — у `features/grid/SheetTables.tsx`;
 * коротко: під `<Suspense>` сітки не з'являлися НІКОЛИ на документі
 * чинного розміру, і жодне перекладання самих меж цього не міняло.
 *
 * ⚠ Динамічний `import()` в ефекті дає рівно ту саму окрему точку розбиття,
 * що й `lazy` (Vite/Rollup ріже чанк за виразом `import()`, а не за тим, хто
 * його обгортає), тому бюджет `D-132`/`H-4` лишається виконаним. Різниця в
 * тому, КОЛИ монтуються сітки: у звичайній фіксації після `setState`, а не
 * всередині повторної спроби межі очікування.
 */
type SheetTablesComponent = typeof import('@/features/grid/SheetTables')['SheetTables'];

interface GridModuleState {
  readonly component: SheetTablesComponent | null;
  readonly error: unknown;
}

function useSheetTablesModule(): GridModuleState & { readonly reload: () => void } {
  const [state, setState] = useState<GridModuleState>({ component: null, error: null });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let alive = true;

    // ⚠ Компонент лежить у ПОЛІ об'єкта стану, а не в стані напряму: `useState`
    // трактує функцію як апдейтер, і компонент (він теж функція) інакше був би
    // ВИКЛИКАНИЙ замість того, щоб бути збереженим.
    void import('@/features/grid/SheetTables').then(
      (module) => {
        if (alive) setState({ component: module.SheetTables, error: null });
      },
      (error: unknown) => {
        // ⛔ Мовчазний провал тут коштував би дорожче за будь-який інший:
        // екран лишився б із заглушками назавжди, і це виглядало б рівно як
        // дефект, який ця картка й закриває.
        if (alive) setState({ component: null, error });
      },
    );

    return () => {
      alive = false;
    };
  }, [attempt]);

  return { ...state, reload: () => setAttempt((value) => value + 1) };
}

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

  /*
   * ⛔ Останній результат перевірки ЧИТАЄТЬСЯ (директива №09 `W8` п.3,
   * `S-19`). Підсумок зберігається на сервері (`ФВ-5.19`), і доти його не
   * читав ніхто: перелік зауважень жив рівно до перезавантаження сторінки, а
   * щоб побачити його знову, оператор мусив ЗАПУСТИТИ перевірку заново — на
   * великому документі це три секунди й повний прогін правил заради списку,
   * який уже пораховано.
   *
   * ⚠ `404` — це «ще не перевіряли», а не помилка: `retry: false` і `null` у
   * стані. «Зауважень немає» показувати замість цього не можна — зелений
   * напис під документом, якого ніхто не перевіряв, повідомляє неправду про
   * готовність.
   */
  const lastValidation = useQuery({
    queryKey: ['validation', documentId, periodKey],
    queryFn: () =>
      apiFetch<ValidationResultResponse>(
        `/api/v1/documents/${documentId}/validation?periodKey=${periodKey}`,
      ),
    retry: false,
  });

  /**
   * Адреса даних, до якої СТОСУЄТЬСЯ ручний прогін перевірки.
   *
   * ⚠ Період — не фільтр показу, а частина адреси (коментар компонента вище),
   * тож і результат перевірки адресний: «3 помилки» без цієї пари — це число
   * без предмета.
   */
  const scope = `${String(documentId)}:${String(periodKey)}`;

  /*
   * Свіжий прогін перекриває прочитаний: після натискання «Перевірити» на
   * екрані має бути те, що щойно порахували, а не те, що лежало в базі.
   *
   * ⛔ Аудит 2026-09-16 §10.7: тут лежав голий `ValidationResultResponse`, і
   * ніщо не скидало його при зміні документа/періоду — а показувався він
   * ПОПЕРЕД прочитаного (`fresh ?? lastValidation.data`). Оператор перевіряв
   * 202401, бачив «3 помилки», міняв період у заголовку — `summary`/`tables`/
   * `lastValidation` коректно перезапитувались, а «3 помилки» лишалися під
   * періодом, який НІХТО не перевіряв. Саме той випадок, проти якого
   * застерігає коментар до `lastValidation` вище, лише з протилежним знаком:
   * не зелений напис під неперевіреним, а червоний.
   *
   * ⚠ Результат зберігається РАЗОМ з адресою, а не скидається ефектом на
   * зміну `scope`. Різниця не стилістична: `scope` на момент ВІДПОВІДІ вже
   * може бути іншим, ніж на момент запиту (оператор змінив період, поки
   * перевірка йшла), і ефект-скидач тут не допоміг би — він відпрацював би
   * ДО того, як прийде відповідь, і та все одно лягла б на новий період.
   * Тому адресу несе сама мутація (її змінна), а показ порівнює її з поточною.
   */
  const [fresh, setFresh] = useState<{ scope: string; result: ValidationResultResponse } | null>(
    null,
  );

  const validate = useMutation({
    mutationFn: (_scope: string) =>
      apiFetch<ValidationResultResponse>(
        `/api/v1/documents/${documentId}/validate`,
        {
          method: 'POST',
          // ⚠ Період — у ТІЛІ. До `A7-28` сервер читав його з рядка запиту, і
          // валідація мовчки йшла по періоду 0, відповідаючи «помилок немає».
          body: JSON.stringify({ periodKey } satisfies DocumentPeriodRequest),
        },
      ),
    onSuccess: (result, requestedScope) => {
      setFresh({ scope: requestedScope, result });

      const errors = result.messages.filter((message) => message.severity === 'Error');

      // ⚠ Тост ЛИШАЄТЬСЯ, але тепер він лише повідомляє, що перевірка
      // завершилася: сам перелік — на екрані, під заголовком. Число без
      // переліку не веде до жодної дії (`ФВ-14.24`).
      notifications.show({
        color: errors.length === 0 ? 'green' : 'statusError',
        message:
          errors.length === 0
            ? t('document.validationClean')
            : t('document.validationErrors', { count: errors.length }),
      });
    },
    onError: showApiError,
  });

  /**
   * Що показувати в панелі: свіже — **лише для своєї адреси** — інакше
   * прочитане, інакше нічого.
   */
  const freshForScope = fresh?.scope === scope ? fresh.result : null;
  const shownValidation = freshForScope ?? lastValidation.data ?? null;

  /*
   * ⛔ «Прочитати не вдалося» — це НЕ «ще не перевіряли», і до цього місця
   * обидва стани були для екрана одним і тим самим: `403`, `500` і обрив
   * мережі дають те саме
   * `data === undefined`, тобто `shownValidation === null`, тобто панель
   * зауважень не малювалася ЗОВСІМ. Документ, у якому на сервері вже лежать
   * БЛОКУВАЛЬНІ помилки, виглядав рівно як неперевірений і чистий: оператор
   * тиснув «Подати» й діставав відмову `ECR-SUB-*` без жодного попередження на
   * екрані — або вважав документ готовим.
   *
   * ⚠ Це той самий дефект, проти якого застерігає коментар до запиту вище
   * («зелений напис під документом, якого ніхто не перевіряв»), лише з
   * протилежним знаком: не зелений напис під неперевіреним, а порожнеча під
   * перевіреним. Стани тепер три, і в цьому порядку: відмова → очікування →
   * дані.
   *
   * ⚠ `404` лишається «ще не перевіряли» — саме так відповідає
   * `DocumentsController.LastValidation`, і банер під кожним документом, якого
   * ніхто не перевіряв, був би тим самим шумом, лише червоним.
   *
   * ⚠ Свіжий прогін ЦІЄЇ адреси знімає невідомість: результат уже на екрані,
   * і невдале читання того, що лежало в базі, більше нікого не вводить в
   * оману.
   *
   * ⛔ Саме `ErrorAlert`, а не `AsyncBoundary`: обгортка малює власний
   * `<Title order={4}>` посеред сторінки, у якої заголовок уже є, і рве
   * `heading-order` (гейти `a11y (dark)`/`a11y (light)`).
   */
  const unreadableValidation =
    freshForScope !== null || isNotValidatedYet(lastValidation.error)
      ? null
      : (lastValidation.error ?? null);

  /*
   * ⛔ `useMemo`, а не виклик на кожному рендері, — і це не мікрооптимізація.
   * `groupBySheet` будує НОВІ масиви щоразу, а `active.tables` іде пропом у
   * `SheetTables`, де від його ІДЕНТИЧНОСТІ залежить ефект, що заводить
   * `IntersectionObserver`. Нестабільний проп означав би, що спостерігача
   * знищують і створюють наново на кожен рендер сторінки (а їх тут кілька
   * поспіль: три запити приходять у різний час), — а перше повідомлення
   * спостерігача АСИНХРОННЕ, тож черга «створили → знищили → створили» здатна
   * не доставити його жодного разу. Сітки не з'явилися б зовсім, і виглядало б
   * це рівно як дефект, який закрив #295.
   *
   * ⚠ `tables.data` від React Query стабільний між рендерами, доки не прийшла
   * нова відповідь, — тобто мемоізація тут справді тримає ідентичність, а не
   * створює її видимість.
   */
  const sheets = useMemo(() => groupBySheet(tables.data ?? []), [tables.data]);
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

  // ⚠ Викликається БЕЗУМОВНО і до будь-якого розгалуження показу: правило
  // хуків не знає про `AsyncBoundary` нижче.
  const gridModule = useSheetTablesModule();

  /*
   * ⛔ Незбережені правки належать ДОКУМЕНТУ, а не сітці (`D14-12`). Хук
   * відкриває сховище на цей документ, бере на себе збереження зрізів, чиї
   * сітки вже розмонтовані (перемикання аркуша, прокрутка — `SheetTables`), і
   * везе все незбережене перед закриттям вкладки. До нього кожна сітка робила
   * це за себе: правка молодша за 500 мс зникала при перемиканні аркуша
   * мовчки, а `beforeunload` віз лише ту сітку, у якій стояв курсор (`W-02`).
   *
   * ⚠ Імпорт статичний і бюджет чанка (`D-132`) не чіпає: `autosave.ts` не
   * тягне ядро `RevoGrid` — воно лишається за виразом `import()` вище.
   */
  useDocumentPending(documentId, session.data?.userId);

  // Видалення чернетки: кнопка — у шапці, відмова сервера — банером під нею.
  const deletion = useDeleteDocumentAction({
    documentId,
    document: summary.data,
    sheetCodes: sheets.map((s) => s.code),
    allowed: can(session.data, DeleteDocumentPermission),
  });

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
        // ⛔ Директива "людське ім'я документа": ім'я ПОРУЧ із бізнес-ключем,
        // а не замість нього — ключ лишається видимим завжди.
        title={
          localized(document.nameL10n).length > 0
            ? `${localized(document.nameL10n)} · ${document.businessKey}`
            : document.businessKey
        }
        actions={
          <Group gap="xs">
            <NumberInput
              size="xs"
              miw={110}
              label={t('documents.period')}
              value={periodKey}
              onChange={(value) => setPeriodKey(typeof value === 'number' ? value : periodKey)}
            />
            <Button
              size="xs"
              variant="default"
              loading={validate.isPending}
              onClick={() => validate.mutate(scope)}
            >
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

            {deletion.trigger}
          </Group>
        }
      />

      {deletion.refusal}

      {/* ⛔ `ФВ-3.6`, `D14-12` крок 3: правки, що загинули з сесією, лежать у
          вкладці (`features/grid/lostEdits.ts`) і чекають ТУТ — раніше їх
          нікуди було покласти. Банер над сітками, а не під ними: пропозиція,
          яку видно лише після прокрутки до дев'яносто першої таблиці, — це
          пропозиція, якої немає.

          ⚠ Статичний імпорт бюджету чанка не чіпає (`D-132`): компонент
          працює зі сховищем правок і зрізом, ядра `RevoGrid` не торкаючись. */}
      <RestoreEditsBanner documentId={documentId} userId={session.data?.userId} />

      {/* ⛔ `BE-10`. Компонент сам вирішує, чи малюватися: доки сервер не
          відповів, він повертає `null`, а не «0 з 0» — заповненість, якої ще
          не знають, і заповненість, якої немає, це різні твердження
          (`D15-06`). Поруч із `ValidationPanel` він і за змістом: обидва
          кажуть про документ за період цілком, а не про активний аркуш. */}
      <SheetFillSummary documentId={documentId} periodKey={periodKey} />

      {/* ⚠ Причина стоїть РІВНО ТАМ, де мала б стояти панель зауважень, і
          перед нею: «зауваження прочитати не вдалося» — твердження про той
          самий предмет, і побачити його має той, хто прийшов дивитися на
          зауваження, а не той, хто відкрив консоль.

          ⚠ Панель нижче лишається БЕЗУМОВНОЮ: якщо відмовив лише ПОВТОРНИЙ
          запит, прочитане раніше нікуди не поділося (React Query тримає
          `data`), і ховати його заради банера означало б втратити відомі
          зауваження. Тоді на екрані обидва — «ось що ми знали» і «оновити не
          вдалося», а не одне замість одного. */}
      {unreadableValidation !== null && (
        <ErrorAlert
          error={unreadableValidation}
          onRetry={() => {
            void lastValidation.refetch();
          }}
        />
      )}

      {/* ⚠ Панель — ПІД заголовком і НАД вкладками: зауваження стосуються
          документа за період цілком, а не активного аркуша, і сховати їх під
          вкладку означало б показувати їх лише тому, хто вгадав, куди
          дивитися.

          ⚠ Доступність кнопки «Подати» від цього стану НЕ залежить і не
          залежала: її показ рахує `SheetActions` за станом аркуша й рівнем
          гранта (`features/workflow/SheetActions.tsx`), валідації він не
          питає. Тобто невідомість кнопки не ВІДКРИВАЄ — вона лише лишала
          оператора без єдиного попередження перед натисканням; банер вище це
          й закриває. Гейт подання за помилками — на сервері (`ECR-SUB-*`). */}
      <ValidationPanel messages={shownValidation?.messages ?? null} />

      {/* `BE-11b`. Над вкладками з тієї ж причини, що й панель вище: журнал —
          про всі аркуші документа за період. Згорнутий, і до розгортання
          запиту не робить; порожній — не малюється зовсім. */}
      <WorkflowHistory documentId={documentId} periodKey={periodKey} />

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

        {/* ⛔ Кожна вкладка має СВОЮ панель: Mantine ставить вкладці
            `aria-controls` на id панелі, і без панелі посилання висіло в
            повітрі (axe `aria-valid-attr-value`, critical; спіймано, щойно
            гейт почав чекати даних). Неактивні — порожні: `keepMounted` у
            `Tabs` не ввімкнено, тож вміст вони не рендерять.

            ⚠ Активна панель — ОКРЕМИЙ елемент на сталому місці дерева, а не
            елемент того ж списку з ключем аркуша: інакше перемикання аркуша
            перемонтовувало б сітку (важкий RevoGrid і стан лінивого
            монтажу). Так, як і раніше, сітка лише отримує нові `tables`. */}
        {sheets
          .filter((s) => s.code !== active?.code)
          .map((s) => (
            <Tabs.Panel key={s.code} value={s.code}>
              {null}
            </Tabs.Panel>
          ))}

        {/* ⚠ `pt="md"` і `Stack` відтворюють відступи, які раніше давав
            зовнішній `Stack` сторінки між вкладками й сіткою. */}
        {active !== undefined && (
          <Tabs.Panel value={active.code} pt="md">
            <Stack>
              {/* ⚠ Три стани чанка сітки замість `<Suspense>`: вантажиться —
                  заглушка ПО ОДНІЙ НА ТАБЛИЦЮ (кількість таблиць відома до
                  завантаження чанка, і одна смужка замість дев'яноста однієї
                  збрехала б про розмір сторінки, яка зараз з'явиться); не
                  завантажився — помилка з кнопкою «повторити», бо порожній
                  екран із заглушками тут не відрізнити від того самого
                  дефекту, який ця картка закриває; завантажився — таблиці. */}
              {gridModule.error !== null && (
                <ErrorAlert error={gridModule.error} onRetry={gridModule.reload} />
              )}

              {/* ⚠ Заглушка чанка повторює розкладку `SheetTables`: заголовок
                  таблиці ПЛЮС смужка зарезервованої висоти. Обидва — не
                  прикраса. Заголовок робить осмисленою прокрутку по ще не
                  завантаженій сторінці (і з'являється він одразу, а не після
                  чанка сітки), а висота слота мусить збігатися з тією, яку
                  тримає `SheetTables` (`TableSlotMinHeight`), інакше поява
                  чанка перекладає всю сторінку під курсором. Числове значення
                  тут не імпортується з модуля сітки навмисно: це той самий
                  модуль, який ця гілка й чекає, і статичний імпорт із нього
                  повернув би `RevoGrid` у чанк маршруту (`D-132`). */}
              {gridModule.error === null && gridModule.component === null && (
                <Stack gap="xs">
                  {active.tables.map((table) => (
                    <Stack
                      key={table.tableInstanceId}
                      gap="xs"
                      style={{ minHeight: 'calc(70vh + 96px)' }}
                    >
                      <Text fw={600}>{localized(table.tableNameL10n)}</Text>
                      <Skeleton height="60vh" radius="sm" />
                    </Stack>
                  ))}
                </Stack>
              )}

              {gridModule.component !== null && (
                <gridModule.component
                  documentId={documentId}
                  periodKey={periodKey}
                  readOnly={readOnly}
                  tables={active.tables}
                />
              )}
            </Stack>
          </Tabs.Panel>
        )}
      </Tabs>

      {/* ⚠ Без аркушів панелі немає, а відмова чанка має лишатися видимою —
          як і до появи панелі. Заглушка й сітка без аркуша не малювалися й
          раніше. */}
      {active === undefined && gridModule.error !== null && (
        <ErrorAlert error={gridModule.error} onRetry={gridModule.reload} />
      )}

      {/* ⛔ Числа методологій — окремо від сітки, і це `D-69`: у комірку
          вони не потрапляють ніколи, а приходять у документ посиланням через
          прив'язку. Доти це число не показував жоден екран.

          ⚠ Під правом `Calculation.View`, як і сусідні дії вище. Панель
          стояла БЕЗУМОВНО, і оператор без цього права бачив на ВЛАСНОМУ
          документі плашку `403 Calculation.View` — там, де решта недоступного
          просто не малюється. Знайдено проходом інтерфейсу як користувач
          (`docs/build/UI-WALKTHROUGH.md`, F2): жоден компонентний тест цього
          не бачив, бо кожен із них монтує панель напряму. */}
      {can(session.data, 'Calculation.View') && (
        <CalculationResultsPanel documentId={documentId} periodKey={periodKey} />
      )}
    </Stack>
      )}
    </AsyncBoundary>
  );
}

/**
 * Чи означає відмова читання «перевірку ще не запускали».
 *
 * ⛔ Рівно `404` і рівно від НАШОГО API. Усе інше — `403` (права на читання
 * підсумку), `500`, обрив мережі, відповідь проксі — це «невідомо», а
 * «невідомо», показане як «нічого», і є та сама неправда про готовність
 * документа, проти якої написаний коментар до запиту.
 */
function isNotValidatedYet(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.status === 404;
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
