import { useState, type JSX } from 'react';
import { Anchor, Button, Group, Modal, TextInput } from '@mantine/core';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  CreateTemplateRequest,
  TemplateIdResponse,
  TemplatePage,
  TemplateSummary,
  TemplateVersionPage,
  TemplateVersionSummary,
} from '@/api/types';
import { NewTemplateVersionModal } from '@/features/templates/NewTemplateVersionModal';
import { can, useSession } from '@/shared/session/useSession';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Перелік шаблонів і їхніх версій.
 *
 * ⚠ Опублікована версія структурно **незмінна**: правка вимагає нової версії,
 * а презентаційна зміна лише піднімає `PresentationRevision` (D-16). Тому в
 * переліку видно і статус, і ревізію: без другої незрозуміло, чому кеш
 * оновився без нової версії.
 */
export function TemplatesPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  const [creating, setCreating] = useState(false);
  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});

  // Для якого шаблону заводимо версію; `null` — діалог закритий.
  const [versioning, setVersioning] = useState<number | null>(null);

  const templates = useQuery({
    queryKey: queryKeys.templates.list(),
    queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),
  });

  const items = templates.data?.items ?? [];

  // ⚠ Версії читаються ПО ШАБЛОНУ: маршрут контракту —
  // `GET /api/v1/templates/{id}/versions`. До аудиту сторінка била в
  // `/api/v1/template-versions`, якого не існує, і перелік версій був
  // порожній завжди (`A7-03`).
  // ⛔ Q-225: ендпоінт курсорний (той самий патерн, що й `/api/v1/templates`
  // поруч) — відповідь тепер `{items, nextCursor, totalCount}`, а не голий
  // масив. `?limit=100` — той самий одноразовий ліміт сторінки, що й вище.
  const versionQueries = useQueries({
    queries: items.map((template) => ({
      queryKey: queryKeys.templates.versionsOf(template.id),
      queryFn: () =>
        apiFetch<TemplateVersionPage>(`/api/v1/templates/${template.id}/versions?limit=100`),
    })),
  });

  const versionsError = versionQueries.find((query) => query.error)?.error ?? null;

  /*
   * Версії РЯДКА — за ідентифікатором шаблону, а не за позицією рядка.
   *
   * ⛔ Доти тут стояло `versionQueries[index]`, де `index` — позиція в
   * `page.items.map(...)`. Поки порядок рядків збігався з порядком відповіді
   * сервера, формула працювала; шапка `DataTable` цей порядок ПЕРЕСТАВЛЯЄ, і та
   * сама формула віддала б рядку ЧУЖИЙ перелік версій — посилання вело б на
   * версію іншого шаблону, лишаючись при цьому цілком правдоподібним на вигляд.
   *
   * ⚠ Сам масив `versionQueries` і далі індексується `items`: `useQueries`
   * повертає результати в порядку переданих запитів, і саме тут цей порядок
   * востаннє має значення.
   */
  const versionsOf = new Map<number, readonly TemplateVersionSummary[]>(
    items.map((template, index) => [template.id, versionQueries[index]?.data?.items ?? []]),
  );

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

  /**
   * Остання версія шаблону — від неї клонується наступна.
   *
   * ⚠ Клон робиться з ОСТАННЬОЇ версії, якщо вона є: порожня версія поруч із
   * наявною структурою — майже завжди помилка, а не намір. Номер задає
   * людина: `Major.Minor.Patch.Build` несе сенс (`ФВ-2.8`), і вигадувати його
   * за користувача означало б вигадувати клас зміни. Сам діалог —
   * `NewTemplateVersionModal` (спільний із карткою шаблону, `U-19`).
   */
  const latestVersionOf = (templateId: number): number | null => {
    const versions = versionsOf.get(templateId) ?? [];

    return versions.length === 0 ? null : (versions[versions.length - 1]?.id ?? null);
  };

  const editable = can(session.data, 'Template.Edit');

  /*
   * ⚠ Дві колонки, межа `L5` — сім: запас є, і третьою напрошувався лічильник
   * версій. Його тут НЕМАЄ навмисно — це рефакторинг, а нова колонка додала б
   * на екран число, якого на ньому не було.
   *
   * ⛔ Колонка версій лишається `render`-колонкою і `sortable: false`: у переліку
   * посилань немає скалярного значення, за яким їх упорядковувати, а сортування,
   * що мовчки нічого не робить, гірше за його відсутність (`DataTableColumn.
   * sortable`).
   */
  const columns: readonly DataTableColumn<TemplateSummary>[] = [
    {
      key: 'code',
      label: t('templates.code'),

      /*
       * ⛔ `UI-09`: код став ПОСИЛАННЯМ на картку шаблону. До цього з переліку
       * не було входу на сам шаблон узагалі — лише на його версію, — і
       * перейменування з архівуванням лишалися недосяжними з інтерфейсу,
       * хоча сервер обидва вміє.
       *
       * ⚠ `sortValue` заданий явно: `render` перебиває клітинку вузлом, а
       * сортувати перелік треба за самим кодом, не за розміткою. Без цього
       * рядка шапка впорядкувала б стовпець за чимось, що не є текстом на
       * екрані, — і порядок виглядав би випадковим.
       */
      render: (template) => (
        <Anchor component={Link} size="sm" to={`/admin/templates/${String(template.id)}`}>
          {template.code}
        </Anchor>
      ),
      sortValue: (template) => template.code,
    },
    {
      key: 'versions',
      label: t('templates.versions'),
      sortable: false,
      render: (template) => (
        <Group gap="xs">
          {(versionsOf.get(template.id) ?? []).map((version) => (
            /*
             * ⛔ Тут стояв ОДИН `Badge`, у тілі якого друкувався
             * `version.status` — тобто код сервера (`Published`,
             * `Deprecated`) як видимий текст. Це той самий дефект,
             * що вже знято з п'яти екранів: код не є текстом
             * інтерфейсу й не перекладається, тож казахський
             * користувач бачив англійське слово, а `Deprecated`
             * нічим не відрізнявся від чинної версії, окрім
             * `variant`, який ніхто не пояснює.
             *
             * ⚠ `Draft` і `Published` у наборі обидва `neutral`, і
             * це навмисно: чернетка — не проблема й не
             * попередження. Розрізняє їх ПІДПИС із каталогу
             * (`status.version.*`), а не колір — рівно те, чого
             * вимагає `L3`. Знятий `variant="filled"` для
             * `Published` нічого не повідомляв: «опублікована» — це
             * норма, а не подія.
             *
             * ⚠ Посилання стало `Anchor`, а не `Badge` із
             * `component={Link}`: перехід на версію — це посилання,
             * і читалка має оголосити його посиланням, а не
             * позначкою з курсором-пальцем.
             */
            <Group key={version.id} gap="xs" wrap="nowrap">
              <Anchor
                component={Link}
                size="sm"
                to={`/admin/templates/${template.id}/versions/${version.id}`}
              >
                {version.version} · r{version.presentationRevision}
              </Anchor>
              <StatusBadge kind="version" state={version.status} quiet />
            </Group>
          ))}

          {/* ⛔ Кнопка стоїть у рядку шаблону, а не на окремому
              екрані: версія завжди належить шаблону, і питання
              «якому саме» не має виникати. */}
          {editable && (
            <Button
              size="compact-xs"
              variant="default"
              onClick={() => setVersioning(template.id)}
            >
              {t('templates.newVersion')}
            </Button>
          )}
        </Group>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        title={t('templates.title')}
        actions={
          editable && (
            <Button size="xs" onClick={() => setCreating(true)}>
              {t('templates.create')}
            </Button>
          )
        }
      />
      {/*
       * ⛔ Помилка версій підмішана до помилки переліку навмисно. Інакше
       * шаблони показувалися б, а колонка версій була б порожньою — тобто
       * «версій немає» замість «версії не завантажилися» (ФВ-14.22).
       *
       * ⛔ `DataTable` замінює `AsyncBoundary` + `<Table>` РАЗОМ, а не лише
       * розмітку: обгортку станів набір тримає всередині себе (той самий
       * `AsyncBoundary`, `skeleton="table"`). Лишити зовнішню поруч означало б
       * два перемикачі станів на одну таблицю — саме ту розбіжність, заради
       * усунення якої таблиця й стала компонентом. Правило «відмова ≠ порожньо»
       * від цього не слабшає: воно переїхало разом із обгорткою.
       *
       * ⚠ `rows` — це `templates.data?.items`, тобто `undefined`, доки запиту не
       * зробили. `?? []` перетворило б «ще не питали» на «порожньо» — рівно ту
       * підміну, яку обгортка й ловить.
       *
       * ⚠ `total`/`onShowMore` не передаються, хоч відповідь і курсорна: екран
       * бере `?limit=100` одним запитом і другої сторінки не просить. Кнопка,
       * яка нічого не довантажує, і підсумок «2 / 2», що не є правдою про
       * сервер, — обидва гірші за їхню відсутність (`D15-06`). `clearFiltersLabel`
       * передавати теж нема куди: фільтрів екран не має.
       */}
      <DataTable<TemplateSummary>
        columns={columns}
        rows={templates.data?.items}
        rowKey={(template) => String(template.id)}
        isPending={templates.isPending}
        error={templates.error ?? versionsError}
        emptyTitle={t('templates.empty')}
        emptyHint={t('templates.emptyHint')}
        onRetry={() => void templates.refetch()}
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

      <NewTemplateVersionModal
        templateId={versioning}
        cloneFrom={versioning === null ? null : latestVersionOf(versioning)}
        onClose={() => setVersioning(null)}
      />
    </>
  );
}
