import { useEffect, useRef, useState, type JSX } from 'react';
import { Badge, Button, Group, Select, Switch, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { SetUiStringRequest, UiStringCatalog, UiStringRevisionResponse } from '@/api/types';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlState } from '@/shared/ui/useUrlState';
import { DefaultLanguage, t } from '@/shared/i18n';

/**
 * Редактор рядків інтерфейсу (`ФВ-14.9`, `D-95`).
 *
 * ⛔ Дії `PUT /ui-strings/{lang}/{key}` не мала в клієнті жодного споживача
 * (`A7-39`). Це не дрібниця: вимога каже, що **рядки самого інтерфейсу
 * приходять із сервера** саме для того, щоб переклад був роботою
 * термінолога, а не розробника. Без цього екрана єдиним способом виправити
 * підпис лишався SQL по продуктивній базі.
 *
 * ⛔ Порівняння з мовою за замовчуванням тут — головна колонка. Відсутній
 * переклад підмінюється англійським і **виглядає як переклад**: побачити, що
 * казахською половина інтерфейсу англійською, інакше неможливо.
 *
 * ⚠ Будь-який запис піднімає `Revision` каталогу, тобто `ETag`. Клієнти
 * перечитають рядки при наступному відкритті — не миттєво, і це навмисно:
 * розсилати зміну підпису всім відкритим вкладкам немає для чого.
 *
 * ⛔ F10 (`docs/build/UI-WALKTHROUGH.md`): екран малював **увесь** каталог
 * однією таблицею — 1340 рядків, висота сторінки 50 730 px, і жодного
 * підсумку, скільки їх узагалі. Тепер рядки домальовуються ПОРЦІЯМИ за
 * прокруткою (`RowsPerChunk`), а лічильник біля фільтра каже «стільки з
 * стількох».
 *
 * ⚠ Прийом той самий, що вже працює для сіток документа
 * (`features/grid/SheetTables.tsx`): `IntersectionObserver` на маячку в кінці
 * списку, новий спостерігач на кожну порцію (інакше маячок, що лишився у
 * видимій області, більше не повідомить про себе), і повна деградація в
 * «намалювати все» там, де `IntersectionObserver` відсутній.
 *
 * ⚠ Сторінок (пагінації) тут НЕМА і навмисно: каталог приходить ОДНИМ
 * документом (`GET /ui-strings/{lang}` віддає `strings` цілком), тож сторінки
 * на клієнті не зменшили б ані запиту, ані пам'яті — лише додали б людині
 * кроків. Дорого тут саме малювання, і прибирається саме воно.
 */
/**
 * Скільки рядків домальовується за один крок прокрутки.
 *
 * ⚠ Сто, а не двадцять: рядок тут — три комірки тексту й кнопка, і порція,
 * менша за екран, означала б, що маячок лишається видимим після кожного кроку
 * і список «дотягується» серією тактів замість одного.
 */
export const RowsPerChunk = 100;

/**
 * Наскільки раніше за появу маячка в екрані домальовувати наступну порцію.
 *
 * ⚠ Те саме число й та сама причина, що в `SheetTables.tsx`: запас приблизно
 * на третину екрана, щоб людина не бачила кінця списку, доки він росте.
 */
const LoadAheadMargin = '200px 0px';

type UiStringCoverageResponse = components['schemas']['UiStringCoverageResponse'];
type UiStringListResponse = components['schemas']['UiStringListResponse'];

export function UiStringsPage(): JSX.Element {
  const queryClient = useQueryClient();
  const languages = useLanguages();

  const [rawLang, setLang] = useUrlState('lang');
  const lang = rawLang ?? DefaultLanguage;
  const [filter, setFilter] = useState('');

  // Який ключ редагуємо і що саме введено.
  const [editingKey, setEditingKey] = useState<string | null>(null);
  const [draft, setDraft] = useState('');

  /** Каталог обраної мови; приватна область містить усе, що видно після входу. */
  const catalog = useQuery({
    queryKey: ['ui-strings', lang],
    queryFn: () => apiFetch<UiStringCatalog>(`/api/v1/ui-strings/${lang}?scope=private`),
  });

  /**
   * Каталог мови за замовчуванням — для порівняння.
   *
   * ⚠ Читається завжди, навіть коли обрана мова і є замовчуванням: інакше
   * колонка «як в оригіналі» то з'являлася б, то зникала, а таблиця міняла б
   * ширину при перемиканні мови.
   */
  const reference = useQuery({
    queryKey: ['ui-strings', DefaultLanguage],
    queryFn: () => apiFetch<UiStringCatalog>(`/api/v1/ui-strings/${DefaultLanguage}?scope=private`),
  });

  /**
   * Покриття перекладу по мовах (`BE-13`).
   *
   * ⚠ Відмова тут НЕ валить сторінку: лічильники — довідка над таблицею, і
   * без них редактор лишається робочим. Тому запит поза `AsyncBoundary`.
   */
  const coverage = useQuery({
    queryKey: ['ui-strings', 'coverage'],
    queryFn: () => apiFetch<UiStringCoverageResponse>('/api/v1/ui-strings/coverage'),
  });

  const [missingOnly, setMissingOnly] = useState(false);
  const isDefault = lang === DefaultLanguage;

  /**
   * «Сирий» перелік відсутніх — БЕЗ підміни мовою за замовчуванням (`BE-13`).
   *
   * ⛔ Каталог вище віддає вже підмінені значення, і відсутній переклад у
   * ньому невидимий; здогад «значення збігається з оригіналом» бреше на
   * кожному «OK» і «ID». Тут відповідає сервер, який бачить базу.
   */
  const missing = useQuery({
    queryKey: ['ui-strings', 'missing', lang],
    queryFn: () =>
      apiFetch<UiStringListResponse>(
        `/api/v1/ui-strings?lang=${encodeURIComponent(lang)}&missingOnly=true`,
      ),
    enabled: missingOnly && !isDefault,
  });

  const save = useMutation({
    mutationFn: (target: { key: string; value: string }) =>
      apiFetch<UiStringRevisionResponse>(
        `/api/v1/ui-strings/${lang}/${encodeURIComponent(target.key)}`,
        {
          method: 'PUT',
          body: JSON.stringify({
            value: target.value,

            // ⛔ Область — ВИДИМІСТЬ, а не рубрика (`D-114`). Редактор працює
            // з приватною: усе, що видно лише після входу. Публічні рядки —
            // сторінка входу і тексти помилок автентифікації — правляться
            // разом із поставкою, бо їх бачить той, хто ще не увійшов.
            scope: 'Private',
          } satisfies SetUiStringRequest),
        },
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['ui-strings'] });
      setEditingKey(null);
      showDone(t('uiStrings.saved', { revision: result.revision }));
    },
    onError: showApiError,
  });

  const strings = catalog.data?.strings ?? {};
  const original = reference.data?.strings ?? {};

  // ⚠ Перелік ключів береться з МОВИ ЗА ЗАМОВЧУВАННЯМ, а не з обраної: у
  // неперекладеної мови сервер віддає підмінені значення, і взяти ключі
  // звідти означало б показати рівно ті самі рядки й ніколи не побачити
  // пропущених.
  const onlyMissing = missingOnly && !isDefault;
  const missingKeys = new Set((missing.data?.items ?? []).map((item) => item.key));

  const keys = Object.keys(original)
    .filter((key) => key.toLowerCase().includes(filter.toLowerCase()))
    .filter((key) => !onlyMissing || missingKeys.has(key))
    .sort((a, b) => a.localeCompare(b));

  /** Скільки рядків зараз намальовано. */
  const [shown, setShown] = useState(RowsPerChunk);

  /*
   * ⛔ Порція скидається на зміні фільтра або мови. Без цього людина, яка
   * догорнула до тисячного рядка й після цього ввела фільтр на п'ять
   * збігів, лишалася б із лічильником «1000 з 5»: `shown` більший за
   * список — це не «показано більше», це просто бреше.
   */
  useEffect(() => {
    setShown(RowsPerChunk);
  }, [filter, lang, missingOnly]);

  const visible = keys.slice(0, shown);

  /** Маячок у кінці списку; його появу й ловить спостерігач. */
  const sentinel = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (shown >= keys.length) return;

    /*
     * ⚠ Немає `IntersectionObserver` (дуже старий браузер) — малюємо все, як
     * і до цієї картки. Свідома деградація до ПОПЕРЕДНЬОЇ, робочої поведінки:
     * сторінка лишається придатною, просто дорогою.
     */
    if (typeof IntersectionObserver === 'undefined') {
      setShown(keys.length);

      return;
    }

    const node = sentinel.current;
    if (node === null) return;

    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((entry) => entry.isIntersecting)) return;

        setShown((previous) => previous + RowsPerChunk);
      },
      { rootMargin: LoadAheadMargin },
    );

    observer.observe(node);

    /*
     * ⚠ Спостерігач створюється НАНОВО на кожну порцію — і це не
     * марнотратство. Маячок не рухається з DOM, він лише з'їжджає нижче;
     * спостерігач, який уже повідомив про його перетин, мовчатиме про той
     * самий елемент, доки той не вийде з області й не зайде знову. Новий
     * спостерігач при першому ж такті повідомляє про ПОТОЧНИЙ перетин — тобто
     * сам добирає наступну порцію, якщо маячок і досі видно (коротка сторінка,
     * велике вікно).
     */
    return () => observer.disconnect();
  }, [shown, keys.length]);

  return (
    <>
      <PageHeader
        title={t('uiStrings.title')}
        actions={
          <Group gap="xs" align="end">
            {/* ⛔ Директива D15 §0, правило L10: перелік мов збирався через
                `?? []`, тож відмова `GET /api/v1/languages` давала порожній
                `Select` — «мов у системі немає». На екрані, де ПЕРЕКЛАДАЮТЬ,
                це найгірше з можливих тверджень: людина не може обрати мову
                призначення і не знає чому. Елемент без даних не малюється
                (`D15-06`), причина стоїть банером під заголовком. */}
            {languages.error === null && (
              <Select
                size="xs"
                miw={160}
                label={t('uiStrings.language')}
                // ⚠ Доки перелік у дорозі — поле недоступне, а не порожнє.
                disabled={languages.isPending}
                data={(languages.data ?? []).map((language) => ({
                  value: language.code,
                  label: language.nameNative,
                }))}
                value={lang}
                onChange={(value) => setLang(value)}
                allowDeselect={false}
              />
            )}

            <TextInput
              size="xs"
              miw={220}
              label={t('uiStrings.filter')}
              value={filter}
              onChange={(event) => setFilter(event.currentTarget.value)}
            />

            {/* ⚠ Для мови за замовчуванням перемикач вимкнений: вона сама є
                еталоном, і «відсутніх» у ній не буває за визначенням. */}
            <Switch
              size="xs"
              label={t('uiStrings.missingOnly')}
              checked={onlyMissing}
              disabled={isDefault}
              onChange={(event) => setMissingOnly(event.currentTarget.checked)}
            />

            {/* ⛔ F10: підсумок «намальовано з усього». Нового рядка каталогу
                тут не заводиться — каталог живе в сіді БД, поза цим пакетом
                (`D-95`), — тож підпис числовий і тому однаковий усіма мовами.
                Число праворуч і є відповідь на питання «скільки їх узагалі»,
                якої на екрані не було зовсім. */}
            <Text size="xs" c="dimmed" data-testid="ui-strings-count">
              {`${String(visible.length)} / ${String(keys.length)}`}
            </Text>
          </Group>
        }
      />

      {/* ⚠ Банер саме тут, а не в шапці: у `PageHeader.actions` він стиснувся
          б у вузьку колонку поруч із фільтром. Сторінка при цьому лишається
          робочою для мови з адреси — відмова переліку забирає ВИБІР мови, а
          не редактор. */}
      {languages.error !== null && (
        <ErrorAlert error={languages.error} onRetry={() => void languages.refetch()} />
      )}

      {/* ⚠ `Array.isArray`, а не довіра типові: тип обіцяє компілятор, а не
          мережа, і відповідь іншої форми мала б лишити сторінку без лічильників,
          а не без таблиці. */}
      {coverage.data !== undefined && Array.isArray(coverage.data.languages) && (
        <Group gap="md" mb="xs" data-testid="ui-strings-coverage">
          {coverage.data.languages.map((row) => (
            <Text key={row.languageCode} size="xs" c="dimmed">
              {t('uiStrings.coverage', {
                language: row.languageCode,
                translated: row.translated,
                total: row.total,
                missing: row.missing,
              })}
            </Text>
          ))}
        </Group>
      )}

      <AsyncBoundary<UiStringCatalog>
        isPending={catalog.isPending || reference.isPending || (onlyMissing && missing.isPending)}
        error={catalog.error ?? reference.error ?? (onlyMissing ? missing.error : null)}
        data={catalog.data}
        isEmpty={() => keys.length === 0}
        emptyTitle={t('uiStrings.empty')}
        emptyHint={t('uiStrings.emptyHint')}
        skeleton="table"
        onRetry={() => {
          void catalog.refetch();
          void reference.refetch();
        }}
      >
        {() => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('uiStrings.key')}</Table.Th>
                <Table.Th>{t('uiStrings.original')}</Table.Th>
                <Table.Th>{t('uiStrings.translation')}</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {visible.map((key) => {
                const value = strings[key] ?? '';
                const source = original[key] ?? '';

                // ⛔ Ознака «немає перекладу»: значення дослівно збігається з
                // оригіналом. Сервер підміняє відсутній переклад мовою за
                // замовчуванням, тому в каталозі порожнеча не видно ніколи —
                // і саме тому неперекладений інтерфейс виглядає перекладеним.
                const untranslated = !isDefault && value === source;

                return (
                  <Table.Tr key={key}>
                    {/* ⚠ `data-allow-dotted`: ключ каталогу тут — ДАНІ редактора,
                        а не неперекладений напис (сторож `ФВ-14.9`, `D-138`). */}
                    <Table.Td data-allow-dotted>
                      <Text size="xs">{key}</Text>
                    </Table.Td>
                    <Table.Td>{source}</Table.Td>
                    <Table.Td>
                      {editingKey === key ? (
                        <TextInput
                          size="xs"
                          aria-label={`${t('uiStrings.translation')} · ${key}`}
                          value={draft}
                          onChange={(event) => setDraft(event.currentTarget.value)}
                          data-autofocus
                        />
                      ) : (
                        <Group gap="xs">
                          <Text>{value}</Text>
                          {untranslated && (
                            <Badge size="xs" color="statusWarning" variant="light">
                              {t('uiStrings.untranslated')}
                            </Badge>
                          )}
                        </Group>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Group gap="xs" justify="flex-end">
                        {editingKey === key ? (
                          <>
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              onClick={() => setEditingKey(null)}
                            >
                              {t('common.cancel')}
                            </Button>
                            <Button
                              size="compact-xs"
                              loading={save.isPending}
                              onClick={() => save.mutate({ key, value: draft })}
                            >
                              {t('common.save')}
                            </Button>
                          </>
                        ) : (
                          <Button
                            size="compact-xs"
                            variant="subtle"
                            onClick={() => {
                              setEditingKey(key);

                              // ⚠ Поле відкривається з ПОРОЖНІМ значенням для
                              // неперекладеного ключа: підставлений оригінал
                              // тут — найлегший спосіб «перекласти» сотню
                              // рядків, натиснувши «зберегти» сто разів.
                              setDraft(untranslated ? '' : value);
                            }}
                          >
                            {t('uiStrings.edit')}
                          </Button>
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}

              {/* ⛔ Маячок — ОСТАННІМ РЯДКОМ таблиці, а не сусіднім блоком:
                  `<div>` між `<tbody>` і `</table>` браузер викидає з таблиці
                  в попередній вузол (foster parenting), і спостерігач стежив
                  би за елементом, що стоїть НЕ там, де здається в коді.
                  Порожній рядок не малює нічого видимого — його робота вся в
                  тому, щоб потрапити в область видимості.

                  ⚠ Рядок є ЛИШЕ доки є що домальовувати: інакше він лишався б
                  порожнім хвостом смугастої таблиці назавжди. */}
              {shown < keys.length && (
                <Table.Tr data-testid="ui-strings-sentinel">
                  <Table.Td colSpan={4}>
                    <div ref={sentinel} aria-hidden="true" />
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </>
  );
}
