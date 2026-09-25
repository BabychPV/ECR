import { useState, type JSX } from 'react';
import { Button, Group, Modal, Skeleton, Stack, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import type {
  AffectedRowsResponse,
  RegistryDefDto,
  RegistryEntryDto,
  RegistryEntryIdResponse,
  RegistryEntryUpsertDto,
  SetValidityRequest,
} from '@/api/types';
import { dataTypeLabel } from '@/features/templates/enumLabels';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

type RegistryEntryDetailDto = components['schemas']['RegistryEntryDetailDto'];

/** Ключ `GET …/entries/{id}` — під префіксом `entries(code)`, тож інвалідується з переліком. */
export function registryEntryKey(
  code: string,
  entryId: number,
): readonly ['registries', 'entries', string, 'detail', number] {
  return ['registries', 'entries', code, 'detail', entryId] as const;
}

/** Стан форми запису. */
export interface EntryFormState {
  readonly code: string;
  readonly display: LocalizedValue;
  readonly values: Readonly<Record<string, string>>;
}

const EmptyForm: EntryFormState = { code: '', display: {}, values: {} };

/** Стан форми з повного запису (X-03, R-04). */
export function entryFormOf(detail: RegistryEntryDetailDto): EntryFormState {
  const values: Record<string, string> = {};

  for (const [field, value] of Object.entries(detail.values)) {
    // ⚠ `null` з сервера — поле не заповнене; у формі це порожній рядок.
    values[field] = (value as string | null) ?? '';
  }

  return { code: detail.code, display: { ...(detail.displayL10n.values ?? {}) }, values };
}

/**
 * Тіло запису: лише ЗМІНЕНЕ відносно того, з чим форму відкрили.
 *
 * ⛔ X-03/R-04: назва везе всі мови форми, а мову, яку людина СВІДОМО стерла,
 * — порожнім рядком (сервер зливає назву з наявною: відсутня мова лишається,
 * порожня — прибирається). Значення полів — лише ті, що змінилися; стерте
 * поле — `null` («очистити»). Незмінене не їде зовсім, тож не перезаписується.
 */
export function entryBody(
  registry: RegistryDefDto,
  entry: RegistryEntryDto | null,
  initial: EntryFormState,
  current: EntryFormState,
): RegistryEntryUpsertDto {
  const display: Record<string, string> = { ...current.display };

  for (const language of Object.keys(initial.display)) {
    if (!(language in current.display)) display[language] = '';
  }

  const values: Record<string, string | null> = {};

  for (const field of registry.fields) {
    const before = initial.values[field.code] ?? '';
    const after = current.values[field.code] ?? '';

    if (after !== before) values[field.code] = after.trim().length === 0 ? null : after;
  }

  return {
    // ⛔ `id: null` означає СТВОРЕННЯ. Той самий ендпоінт і на створення, і
    // на правку: розділяти їх означало б два шляхи до одного інваріанта
    // унікальності коду.
    id: entry?.id ?? null,
    registryDefId: registry.id,
    code: current.code.trim(),
    display: { values: display },
    parentEntryId: entry?.parentEntryId ?? null,
    values,
  } as RegistryEntryUpsertDto;
}

/**
 * Заведення і правка запису довідника (`ФВ-8.12`).
 *
 * ⛔ Дії не було в інтерфейсі: сторож вважав `POST /registries/{code}/entries`
 * досяжним, бо клієнт ЧИТАЄ ту саму адресу (`A7-42`). Довідники — це те, на
 * що посилаються колонки типу `Lookup`; без жодного запису такі колонки не
 * пропонують нічого, і документ заповнити неможливо.
 *
 * ⛔ X-03/R-04 (четвертий раунд UX, critical): правка відкривалася з РЯДКА
 * ПЕРЕЛІКУ — там назва однією мовою і немає значень полів. Форма підставляла
 * назву під `en`, поля лишала порожніми, і збереження стирало переклади назви.
 * Тепер правка чекає `GET …/entries/{id}` (усі мови й значення) і шле лише
 * змінене (`entryBody`).
 *
 * ⚠ Стан форми живе у внутрішньому `EntryForm`, який монтується лише поки
 * діалог відкритий: повторне відкриття (зокрема «New entry» після збереження)
 * завжди починає з чистого стану, без ручного скидання.
 */
export function RegistryEntryEditor({
  registry,
  entry,
  opened,
  onClose,
}: {
  registry: RegistryDefDto;
  entry: RegistryEntryDto | null;
  opened: boolean;
  onClose: () => void;
}): JSX.Element {
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={entry === null ? t('registries.newEntry') : t('registries.editEntry')}
    >
      {opened &&
        (entry === null ? (
          <EntryForm registry={registry} entry={null} initial={EmptyForm} onClose={onClose} />
        ) : (
          <ExistingEntry registry={registry} entry={entry} onClose={onClose} />
        ))}
    </Modal>
  );
}

/** Завантажує запис цілком і лише тоді віддає форму. */
function ExistingEntry({
  registry,
  entry,
  onClose,
}: {
  registry: RegistryDefDto;
  entry: RegistryEntryDto;
  onClose: () => void;
}): JSX.Element {
  const detail = useQuery({
    queryKey: registryEntryKey(registry.code, entry.id),
    queryFn: () =>
      apiFetch<RegistryEntryDetailDto>(
        `/api/v1/registries/${encodeURIComponent(registry.code)}/entries/${String(entry.id)}`,
      ),
    // ⚠ Форма бере стан із відповіді один раз при монтуванні — кешована з
    // минулого відкриття відповідь підставила б те, чого вже немає.
    gcTime: 0,
  });

  if (detail.error !== null) {
    return <ErrorAlert error={detail.error} onRetry={() => void detail.refetch()} />;
  }

  if (detail.isPending) {
    return <Skeleton height={160} radius="sm" data-registry-entry="pending" />;
  }

  return <EntryForm registry={registry} entry={entry} initial={entryFormOf(detail.data)} onClose={onClose} />;
}

function EntryForm({
  registry,
  entry,
  initial,
  onClose,
}: {
  registry: RegistryDefDto;
  entry: RegistryEntryDto | null;
  initial: EntryFormState;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const [code, setCode] = useState(initial.code);
  const [display, setDisplay] = useState<LocalizedValue>(initial.display);
  const [values, setValues] = useState<Record<string, string>>({ ...initial.values });

  const upsert = useMutation({
    mutationFn: () =>
      apiFetch<RegistryEntryIdResponse>(
        `/api/v1/registries/${encodeURIComponent(registry.code)}/entries`,
        {
          method: 'POST',
          body: JSON.stringify(entryBody(registry, entry, initial, { code, display, values })),
        },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(registry.code) });
      onClose();
      showDone(entry === null ? t('registries.entryCreated') : t('registries.entrySaved'));
    },
    onError: showApiError,
  });

  return (
    <>
      <TextInput
        label={t('registries.code')}
        description={t('registries.entryCodeHint')}
        value={code}
        onChange={(event) => setCode(event.currentTarget.value)}
        data-autofocus
      />

      <LocalizedInput label={t('registries.name')} value={display} onChange={setDisplay} />

      {/* ⚠ Поля довідника описані в його схемі й у кожного свій тип. Поки
          редактор приймає їх текстом: сервер знає типи і відхилить невідповідне
          значення з поясненням. Типізоване поле на кожен тип — окрема робота,
          і робити її наосліп, без жодного заведеного довідника, немає сенсу. */}
      {registry.fields.length > 0 && (
        <Stack gap="xs" mt="sm">
          {registry.fields.map((field) => (
            <TextInput
              key={field.id}
              // ⛔ X-16: підписом стояв код поля, описом — сирий тип (`Decimal`).
              label={`${localized(field.nameL10n) || field.code}${field.isRequired ? ' *' : ''}`}
              description={`${field.code} · ${dataTypeLabel(field.dataType)}`}
              value={values[field.code] ?? ''}
              onChange={(event) => {
                // ⛔ Той самий клас дефекту, що `CreateDocumentModal.tsx`:
                // `event.currentTarget` React обнуляє одразу після завершення
                // обробника, а апдейтер `setValues` читав його ЛІНИВО — під
                // `StrictMode` (`main.tsx`) React навмисно кличе апдейтер
                // ДВІЧІ, і другий виклик падає з `TypeError: Cannot read
                // properties of null (reading 'value')`, розбиваючи весь
                // застосунок без `ErrorBoundary` на маршруті.
                const value = event.currentTarget.value;

                setValues((current) => ({ ...current, [field.code]: value }));
              }}
            />
          ))}
        </Stack>
      )}

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={code.trim().length === 0 || !hasAnyText(display)}
          loading={upsert.isPending}
          onClick={() => upsert.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </>
  );
}

/**
 * Вікно чинності запису (`ФВ-8.5`).
 *
 * ⛔ Це заміна видалення, а не додаткова властивість. Запис, який більше не
 * використовують, закривають датою: у комірках лежить його `Id`, і видалення
 * зробило б історичні документи нечитабельними.
 *
 * ⚠ Відповідь несе кількість зачеплених рядків документів — саме вони стають
 * **осиротілими** (`ФВ-8.13`) і блокують подання. Показати це число до того,
 * як людина дізнається про наслідок із відмови подання, — і є сенс дії.
 */
export function ValidityEditor({
  registryCode,
  entry,
  onClose,
}: {
  registryCode: string;
  entry: RegistryEntryDto | null;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [loadedFor, setLoadedFor] = useState<number | null>(null);

  if (entry !== null && loadedFor !== entry.id) {
    setLoadedFor(entry.id);
    setFrom(entry.validFrom ?? '');
    setTo(entry.validTo ?? '');
  }

  const save = useMutation({
    mutationFn: () =>
      apiFetch<AffectedRowsResponse>(
        `/api/v1/registries/${encodeURIComponent(registryCode)}/entries/${entry?.id ?? 0}/validity`,
        {
          method: 'POST',
          body: JSON.stringify({
            // ⚠ Порожнє поле — це `null`, тобто «без межі», а не порожній
            // рядок: `DateOnly?` сервера розрізняє їх, і `''` дав би 400.
            from: from.length === 0 ? null : from,
            to: to.length === 0 ? null : to,
          } satisfies SetValidityRequest),
        },
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(registryCode) });
      onClose();
      showDone(t('registries.validitySaved', { count: result.affectedRows }));
    },
    onError: showApiError,
  });

  return (
    <Modal opened={entry !== null} onClose={onClose} title={t('registries.validity')}>
      <TextInput
        // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №5/8: перехід на DateInput змінює тип значення (string → Date), тому окремим PR; список боргу сторожить lintRules.test.ts
        type="date"
        label={t('registries.validFrom')}
        description={t('registries.validityHint')}
        value={from}
        onChange={(event) => setFrom(event.currentTarget.value)}
        data-autofocus
      />

      <TextInput
        mt="sm"
        // eslint-disable-next-line no-restricted-syntax -- D15-09, борг №6/8: див. коментар вище
        type="date"
        label={t('registries.validTo')}
        value={to}
        onChange={(event) => setTo(event.currentTarget.value)}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button loading={save.isPending} onClick={() => save.mutate()}>
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
