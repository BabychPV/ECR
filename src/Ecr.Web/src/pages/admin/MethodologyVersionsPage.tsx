import { lazy, Suspense, useMemo, useState, type JSX } from 'react';
import {
  Alert,
  Button,
  Code,
  Group,
  Modal,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Textarea,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import type {
  CalculationLevel,
  ExpressionValidationDto,
  MethodologyDraftVersionDto,
  MethodologyFormulaDto,
  MethodologyPublicationDiff,
  PublishMethodologyRequest,
  UnitRef,
} from '@/api/types';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { ExpressionEditor } from '@/features/expressions/ExpressionEditor';
import type { ExpressionPlacement } from '@/features/expressions/api';
import { localizedMessage } from '@/features/expressions/markers';
import {
  createMethodologyVersion,
  deleteMethodologyFormula,
  methodologyFormulas,
  methodologyVersions,
  publishMethodologyVersion,
  saveMethodologyFormula,
} from '@/features/methodologies/api';
import {
  defaultVersion,
  mayEditContent,
  type FormulaDraft,
} from '@/features/methodologies/draft';
import { useDeleteVersionAction } from '@/features/methodologies/DeleteVersionAction';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';

/*
 * ⛔ Сім панелей змісту версії — за `import()`, і це вимога бюджету (`D-132`),
 * а не смак. `MethodologyContentPanels.tsx` — 59.7 КБ джерела, і всі сім
 * експортів статично лежали в чанку маршруту, який стояв на 248.4 з 250 КБ.
 *
 * ⚠ Ліниві ВСІ СІМ, а не частина: доки бодай один експорт імпортовано
 * статично, модуль лишається у вхідному чанку ЦІЛКОМ, і ліниві шість не
 * економлять нічого. Усі сім резолвляться в ОДИН чанк, тож мережевий запит
 * теж один.
 *
 * ⚠ Панелі й далі малюються ОДНОЧАСНО, а не по вкладках — рішення нижче
 * (коментар перед `MethodologyConstantsPanel`) не скасоване. Змінився момент
 * ЗАВАНТАЖЕННЯ коду, а не склад екрана: жодна панель не з'являється раніше за
 * обрану версію, бо всі стоять під `selected !== undefined`, і чанк іде
 * мережею паралельно із запитом її змісту.
 */
type PanelModule = typeof import('@/features/methodologies/MethodologyContentPanels');

function lazyPanel<K extends keyof PanelModule>(name: K): ReturnType<typeof lazy> {
  return lazy(async () => {
    const loaded = await import('@/features/methodologies/MethodologyContentPanels');

    return { default: loaded[name] as never };
  });
}

const MethodologyBindingsPanel = lazyPanel('MethodologyBindingsPanel');
const MethodologyConstantsPanel = lazyPanel('MethodologyConstantsPanel');
const MethodologyModesForm = lazyPanel('MethodologyModesForm');
const MethodologyOutputsPanel = lazyPanel('MethodologyOutputsPanel');
const MethodologyRequiredInputsPanel = lazyPanel('MethodologyRequiredInputsPanel');
const MethodologyRulesPanel = lazyPanel('MethodologyRulesPanel');
const MethodologyTestsPanel = lazyPanel('MethodologyTestsPanel');

/**
 * Конфігуратор версії методології: формули чернетки (`ФВ-9.15`).
 *
 * ⛔ Екран існує заради одного правила, і воно ж робить його небезпечним:
 * **чернетку правлять, опубліковану — ні** (`ФВ-9.1`, `ФВ-13.2`). Опублікована
 * версія рахує числа, які вже подані регуляторові; правка в ній не має ні
 * diff-у, ні публікації, ні сліду — вона просто змінює минуле. Тому екран
 * показує опубліковану версію **тільки на читання**, а «змінити» пропонує
 * єдиним чинним способом — клоном у нову чернетку.
 *
 * ⚠ Заборону тримає не ця форма. Сервер відхиляє правку опублікованої версії
 * доменом (`MethodologyVersion.EditFormula`, `ECR-CALC-0409`); тут лише
 * показано, чому кнопки немає. Форма, яка була б єдиною перевіркою, впала б
 * від першого прямого запиту.
 *
 * ⚠ Редактор виразів — той самий компонент, що й на сторінці виразів
 * (`ФВ-9.15a`, `D-113`): діалект у нього параметр. Другий редактор означав би
 * дві розбіжні відповіді на питання «що тут можна написати».
 */
export function MethodologyVersionsPage(): JSX.Element {
  const params = useParams();
  const methodologyId = Number(params['id']);
  const known = Number.isFinite(methodologyId);

  const queryClient = useQueryClient();
  const session = useSession();
  const mayEdit = can(session.data, 'Calculation.EditFormula');

  // ⛔ Окреме право від `Calculation.EditFormula` (директива «обов'язкові
  // вхідні колонки методології»): gate перед збереженням клітинки читає ці
  // записи для ВСІХ, хто вводить дані, а хто вирішує, ЯКІ колонки
  // обов'язкові, — вужча відповідальність, і саме тому в неї свій дозвіл.
  const mayManageRequiredInputs = can(session.data, 'Calculation.ManageRequiredInputs');

  // ⛔ Право небезпечне (`sec.Permission.IsDangerous`) і вбудованим ролям
  // seed-ом не видається взагалі (`PublishMethodologyHandler`, D-40):
  // публікація змінює числа, які вже подані регуляторові.
  const mayPublish = can(session.data, 'Calculation.Publish');

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [newVersion, setNewVersion] = useState('');
  const [copyFrom, setCopyFrom] = useState<string | null>(null);

  // ⛔ Кнопка публікації жила лише на `MethodologiesPage.tsx` (перелік
  // методологій), а не тут — на екрані, де версію насправді доводять до
  // готовності (формули, константи, виходи, правила, обов'язкові колонки,
  // золотий набір, прив'язки). Хто пройшов увесь цей шлях сторінка за
  // сторінкою, не бачив жодного «Publish» ні в рядку версії, ні в
  // розгорнутій панелі — доводилося здогадуватися повернутися в перелік.
  const [publishing, setPublishing] = useState<{ versionId: number } | null>(null);
  const [publishReason, setPublishReason] = useState('');
  const [publishEffectiveFrom, setPublishEffectiveFrom] = useState('');
  const [publishDiff, setPublishDiff] = useState<MethodologyPublicationDiff | null>(null);

  // ⛔ Рівень драбини виразності (`ФВ-9.2`) став вибором, а не константою.
  // Тут стояло зашите `'Configuration'` із поясненням «клон бере рівень із
  // джерела» — правдивим рівно наполовину: для ПОРОЖНЬОЇ чернетки, тобто для
  // першої версії щойно заведеної методології, рівень не мав звідки взятися
  // взагалі, і будь-яка нова методологія народжувалася першим рівнем мовчки.
  const [level, setLevel] = useState<CalculationLevel>('Configuration');
  const [editing, setEditing] = useState<FormulaDraft | null>(null);

  // ⛔ UI-аудит, lane 5: діалог формули мав ТОЙ САМИЙ `ExpressionEditor`
  // (`ФВ-9.15a`), що й `/admin/expressions`, — і той самий сервер, що
  // повертає діагностику синтаксису (`Q-303`) — але жодного місця на
  // екрані, куди цю діагностику показати, і жодної перевірки перед
  // збереженням: `(1 + 2))` (зайва дужка) зберігався як є, з видимим лише
  // непідписаним підкресленням у Monaco. `expressions.findings`
  // (`/admin/expressions`) — той самий текст, не власний ключ: діагностика
  // й тут, і там — той самий контракт (`ExpressionValidationDto`).
  const [expressionErrors, setExpressionErrors] = useState<ExpressionValidationDto | null>(null);

  // ⛔ Видалення формули незворотне (`ФВ-9.15`) і досі спрацьовувало прямо з
  // кліку — та сама помилка одним кліком, проти якої вже стоїть підтвердження
  // в `DocumentGrid` (`AllowWithConfirmation`, `#43`) і в перемиканні джерела
  // реєстру (`SourceKindSwitch`). Тут пояснювати ПРИЧИНУ нема чого — формула
  // не лишає слід у журналі так, як перемикання джерела, тож досить простого
  // так/ні, а не повної форми з причиною.
  const [deleteTarget, setDeleteTarget] = useState<{ versionId: number; code: string } | null>(
    null,
  );

  const versions = useQuery({
    queryKey: queryKeys.methodologies.versionsOf(methodologyId),
    queryFn: () => methodologyVersions(methodologyId),
    enabled: known,
  });

  const all = useMemo(() => versions.data ?? [], [versions.data]);

  // BE-25: видалення чернетки — у `features/methodologies/`.
  const versionDeletion = useDeleteVersionAction({ methodologyId, allowed: mayEdit });

  // ⚠ Обрана версія — стан, але за замовчуванням береться ЧЕРНЕТКА, а не
  // перша в переліку: екран існує заради редагування, і відкривати його на
  // версії, яку не можна правити, означало б щоразу починати з глухого кута.
  const selected = useMemo(() => defaultVersion(all, selectedId), [all, selectedId]);

  // ⚠ Дві умови разом: стан версії каже сервер, право — профіль. Кожна окремо
  // веде користувача у відмову — `ECR-CALC-0409` або `403`.
  const editable = mayEditContent(selected, mayEdit);
  const requiredInputsEditable = mayEditContent(selected, mayManageRequiredInputs);

  const formulas = useQuery({
    queryKey: queryKeys.methodologies.formulas(selected?.id),
    queryFn: () => methodologyFormulas(methodologyId, selected?.id ?? 0),
    enabled: known && selected !== undefined,
  });

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: 60 * 60 * 1000,
  });

  const create = useMutation({
    mutationFn: () =>
      createMethodologyVersion(methodologyId, {
        versionNumber: newVersion,
        copyFromVersionId: copyFrom === null ? null : Number(copyFrom),

        // ⚠ Рівень має значення лише для ПОРОЖНЬОЇ чернетки: клон бере його з
        // джерела, бо версія, що змінила рівень, — уже інша методологія, а не
        // її нова редакція. Тому поле і ховається, коли обрано джерело.
        level,
      }),
    onSuccess: async (draft) => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.versionsOf(methodologyId),
      });
      setSelectedId(String(draft.id));
      setCreating(false);
      setNewVersion('');
      showDone(t('methodologies.versionCreated'));
    },
    onError: showApiError,
  });

  const save = useMutation({
    mutationFn: (draft: FormulaDraft) => saveMethodologyFormula(methodologyId, draft),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.allFormulas() });
      setEditing(null);
      setExpressionErrors(null);
      showDone(t('methodologies.formulaSaved'));
    },
    onError: showApiError,
  });

  const remove = useMutation({
    mutationFn: (target: { versionId: number; code: string }) =>
      deleteMethodologyFormula(methodologyId, target.versionId, target.code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.allFormulas() });
      setDeleteTarget(null);
      showDone(t('methodologies.formulaDeleted'));
    },
    onError: showApiError,
  });

  /**
   * Публікація версії, відкритої на цьому екрані. Право `Calculation.Publish`.
   *
   * ⛔ Причина й дата обов'язкові на сервері (`ECR-CALC-0422`, ФВ-14.7); тут
   * лише не пускають порожню форму — сама заборона лишається доменною.
   */
  const publish = useMutation({
    mutationFn: () =>
      publishMethodologyVersion(methodologyId, publishing?.versionId ?? 0, {
        changeReason: publishReason,
        // ⚠ `date`, не `Date`: `toISOString()` іде через UTC і ввечері
        // зсуває дату на добу назад (той самий застережний коментар, що й у
        // `MethodologiesPage.tsx`).
        effectiveFrom: publishEffectiveFrom,
      } satisfies PublishMethodologyRequest),
    onSuccess: async (diff) => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.methodologies.versionsOf(methodologyId),
      });
      setPublishing(null);
      setPublishReason('');
      setPublishEffectiveFrom('');
      setPublishDiff(diff);
      showDone(t('methodologies.published'));
    },

    // ⚠ Публікація падає з переліком конкретних проблем — немає золотого
    // тесту (ФВ-9.12), незайнята дата, цикл формул тощо (`ECR-CALC-0422`,
    // `ECR-CALC-0409`). `showApiError` показує ТЕКСТ відмови сервера, а не
    // узагальнене «не вдалося»: саме цей клас багів (проковтнута відповідь
    // сервера) уже знайдено в іншому місці цього аудиту.
    onError: showApiError,
  });

  const placement = useMemo<ExpressionPlacement>(
    () => ({ methodologyVersionId: editing?.versionId }),
    [editing?.versionId],
  );

  return (
    <Stack gap="md">
      <PageHeader
        title={t('methodologies.versionsTitle')}
        actions={
          <Group gap="xs">
            {/* ⛔ Публікація ВІДКРИТОЇ версії — тут, поруч із заголовком, а не
                лише в рядку таблиці внизу: саме сюди дивиться той, хто щойно
                заповнив усі панелі версії (формули, константи, виходи,
                правила, обов'язкові колонки, золотий набір, прив'язки) і шукає
                «що далі». */}
            {selected !== undefined && selected.status !== 'Published' && mayPublish && (
              <Button onClick={() => setPublishing({ versionId: selected.id })}>
                {t('methodologies.publish')}
              </Button>
            )}

            {mayEdit && (
              <Button variant="default" onClick={() => setCreating(true)}>
                {t('methodologies.newVersion')}
              </Button>
            )}
          </Group>
        }
      />

      {versionDeletion.refusal}

      <AsyncBoundary<MethodologyDraftVersionDto[]>
        isPending={versions.isPending && known}
        error={versions.error}
        data={known ? versions.data : []}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('methodologies.noVersions')}
        emptyHint={t('methodologies.noVersionsHint')}
        skeleton="table"
        onRetry={() => void versions.refetch()}
      >
        {(list) => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.version')}</Table.Th>
                <Table.Th>{t('methodologies.status')}</Table.Th>
                <Table.Th>{t('methodologies.modes')}</Table.Th>
                <Table.Th>{t('methodologies.effectiveFrom')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((version) => (
                <Table.Tr key={version.id}>
                  <Table.Td>{version.versionNumber}</Table.Td>
                  <Table.Td>
                    {/*
                     * ⛔ Тут стояв `<Badge variant={version.isEditable ? …}>
                     * {version.status}</Badge>` — дві вади в одному рядку. Код
                     * сервера (`Draft`, `Published`, `Deprecated`) друкувався
                     * як видимий текст; а колір ніс `isEditable`, тобто
                     * означав «чи можна правити», а не статус — застаріла
                     * версія й чернетка різнилися не тим, чим вони є.
                     *
                     * ⚠ `statusTable.version` навмисно один на обидва
                     * словники: `TemplateVersion.Status` і
                     * `MethodologyVersion.Status` — це той самий перелік
                     * (`StatusBadge.tsx`), тож і підпис у них один.
                     *
                     * ⚠ Що саме можна робити з версією, каже колонка дій
                     * праворуч, і це чесніше: право залежить не лише від
                     * стану, а й від профілю (`mayEditContent`).
                     */}
                    <StatusBadge kind="version" state={version.status} />
                  </Table.Td>
                  <Table.Td>
                    {/* ⚠ Режими стоять поруч зі статусом, а не в налаштуваннях:
                        саме вони визначають числа (`ФВ-9.9`, `ФВ-16.11`), і саме
                        їх клон переносить незмінними. */}
                    <Text size="sm" c="dimmed">
                      {version.numericMode} · {version.calendarMode} · {version.traceLevel}
                    </Text>
                  </Table.Td>
                  {/* ⚠ `dateOnly`, і не тому, що контракт віддає `Format: date`
                      (хоча віддає), а тому, що на питання цієї колонки — «з
                      якого ДНЯ періоди рахує ця версія» — година не відповідає
                      взагалі: дата набуття чинності порівнюється з календарем
                      періодів, а не з годинником. Приписати їй «12:00 AM»
                      означало б показати точність, якої в даних немає.

                      ⚠ Тире лишається дефолтне: `null` тут — «чернетка, ще не
                      опублікована» (`MethodologyDraftVersionDto.effectiveFrom`),
                      тобто значення справді НЕМАЄ, а не «діє безстроково». */}
                  <Table.Td>
                    <Timestamp value={version.effectiveFrom} dateOnly />
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      <Button
                        size="compact-xs"
                        variant={selected?.id === version.id ? 'filled' : 'subtle'}
                        onClick={() => setSelectedId(String(version.id))}
                      >
                        {t('methodologies.openVersion')}
                      </Button>

                      {/* ⛔ Публікація — тут, а не лише на переліку методологій
                          (`MethodologiesPage.tsx`): той екран не показує жодної
                          з панелей, якими version доводять до готовності
                          (формули, константи, золотий набір), тож кнопка на
                          ньому дає публікувати те, чого автор щойно не бачив. */}
                      {version.status !== 'Published' && mayPublish && (
                        <Button
                          size="compact-xs"
                          variant="default"
                          onClick={() => setPublishing({ versionId: version.id })}
                        >
                          {t('methodologies.publish')}
                        </Button>
                      )}

                      {versionDeletion.triggerFor(version)}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      {selected !== undefined && !selected.isEditable && (
        <Alert color="statusWarning" title={t('methodologies.readOnly')}>
          {t('methodologies.readOnlyHint')}
        </Alert>
      )}

      {selected !== undefined && (
        /* ⚠ `fallback={null}`, а не заглушка: панель з'являється разом із
            обраною версією, і чанк іде мережею паралельно із запитом її
            змісту. Смужка-привид на цей час показувала б «щось вантажиться»
            там, де секундою раніше не було нічого. */
        <Suspense fallback={null}>
          <MethodologyModesForm
            methodologyId={methodologyId}
            version={selected}
            editable={editable}
          />
        </Suspense>
      )}

      {selected !== undefined && (
        <>
          <Group justify="space-between">
            <Text fw={600}>{t('methodologies.formulas')}</Text>

            {editable && (
              <Button
                size="compact-sm"
                variant="default"
                onClick={() => {
                  setEditing({
                    versionId: selected.id,
                    code: '',
                    expression: '',
                    resultType: 'Number',
                    outputUnitId: null,
                    argumentsCsv: '',
                    isNew: true,
                  });
                  setExpressionErrors(null);
                }}
              >
                {t('methodologies.addFormula')}
              </Button>
            )}
          </Group>

          <AsyncBoundary<MethodologyFormulaDto[]>
            isPending={formulas.isPending}
            error={formulas.error}
            data={formulas.data}
            isEmpty={(list) => list.length === 0}
            emptyTitle={t('methodologies.noFormulas')}
            emptyHint={t('methodologies.noFormulasHint')}
            skeleton="table"
            onRetry={() => void formulas.refetch()}
          >
            {(list) => (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.formulaCode')}</Table.Th>
                    <Table.Th>{t('methodologies.expression')}</Table.Th>
                    <Table.Th>{t('methodologies.resultType')}</Table.Th>
                    <Table.Th>{t('methodologies.evaluationOrder')}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {list.map((formula) => (
                    <Table.Tr key={formula.id}>
                      <Table.Td>{formula.code}</Table.Td>
                      <Table.Td>
                        <Text size="sm" ff="monospace">
                          {formula.expression}
                        </Text>
                      </Table.Td>
                      <Table.Td>{formula.resultType}</Table.Td>
                      {/* ⚠ Порядок ПОКАЗУЄТЬСЯ і не редагується: він
                          топологічний і рахується при публікації (`ФВ-9.4`).
                          Поле вводу тут дозволило б людині зсунути обчислення
                          так, що помилка стала б числом у звіті. */}
                      <Table.Td>{formula.evaluationOrder}</Table.Td>
                      <Table.Td>
                        {editable && (
                          <Group gap="xs">
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              onClick={() => {
                                setEditing({
                                  versionId: selected.id,
                                  code: formula.code,
                                  expression: formula.expression,
                                  resultType: formula.resultType,
                                  outputUnitId: formula.outputUnitId,
                                  argumentsCsv: formula.argumentsCsv ?? '',
                                  isNew: false,
                                });
                                setExpressionErrors(null);
                              }}
                            >
                              {t('methodologies.editFormula')}
                            </Button>
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              color="statusError"
                              onClick={() =>
                                setDeleteTarget({ versionId: selected.id, code: formula.code })
                              }
                            >
                              {t('methodologies.deleteFormula')}
                            </Button>
                          </Group>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </AsyncBoundary>

          {/* ⛔ П'ять панелей стоять на ОДНОМУ екрані з формулами, а не по
              вкладках. Методологія не рахує нічого, поки бракує бодай однієї:
              формула без константи дає нуль, версія без оголошеного виходу
              нічого не записує, без правила відбору не зачіпає жодного рядка,
              без золотого набору не публікується (`ФВ-9.12`), а без прив'язки
              перерахунок завершується успіхом і не рахує нічого. Розкидані по
              вкладках, вони виглядали б необов'язковими. */}
          <Suspense fallback={null}>
          <MethodologyConstantsPanel
            methodologyId={methodologyId}
            versionId={selected.id}
            editable={editable}
          />

          <MethodologyOutputsPanel
            methodologyId={methodologyId}
            versionId={selected.id}
            editable={editable}
          />

          <MethodologyRulesPanel
            methodologyId={methodologyId}
            versionId={selected.id}
            editable={editable}
          />

          <MethodologyRequiredInputsPanel
            methodologyId={methodologyId}
            versionId={selected.id}
            editable={requiredInputsEditable}
          />

          <MethodologyTestsPanel
            methodologyId={methodologyId}
            versionId={selected.id}
            editable={editable}
          />

          {/* ⚠ Прив'язка належить МЕТОДОЛОГІЇ, а не версії: вона переживає всі
              версії одразу і клонуванням не копіюється. Тому право на неї
              питається окремо — від стану версії воно не залежить. */}
          <MethodologyBindingsPanel
            methodologyId={methodologyId}
            editable={can(session.data, 'Calculation.EditRule')}
          />
          </Suspense>
        </>
      )}

      <Modal
        opened={creating}
        onClose={() => setCreating(false)}
        title={t('methodologies.newVersionTitle')}
      >
        <Stack gap="sm">
          {/* ⛔ Пояснення стоїть у діалозі, а не в довідці: клон — це не
              «зробити копію», а єдиний спосіб змінити опубліковану версію
              (`ФВ-9.1`), і людина має розуміти, чому інакше не можна. */}
          <Alert color="statusWarning">{t('methodologies.cloneHint')}</Alert>

          <TextInput
            label={t('methodologies.versionNumber')}
            description={t('methodologies.versionNumberHint')}
            value={newVersion}
            onChange={(event) => setNewVersion(event.currentTarget.value)}
            data-autofocus
          />

          {/*
           * ⛔ Відмова `GET …/versions` робила цей перелік ПОРОЖНІМ і мовчала
           * (`D15-00`, L10). Порожнеча тут має готове хибне прочитання, і воно
           * коштує найдорожче саме в цьому діалозі: поруч стоїть плейсхолдер
           * «порожня чернетка», тож нуль варіантів читається як «копіювати нема
           * з чого — це перша версія методології». Людина, яка прийшла сюди
           * ЄДИНИМ дозволеним шляхом зміни опублікованої версії (`ФВ-9.1`,
           * клон), натомість заводить порожню чернетку — і далі пише формули з
           * нуля замість того, щоб правити копію чинних.
           *
           * ⚠ Банер сторінки під модалкою (`AsyncBoundary` над таблицею) цього
           * не рятує: модалка перекриває сторінку, і користувач бачить лише її
           * вміст.
           *
           * ⚠ Порядок — `error` → `isPending` → дані. Під час першого запиту
           * перелік НЕДОСТУПНИЙ, а не порожній: вимкнений контрол не обіцяє
           * фактів, яких ще ніхто не читав.
           */}
          {versions.error !== null ? (
            <ErrorAlert error={versions.error} onRetry={() => void versions.refetch()} />
          ) : (
            <Select
              label={t('methodologies.copyFrom')}
              description={t('methodologies.copyFromHint')}
              placeholder={t('methodologies.emptyDraft')}
              clearable
              disabled={versions.isPending}
              value={copyFrom}
              data={all.map((version) => ({
                value: String(version.id),
                // ⚠ У варіанті списку компонента бути не може — потрібен РЯДОК.
                // Тому підпис береться тим самим ключем каталогу, яким малює
                // `StatusBadge`: інакше та сама версія називалася б у таблиці
                // мовою користувача, а тут — англійським `Published`.
                label: `${version.versionNumber} · ${t(statusKey('version', version.status))}`,
              }))}
              onChange={setCopyFrom}
            />
          )}

          {/* ⛔ Показується лише для ПОРОЖНЬОЇ чернетки: клон бере рівень із
              джерела, і поле вводу поруч із обраним джерелом обіцяло б вибір,
              якого сервер не зробить. */}
          {copyFrom === null && (
            <Select
              label={t('methodologies.level')}
              description={t('methodologies.levelHint')}
              allowDeselect={false}
              value={level}
              data={[
                { value: 'Configuration', label: 'Configuration' },
                { value: 'Module', label: 'Module' },
              ]}
              onChange={(value) => setLevel(value === 'Module' ? 'Module' : 'Configuration')}
            />
          )}

          <Button
            disabled={newVersion.trim().length === 0}
            loading={create.isPending}
            onClick={() => create.mutate()}
          >
            {t('methodologies.createVersion')}
          </Button>
        </Stack>
      </Modal>

      <Modal
        opened={editing !== null}
        onClose={() => {
          setEditing(null);
          setExpressionErrors(null);
        }}
        title={t('methodologies.formulaTitle')}
        size="lg"
      >
        {editing !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('methodologies.formulaCode')}
              description={t('methodologies.formulaCodeHint')}
              value={editing.code}
              disabled={!editing.isNew}
              onChange={(event) =>
                setEditing({ ...editing, code: event.currentTarget.value })
              }
            />

            <ExpressionEditor
              value={editing.expression}
              onChange={(value) => setEditing({ ...editing, expression: value })}
              dialect="Methodology"
              placement={placement}
              ariaLabel={t('methodologies.expression')}
              height="140px"
              onValidated={setExpressionErrors}
            />

            {/* ⛔ UI-аудит, lane 5: підкреслення в Monaco саме по собі не
                несе тексту (ані підказки, ані тултипа) — без цього блоку
                єдиний спосіб дізнатися, ЩО саме не так, був недоступний
                із цього діалогу взагалі, хоча сервер його вже повертає. */}
            {(expressionErrors?.diagnostics.length ?? 0) > 0 && (
              <Alert color="statusError" title={t('expressions.findings', { count: expressionErrors?.diagnostics.length ?? 0 })}>
                <Stack gap="xs">
                  {expressionErrors?.diagnostics.map((d, index) => (
                    <Text key={`${d.code}-${String(d.position)}-${String(index)}`} size="sm">
                      <Text span fw={600}>
                        {d.code}
                      </Text>{' '}
                      {localizedMessage(d)}
                    </Text>
                  ))}
                </Stack>
              </Alert>
            )}

            <Select
              label={t('methodologies.resultType')}
              description={t('methodologies.resultTypeHint')}
              allowDeselect={false}
              value={editing.resultType}
              data={[
                { value: 'Number', label: t('methodologies.resultNumber') },
                { value: 'Text', label: t('methodologies.resultText') },
              ]}
              onChange={(value) =>
                setEditing({
                  ...editing,
                  resultType: value === 'Text' ? 'Text' : 'Number',

                  // ⛔ Текстовий результат не має одиниці: вимір — властивість
                  // числа (`ФВ-16.6`). Лишити її означало б відправити запит,
                  // який сервер відхилить, — і показати відмову там, де вибір
                  // уже зроблено правильно.
                  outputUnitId: value === 'Text' ? null : editing.outputUnitId,
                })
              }
            />

            {/* ⛔ Оголошений список аргументів. Доти його не було чим
                заповнити, і звірка пастки 2 (`ECR-CALC-0432`) отримувала
                `null` на кожній формулі й мовчала: перевірка була написана,
                покрита тестами й недосяжна. */}
            <TextInput
              label={t('methodologies.arguments')}
              description={t('methodologies.argumentsHint')}
              value={editing.argumentsCsv}
              onChange={(event) =>
                setEditing({ ...editing, argumentsCsv: event.currentTarget.value })
              }
            />

            {/*
             * ⛔ `GET /api/v1/units` збирався через `?? []`, і його відмова
             * давала перелік із нуля варіантів (`D15-00`, L10). Прочитання
             * порожнечі тут однозначне й хибне: плейсхолдер каже «без одиниці»,
             * тож людина читає «жодної одиниці в системі не заведено» — і
             * зберігає ЧИСЛОВУ формулу безрозмірною. Сервер таку формулу
             * приймає (`outputUnitId` необов'язковий), тож помилка не
             * спливає ніде: вимір — властивість числа (`ФВ-16.6`), і число без
             * нього доїжджає до звіту як є.
             *
             * ⚠ Перелік не малюється зовсім: вимкнений `Select` із нулем
             * варіантів однаково виглядав би як факт про світ.
             */}
            {editing.resultType === 'Number' &&
              (units.error !== null ? (
                <ErrorAlert error={units.error} onRetry={() => void units.refetch()} />
              ) : (
                <Select
                  label={t('methodologies.outputUnit')}
                  description={t('methodologies.outputUnitHint')}
                  placeholder={t('methodologies.noUnit')}
                  clearable
                  searchable
                  disabled={units.isPending}
                  value={editing.outputUnitId === null ? null : String(editing.outputUnitId)}
                  data={(units.data ?? []).map((unit) => ({
                    value: String(unit.id),
                    label: unit.code,
                  }))}
                  onChange={(value) =>
                    setEditing({
                      ...editing,
                      outputUnitId: value === null ? null : Number(value),
                    })
                  }
                />
              ))}

            <Button
              disabled={
                editing.code.trim().length === 0 ||
                editing.expression.trim().length === 0 ||
                (expressionErrors?.diagnostics.length ?? 0) > 0
              }
              loading={save.isPending}
              onClick={() => save.mutate(editing)}
            >
              {t('methodologies.saveFormula')}
            </Button>
          </Stack>
        )}
      </Modal>

      {/*
       * ⛔ Той самий рисунок, що й підтвердження правки поза вікном доступу в
       * `DocumentGrid` (`ФВ-2.16`, `#43`): просте так/ні, а не `ReasonModal`
       * — видалення формули чернетки не лишає підстави в журналі так, як
       * публікація чи перемикання джерела реєстру, тож форма з причиною тут
       * питала б про те, чого нікуди не пише. Закриття без кнопки (хрестик,
       * `Esc`, клік поза) — СКАСУВАННЯ: `remove.mutate` тут не викликаний
       * узагалі, формула лишається на місці.
       */}
      <Modal
        opened={deleteTarget !== null}
        onClose={() => setDeleteTarget(null)}
        title={t('methodologies.deleteFormulaConfirmTitle')}
      >
        <Text size="sm" mb="md">
          {t('methodologies.deleteFormulaConfirmText', { code: deleteTarget?.code ?? '' })}
        </Text>
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={() => setDeleteTarget(null)}>
            {t('common.cancel')}
          </Button>
          <Button
            color="statusError"
            loading={remove.isPending}
            onClick={() => {
              if (deleteTarget !== null) remove.mutate(deleteTarget);
            }}
          >
            {t('methodologies.deleteFormulaConfirmTitle')}
          </Button>
        </Group>
      </Modal>

      {/*
       * ⛔ Причина й дата — той самий патерн, що й публікація зі списку
       * методологій (`MethodologiesPage.tsx`) і публікація версії шаблону
       * (`TemplateVersionPage.tsx`, `ReasonModal`): обидва поля обов'язкові на
       * сервері (`ECR-CALC-0422`, ФВ-14.7), і кнопка тут вимкнена, доки вони
       * порожні — не з ввічливості, а щоб не вести на гарантовану відмову.
       */}
      <Modal
        opened={publishing !== null}
        onClose={() => setPublishing(null)}
        title={t('methodologies.publishTitle')}
      >
        <Stack gap="sm">
          <Textarea
            label={t('methodologies.reason')}
            description={t('methodologies.reasonHint')}
            value={publishReason}
            onChange={(event) => setPublishReason(event.currentTarget.value)}
            minRows={3}
            autosize
            data-autofocus
          />

          <TextInput
            // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №8/8: перехід на DateInput змінює тип значення (string → Date), тому окремим PR; список боргу сторожить lintRules.test.ts
            type="date"
            label={t('methodologies.effectiveFrom')}
            description={t('methodologies.effectiveFromHint')}
            value={publishEffectiveFrom}
            onChange={(event) => setPublishEffectiveFrom(event.currentTarget.value)}
          />

          <Button
            disabled={publishReason.trim().length === 0 || publishEffectiveFrom.length === 0}
            loading={publish.isPending}
            onClick={() => publish.mutate()}
          >
            {t('methodologies.publish')}
          </Button>
        </Stack>
      </Modal>

      {/*
       * ⛔ Diff РЕЗУЛЬТАТІВ, не тексту формул (ФВ-9.6): змінений рядок виразу
       * не каже нічого, змінена на 4 % емісія каже все. Той самий вигляд, що
       * й на `MethodologiesPage.tsx` — друга розбіжна відповідь на «що
       * показати після публікації» була б гіршою за одну спільну.
       */}
      <Modal
        opened={publishDiff !== null}
        onClose={() => setPublishDiff(null)}
        title={t('methodologies.diffTitle')}
        size="lg"
      >
        {publishDiff !== null && (
          <Stack gap="sm">
            <Text size="sm">
              {t('methodologies.diffNumeric')}: {publishDiff.numeric.before} → {publishDiff.numeric.after}
            </Text>
            <Text size="sm">
              {t('methodologies.diffCalendar')}: {publishDiff.calendar.before} → {publishDiff.calendar.after}
            </Text>

            <Text fw={600}>{t('methodologies.diffChanges')}</Text>
            {publishDiff.changes.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('methodologies.diffNone')}
              </Text>
            ) : (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.testCode')}</Table.Th>
                    <Table.Th>{t('methodologies.output')}</Table.Th>
                    <Table.Th>{t('methodologies.before')}</Table.Th>
                    <Table.Th>{t('methodologies.after')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {publishDiff.changes.map((change) => (
                    <Table.Tr
                      key={`${change.testCode}:${change.outputCode}:${String(change.substanceEntryId)}`}
                    >
                      <Table.Td>{change.testCode}</Table.Td>
                      <Table.Td>{change.outputCode}</Table.Td>
                      <Table.Td>{change.before ?? '—'}</Table.Td>
                      <Table.Td>{change.after}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {(publishDiff.warnings ?? []).length > 0 && (
              <>
                <Text fw={600}>{t('methodologies.warnings')}</Text>
                <Code block>{(publishDiff.warnings ?? []).join('\n')}</Code>
              </>
            )}
          </Stack>
        )}
      </Modal>

      {versionDeletion.dialog}
    </Stack>
  );
}
