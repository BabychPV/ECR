import { useEffect, useState, type JSX } from 'react';
import { Checkbox, Select, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { CreateDocumentRequest, DocumentIdResponse, PeriodCalendarDto } from '@/api/types';
import { fetchAllProjects } from '@/features/projects/allProjects';
import { groupRuleViolations } from './groupRuleViolations';
import { newestOpenPeriodKey } from './newDocumentPeriod';
import { formatPeriodKey } from '@/shared/format';
import { localized } from '@/shared/i18n/localized';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { KeyValue } from '@/shared/ui/KeyValue';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';
import { t } from '@/shared/i18n';

/**
 * Створення документа (`ФВ-3.1`, `ФВ-3.2`) — майстром (UI-31, `KIT.md` §6.9:
 * «≥ 2 кроків або є що перевірити перед застосуванням (створити документ…)» →
 * `Wizard`; макет — `docs/design/hybrid/screens-work.js`, `openCreate`).
 *
 * Кроки: Project → Period → Sheets → Review (додає `Wizard`). Кроку «Template»
 * з макета немає навмисно: версію шаблону визначає ПРОЄКТ (V-12, V-11), обирати
 * її нема з чого — тож вона показується в кроці Project і в підсумку.
 *
 * ⛔ Дії не було в інтерфейсі: сторож вважав `POST /documents` досяжним, бо
 * клієнт ЧИТАЄ `GET /documents` тією самою адресою (`A7-42`).
 *
 * ⛔ Склад аркушів перевіряє сервер за `SheetGroupRule` (`ФВ-3.2`); тут —
 * live-попередження, і людина бачить склад у підсумку ДО створення («N з M»).
 * Типово включені всі аркуші, які віддав сервер (макет: «All are included by
 * default») — `RequiresAll`/`RequiresOne` повний склад не порушує.
 *
 * ⛔ Аркуші — ЛИШЕ з відповіді `GET /projects/{id}/document-template`: сервер
 * не віддає аркушів, яких користувач не бачить (прихований аркуш, P1), і
 * клієнт нічого не домальовує сам.
 *
 * ⚠ Вибір живе в цьому компоненті, а не в `data` майстра: від проєкту
 * залежать запити (шаблон, календар), а хуки запитів мусять жити тут.
 * `Wizard` дає кроки, перевірку, банер помилки й підсумок.
 */

// ⚠ Прямо зі схеми: `api/types.ts` — спільний файл.
type DocumentTemplateDto = components['schemas']['DocumentTemplateDto'];

/** Даних майстра немає: стан — нижче, у компоненті (див. шапку). */
type NoData = Record<string, never>;
const NoInitialData: NoData = {};

export function CreateDocumentModal({
  opened,
  onClose,
}: {
  opened: boolean;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  // ⛔ T2-06: діалог монтується ВЖЕ відкритим (лінивий чанк + `creatingRequested &&` на сторінці), а
  // Mantine `useFocusReturn` запам'ятовує елемент лише при ПЕРЕХОДІ `opened` false → true. Без переходу
  // перше закриття (Esc) лишало фокус на `BODY`. Тому майстер спершу монтується закритим і
  // відкривається наступним рендером, поки фокус ще на кнопці.
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    setArmed(true);
  }, []);

  const [projectId, setProjectId] = useState<string | null>(null);

  // `null` — типовий склад (усі аркуші з відповіді сервера); масив — вибір людини.
  const [pickedSheets, setPickedSheets] = useState<number[] | null>(null);

  // ⛔ Опційне: `BusinessKey` лишається унікальним технічним ключем незалежно від імені.
  const [name, setName] = useState<LocalizedValue>({});

  // A2-05: період, у якому відкриється документ; типово — найновіший ВІДКРИТИЙ.
  const [pickedPeriod, setPickedPeriod] = useState<string | null>(null);

  // ⚠ Скидання при кожному відкритті — як і дані самого `Wizard`: вибір попереднього
  // разу, що лишився в полях, — найтихіший спосіб створити документ не того проєкту.
  useEffect(() => {
    if (!opened) return;

    setProjectId(null);
    setPickedSheets(null);
    setName({});
    setPickedPeriod(null);
  }, [opened]);

  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: fetchAllProjects,
    enabled: opened,
  });

  const template = useQuery({
    queryKey: ['projects', Number(projectId), 'document-template'],
    queryFn: () =>
      apiFetch<DocumentTemplateDto>(`/api/v1/projects/${projectId ?? ''}/document-template`),
    enabled: opened && projectId !== null,
  });

  const calendar = useQuery({
    queryKey: ['periods', Number(projectId)],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${projectId ?? ''}/periods`),
    enabled: opened && projectId !== null,
  });

  const versionId = template.data?.templateVersionId ?? null;
  const available = template.data?.sheets ?? [];
  const sheets = pickedSheets ?? available.map((sheet) => sheet.id);

  // ✎ 2026-10-06, рішення людини: сервер не забороняє документ у закритому періоді, і майстер
  // теж — закритий період у переліку є, з попередженням. Не пропонуються лише ще не відкриті.
  const choosablePeriods = (calendar.data?.periods ?? [])
    .filter((period) => period.state === 'Open' || period.state === 'Grace' || period.state === 'Closed')
    .sort((a, b) => b.periodKey - a.periodKey);
  const newestOpen = newestOpenPeriodKey(calendar.data?.periods);
  const periodKey =
    pickedPeriod ??
    (newestOpen === null ? (choosablePeriods[0] === undefined ? null : String(choosablePeriods[0].periodKey)) : String(newestOpen));
  const pickedState = choosablePeriods.find((period) => String(period.periodKey) === periodKey)?.state ?? null;
  const periodLabel = (key: number): string =>
    formatPeriodKey(key, calendar.data?.periodKind) || String(key);

  const projectCode =
    projects.data?.items.find((project) => String(project.id) === projectId)?.code ?? null;

  const sheetLabel = (sheet: (typeof available)[number]): string =>
    `${localized(sheet.nameL10n) || sheet.code} (${sheet.code})`;

  // ⛔ Директива «live-попередження про порушення SheetGroupRule»: не блокує — сервер
  // лишається останньою лінією правди.
  const violations = groupRuleViolations(template.data, sheets);

  const create = useMutation({
    mutationFn: () =>
      apiFetch<DocumentIdResponse>('/api/v1/documents', {
        method: 'POST',
        body: JSON.stringify({
          projectId: Number(projectId),
          templateVersionId: Number(versionId),
          sheetDefIds: sheets,

          // ⚠ Поле пропускається цілком, а не надсилається `undefined`
          // (`exactOptionalPropertyTypes`).
          ...(hasAnyText(name) ? { name } : {}),
        } satisfies CreateDocumentRequest),
      }),
  });

  /*
   * ⛔ Перелік проєктів збирався через `?? []`, тобто відмова сервера робила
   * його порожнім і мовчала: людина читала його як «активних проєктів немає».
   */
  const sourceError = projects.error ?? null;

  const steps: WizardStep<NoData>[] = [
    {
      id: 'project',
      label: t('documents.project'),
      hint: t('documents.createProjectHint'),
      canNext: () => projectId !== null && versionId !== null,
      render: () => (
        <Stack gap="xs">
          {sourceError !== null && (
            <ErrorAlert error={sourceError} onRetry={() => void projects.refetch()} />
          )}

          <Select
            label={t('documents.project')}
            placeholder={t('periods.pickProject')}
            required
            data={(projects.data?.items ?? [])
              // ⛔ Лише активні: у чернетці періоди закриті (`A7-25`).
              .filter((project) => project.status === 'Active')
              .map((project) => ({ value: String(project.id), label: project.code }))}
            value={projectId}
            onChange={(value) => {
              setProjectId(value);

              // Аркуші належать версії проєкту: залишений вибір від попереднього послав би
              // на сервер ідентифікатори з чужої структури.
              setPickedSheets(null);
              setPickedPeriod(null);
            }}
          />

          {/* ⚠ Відмова окремим банером: без нього «немає права» чи архівний шаблон
              (`ECR-TMPL-0409`) виглядали б як проєкт без аркушів. */}
          {template.error !== null && (
            <ErrorAlert error={template.error} onRetry={() => void template.refetch()} />
          )}

          {template.data !== undefined && (
            <KeyValue
              items={[
                {
                  label: t('documents.version'),
                  value: `${template.data.templateCode} · v${template.data.version}`,
                  hint: t('documents.createTemplateHint'),
                },
              ]}
            />
          )}

          <LocalizedInput
            label={t('documents.name')}
            description={t('documents.nameHint')}
            value={name}
            onChange={setName}
          />
        </Stack>
      ),
    },
    {
      id: 'period',
      label: t('documents.period'),
      hint: t('documents.createPeriodHint'),
      render: () =>
        calendar.data !== undefined && choosablePeriods.length === 0 ? (
          <Banner tone="warning" text={t('documents.createNoOpenPeriod')} testId="create-no-open-period" />
        ) : (
          <Stack gap="xs">
            {calendar.error !== null && (
              <ErrorAlert error={calendar.error} onRetry={() => void calendar.refetch()} />
            )}
            <Select
              label={t('documents.period')}
              data={choosablePeriods.map((period) => ({
                value: String(period.periodKey),
                // ⚠ Ключі літералами (сторож EndpointCoverageTests): не-Open тут лише Grace і Closed.
                label:
                  period.state === 'Open'
                    ? periodLabel(period.periodKey)
                    : `${periodLabel(period.periodKey)} · ${
                        period.state === 'Closed' ? t('status.period.Closed') : t('status.period.Grace')
                      }`,
              }))}
              value={periodKey}
              onChange={(value) => setPickedPeriod(value)}
              allowDeselect={false}
            />

            {/* ⚠ Лише попередження, не відмова: створення в закритому періоді дозволене. */}
            {pickedState === 'Closed' && (
              <Banner tone="warning" text={t('documents.createClosedPeriod')} testId="create-closed-period" />
            )}
          </Stack>
        ),
    },
    {
      id: 'sheets',
      label: t('documents.sheets'),
      hint: t('documents.createSheetsHint'),
      validate: () =>
        sheets.length === 0
          ? { message: t('documents.createNoSheets'), focus: 'input[type="checkbox"]' }
          : null,
      render: () => (
        <Stack gap="xs">
          <Text size="xs" c="dimmed">
            {t('documents.sheetsHint')}
          </Text>

          {available.map((sheet) => (
            <Checkbox
              key={sheet.id}
              label={sheetLabel(sheet)}
              checked={sheets.includes(sheet.id)}
              onChange={(event) => {
                // ⛔ `checked` читається ОДРАЗУ: `event.currentTarget` React обнуляє після
                // обробника, а апдейтер під `StrictMode` викликається двічі.
                const checked = event.currentTarget.checked;

                setPickedSheets(
                  checked ? [...sheets, sheet.id] : sheets.filter((id) => id !== sheet.id),
                );
              }}
            />
          ))}

          {violations.length > 0 && (
            <Stack gap="xs" mt="xs" data-testid="create-group-violations">
              {violations.map((message) => (
                <Text key={message} size="sm" c="statusError">
                  {message}
                </Text>
              ))}
            </Stack>
          )}
        </Stack>
      ),
    },
  ];

  const included = available.filter((sheet) => sheets.includes(sheet.id));

  return (
    <Wizard<NoData>
      opened={opened && armed}
      title={t('documents.create')}
      initialData={NoInitialData}
      steps={steps}
      labels={{ back: t('wizard.back'), next: t('wizard.next'), review: t('wizard.review') }}
      reviewText={t('documents.createReviewText')}
      applyLabel={t('documents.createApply')}
      isApplying={create.isPending}
      summary={() => (
        <KeyValue
          items={[
            { label: t('documents.project'), value: projectCode },
            {
              label: t('documents.version'),
              value: template.data === undefined ? null : template.data.templateCode,
              mono: true,
              ...(template.data === undefined ? {} : { hint: `v${template.data.version}` }),
            },
            {
              label: t('documents.period'),
              value: periodKey === null ? null : periodLabel(Number(periodKey)),
            },
            {
              label: t('documents.sheets'),
              value: t('documents.createSheetsCount', {
                included: included.length,
                total: available.length,
              }),
              hint: included.map((sheet) => localized(sheet.nameL10n) || sheet.code).join(' · '),
            },
            { label: t('documents.name'), value: hasAnyText(name) ? localized({ values: name }) : null },
          ]}
        />
      )}
      onApply={(_data, api) => {
        create.mutate(undefined, {
          onSuccess: async (result) => {
            await queryClient.invalidateQueries({ queryKey: ['documents'] });

            api.close();
            showDone(t('documents.created'));

            // Одразу відкриваємо документ: інакше людина шукає його в переліку за
            // бізнес-ключем, якого ще не бачила.
            await navigate(
              `/documents/${result.documentId}` + (periodKey === null ? '' : `?periodKey=${periodKey}`),
            );
          },

          // ⚠ Відмова (порушення складу, 403, 409) — банером у кроці Review, без втрати
          // введеного: «Back» веде до аркушів, де її можна виправити.
          onError: (error) => {
            const shown = problemText(error);
            api.fail(shown.detail === null ? shown.title : `${shown.title}: ${shown.detail}`);
          },
        });
      }}
      onClose={onClose}
      // ⚠ Нічого не створено до «Create document», а вибір (проєкт, період, аркуші)
      // відновлюється за кілька кліків — питання «втратити зміни?» тут лише
      // привчало б відповідати «так», не читаючи.
      onExitUnsaved={() => true}
    />
  );
}
