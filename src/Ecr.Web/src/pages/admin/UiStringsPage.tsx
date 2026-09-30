import { useCallback, useDeferredValue, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Group, Select, Switch, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { SetUiStringRequest, UiStringCatalog, UiStringRevisionResponse } from '@/api/types';
import { UiStringsCsvPanel } from '@/features/localization/UiStringsCsvPanel';
import { UiStringsTable } from '@/features/localization/UiStringsTable';
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

/** Стабільна порожнеча — щоб `UiStringsTable` (`memo`) не отримував новий `{}` на кожен рендер. */
const NoStrings: Record<string, string> = {};

type UiStringCoverageResponse = components['schemas']['UiStringCoverageResponse'];
type UiStringListResponse = components['schemas']['UiStringListResponse'];

export function UiStringsPage(): JSX.Element {
  const queryClient = useQueryClient();
  const languages = useLanguages();

  const [rawLang, setLang] = useUrlState('lang');
  const lang = rawLang ?? DefaultLanguage;
  const [filter, setFilter] = useState('');

  /*
   * ⛔ Живий дефект (2026-09-24, замір на стенді): «Filter by key» фільтрував і
   * перемальовував таблицю СИНХРОННО на кожен символ — максимум 227 мс на
   * символ (dev). Поле вводу оновлюється одразу (`filter`), а перелік і
   * таблиця йдуть за ВІДКЛАДЕНИМ значенням: React малює їх перервним рендером
   * після кадру з новим символом, а `UiStringsTable` (`memo`) у терміновому
   * рендері не чіпається зовсім. Результат фільтра той самий — він лише
   * наздоганяє поле, а не блокує його.
   */
  const deferredFilter = useDeferredValue(filter);

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

  const strings = catalog.data?.strings ?? NoStrings;
  const original = reference.data?.strings ?? NoStrings;

  // ⚠ Перелік ключів береться з МОВИ ЗА ЗАМОВЧУВАННЯМ, а не з обраної: у
  // неперекладеної мови сервер віддає підмінені значення, і взяти ключі
  // звідти означало б показати рівно ті самі рядки й ніколи не побачити
  // пропущених.
  const onlyMissing = missingOnly && !isDefault;
  const missingItems = missing.data?.items;

  const keys = useMemo(() => {
    const needle = deferredFilter.toLowerCase();
    const missingKeys = new Set((missingItems ?? []).map((item) => item.key));

    return Object.keys(original)
      .filter((key) => key.toLowerCase().includes(needle))
      .filter((key) => !onlyMissing || missingKeys.has(key))
      .sort((a, b) => a.localeCompare(b));
  }, [original, deferredFilter, onlyMissing, missingItems]);

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
  }, [deferredFilter, lang, missingOnly]);

  const visible = useMemo(() => keys.slice(0, shown), [keys, shown]);

  const saveMutate = save.mutate;
  const startEdit = useCallback((key: string, initial: string) => {
    setEditingKey(key);
    setDraft(initial);
  }, []);
  const cancelEdit = useCallback(() => setEditingKey(null), []);
  const saveKey = useCallback(
    (key: string) => saveMutate({ key, value: draft }),
    [saveMutate, draft],
  );

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

      {/* ⛔ `BE-13` ч.2: обмін перекладом через CSV. Редагування по одному полю
          лишається — воно для виправлення підпису; CSV існує для іншої роботи:
          віддати тисячу рядків термінологові й прийняти їх назад. Без цього
          екрана обидві серверні дії були недосяжні, і сторож
          `Кожна_дія_сервера_має_споживача_в_інтерфейсі` червонів саме на них. */}
      <UiStringsCsvPanel />

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
          <UiStringsTable
            visible={visible}
            hasMore={shown < keys.length}
            sentinel={sentinel}
            strings={strings}
            original={original}
            isDefault={isDefault}
            editingKey={editingKey}
            draft={draft}
            saving={save.isPending}
            onDraftChange={setDraft}
            onEdit={startEdit}
            onCancel={cancelEdit}
            onSave={saveKey}
          />
        )}
      </AsyncBoundary>
    </>
  );
}
