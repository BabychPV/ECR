import { useEffect, useState, type JSX } from 'react';
import { Checkbox, Select, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { CreateDocumentRequest, DocumentIdResponse } from '@/api/types';
import { projectsQuery } from '@/features/projects/allProjects';
import { listDocuments } from './api';
import { groupRuleViolations } from './groupRuleViolations';
import { localized } from '@/shared/i18n/localized';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
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
 * Кроки: Project → Sheets → Review (додає `Wizard`; ✎ Period clarity: кроку Period немає — «один документ
 * на об'єкт, усі місяці всередині»). Кроку «Template»
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
  preferredPeriodKey = null,
}: {
  opened: boolean;
  onClose: () => void;
  /**
   * ✎ Period clarity (людина 07.11, п. e): кроку «Period» більше немає — документ охоплює весь рік (сервер період
   * не приймає). Місяць, обраний у переліку, лише визначає, на якому місяці документ відкриється після створення.
   */
  preferredPeriodKey?: number | null;
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

  // ⚠ Скидання при кожному відкритті — як і дані самого `Wizard`: вибір попереднього
  // разу, що лишився в полях, — найтихіший спосіб створити документ не того проєкту.
  useEffect(() => {
    if (!opened) return;

    setProjectId(null);
    setPickedSheets(null);
    setName({});
  }, [opened]);

  const projects = useQuery({
    ...projectsQuery,
    enabled: opened,
  });

  /*
   * ✎ Pass2 п. 4 (людський прохід 07.11, P3): той самий проєкт двічі давав два документи без жодного слова, хоча
   * перелік каже «один документ на об'єкт». Дублі ДОЗВОЛЕНІ задумом (документ унікальний за `(ProjectId,
   * BusinessKey)`, ключ видає сервер — `docs/tz/01-context.md` «Документ», `02a-db-schema.md` `UQ_Document`), тож
   * сервер не відмовляє, а майстер ПОПЕРЕДЖАЄ: документи проєкту — наявним переліком (`listDocuments`).
   * ⚠ Ключ під `['documents', …]`: створення інвалідує його разом із переліком.
   */
  const existing = useQuery({
    queryKey: ['documents', 'create-existing', projectId],
    queryFn: () => listDocuments({ periodKey: null, projectId: Number(projectId) }),
    enabled: opened && projectId !== null,
  });

  const existingDocs = existing.data?.items ?? [];
  const firstExisting = existingDocs[0] ?? null;

  const template = useQuery({
    queryKey: ['projects', Number(projectId), 'document-template'],
    queryFn: () =>
      apiFetch<DocumentTemplateDto>(`/api/v1/projects/${projectId ?? ''}/document-template`),
    enabled: opened && projectId !== null,
  });

  const versionId = template.data?.templateVersionId ?? null;
  const available = template.data?.sheets ?? [];
  const sheets = pickedSheets ?? available.map((sheet) => sheet.id);

  const projectCode =
    projects.data?.items.find((project) => String(project.id) === projectId)?.code ?? null;

  const sheetLabel = (sheet: (typeof available)[number]): string =>
    `${localized(sheet.nameL10n) || sheet.code} (${sheet.code})`;

  // ⛔ Директива «live-попередження про порушення SheetGroupRule»: не блокує — сервер
  // лишається останньою лінією правди.
  const violations = groupRuleViolations(template.data, sheets);

  const create = useMutation({
    // ⛔ N3-05: відмову показує майстер (банер у кроці Review через `api.fail` у per-call
    // `onError`). Глобальна сітка (`queryClient.ts`) бачить лише `options.onError`/`meta.handled`,
    // тож без прапорця додавала другий тост поверх банера.
    meta: { handled: true },
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

  /*
   * ✎ Pass2 п. 5 (P3): Esc / ✕ / Cancel посеред майстра мовчки губили введене. Вибір живе тут, а не в `data`
   * майстра (див. шапку), тож `Wizard` не бачить «незбереженого» — питання ставить сам майстер документа, коли є
   * що втрачати (обраний проєкт, змінений склад аркушів або назва). Порожній майстер закривається одразу.
   */
  const hasInput = projectId !== null || pickedSheets !== null || hasAnyText(name);
  const [discarding, setDiscarding] = useState(false);

  useEffect(() => {
    if (opened) setDiscarding(false);
  }, [opened]);

  const requestClose = (): void => {
    if (hasInput) setDiscarding(true);
    else onClose();
  };

  /** Відкрити наявний документ замість створення ще одного (п. 4). */
  const openExisting = async (documentId: number): Promise<void> => {
    onClose();
    await navigate(
      `/documents/${String(documentId)}` +
        (preferredPeriodKey === null ? '' : `?periodKey=${String(preferredPeriodKey)}`),
    );
  };

  const existingBanner =
    firstExisting === null ? null : (
      <Banner
        tone="warning"
        testId="create-existing-documents"
        title={t('documents.createExistsTitle')}
        text={t('documents.createExistsText', { key: firstExisting.businessKey })}
        actions={[
          {
            label: t('documents.createExistsOpen', { key: firstExisting.businessKey }),
            onClick: () => void openExisting(firstExisting.id),
          },
          ...(existingDocs.length > 1 || (existing.data?.nextCursor ?? null) !== null
            ? [
                {
                  label: t('documents.createExistsShowAll'),
                  onClick: () => {
                    onClose();
                    // Перелік документів — маршрут `/`; фільтр проєкту — `?projectId=` (`documentListFilters`).
                    void navigate(`/?projectId=${String(projectId)}`);
                  },
                  variant: 'subtle' as const,
                },
              ]
            : []),
        ]}
      />
    );

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

          {existingBanner}

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
    <>
    <Wizard<NoData>
      opened={opened && armed}
      title={t('documents.create')}
      initialData={NoInitialData}
      steps={steps}
      labels={{ back: t('wizard.back'), next: t('wizard.next'), review: t('wizard.review') }}
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
      // ✎ Pass2 п. 4: підсумок перед створенням теж каже, що документ проєкту вже є (це буде ще один).
      reviewText={
        firstExisting === null
          ? t('documents.createReviewText')
          : `${t('documents.createReviewText')} ${t('documents.createExistsReview', { key: firstExisting.businessKey })}`
      }
      onApply={(_data, api) => {
        create.mutate(undefined, {
          onSuccess: async (result) => {
            await queryClient.invalidateQueries({ queryKey: ['documents'] });

            // ⚠ Не `api.close()`: це той самий `onClose` майстра, тобто `requestClose` з питанням «Discard?» —
            // після створення втрачати нічого.
            onClose();
            showDone(t('documents.created'));

            // Одразу відкриваємо документ: інакше людина шукає його в переліку за
            // бізнес-ключем, якого ще не бачила.
            // ✎ Period clarity: документ — на весь рік; відкривається на місяці, обраному в переліку, а без
            // нього — на поточному (`DocumentPage`).
            await navigate(
              `/documents/${result.documentId}` +
                (preferredPeriodKey === null ? '' : `?periodKey=${String(preferredPeriodKey)}`),
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
      onClose={requestClose}
      // ⚠ `dirty` майстра тут ніколи не стає `true` (вибір живе в цьому компоненті, `update` не викликається),
      // тож питання «Discard?» ставить `requestClose` вище (✎ Pass2 п. 5), а не цей колбек.
      onExitUnsaved={() => true}
    />

    {/* ✎ Pass2 п. 5: підтвердження всередині сторінки (не `window.confirm`) — той самий `ConfirmModal`, що в
        `BindingWizard`/`AddTableWizard`. «Keep editing» лишає майстер із введеним. */}
    <ConfirmModal
      opened={opened && discarding}
      title={t('documents.createDiscardTitle')}
      text={t('documents.createDiscardText')}
      verb={t('documents.createDiscardVerb')}
      cancelLabel={t('documents.createKeepEditing')}
      onConfirm={() => {
        setDiscarding(false);
        onClose();
      }}
      onClose={() => setDiscarding(false)}
    />
    </>
  );
}
