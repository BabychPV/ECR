import { useState, type JSX } from 'react';
import { Button, Group, Modal, NumberInput, Select, Stack, Switch, TextInput } from '@mantine/core';
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

type FailedField = 'code' | 'endpoint' | 'secondaryEndpoint' | 'secretConfirmation';

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
  'err.ECR-REQ-0422.dataSourceCodeTaken': 'code',
  'err.ECR-REQ-0422.dataSourceSecretReentryRequired': 'secretConfirmation',
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
  const field = byKey === 'endpoint' && named === 'secondaryEndpoint' ? named : byKey;

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

  const save = useMutation({
    mutationFn: (body: SaveDataSourceBody) =>
      source === null ? createDataSource(body) : updateDataSource(source.id, body, rowVersion),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
      notifications.show({ message: t(source === null ? 'sources.created' : 'sources.saved') });
      onDone();
    },
    onError: (error) => {
      if (fieldFailureOf(error)?.field === 'secretConfirmation') setSecretAsked(true);
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

  const incomplete =
    draft.name.trim().length === 0 ||
    draft.endpoint.trim().length === 0 ||
    (source === null && draft.code.trim().length === 0) ||
    (secretNeeded && draft.secretConfirmation.length === 0);

  return (
    <Stack gap="sm" data-data-source-form="">
      <TextInput
        label={t('sources.code')}
        value={draft.code}
        onChange={(event) => set('code', event.currentTarget.value)}
        // ⚠ Код не змінюється: на нього спираються сутності збору.
        disabled={source !== null}
        description={source === null ? undefined : t('sources.codeFixed')}
        error={onField?.field === 'code' ? onField.text : undefined}
        required={source === null}
        ff="monospace"
        autoComplete="off"
        spellCheck={false}
      />

      <TextInput
        label={t('sources.name')}
        value={draft.name}
        onChange={(event) => set('name', event.currentTarget.value)}
        required
      />

      <Select
        label={t('sources.transport')}
        data={Transports.map((value) => ({ value, label: value }))}
        value={draft.transport}
        onChange={(value) => {
          if (value !== null) set('transport', value as SaveDataSourceBody['transport']);
        }}
        allowDeselect={false}
      />

      <TextInput
        label={t('sources.endpoint')}
        value={draft.endpoint}
        onChange={(event) => set('endpoint', event.currentTarget.value)}
        error={onField?.field === 'endpoint' ? onField.text : undefined}
        required
        ff="monospace"
        autoComplete="off"
        spellCheck={false}
        data-field="endpoint"
      />

      <TextInput
        label={t('sources.secondaryEndpoint')}
        value={draft.secondaryEndpoint}
        onChange={(event) => set('secondaryEndpoint', event.currentTarget.value)}
        error={onField?.field === 'secondaryEndpoint' ? onField.text : undefined}
        ff="monospace"
        autoComplete="off"
        spellCheck={false}
      />

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

      <TextInput
        label={t('sources.catalog')}
        value={draft.catalog}
        onChange={(event) => set('catalog', event.currentTarget.value)}
        ff="monospace"
        autoComplete="off"
        spellCheck={false}
      />

      <NumberInput
        label={t('sources.maxParallel')}
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
          <Button size="xs" variant="default" onClick={() => takeFresh(fresh)} data-take-fresh="">
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
