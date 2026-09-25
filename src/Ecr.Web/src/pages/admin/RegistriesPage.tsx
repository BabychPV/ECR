import { useMemo, useState, type JSX } from 'react';
import { Badge, Button, Group, List, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, RegistryEntryDto } from '@/api/types';
import {
  entryReferenceKinds,
  entryReferences,
  useDeleteRegistryEntry,
} from '@/features/registries/api';
import { CreateRegistryModal } from '@/features/registries/CreateRegistryModal';
import {
  RegistryEntryEditor,
  ValidityEditor,
} from '@/features/registries/RegistryEntryEditor';
import { RegistryImportPanel } from '@/features/registries/RegistryImportPanel';
import { SourceKindSwitch } from '@/features/registries/SourceKindSwitch';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Що стоїть у межі вікна чинності запису, коли межі НЕМАЄ.
 *
 * ⛔ Не тире — і це не нове рішення, а те саме, що вже прийнято для вікна дії
 * константи методології (`MethodologyConstantsPanel`, `#415`). Дефолт
 * `Timestamp` — тире, і воно читається як «значення немає»; запис із порожнім
 * `validTo` не «не має дати кінця» — він **чинний і далі**, і саме це
 * відрізняє його від запису, виведеного з обігу датою (`ФВ-8.5`). Тире тут
 * збрехало б у найдорожчий бік: людина шукає, чому рядок документа
 * осиротів (`ФВ-8.13`), а комірка каже «тут порожньо».
 *
 * ⚠ Сам символ тут не новий: склейка рядком, яку цей набір замінює, уже
 * друкувала `'…'` — тобто СЕНС був правильний від початку, бракувало лише
 * набору. Дві сторінки мусять відповідати на те саме питання однаково,
 * інакше «…» на одній і тире на іншій читаються як різні стани даних.
 *
 * ⚠ Символ, а не слово з каталогу, навмисно: напис («безстроково») — це новий
 * ключ `ui-strings` у трьох мовах і рядок сіду, тобто чужі файли. Три крапки
 * нейтральні до мови й читаються з обох боків вікна.
 */
const Unbounded = '…';

/**
 * Сьогоднішня дата КЛІЄНТА як `"YYYY-MM-DD"` — `asOf` для темпорального
 * довідника на цьому екрані (перелік без контексту періоду документа).
 *
 * ⛔ НЕ `toISOString().slice(0, 10)`: той читає північ як UTC і в
 * від'ємному зсуві зсуває календарний день на добу (та сама пастка, що
 * задокументована для `DocumentHeaderPanel.tsx`, `isoDateOf`).
 */
function todayIso(): string {
  const now = new Date();
  const year = String(now.getFullYear()).padStart(4, '0');
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');

  return `${year}-${month}-${day}`;
}

/**
 * Конструктор реєстрів: схема, дані, темпоральність.
 *
 * ⚠ Вікно чинності показується завжди, навіть порожнє. Запис без вікна і
 * запис, чинний до минулого місяця, у списку виглядають однаково — і саме
 * друге робить рядки документів осиротілими (ФВ-8.13).
 */
export function RegistriesPage(): JSX.Element {
  const [code, setCode] = useUrlState('code');
  const session = useSession();

  /*
   * ⛔ UI-аудит-пас 8, lane4, п.6: таблиця записів довідника була голим
   * списком без жодного пошуку чи фільтра — акцесибіліті-дерево теж
   * підтверджувало відсутність контролю. Клієнтський фільтр достатній: цей
   * запит (`entries` нижче) повертає ПОВНИЙ масив без курсора/пагінації —
   * сервер тут не розбиває відповідь на сторінки (`RegistryEntryDto[]`, не
   * `Paged*`), тож фільтрувати вже завантажене дешевше й миттєвіше, ніж
   * додавати параметр пошуку в API заради списку, що й так цілий.
   *
   * ⚠ Значення переїхало з `useState` в АДРЕСУ (`?q=`) разом із переходом на
   * `FilterBar` — це вимога самого набору (`ФВ-14.29`), а не вподобання:
   * рядок фільтрів інакше не вміє, бо кожне його поле читає `useUrlState`
   * САМЕ. Той самий параметр читається тут, а не через `onChange` рядка
   * фільтрів: сторінці потрібне значення в ТОМУ Ж рендері, що й таблиці, а
   * `onChange` кличеться з ефекту — тобто на такт пізніше.
   */
  const [query, setQuery] = useUrlState('q');

  // `undefined` — діалог закритий; `null` — новий запис; об'єкт — правка.
  const [editing, setEditing] = useState<RegistryEntryDto | null | undefined>(undefined);

  // Для якого запису правимо вікно чинності.
  const [validity, setValidity] = useState<RegistryEntryDto | null>(null);

  // Який запис підтверджуємо до видалення (`ФВ-8.6`).
  const [deleting, setDeleting] = useState<RegistryEntryDto | null>(null);

  // Заведення довідника з нуля (директива №11, T4): доти в системі не було
  // жодного способу, доступного людині, додати довідник, якого немає в seed.
  const [creating, setCreating] = useState(false);

  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
  });

  const selected = registries.data?.find((registry) => registry.code === code);

  /*
   * ⛔ Дефект живого прогону: `GET …/entries` вимагав `asOf` БЕЗУМОВНО для
   * БУДЬ-ЯКОГО довідника (сервер фіксив у `GetRegistryEntriesHandler.cs`,
   * той самий PR). Нетемпоральний довідник (`isTemporal === false`) не має
   * дати, від якої залежить перелік, — і `asOf` для нього тепер не
   * надсилається зовсім, а не підставляється «сьогодні» про людське око.
   * Темпоральний — сьогоднішня дата КЛІЄНТА: цей екран показує «поточний»
   * стан довідника адміністратору, без контексту періоду документа (той є
   * лише в `DocumentGrid`/`DocumentHeaderPanel`).
   */
  const asOf = selected?.isTemporal === true ? todayIso() : null;

  const entries = useQuery({
    queryKey: [...queryKeys.registries.entries(code ?? ''), asOf],
    queryFn: () => {
      // ⚠ Базовий шлях — ОКРЕМИЙ шаблонний рядок, без `?asOf=` усередині:
      // `EndpointCoverageTests.Кожна_адреса_яку_викликає_клієнт_існує_на_сервері`
      // читає джерело регуляркою, що бере ВЕСЬ вміст МІЖ парою лапок як
      // адресу, — рядок запиту в тому самому літералі виглядав би для неї
      // окремим (і неіснуючим) серверним маршрутом.
      const baseUrl = `/api/v1/registries/${encodeURIComponent(code ?? '')}/entries`;

      return apiFetch<RegistryEntryDto[]>(
        asOf === null ? baseUrl : `${baseUrl}?asOf=${asOf}`,
      );
    },
    enabled: code !== null && selected !== undefined,
  });

  const remove = useDeleteRegistryEntry(code ?? '');

  // ⛔ Не `null` означає «відмовлено, бо на запис посилаються N комірок». Саме
  // це число, а не текст відмови, вирішує, ЩО показати замість «повторити».
  const blocked = entryReferences(remove.error);

  // ⛔ V-08: ХТО посилається — комірки, інші записи, константи методологій.
  const blockedBy = entryReferenceKinds(remove.error);

  const needle = (query ?? '').trim().toLowerCase();

  /*
   * ⛔ `undefined` мусить лишатися `undefined`: `?? []` перетворило б «запиту
   * ще не робили» (довідник не обрано) на «записів немає» — рівно ту підміну,
   * яку `AsyncBoundary` всередині таблиці й ловить.
   *
   * ⚠ Фільтрування переїхало СЮДИ з тіла `AsyncBoundary`: таблиця набору має
   * отримати вже звужений перелік РАЗОМ із прапорцем `filtered`, інакше вона
   * не відрізнить «нічого не знайдено» від «нічого немає».
   */
  const rows = useMemo<readonly RegistryEntryDto[] | undefined>(() => {
    const all = code === null ? undefined : entries.data;

    if (all === undefined) return undefined;
    if (needle.length === 0) return all;

    return all.filter(
      (entry) =>
        entry.code.toLowerCase().includes(needle) ||
        entry.display.toLowerCase().includes(needle),
    );
  }, [code, entries.data, needle]);

  /*
   * ⛔ `U-08`. Правило порожніх станів, яке цей набір встановлює: **на екрані
   * одночасно видно рівно ОДИН порожній стан — той, що пояснює НАЙБЛИЖЧУ
   * перешкоду**; підпорядкований розділ свого не показує, доки головну
   * перешкоду не знято.
   *
   * Тут це було порушено буквально: на чистій базі межа переліку довідників
   * казала «No registries yet», а таблиця під нею — «Pick a registry above».
   * Друга порада нездійсненна САМЕ тоді, коли показана перша: обирати нема з
   * чого. Перелік записів підпорядкований переліку довідників (без довідника
   * запису не існує), тож він і мовчить.
   *
   * ⚠ Умова — «дані приїхали І не порожні», а не `length > 0` над `?? []`:
   * поки перелік у дорозі або відмовив, стан показує межа вище, і «Pick a
   * registry» під спінером чи під помилкою — той самий другий порожній стан,
   * лише з іншої причини.
   */
  const hasRegistries = registries.data !== undefined && registries.data.length > 0;

  const canEditData = can(session.data, 'Registry.EditData');

  /*
   * Колонки переліку записів. Чотири плюс дії — межа `L5` (сім) із запасом.
   *
   * ⛔ Жодного `num`: він означає ВЕЛИЧИНУ (вирівнювання праворуч,
   * роздільники розрядів, округлення до трьох знаків), а тут числової
   * величини немає взагалі. `parentEntryId` — ідентифікатор, і саме
   * відсутність `num` тримає його на екрані як `1234`, а не `1,234`: числове
   * поле колонки без `num` набір показує як є.
   */
  const columns: readonly DataTableColumn<RegistryEntryDto>[] = [
    { key: 'code', label: t('registries.code') },
    {
      key: 'display',
      label: t('registries.name'),

      /* ⛔ UI-аудит-пас 8, lane4, п.5: довгий рядок БЕЗ пробілів (~570
         символів в «Name · English») розтягував клітинку, таблицю й ВСЮ
         сторінку — навігація теж їхала вбік. `.ecr-wrap-anywhere`
         (`motion.css`) дає браузеру переносити такий рядок замість
         розтягувати розкладку; повне значення лишається читаним, на відміну
         від еліпсиса.

         ⚠ Клас переїхав із `<td>` на вузол ВСЕРЕДИНІ клітинки: розмітку
         рядка тепер малює набір, і власного `className` колонка йому не
         передає. Властивість та сама — `overflow-wrap: anywhere` змінює
         мінімальний вклад рядка в розкладку незалежно від того, на якому з
         двох вузлів вона оголошена. */
      render: (entry) => <span className="ecr-wrap-anywhere">{entry.display}</span>,
    },
    {
      key: 'parentEntryId',
      label: t('registries.parent'),

      // ⛔ `D15-06`: елемента без даних не малюємо — ні прочерку, ні
      // заглушки: `null` набір лишає порожньою клітинкою сам. Прочерк —
      // твердження «батька немає», яке не відрізнити від «поле не приїхало».
    },
    {
      key: 'validity',
      label: t('registries.validity'),

      // ⛔ Без `sortValue` сортування цієї колонки мовчки не робило б нічого:
      // `validity` не є полем рядка, і `row['validity']` — `undefined` для
      // кожного запису. Порядок вікна задає його ПОЧАТОК; порожні межі
      // `compareKeys` відправляє в кінець і при зростанні, і при спаданні.
      sortValue: (entry) => entry.validFrom,

      /* ⛔ Склейку рядком РОЗІБРАНО на вузли, а не замінено на `formatDate()`
         всередині неї. Обидва варіанти дали б читабельний текст, але рядок не
         має атрибутів — точне значення не лишилося б ДЕ. Тут воно лишається в
         `dateTime` кожної межі окремо.

         ⚠ `dateOnly` в обох: контракт віддає `validFrom`/`validTo` як
         `Format: date` (`RegistryEntryDto`) — це КАЛЕНДАРНІ межі вікна
         чинності, які звіряються з днем документа, а не з годинником.
         «12:00 AM» приписало б їм точність, якої в даних немає. */
      render: (entry) => (
        <>
          <Timestamp value={entry.validFrom} dateOnly fallback={Unbounded} />
          {' — '}
          <Timestamp value={entry.validTo} dateOnly fallback={Unbounded} />
        </>
      ),
    },

    /*
     * ⚠ Колонка дій з'являється лише з правом — рівно як і до переїзду
     * (раніше порожня клітинка малювалася завжди; тепер порожньої колонки
     * просто немає, `D15-06`). `sortable: false` обов'язковий: у кнопок
     * немає скалярного значення, а шапка з порожнім підписом стала б
     * кнопкою БЕЗ імені й завалила б `button-name` у гейті `a11y`.
     */
    ...(canEditData
      ? [
          {
            key: 'actions',
            label: '',
            sortable: false,
            render: (entry: RegistryEntryDto) => (
              <Group gap="xs" justify="flex-end">
                <Button size="compact-xs" variant="subtle" onClick={() => setEditing(entry)}>
                  {t('registries.editEntry')}
                </Button>

                {/* ⚠ Вікно чинності — окрема дія, і саме воно замінює
                    видалення: запис, на який посилаються комірки, закривають
                    датою (`ФВ-8.5`). */}
                <Button size="compact-xs" variant="subtle" onClick={() => setValidity(entry)}>
                  {t('registries.validity')}
                </Button>

                {/* ⛔ Видалення не мало в інтерфейсі жодної кнопки: обробник
                    на сервері існував, перевіряв право й рахував посилання —
                    і не викликався ніколи (директива №15, BE-01).

                    ⚠ Дія тут ДРУГА за помітністю після вікна чинності
                    навмисно: у більшості випадків правильна дія саме закрити
                    датою, а видалення доступне лише запису, на який ще ніхто
                    не послався. */}
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="statusError"
                  onClick={() => {
                    remove.reset();
                    setDeleting(entry);
                  }}
                >
                  {t('common.delete')}
                </Button>
              </Group>
            ),
          } satisfies DataTableColumn<RegistryEntryDto>,
        ]
      : []),
  ];

  return (
    <>
      <PageHeader
        title={t('registries.title')}
        actions={
          <Group gap="xs" align="end">
            <Select
              size="xs"
              miw={220}
              label={t('registries.title')}
              placeholder={t('registries.pick')}
              value={code}
              onChange={setCode}
              data={(registries.data ?? []).map((registry) => ({
                value: registry.code,
                label: `${localized(registry.nameL10n)} (${registry.code})`,
              }))}
            />

            {/* ⛔ Заведення НОВОГО довідника не мало кнопки (директива №11,
                T4): контролер умів лише читати перелік і правити опис
                НАЯВНОГО довідника, а сам довідник заводив тільки офлайновий
                seed — тобто довідника, якого там немає, не міг завести ніхто. */}
            {can(session.data, 'Registry.EditDefinition') && (
              <Button size="xs" variant="default" onClick={() => setCreating(true)}>
                {t('registries.newRegistry')}
              </Button>
            )}

            {/* ⛔ Заведення запису не мало кнопки (`A7-42`). Довідник без
                записів — це колонка типу `Lookup`, яка не пропонує нічого,
                тобто документ, який неможливо заповнити. */}
            {selected !== undefined && can(session.data, 'Registry.EditData') && (
              <Button size="xs" onClick={() => setEditing(null)}>
                {t('registries.newEntry')}
              </Button>
            )}

            {/* ⛔ Масове заведення/оновлення записів не мало в інтерфейсі
                жодного споживача (`BE-24`): дію на сервері викликати можна
                було лише напряму HTTP-клієнтом. Те саме право, що ручний
                upsert запису (`Registry.EditData`) — імпорт лише пришвидшує
                той самий шлях, не обходить його. */}
            {selected !== undefined && canEditData && (
              <RegistryImportPanel registryCode={selected.code} />
            )}

            {/* ⛔ Вхід у конструктор (`ФВ-8.12`). Опис довідника — поля,
                зв'язки, правила, мапінг — не мав в інтерфейсі жодного
                споживача: подивитися, за яким правилом довідник перевіряє
                свої записи, було ніде. */}
            {selected !== undefined && (
              <Button
                component={Link}
                to={`/admin/registries/${encodeURIComponent(selected.code)}/definition`}
                size="xs"
                variant="default"
              >
                {t('registries.constructor')}
              </Button>
            )}

            {/* ⛔ Перемикання master набором (`ФВ-13.10`). Дія існувала на
                сервері й не мала в інтерфейсі жодного споживача — тобто
                поетапний перехід майстра (`ФВ-11.4`) був неможливий інакше,
                як руками в базі.

                ⚠ Право небезпечне (`Integration.Manage`) і в seed його не
                має ніхто: кнопка з'явиться лише в того, кому його видали
                поіменно. */}
            {can(session.data, 'Integration.Manage') && (
              <SourceKindSwitch registries={registries.data ?? []} />
            )}
          </Group>
        }
      />

      {/*
       * ⛔ Помилка переліку довідників показується ОКРЕМО від помилки записів:
       * недоступний перелік лишає порожнім сам вибір, і мовчазна порожнеча в
       * ньому виглядає як «довідників немає».
       */}
      <AsyncBoundary<RegistryDefDto[]>
        isPending={registries.isPending}
        error={registries.error}
        data={registries.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('registries.empty')}
        emptyHint={t('registries.emptyHint')}
        onRetry={() => void registries.refetch()}
      >
        {() => null}
      </AsyncBoundary>

      {selected !== undefined && (
        <Group gap="xs" mb="sm">
          {/* ⚠ Ознаки довідника видно поруч із даними: у темпоральному
              запис має вікно чинності, і рядки документів, що на нього
              посилаються, стають осиротілими поза цим вікном (ФВ-8.13). */}
          {selected.isTemporal && <Badge variant="light">{t('registries.temporal')}</Badge>}
          {selected.isHierarchical && <Badge variant="light">{t('registries.hierarchical')}</Badge>}
          <Text size="xs" c="dimmed">
            {t('registries.fields', { count: selected.fields.length })}
          </Text>
        </Group>
      )}

      {/*
       * ⚠ Рядок фільтрів стоїть НАД таблицею й поза її станами — на відміну
       * від поля пошуку, яке жило всередині гілки «дані» й зникало разом із
       * нею. Це не косметика: зі стану «фільтр нічого не знайшов» вийти можна
       * лише правкою фільтра, а поле, що зникло разом із рядками, лишало
       * людину в тупику.
       *
       * ⚠ Малюється лише з обраним довідником: без нього фільтрувати нема
       * чого, а контрол без даних — це `D15-06`.
       */}
      {hasRegistries && code !== null && (
        <FilterBar
          search={{
            label: t('registries.search'),
            param: 'q',
            placeholder: t('registries.searchPlaceholder'),
          }}
          clearLabel={t('filters.clear')}
        />
      )}

      {/*
       * ⛔ `DataTable` замінює `AsyncBoundary` + `<Table>` РАЗОМ із власним
       * перемикачем «фільтр нічого не знайшов»: обгортку станів набір тримає
       * всередині себе (той самий `AsyncBoundary`, `skeleton="table"`).
       * Лишити зовнішню поруч означало б два перемикачі станів на одну
       * таблицю — саме ту розбіжність, заради усунення якої таблиця й стала
       * компонентом.
       *
       * ⛔ `L10`: різниця «порожньо» / «фільтр нічого не знайшов» ПЕРЕЇХАЛА, а
       * не зникла. Раніше її тримали два різні місця — `emptyTitle` обгортки і
       * власний `<Text>` під таблицею; тепер її тримає `filtered`, і перелік
       * станів став трьома, а не двома: «оберіть довідник» (запиту не було),
       * «у довіднику немає записів» (запит був, порожньо), «нічого не
       * знайдено» (записи є, фільтр їх не пропустив).
       *
       * ⚠ Сортування шапкою прийшло з набором, і його тут не було: чотири
       * перші колонки стали клікабельними з `aria-sort`. Порядок при
       * відкритті — той самий, у якому віддав сервер.
       *
       * ⚠ `total`/`onShowMore` цьому екрану передавати нема куди:
       * `GET …/entries` віддає повний масив без курсора, тож підсумок «N / M»
       * не малюється зовсім (`D15-06`).
       */}
      {/* ⛔ `U-08`: підпорядкована таблиця мовчить, доки перешкода «довідників
          немає» не знята — див. `hasRegistries` вище. */}
      {hasRegistries && (
      <DataTable<RegistryEntryDto>
        columns={columns}
        rows={rows}
        rowKey={(entry) => String(entry.id)}
        isPending={code !== null && entries.isPending}
        error={entries.error}
        filtered={code !== null && needle.length > 0}
        emptyTitle={code === null ? t('registries.pick') : t('registries.noEntries')}
        emptyHint={code === null ? t('registries.pickHint') : t('registries.noEntriesHint')}
        noMatchTitle={t('registries.searchNoMatches')}
        onClearFilters={() => setQuery(null)}
        clearFiltersLabel={t('filters.clear')}
        onRetry={() => void entries.refetch()}
      />
      )}

      {selected !== undefined && (
        <RegistryEntryEditor
          registry={selected}
          entry={editing ?? null}
          opened={editing !== undefined}
          onClose={() => setEditing(undefined)}
        />
      )}

      <ValidityEditor
        registryCode={code ?? ''}
        entry={validity}
        onClose={() => setValidity(null)}
      />

      {/*
       * Підтвердження видалення запису і — на відмову `ECR-REG-0409` — той
       * самий діалог у стані «заблоковано».
       *
       * ⛔ Двох діалогів тут немає навмисно: «підтвердьте» і «не можна, бо на
       * запис посилаються N комірок» — це два стани однієї розмови, і людина
       * не має шукати причину в плашці, що з'їхала кудись у куток.
       */}
      {/*
       * ⛔ X-23: тут стояв голий `<Modal>` із заголовком «Remove» без назви
       * запису й фокусом на хрестику. Тепер — `ConfirmModal`: назва запису в
       * заголовку, фокус на «Cancel». Стан «заблоковано» — той самий діалог
       * із недоступним підтвердженням і дією, яка справді можлива.
       */}
      <ConfirmModal
        opened={deleting !== null}
        title={t('registries.deleteEntryTitle', { code: deleting?.code ?? '' })}
        text={blocked === null ? deleting?.display : undefined}
        verb={t('common.delete')}
        confirmDisabled={blocked !== null}
        isPending={remove.isPending}
        onConfirm={() => {
          if (deleting !== null) {
            remove.mutate(deleting.id, { onSuccess: () => setDeleting(null) });
          }
        }}
        onClose={() => setDeleting(null)}
      >
        {blocked !== null && (
          <Stack gap="sm">
            {/* ⚠ Текст відмови — СЕРВЕРНИЙ: він уже локалізований каталогом
                і називає причину словами. Поруч — саме число посилань, бо
                воно і є мірою наслідку. */}
            <Group gap="xs">
              <Badge color="statusWarning">{blocked}</Badge>
              <Text size="sm">{remove.error?.message}</Text>
            </Group>

            {/* ⛔ V-08: розклад посилань за видами — куди йти виправляти. */}
            {blockedBy.length > 0 && (
              <List size="sm" aria-label={t('registries.referencedBy')}>
                {blockedBy.map(({ kind, count }) => (
                  <List.Item key={kind}>
                    {t(`registries.referenceKind.${kind}`)}: {count}
                  </List.Item>
                ))}
              </List>
            )}

            {/* ⛔ Замість «повторити». Повтор дасть ту саму відмову: змінити
                треба не запит, а намір — запис виводять з обігу датою
                (`ФВ-8.5`), і тоді історичні документи лишаються читабельними. */}
            <Group justify="flex-start">
              <Button
                variant="light"
                onClick={() => {
                  setValidity(deleting);
                  setDeleting(null);
                }}
              >
                {t('registries.validity')}
              </Button>
            </Group>
          </Stack>
        )}
      </ConfirmModal>

      {/* ⛔ U-18: діалог винесено в `CreateRegistryModal` — той самий
          контракт, що й «New project» (зірочки, «Still needed», Cancel/Save). */}
      <CreateRegistryModal
        opened={creating}
        onClose={() => setCreating(false)}
        onCreated={setCode}
      />
    </>
  );
}
