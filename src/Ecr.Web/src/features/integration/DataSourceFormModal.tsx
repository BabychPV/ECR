import { useState, type JSX } from 'react';
import { useReturnFocusOnUnmount } from '@/shared/a11y/focus';
import { Button, Checkbox, Group, Modal, NumberInput, Select, Stack, Switch, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { language, t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { dataSourceName, freshVersionOf } from './DataSourceDrawer';
import {
  createDataSource,
  updateDataSource,
  type DataSource,
  type SaveDataSourceBody,
} from './dataSourceApi';
import { DataSourcesQueryKey } from './dataSourcesKey';

/** Транспорти, які знає сервер (`ExternalTransport`). */
const Transports: readonly SaveDataSourceBody['transport'][] = ['PiWebApi', 'PiSqlClient', 'Sql'];

type Transport = SaveDataSourceBody['transport'];

/**
 * Підказки полів за транспортом: що саме вводити. Ключі — літерали (а не склеєні), щоб сторож ключів
 * каталогу їх бачив. Зміст звірено з сервером: `DataSourceHandlers.ValidateAsync`, `DataSourceEndpointPolicy`,
 * `SqlDataSource` (рядок SqlClient, `Catalog` перекриває `Initial Catalog`), `PiWebApiDataSource`
 * (`assetdatabases/{Catalog}/elements`), `PiSqlClientDataSource` (рядок ODBC; `Catalog` не читає),
 * `EndpointFailover` (запасна адреса — лише PiWebApi і PiSqlClient).
 */
/** Літерали в умовному виразі, а не склеєний ключ: сторож ключів каталогу бачить кожен. */
function transportHint(transport: Transport): string {
  return t(
    transport === 'PiSqlClient'
      ? 'sources.transportHint.PiSqlClient'
      : transport === 'PiWebApi'
        ? 'sources.transportHint.PiWebApi'
        : 'sources.transportHint.Sql',
  );
}

function endpointHint(transport: Transport): string {
  return t(
    transport === 'PiSqlClient'
      ? 'sources.endpointHint.PiSqlClient'
      : transport === 'PiWebApi'
        ? 'sources.endpointHint.PiWebApi'
        : 'sources.endpointHint.Sql',
  );
}

function secondaryEndpointHint(transport: Transport): string {
  return t(
    transport === 'PiSqlClient'
      ? 'sources.secondaryEndpointHint.PiSqlClient'
      : transport === 'PiWebApi'
        ? 'sources.secondaryEndpointHint.PiWebApi'
        : 'sources.secondaryEndpointHint.Sql',
  );
}

function catalogHint(transport: Transport): string {
  return t(
    transport === 'PiSqlClient'
      ? 'sources.catalogHint.PiSqlClient'
      : transport === 'PiWebApi'
        ? 'sources.catalogHint.PiWebApi'
        : 'sources.catalogHint.Sql',
  );
}
/**
 * Приклади-заповнювачі — технічні значення, не переклад, тому не в каталозі. Лише нейтральні хости
 * (`*.example.local`), без імен користувачів і паролів; для рядків з'єднання — лише вбудована
 * автентифікація Windows. `undefined` — прикладу немає (поле для транспорту не використовується).
 */
const EndpointExample: Readonly<Record<Transport, string>> = {
  PiSqlClient: 'Driver={PI SQL Client};AF Server=pi.example.local;AF Database=ECR;Integrated Security=SSPI',
  PiWebApi: 'https://pi.example.local/piwebapi',
  Sql: 'Server=sql01.example.local;Database=Reports;Integrated Security=true',
};

const SecondaryExample: Readonly<Record<Transport, string | undefined>> = {
  PiSqlClient: 'Driver={PI SQL Client};AF Server=pi02.example.local;AF Database=ECR;Integrated Security=SSPI',
  PiWebApi: 'https://pi02.example.local/piwebapi',
  Sql: undefined,
};

const CatalogExample: Readonly<Record<Transport, string | undefined>> = {
  PiSqlClient: undefined,
  PiWebApi: 'ECR',
  Sql: 'Reports',
};

/**
 * Чи читає транспорт поле. ⛔ Sql не має запасної адреси (`EndpointFailover` — лише PiWebApi і PiSqlClient),
 * PiSqlClient не читає каталогу (база — `AF Database` у рядку з'єднання); сервер відмовляє в значенні
 * такого поля `dataSourceFieldNotRead`. Тому порожнє поле для такого транспорту приховане, а вже введене чи
 * збережене значення показується лише для читання з поясненням і кнопкою «Очистити» — мовчки не зникає.
 */
const ReadsSecondary: Readonly<Record<Transport, boolean>> = { PiSqlClient: true, PiWebApi: true, Sql: false };
const ReadsCatalog: Readonly<Record<Transport, boolean>> = { PiSqlClient: false, PiWebApi: true, Sql: true };

/**
 * Кнопка очищення поля, якого транспорт не читає. ⚠ Таке поле в формі щонайбільше одне (Sql читає каталог,
 * PiSqlClient — запасну адресу), тож назва кнопки однозначна й без імені поля.
 */
function ClearUnused({ onClear }: { readonly onClear: () => void }): JSX.Element {
  return (
    <Button variant="subtle" size="compact-xs" mr="xs" onClick={onClear} data-clear-unused="">
      {t('sources.clearUnusedField')}
    </Button>
  );
}

type FailedField =
  | 'code'
  | 'endpoint'
  | 'secondaryEndpoint'
  | 'catalog'
  | 'secretConfirmation'
  | 'confirmEndpointChange';

/**
 * Відмови сервера, які належать КОНКРЕТНОМУ полю, — за `messageKey`.
 *
 * ⛔ `dataSourceSecretReentryRequired` (S3): адреса нова, а секрет середовища
 * під це джерело не підтверджено — відмова стоїть біля поля секрету.
 *
 * Решта відмов (`dataSourceInvalid`, 403, 5xx) — загальний `ErrorAlert` над
 * кнопками.
 *
 * ⚠ `dataSourceEndpointCarriesSecret` сервер кидає за будь-яку з двох адрес і
 * називає її полем `field` верхнього рівня (`endpoint` | `secondaryEndpoint`).
 * Без `field` (старий сервер) — біля ОСНОВНОЇ: власного правила «де саме
 * облікові дані» клієнт не вигадує.
 */
const FieldOfKey: Readonly<Record<string, FailedField>> = {
  'err.ECR-REQ-0422.dataSourceEndpointCarriesSecret': 'endpoint',
  // D1: політика адреси (SSRF) — відмова стоїть біля поля адреси, а не загальним алертом.
  'err.ECR-REQ-0422.dataSourceEndpointScheme': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointHostForbidden': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointMalformed': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointHostNotAllowed': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointSqlScheme': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointSqlForbiddenOption': 'endpoint',
  'err.ECR-REQ-0422.dataSourceEndpointNotConnectionString': 'endpoint',
  // Поле, якого транспорт не читає: сервер називає його в `field` (`secondaryEndpoint` | `catalog`).
  'err.ECR-REQ-0422.dataSourceFieldNotRead': 'secondaryEndpoint',
  'err.ECR-REQ-0422.dataSourceCodeTaken': 'code',
  'err.ECR-REQ-0422.dataSourceSecretReentryRequired': 'secretConfirmation',
  'err.ECR-REQ-0422.dataSourceEndpointChangeUnconfirmed': 'confirmEndpointChange',
};

interface FieldFailure {
  readonly field: FailedField;
  readonly text: string;
}

/** Відмова → поле, біля якого її показати; `null` — відмова не про поле. */
export function fieldFailureOf(error: unknown): FieldFailure | null {
  if (!(error instanceof EcrApiError) || error.problem.status !== 422) return null;

  const key = error.problem.extensions2?.['messageKey'];
  if (typeof key !== 'string') return null;

  const byKey = FieldOfKey[key];
  if (byKey === undefined) return null;

  const named = error.problem.extensions2?.['field'];
  const field =
    (byKey === 'endpoint' && named === 'secondaryEndpoint') ||
    (key === 'err.ECR-REQ-0422.dataSourceFieldNotRead' && named === 'catalog')
      ? named
      : byKey;

  const code = error.problem.extensions2?.['code'];

  return { field, text: t(key, typeof code === 'string' ? { code } : undefined) };
}

/** Чернетка форми: рядки — як їх бачить людина, `null` лише в числі. */
interface Draft {
  code: string;
  name: string;
  transport: SaveDataSourceBody['transport'];
  endpoint: string;
  secondaryEndpoint: string;
  catalog: string;
  maxParallel: number | null;
  isActive: boolean;
  /** Повторно введений секрет; живе лише в чернетці, у переліку не буває. */
  secretConfirmation: string;
  /** Явне підтвердження зміни адреси джерела з Windows-автентифікацією (без секрету). */
  confirmEndpointChange: boolean;
}

/** Чи змінює правка транспорт чи адресу (буквальне порівняння; остаточне слово — за сервером). */
function addressChanged(draft: Draft, source: DataSource | null): boolean {
  return (
    source !== null &&
    (draft.transport !== source.transport ||
      draft.endpoint.trim() !== source.endpoint.trim() ||
      draft.secondaryEndpoint.trim() !== (source.secondaryEndpoint ?? '').trim())
  );
}

/** Правка адреси джерела БЕЗ секрету (Windows-автентифікація) потребує явного підтвердження. */
export function needsEndpointConfirmation(draft: Draft, source: DataSource | null): boolean {
  return source !== null && !source.hasSecret && addressChanged(draft, source);
}

/**
 * Чи правка веде секрет джерела на НОВУ адресу (S3).
 *
 * ⚠ Порівняння буквальне — ширше за серверне (сервер не вважає зміною регістр
 * хоста чи кінцеву `/`). Зайве поле коштує одного введення; пропущене — `422`
 * після натиску «Зберегти». Остаточне слово за сервером: його відмова
 * `dataSourceSecretReentryRequired` теж відкриває поле.
 */
export function needsSecretReentry(draft: Draft, source: DataSource | null): boolean {
  if (source === null || !source.hasSecret) return false;

  return (
    draft.transport !== source.transport ||
    draft.endpoint.trim() !== source.endpoint.trim() ||
    draft.secondaryEndpoint.trim() !== (source.secondaryEndpoint ?? '').trim()
  );
}

function draftOf(source: DataSource | null): Draft {
  return {
    code: source?.code ?? '',
    name: source === null ? '' : (source.nameL10n[language()] ?? dataSourceName(source)),
    // D-212: типовий транспорт нового з'єднання — PI SQL (views — основний канал), не PI Web API.
    transport: source?.transport ?? 'PiSqlClient',
    endpoint: source?.endpoint ?? '',
    secondaryEndpoint: source?.secondaryEndpoint ?? '',
    catalog: source?.catalog ?? '',
    maxParallel: source?.maxParallel ?? null,
    isActive: source?.isActive ?? true,
    secretConfirmation: '',
    confirmEndpointChange: false,
  };
}

/**
 * Тіло запиту з чернетки.
 *
 * ⚠ Назва пишеться мовою інтерфейсу ПОВЕРХ наявних мов, а не замість них:
 * правка англійською не має стирати російську й казахську назви.
 *
 * ⚠ `isActive` при створенні сервер ігнорує — тому й перемикача в формі
 * створення немає, а в тілі стоїть `true`.
 */
export function bodyOf(draft: Draft, base: DataSource | null): SaveDataSourceBody {
  const optional = (value: string): string | null => (value.trim().length === 0 ? null : value);

  return {
    code: base?.code ?? draft.code,
    nameL10n: { ...(base?.nameL10n ?? {}), [language()]: draft.name },
    transport: draft.transport,
    endpoint: draft.endpoint,
    secondaryEndpoint: optional(draft.secondaryEndpoint),
    catalog: optional(draft.catalog),
    maxParallel: draft.maxParallel,
    isActive: base === null ? true : draft.isActive,
    // ⛔ Лише коли людина його ввела: сервер звіряє й не зберігає.
    secretConfirmation: draft.secretConfirmation.length === 0 ? null : draft.secretConfirmation,
    // `true` лише коли людина підтвердила й це правка (при створенні не потрібне).
    confirmEndpointChange: base !== null && draft.confirmEndpointChange,
  };
}

/**
 * Створення (`source === null`) або правка з'єднання (`ФВ-14.3`, `UI-09`).
 *
 * ⛔ Поля «задати секрет» НЕМАЄ (`Q15-06`): джерела ходять під службовим
 * обліковим записом. Облікові дані в адресі сервер відхиляє `422
 * dataSourceEndpointCarriesSecret` — відмова стоїть біля поля адреси.
 *
 * ⛔ S3: якщо середовище дає секрет під це джерело (`hasSecret`) і правка
 * змінює транспорт чи адресу, з'являється поле «секрет ще раз» — без нього
 * сервер не пустить секрет на нову адресу. Це підтвердження, а не збереження.
 *
 * ⛔ Правка шле `If-Match` із `rowVersion` рядка, який ПОКАЗАЛИ людині.
 * Хтось змінив з'єднання між читанням і збереженням — `409 dataSourceChanged`:
 * причина над кнопками, «Зберегти» заблоковано, а кнопка «взяти свіжу версію»
 * бере чинну версію з тіла відмови й перечитує перелік. Поля людини при цьому
 * лишаються — вона бачить, що змінилося, і зберігає ще раз свідомо.
 *
 * ⛔ Чернетка живе, доки діалог відкритий: будь-яка відмова лишає введене як
 * є, а перелік, перечитаний під час правки, чернетки не перезаписує
 * (`useState` читає `source` лише при монтуванні).
 */
export function DataSourceFormModal({
  opened,
  source,
  onClose,
}: {
  readonly opened: boolean;
  readonly source: DataSource | null;
  readonly onClose: () => void;
}): JSX.Element {
  useReturnFocusOnUnmount();
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        source === null
          ? t('sources.newConnection')
          : t('sources.editTitle', { name: dataSourceName(source) })
      }
    >
      {/* Форма монтується з кожним відкриттям: чернетка попередньої спроби
          не переходить у наступну. */}
      {opened && <DataSourceForm source={source} onDone={onClose} />}
    </Modal>
  );
}

function DataSourceForm({
  source,
  onDone,
}: {
  readonly source: DataSource | null;
  readonly onDone: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<Draft>(() => draftOf(source));

  // ⚠ Версія, яку шле `If-Match`: спершу — показаного рядка; після `409` —
  // та, яку сервер назвав чинною, і лише коли людина сама її взяла.
  const [rowVersion, setRowVersion] = useState<string>(() => source?.rowVersion ?? '');

  // ⚠ Сервер попросив секрет (зокрема при СТВОРЕННІ, коли секрет під цей код
  // уже заданий у середовищі, — клієнт цього знати не може): поле лишається
  // відкритим до кінця діалогу, а не зникає на час наступного запиту.
  const [secretAsked, setSecretAsked] = useState(false);

  // ⚠ Те саме для підтвердження зміни адреси (Negotiate-джерело): сервер знає
  // про секрет-«Negotiate», клієнт — лише `hasSecret`.
  const [endpointAsked, setEndpointAsked] = useState(false);

  const save = useMutation({
    mutationFn: (body: SaveDataSourceBody) =>
      source === null ? createDataSource(body) : updateDataSource(source.id, body, rowVersion),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
      notifications.show({ message: t(source === null ? 'sources.created' : 'sources.saved') });
      onDone();
    },
    onError: (error) => {
      const failed = fieldFailureOf(error)?.field;
      if (failed === 'secretConfirmation') setSecretAsked(true);
      if (failed === 'confirmEndpointChange') setEndpointAsked(true);
    },
  });

  const set = <K extends keyof Draft>(key: K, value: Draft[K]): void => {
    setDraft((current) => ({ ...current, [key]: value }));
  };

  const onField = fieldFailureOf(save.error);
  const general = save.error !== null && onField === null ? save.error : null;
  const fresh = freshVersionOf(save.error);

  const takeFresh = (version: string): void => {
    setRowVersion(version);
    save.reset();
    void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
  };

  // ⚠ Лише обов'язковість — те, що сервер вимагає однаково й без чого запит
  // гарантовано дав би `422`. Власних правил формату тут немає.
  const secretNeeded = needsSecretReentry(draft, source);
  const secretShown = secretNeeded || secretAsked;

  const confirmNeeded = needsEndpointConfirmation(draft, source);
  const confirmShown = confirmNeeded || endpointAsked;

  const incomplete =
    draft.name.trim().length === 0 ||
    draft.endpoint.trim().length === 0 ||
    (source === null && draft.code.trim().length === 0) ||
    (secretNeeded && draft.secretConfirmation.length === 0) ||
    (confirmShown && !draft.confirmEndpointChange);

  return (
    <Stack gap="sm" data-data-source-form="">
      <TextInput
        label={t('sources.code')}
        value={draft.code}
        onChange={(event) => set('code', event.currentTarget.value)}
        // ⚠ Код не змінюється: на нього спираються сутності збору.
        disabled={source !== null}
        description={source === null ? t('sources.codeHint') : t('sources.codeFixed')}
        placeholder={source === null ? 'PI_MAIN' : undefined}
        error={onField?.field === 'code' ? onField.text : undefined}
        required={source === null}
        ff="monospace"
        autoComplete="off"
        spellCheck={false}
      />

      <TextInput
        label={t('sources.name')}
        description={t('sources.nameHint')}
        placeholder={t('sources.namePlaceholder')}
        value={draft.name}
        onChange={(event) => set('name', event.currentTarget.value)}
        required
      />

      <Select
        label={t('sources.transport')}
        description={transportHint(draft.transport)}
        data={Transports.map((value) => ({ value, label: value }))}
        value={draft.transport}
        onChange={(value) => {
          if (value !== null) set('transport', value as SaveDataSourceBody['transport']);
        }}
        allowDeselect={false}
      />

      <TextInput
        label={t('sources.endpoint')}
        description={endpointHint(draft.transport)}
        placeholder={EndpointExample[draft.transport]}
        value={draft.endpoint}
        onChange={(event) => set('endpoint', event.currentTarget.value)}
        error={onField?.field === 'endpoint' ? onField.text : undefined}
        required
        ff="monospace"
        autoComplete="off"
        spellCheck={false}
        data-field="endpoint"
      />

      {(ReadsSecondary[draft.transport] || draft.secondaryEndpoint.length > 0) && (
        <TextInput
          label={t('sources.secondaryEndpoint')}
          description={secondaryEndpointHint(draft.transport)}
          placeholder={SecondaryExample[draft.transport]}
          value={draft.secondaryEndpoint}
          onChange={(event) => set('secondaryEndpoint', event.currentTarget.value)}
          // Транспорт поля не читає, а значення є: лише для читання й очищення (сервер його не збереже).
          readOnly={!ReadsSecondary[draft.transport]}
          rightSectionWidth={ReadsSecondary[draft.transport] ? undefined : 'auto'}
          rightSection={
            ReadsSecondary[draft.transport] ? undefined : (
              <ClearUnused onClear={() => set('secondaryEndpoint', '')} />
            )
          }
          error={onField?.field === 'secondaryEndpoint' ? onField.text : undefined}
          ff="monospace"
          autoComplete="off"
          spellCheck={false}
          data-field="secondaryEndpoint"
        />
      )}

      {/* ⛔ S3: секрет середовища йде лише на адресу, яку підтвердив той, хто
          його знає. Значення не зберігається ні тут, ні на сервері. */}
      {/* ⚠ `TextInput type="password"`, а не `PasswordInput`: той не зв'язує
          текст помилки з полем (`aria-describedby`), і читалка не почула б,
          чому збереження відмовлено. */}
      {secretShown && (
        <TextInput
          type="password"
          label={t('sources.secretConfirmation')}
          description={t('sources.secretConfirmationHint')}
          value={draft.secretConfirmation}
          onChange={(event) => set('secretConfirmation', event.currentTarget.value)}
          error={onField?.field === 'secretConfirmation' ? onField.text : undefined}
          required={secretNeeded}
          autoComplete="new-password"
          data-field="secretConfirmation"
        />
      )}

      {/* ⛔ Адреса джерела з Windows-автентифікацією змінюється лише свідомо:
          службова обліковка піде на новий хост. */}
      {confirmShown && (
        <Checkbox
          label={t('sources.confirmEndpointChange')}
          checked={draft.confirmEndpointChange}
          onChange={(event) => set('confirmEndpointChange', event.currentTarget.checked)}
          error={onField?.field === 'confirmEndpointChange' ? onField.text : undefined}
          data-field="confirmEndpointChange"
        />
      )}

      {(ReadsCatalog[draft.transport] || draft.catalog.length > 0) && (
        <TextInput
          label={t('sources.catalog')}
          description={catalogHint(draft.transport)}
          placeholder={CatalogExample[draft.transport]}
          value={draft.catalog}
          onChange={(event) => set('catalog', event.currentTarget.value)}
          readOnly={!ReadsCatalog[draft.transport]}
          rightSectionWidth={ReadsCatalog[draft.transport] ? undefined : 'auto'}
          rightSection={
            ReadsCatalog[draft.transport] ? undefined : (
              <ClearUnused onClear={() => set('catalog', '')} />
            )
          }
          error={onField?.field === 'catalog' ? onField.text : undefined}
          ff="monospace"
          autoComplete="off"
          spellCheck={false}
          data-field="catalog"
        />
      )}

      <NumberInput
        label={t('sources.maxParallel')}
        description={t('sources.maxParallelHint')}
        placeholder="4"
        value={draft.maxParallel ?? ''}
        onChange={(value) => set('maxParallel', typeof value === 'number' ? value : null)}
        min={1}
        max={32}
        allowDecimal={false}
        allowNegative={false}
      />

      {source !== null && (
        <Switch
          label={t('sources.isActive')}
          checked={draft.isActive}
          onChange={(event) => set('isActive', event.currentTarget.checked)}
        />
      )}

      {general !== null && <ErrorAlert error={general} />}

      {fresh !== null && (
        <Group gap="xs">
          <Button variant="default" onClick={() => takeFresh(fresh)} data-take-fresh="">
            {t('sources.reloadCurrent')}
          </Button>
        </Group>
      )}

      <Group justify="flex-end" gap="xs">
        <Button variant="default" onClick={onDone}>
          {t('common.cancel')}
        </Button>

        <Button
          loading={save.isPending}
          // ⛔ Поверх нерозв'язаного конфлікту не зберігаємо: та сама версія
          // дала б той самий `409`.
          disabled={incomplete || fresh !== null}
          onClick={() => save.mutate(bodyOf(draft, source))}
        >
          {source === null ? t('sources.create') : t('common.save')}
        </Button>
      </Group>
    </Stack>
  );
}
