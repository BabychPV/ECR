import { useState, type JSX } from 'react';
import {
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Group,
  Loader,
  Modal,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useQueries } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { problemText } from '@/shared/ui/problemText';
import {
  CatalogPermission,
  catalogLabel,
  catalogValueOf,
  fetchSourceCatalog,
  isAttribute,
  isSourceOutage,
  type SourceCatalogItem,
} from './piafCatalogApi';

/**
 * Кнопка «вибрати з каталогу» біля поля джерельного шляху мапінгу (`ФВ-13.13`).
 *
 * ⛔ До неї людина вписувала шлях AF руками — рядок на 60–120 символів із
 * зворотними скісними й вертикальною рискою перед атрибутом. Друкарська
 * помилка в ньому не відмовляє нічого: збір проходить «успішно», точок нуль, і
 * в журналі прогонів такий мапінг не відрізняється від справного
 * (`MappingGaps.brokenHint`, `NoData`). Тобто ціна одруківки — місяць
 * порожньої колонки і звірка, яка шукає причину не там.
 *
 * ⛔ Кнопки немає БЕЗ права `Integration.Manage`: сервер вимагає його на
 * каталозі, і кнопка, яка гарантовано дасть `403`, обіцяє те, чого не буде.
 * Це не заміна серверній перевірці — та лишається єдиним рішенням.
 *
 * ⚠ Компонент окремий (а не гілка всередині форми) навмисно: форма мапінгу
 * існує й там, де з'єднання невідоме, і тоді вона не має ані питати профіль,
 * ані знати про каталог узагалі.
 */
export function PiAfCatalogButton({
  dataSourceId,
  onPick,
}: {
  readonly dataSourceId: number;
  readonly onPick: (value: string) => void;
}): JSX.Element | null {
  const session = useSession();
  const [opened, setOpened] = useState(false);

  if (!can(session.data, CatalogPermission)) return null;

  return (
    <Group gap="xs">
      <Button size="xs" variant="default" onClick={() => setOpened(true)}>
        {t('mapping.catalogOpen')}
      </Button>

      <PiAfCatalogPicker
        dataSourceId={dataSourceId}
        opened={opened}
        onClose={() => setOpened(false)}
        onPick={(item) => {
          onPick(catalogValueOf(item));
          setOpened(false);
        }}
      />
    </Group>
  );
}

/**
 * Перегляд каталогу імен PI AF: дерево з лінивим розгортанням, пошук і
 * посторінкове дочитування курсором.
 *
 * ⚠ Дерево, а не таблиця й не один `Select`. Каталог AF ієрархічний і
 * нерівномірний: у корені десятки елементів, під окремим вузлом — тисячі
 * атрибутів. Плаский список змусив би тягнути все, а `Select` із пошуком
 * загубив би саме те, заради чого сюди приходять, — ЯКОМУ вузлові належить
 * атрибут із таким самим іменем, як у сусіднього.
 *
 * ⛔ `L2`: закрито за замовчуванням — доки `opened` хибне, Mantine не малює
 * нічого, і жодного запиту до чужої системи не йде.
 *
 * ⚠ Пошук застосовується ЯВНО (Enter або кнопка), а не на кожну літеру: за
 * полем стоїть запит у чужу систему з межею очікування 10 с, і запит на
 * кожен натиск клавіші — це десяток одночасних звернень до PI на одне слово.
 */
export function PiAfCatalogPicker({
  dataSourceId,
  opened,
  onClose,
  onPick,
}: {
  readonly dataSourceId: number;
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly onPick: (item: SourceCatalogItem) => void;
}): JSX.Element {
  const [draft, setDraft] = useState('');
  const [search, setSearch] = useState('');

  const clearSearch = (): void => {
    setDraft('');
    setSearch('');
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('mapping.catalogTitle')} size="lg">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          {t('mapping.catalogHint')}
        </Text>

        <Group gap="xs" align="end" wrap="nowrap">
          <TextInput
            miw={220}
            style={{ flexGrow: 1 }}
            label={t('mapping.catalogSearch')}
            value={draft}
            onChange={(event) => setDraft(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                setSearch(draft.trim());
              }
            }}
          />

          <Button variant="default" onClick={() => setSearch(draft.trim())}>
            {t('mapping.catalogSearchApply')}
          </Button>
        </Group>

        {/* ⚠ `key` скидає і накопичені сторінки, і розгорнуті вузли: результат
            пошуку — інше дерево, і курсор попереднього в ньому не значить
            нічого. Скидання ключем, а не ефектом: похідний стан, який
            доводиться синхронізувати руками, тут і був би джерелом зайвих
            перемальовувань. */}
        <CatalogLevel
          key={search}
          dataSourceId={dataSourceId}
          path={null}
          search={search}
          onPick={onPick}
          onClearSearch={clearSearch}
        />
      </Stack>
    </Modal>
  );
}

/**
 * Один рівень дерева: сторінки цього рівня, дочитані курсором.
 *
 * ⛔ Сторінки живуть СПИСКОМ КУРСОРІВ у стані, а зібрані позиції — похідні від
 * `useQueries`. Накопичувати самі позиції в стані (через ефект на приході
 * даних) означало б тримати другу копію кешу, яка розходиться з ним при
 * будь-якому перечитуванні, — і саме такий ефект дає цикл «дані → стан →
 * рендер → дані».
 */
function CatalogLevel({
  dataSourceId,
  path,
  search,
  onPick,
  onClearSearch,
}: {
  readonly dataSourceId: number;
  readonly path: string | null;
  readonly search: string;
  readonly onPick: (item: SourceCatalogItem) => void;
  readonly onClearSearch: () => void;
}): JSX.Element {
  // Порожній рядок — перша сторінка; далі курсори з відповідей сервера.
  const [cursors, setCursors] = useState<readonly string[]>(['']);

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: ['piaf-catalog', dataSourceId, path ?? '', search, cursor] as const,
      queryFn: () =>
        fetchSourceCatalog(dataSourceId, {
          path,
          search,
          cursor: cursor.length === 0 ? null : cursor,
        }),
      retry: false,
    })),
  });

  const failure = pages.find((page) => page.error !== null)?.error ?? null;
  const items = pages.flatMap((page) => page.data?.items ?? []);
  const nextCursor = pages.at(-1)?.data?.nextCursor ?? null;

  const retry = (): void => {
    for (const page of pages) void page.refetch();
  };

  /*
   * ⛔ Стан «джерело не відповідає» — ПЕРШИЙ, і він не є помилкою форми.
   * `503`/`502` кажуть, що мовчить чужа система; під полем шляху таке
   * повідомлення читалося б як «ти ввела не те», а виправити його в полі
   * неможливо. Тому окремий блок із причиною і «повторити», а решта
   * інтерфейсу — пошук, форма мапінгу, ручне введення шляху — лишається
   * робочою (`ФВ-14.24`: тупикових екранів не буває).
   */
  if (failure !== null && isSourceOutage(failure)) {
    const shown = problemText(failure);

    return (
      <Alert
        color="statusWarning"
        title={t('mapping.catalogUnavailable')}
        data-catalog-state="unavailable"
      >
        <Stack gap="xs" align="flex-start">
          <Text size="sm">{t('mapping.catalogUnavailableHint')}</Text>

          {/* Причину називає сервер: лише вона знає, це межа очікування чи
              джерело лежить (`catalogTimeout` / `catalogUnavailable`). */}
          {shown.detail !== null && <Text size="sm">{shown.detail}</Text>}

          <Button size="xs" variant="default" onClick={retry}>
            {t('common.retry')}
          </Button>
        </Stack>
      </Alert>
    );
  }

  // Решта відмов (403, 404, 422, збій мережі) — звичайний збій запиту з кодом
  // і кореляцією: їх виправляють не повторенням, а зверненням у підтримку.
  if (failure !== null) return <ErrorAlert error={failure} onRetry={retry} />;

  if (pages.some((page) => page.isPending)) return <Loader size="sm" />;

  /*
   * ⛔ `L10`: три різні порожнечі — три різні відповіді. «Фільтр нічого не
   * знайшов» дає дію (зняти фільтр), «джерело нічого не повернуло» пояснює,
   * де шукати причину, а «немає прав» сюди не доходить узагалі: без
   * `Integration.Manage` немає й кнопки, що відкриває це вікно.
   */
  if (items.length === 0) {
    return search.length === 0 ? (
      <Stack gap="xs" data-catalog-state="empty">
        <Text size="sm" c="dimmed">
          {t('mapping.catalogEmpty')}
        </Text>
        <Text size="sm" c="dimmed">
          {t('mapping.catalogEmptyHint')}
        </Text>
      </Stack>
    ) : (
      <Stack gap="xs" align="flex-start" data-catalog-state="search-empty">
        <Text size="sm" c="dimmed">
          {t('mapping.catalogSearchEmpty')}
        </Text>
        <Button size="xs" variant="default" onClick={onClearSearch}>
          {t('mapping.catalogSearchClear')}
        </Button>
      </Stack>
    );
  }

  return (
    <Stack gap="xs" data-catalog-level={path ?? ''}>
      {items.map((item) => (
        <CatalogRow
          key={`${item.kind}:${item.path ?? item.code}`}
          dataSourceId={dataSourceId}
          item={item}
          search={search}
          onPick={onPick}
          onClearSearch={onClearSearch}
        />
      ))}

      {nextCursor !== null && (
        <Group gap="xs">
          <Button
            size="xs"
            variant="subtle"
            onClick={() => setCursors((prev) => [...prev, nextCursor])}
          >
            {t('mapping.catalogMore')}
          </Button>
        </Group>
      )}
    </Stack>
  );
}

/**
 * Рядок каталогу: елемент, який розгортається, або атрибут, який завершує вибір.
 *
 * ⚠ Атрибут — кінцевий вузол за визначенням контракту (`kind: "Attribute"`), і
 * саме він має одиницю. `unitSymbol` показується тут, а не в одній колонці
 * «тип»: одиниця джерела — це те, за чим на межі інтеграції ловлять мовчазну
 * зміну (`ECR-INT-0422`), і побачити її треба ДО того, як мапінг заведено.
 */
function CatalogRow({
  dataSourceId,
  item,
  search,
  onPick,
  onClearSearch,
}: {
  readonly dataSourceId: number;
  readonly item: SourceCatalogItem;
  readonly search: string;
  readonly onPick: (item: SourceCatalogItem) => void;
  readonly onClearSearch: () => void;
}): JSX.Element {
  const [expanded, setExpanded] = useState(false);
  const label = catalogLabel(item);

  if (isAttribute(item)) {
    return (
      <Group gap="xs" wrap="nowrap" data-catalog-attribute={item.path ?? item.code}>
        {/* Кнопка, а не клац по рядку: з клавіатури рядок недосяжний. */}
        <Anchor component="button" type="button" size="sm" onClick={() => onPick(item)}>
          {label}
        </Anchor>

        <Badge variant="light" color="gray">
          {t('mapping.catalogAttribute')}
        </Badge>

        {/* `D15-06`: одиниці немає — немає й порожнього рядка. */}
        {item.unitSymbol !== null && item.unitSymbol.length > 0 && (
          <Text size="xs" c="dimmed">
            {item.unitSymbol}
          </Text>
        )}

        {item.dataType !== null && item.dataType.length > 0 && (
          <Text size="xs" c="dimmed">
            {item.dataType}
          </Text>
        )}
      </Group>
    );
  }

  /*
   * ⛔ Дочірні читаються ЛИШЕ при розгортанні і ЛИШЕ зі шляхом цього вузла.
   * Без `path` сервер віддає корені (`BrowseSourceCatalogHandler`), тобто
   * розгорнутий вузол показав би сам себе разом із сусідами — і це виглядало б
   * не як дефект клієнта, а як дивна ієрархія в джерелі.
   */
  const canExpand = item.path !== null && item.path.length > 0;

  return (
    <Stack gap="xs">
      <Group gap="xs" wrap="nowrap" data-catalog-element={item.path ?? item.code}>
        <Button
          size="compact-xs"
          variant="subtle"
          disabled={!canExpand}
          aria-expanded={expanded}
          onClick={() => setExpanded((prev) => !prev)}
        >
          {expanded ? t('mapping.catalogCollapse') : t('mapping.catalogExpand')}
        </Button>

        <Text size="sm">{label}</Text>

        <Badge variant="light" color="gray">
          {t('mapping.catalogElement')}
        </Badge>
      </Group>

      {expanded && item.path !== null && (
        <Box pl="lg">
          <CatalogLevel
            dataSourceId={dataSourceId}
            path={item.path}
            search={search}
            onPick={onPick}
            onClearSearch={onClearSearch}
          />
        </Box>
      )}
    </Stack>
  );
}
