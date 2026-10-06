import { useMemo, useState, type JSX } from 'react';
import { Anchor, Button, Group, Modal, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  CreateTemplateRequest,
  TemplateIdResponse,
  TemplatePage,
  TemplateVersionsForTemplate,
  TemplateVersionSummary,
} from '@/api/types';
import {
  draftCount,
  filterTemplateRows,
  publishedVersionCount,
  toTemplateListRow,
  type TemplateListRow,
} from '@/features/templates/templateListModel';
import { can, useSession } from '@/shared/session/useSession';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { ListPage } from '@/shared/ui/ListPage';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Перелік шаблонів (`UI-34`): поточна версія, чернетка, стан.
 *
 * Макет: `docs/design/hybrid/screens-templates.js` → «1. /admin/templates —
 * перелік» (`ListPage + StatStrip`, `KIT.md` §1.5 і §3): шапка з поясненням і
 * однією головною дією, смуга показників, один рядок фільтрів, таблиця; клац
 * по рядку веде на картку шаблону.
 *
 * ⚠ Ланцюжок бейджів УСІХ версій і кнопка «New version» у рядку прибрані за
 * макетом: історія версій і клон живуть на картці шаблону
 * (`TemplateVersionsSection`, `NewTemplateVersionModal`), а перелік відповідає
 * на «що зараз чинне і що в роботі». Ревізія вигляду (`PresentationRevision`,
 * D-16) лишається біля поточної версії — без неї незрозуміло, чому кеш
 * оновився без нової версії.
 */
export function TemplatesPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  const [creating, setCreating] = useState(false);
  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
  });

  const items = templates.data?.items ?? [];
  const templateIds = items.map((template) => template.id);

  /*
   * ⛔ BR-07: до цього тут стояв `useQueries` — ОКРЕМИЙ HTTP-запит версій на
   * КОЖЕН шаблон переліку (N шаблонів → N запитів; підтверджений 2026-09-25
   * пробіл продуктивності). Версії читалися ПО ШАБЛОНУ:
   * `GET /api/v1/templates/{id}/versions` (до аудиту сторінка била в
   * `/api/v1/template-versions`, якого не існує, — `A7-03`).
   *
   * Тепер — ОДИН запит на весь видимий перелік:
   * `GET /api/v1/templates/versions?ids=...` (`TemplatesController.
   * ListVersionsForTemplates` → `ListTemplateVersionsHandler.HandleBatchAsync`).
   *
   * ⚠ Ключ кешу — `queryKeys.templates.versionsBatch(ids)`: навмисно НЕ
   * префікс і не суфікс `versionsOf(id)`/`allVersionsOf()` — ті лишаються
   * ОКРЕМИМ записом, бо на них і далі спираються ІНШІ екрани
   * (`TemplateVersionsSection`, `ExpressionsPage`, `TemplateVersionPage`,
   * `CreateProjectModal`, `breadcrumbResolvers`).
   */
  const versionsBatch = useQuery({
    queryKey: queryKeys.templates.versionsBatch(templateIds),
    queryFn: () => {
      const idsQuery = templateIds.map((id) => `ids=${id}`).join('&');
      return apiFetch<readonly TemplateVersionsForTemplate[]>(
        `/api/v1/templates/versions?${idsQuery}`,
      );
    },
    enabled: templateIds.length > 0,
  });

  const versionsError = versionsBatch.error;

  /**
   * Створення шаблону (`ФВ-2.1`).
   *
   * ⛔ Дії не було в інтерфейсі зовсім: сторож вважав `POST /templates`
   * досяжним лише тому, що клієнт читає `GET /templates` тією ж адресою
   * (`A7-42`). Тобто перший шаблон системи неможливо було завести інакше, як
   * запитом повз інтерфейс.
   */
  const create = useMutation({
    mutationFn: () =>
      apiFetch<TemplateIdResponse>('/api/v1/templates', {
        method: 'POST',
        body: JSON.stringify({ code: code.trim(), nameL10n: name } satisfies CreateTemplateRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.list() });
      setCreating(false);
      setCode('');
      setName({});
      showDone(t('templates.created'));
    },
    onError: showApiError,
  });

  const editable = can(session.data, 'Template.Edit');

  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [stat, setStat] = useUrlState('stat');
  const setParams = useUrlParamsSetter();

  /*
   * Версії РЯДКА — за ідентифікатором шаблону, а не за позицією рядка.
   *
   * ⛔ Доти тут стояло `versionQueries[index]`, де `index` — позиція в
   * `page.items.map(...)`. Поки порядок рядків збігався з порядком відповіді
   * сервера, формула працювала; шапка `DataTable` цей порядок ПЕРЕСТАВЛЯЄ, і та
   * сама формула віддала б рядку ЧУЖИЙ перелік версій — посилання вело б на
   * версію іншого шаблону, лишаючись при цьому цілком правдоподібним на вигляд.
   *
   * ⚠ Тепер джерело — Map за `templateId` із ОДНІЄЇ пакетної відповіді, а не
   * позиція в масиві паралельних запитів: те саме правило, новий носій.
   */
  /*
   * ⚠ Рядки будуються лише коли прийшли І шаблони, І версії: рядок без версій
   * показав би «Not published yet» там, де версії просто ще вантажаться.
   * `undefined` — це «ще не знаємо» для `DataTable` (скелет), не «порожньо».
   */
  const rows = useMemo<readonly TemplateListRow[] | undefined>(() => {
    if (templates.data === undefined) return undefined;
    if (templates.data.items.length > 0 && versionsBatch.data === undefined) return undefined;

    const versionsOf = new Map<number, readonly TemplateVersionSummary[]>(
      (versionsBatch.data ?? []).map((entry) => [entry.templateId, entry.versions]),
    );

    return templates.data.items.map((template) =>
      toTemplateListRow(template, versionsOf.get(template.id) ?? []),
    );
  }, [templates.data, versionsBatch.data]);

  const query = params.get('q');
  const state = params.get('state');
  const filtered = query !== null || state !== null || stat !== null;

  const visible = useMemo(
    () => (rows === undefined ? undefined : filterTemplateRows(rows, { query, state, stat })),
    [rows, query, state, stat],
  );

  const versionLink = (templateId: number, version: TemplateVersionSummary): JSX.Element => (
    <Anchor
      component={Link}
      size="sm"
      ff="monospace"
      to={`/admin/templates/${String(templateId)}/versions/${String(version.id)}`}
      // ⛔ Посилання на версію живе в рядку, що сам веде на картку шаблону:
      // без цього клац по версії відкрив би версію і тут-таки картку.
      onClick={(event) => event.stopPropagation()}
    >
      {version.version}
    </Anchor>
  );

  /*
   * ⚠ Колонки макета, для яких у переліку НЕМАЄ даних («Documents», «Updated»,
   * автор чернетки), не малюються (`D15-06`); їх місце — TODO-контракт у листі
   * готовності. Замість «Documents» стоїть лічильник версій — він є у
   * `TemplateSummary.versionCount`.
   */
  const columns: readonly DataTableColumn<TemplateListRow>[] = [
    {
      key: 'code',
      label: t('templates.card'),
      minWidth: 200,

      /*
       * ⛔ `UI-09`: код — ПОСИЛАННЯ на картку шаблону; без нього з переліку не
       * було входу на сам шаблон. Назви шаблону в `TemplateSummary` немає,
       * тож перший рядок макета («назва + код») зводиться до коду.
       *
       * ⚠ `sortValue` явно: `render` дає вузол, сортувати треба за кодом.
       */
      render: (row) => (
        <Anchor
          component={Link}
          size="sm"
          to={`/admin/templates/${String(row.template.id)}`}
          onClick={(event) => event.stopPropagation()}
        >
          {row.template.code}
        </Anchor>
      ),
      sortValue: (row) => row.template.code,
    },
    {
      key: 'current',
      label: t('templates.currentVersion'),
      sortValue: (row) => row.current?.version ?? '',
      render: (row) => {
        if (row.current !== null) {
          return (
            <Group gap="xs" wrap="nowrap">
              {versionLink(row.template.id, row.current)}
              {/* ⚠ Ревізія вигляду — лише коли вона щось каже: «r0» без
                  підпису читався як незрозумілий код (UX-прохід 2026-09-23). */}
              {row.current.presentationRevision > 0 && (
                <Text size="xs" c="dimmed">
                  {t('version.presentationRevision', {
                    revision: row.current.presentationRevision,
                  })}
                </Text>
              )}
            </Group>
          );
        }

        if (row.lastDeprecated !== null) {
          return (
            <TwoLine
              primary={versionLink(row.template.id, row.lastDeprecated)}
              secondary={t('templates.noCurrentVersion')}
            />
          );
        }

        return (
          <Text size="sm" c="dimmed">
            {t('templates.notPublished')}
          </Text>
        );
      },
    },
    {
      key: 'draft',
      label: t('templates.draft'),
      sortValue: (row) => row.draft?.version ?? '',
      render: (row) => (row.draft === null ? null : versionLink(row.template.id, row.draft)),
    },
    {
      key: 'versions',
      label: t('templates.versions'),
      num: true,
      sortValue: (row) => row.template.versionCount,
      render: (row) => row.template.versionCount,
    },
    {
      key: 'state',
      label: t('templates.state'),
      sortValue: (row) => row.state ?? '',
      render: (row) =>
        row.state === null ? null : <StatusBadge kind="version" state={row.state} quiet />,
    },
  ];

  /*
   * ⚠ Смуга — лише з повних даних: показник «0 drafts», порахований до приходу
   * версій, був би неправдою, а не нулем (`StatStrip`: нуль — це дані).
   * «Documents using them» з макета немає — немає агрегату (TODO-контракт).
   */
  const stats =
    rows === undefined || rows.length === 0
      ? undefined
      : {
          label: t('templates.stats'),
          items: [
            { id: 'all', label: t('templates.stat.all'), value: rows.length, filter: false },
            {
              id: 'published',
              label: t('templates.stat.published'),
              value: publishedVersionCount(rows),
              hint: t('templates.stat.publishedHint'),
            },
            {
              id: 'drafts',
              label: t('templates.stat.drafts'),
              value: draftCount(rows),
              hint: t('templates.stat.draftsHint'),
            },
          ] as const,
          active: stat,
          onSelect: setStat,
        };

  return (
    <>
      <ListPage
        header={{
          title: t('templates.title'),
          count: rows?.length,
          // Пояснення сторінки ЗАМІСТЬ пояснення маршруту; `<p>` у `<p>` теж зник (batch-2-a, дефект 3).
          description: t('templates.subtitle'),
          primary: editable
            ? { label: t('templates.create'), onClick: () => setCreating(true) }
            : undefined,
        }}
        stats={stats}
        filters={
          rows === undefined || rows.length === 0 ? null : (
            <FilterBar
              search={{
                label: t('templates.search'),
                placeholder: t('templates.searchPlaceholder'),
              }}
              filters={[
                {
                  id: 'state',
                  label: t('templates.state'),
                  options: (['Published', 'Draft', 'Deprecated'] as const).map((value) => ({
                    value,
                    label: t(`status.version.${value}`),
                  })),
                },
              ]}
              clearLabel={t('filters.clear')}
            />
          )
        }
        table={
          /*
           * ⛔ Помилка версій підмішана до помилки переліку навмисно: інакше
           * шаблони показувалися б без поточних версій — тобто «не
           * опубліковано» замість «версії не завантажилися» (ФВ-14.22).
           *
           * ⚠ `rows` — `undefined`, доки дані не прийшли: `?? []` перетворило б
           * «ще не питали» на «порожньо» (`DataTable` тримає стани сам).
           */
          <DataTable<TemplateListRow>
            columns={columns}
            rows={visible}
            rowKey={(row) => String(row.template.id)}
            isPending={templates.isPending || (rows === undefined && versionsBatch.isPending)}
            error={templates.error ?? versionsError}
            emptyTitle={t('templates.empty')}
            emptyHint={t('templates.emptyHint')}
            filtered={filtered}
            noMatchTitle={t('templates.noMatch')}
            onClearFilters={() => setParams({ q: null, state: null, stat: null })}
            clearFiltersLabel={t('filters.clear')}
            onRowClick={(row) => navigate(`/admin/templates/${String(row.template.id)}`)}
            onRetry={() => {
              void templates.refetch();
              void versionsBatch.refetch();
            }}
          />
        }
      />

      <Modal opened={creating} onClose={() => setCreating(false)} title={t('templates.create')}>
        {/* ⚠ Код — це бізнес-ключ шаблону: за ним на нього посилаються
            проєкти, і змінити його потім не можна. */}
        <TextInput
          label={t('templates.code')}
          description={t('templates.codeHint')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />

        <LocalizedInput
          label={t('templates.name')}
          description={t('templates.nameHint')}
          value={name}
          onChange={setName}
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCreating(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code.trim().length === 0 || !hasAnyText(name)}
            loading={create.isPending}
            onClick={() => create.mutate()}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>

    </>
  );
}
