import { useState, type JSX } from 'react';
import { Button, Group, Loader, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { normalizeDecimal } from '@/shared/format';
import { language, t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { getUnit, unitUsage, updateUnit, type UnitDetail, type UsageResponse } from './api';

const UnitChanged = 'err.ECR-UOM-0409.unitChanged';
const FactorInUse = 'err.ECR-UOM-0409.unitFactorInUse';

function messageKeyOf(error: unknown, status: number): string | null {
  if (!(error instanceof EcrApiError) || error.problem.status !== status) return null;
  const key = error.problem.extensions2?.['messageKey'];
  return typeof key === 'string' ? key : null;
}

/** `409 unitChanged` → чинна версія з тіла відмови; `null` — відмова інша. */
export function freshUnitVersionOf(error: unknown): string | null {
  if (messageKeyOf(error, 409) !== UnitChanged || !(error instanceof EcrApiError)) return null;
  const rowVersion = error.problem.extensions2?.['rowVersion'];
  return typeof rowVersion === 'string' && rowVersion.length > 0 ? rowVersion : null;
}

/**
 * `409 unitFactorInUse` → кількість посилань; `null` — відмова інша.
 *
 * ⚠ `total` їде рядком (параметр каталогу повідомлень).
 */
export function factorInUseOf(error: unknown): number | null {
  if (messageKeyOf(error, 409) !== FactorInUse || !(error instanceof EcrApiError)) return null;
  const total = Number(error.problem.extensions2?.['total']);
  return Number.isFinite(total) ? total : 0;
}

/** `422 ECR-UOM-0422` на правці — множник не додатний; належить полю множника. */
function isFactorRejected(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.status === 422 && error.problem.errorCode === 'ECR-UOM-0422';
}

/**
 * Причина, з якої множник і зсув не редагуються; `null` — редагуються.
 *
 * ⛔ Деградація — в бік ЗАБОРОНИ: поки перелік посилань вантажиться або якщо
 * він відмовив, поля вимкнені. Дозволити правку «наосліп» означало б вести
 * людину у відому відмову сервера, а для сервера з дефектом — у тихо
 * перераховані збережені значення.
 */
export function factorLockReason(
  unit: UnitDetail,
  usage: { readonly isPending: boolean; readonly isError: boolean; readonly data: UsageResponse | undefined },
  serverInUse: number | null,
): string | null {
  if (unit.isBase) return t('units.factorLockedBase');
  if (serverInUse !== null) return t('units.factorLockedUsed', { total: serverInUse });
  if (usage.isError) return t('units.factorLockedUnknown');
  if (usage.isPending || usage.data === undefined) return t('units.factorLockedChecking');
  if (usage.data.total > 0) return t('units.factorLockedUsed', { total: usage.data.total });
  return null;
}

/**
 * Правка одиниці виміру (BE-15 ч.2).
 *
 * ⛔ Код, розмірність і ознака базової не редагуються — їх у формі немає як
 * полів. Позначення й назва — завжди; множник і зсув — лише в небазової
 * одиниці, на яку ніщо не посилається.
 */
export function UnitEditModal({
  unitId,
  code,
  onClose,
}: {
  readonly unitId: number | null;
  readonly code: string;
  readonly onClose: () => void;
}): JSX.Element {
  return (
    <Modal opened={unitId !== null} onClose={onClose} title={t('units.editTitle', { code })}>
      {/* Форма монтується з кожним відкриттям: чернетка попередньої спроби
          не переходить у наступну. */}
      {unitId !== null && <UnitEditLoader unitId={unitId} onDone={onClose} />}
    </Modal>
  );
}

function UnitEditLoader({ unitId, onDone }: { readonly unitId: number; readonly onDone: () => void }): JSX.Element {
  const detail = useQuery({
    queryKey: ['units', unitId, 'detail'],
    queryFn: () => getUnit(unitId),
    staleTime: 0,
  });

  if (detail.error !== null) return <ErrorAlert error={detail.error} />;
  if (detail.data === undefined) return <Loader size="sm" />;

  return <UnitEditForm unit={detail.data} onDone={onDone} />;
}

interface Draft {
  symbol: string;
  name: string;
  factor: string;
  offset: string;
}

function textIn(values: Record<string, string>): string {
  return values[language()] ?? Object.values(values).find((value) => value.length > 0) ?? '';
}

function UnitEditForm({ unit, onDone }: { readonly unit: UnitDetail; readonly onDone: () => void }): JSX.Element {
  const queryClient = useQueryClient();

  // ⚠ `unit` лише СТАРТУЄ чернетку: перечитана під час правки одиниця
  // введеного не перезаписує.
  const [draft, setDraft] = useState<Draft>(() => ({
    symbol: textIn(unit.symbolL10n),
    name: textIn(unit.nameL10n),
    factor: unit.factorToBase,
    offset: unit.offsetToBase,
  }));

  // ⚠ Версія для `If-Match`: спершу — показаної одиниці; після `409` — та, яку
  // сервер назвав чинною, і лише коли людина сама її взяла.
  const [rowVersion, setRowVersion] = useState(unit.rowVersion);

  // ⛔ Відмова сервера `unitFactorInUse` остаточна: після неї поля не
  // розблоковує вже ніщо в цій формі.
  const [serverInUse, setServerInUse] = useState<number | null>(null);

  const usage = useQuery({
    queryKey: ['units', unit.id, 'usage'],
    queryFn: () => unitUsage(unit.id),
    enabled: !unit.isBase,
    staleTime: 0,
  });

  const lock = factorLockReason(unit, usage, serverInUse);

  // ⚠ Заблоковані поля показують і шлють ЧИННІ значення, а не чернетку: інакше
  // правка позначення падала б на множнику, якого людина змінити не може.
  const factor = lock === null ? draft.factor : unit.factorToBase;
  const offset = lock === null ? draft.offset : unit.offsetToBase;

  const save = useMutation({
    meta: { handled: true },
    mutationFn: () =>
      updateUnit(
        unit.id,
        {
          // ⚠ Поточна мова ПОВЕРХ наявних, а не замість них.
          symbolL10n: { ...unit.symbolL10n, [language()]: draft.symbol.trim() },
          nameL10n: { ...unit.nameL10n, [language()]: draft.name.trim() },
          // ⛔ Рядком: жодного `Number` — 20-та значуща цифра множника доїжджає.
          factorToBase: normalizeDecimal(factor) ?? factor,
          offsetToBase: normalizeDecimal(offset) ?? offset,
        },
        rowVersion,
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['units'] });
      showDone(t('units.saved'));
      onDone();
    },
    onError: (error) => {
      const total = factorInUseOf(error);
      if (total !== null) setServerInUse(total);
    },
  });

  const fresh = freshUnitVersionOf(save.error);
  const inUse = factorInUseOf(save.error);
  const factorRejected = isFactorRejected(save.error);
  const general = save.error !== null && fresh === null && inUse === null && !factorRejected ? save.error : null;

  const takeFresh = (version: string): void => {
    setRowVersion(version);
    save.reset();
    void queryClient.invalidateQueries({ queryKey: ['units'] });
  };

  const set = <K extends keyof Draft>(key: K, value: Draft[K]): void => {
    setDraft((current) => ({ ...current, [key]: value }));
  };

  const incomplete =
    draft.symbol.trim().length === 0 ||
    draft.name.trim().length === 0 ||
    normalizeDecimal(factor) === null ||
    normalizeDecimal(offset) === null;

  return (
    <Stack gap="sm" data-unit-edit-form="">
      <Text size="sm" c="dimmed">
        {unit.code} · {t('units.codeFixed')}
      </Text>

      <TextInput
        label={t('units.symbol')}
        value={draft.symbol}
        onChange={(event) => set('symbol', event.currentTarget.value)}
        required
        data-autofocus
      />

      <TextInput
        label={t('units.name')}
        value={draft.name}
        onChange={(event) => set('name', event.currentTarget.value)}
        required
      />

      {/* ⛔ `TextInput`, а не `NumberInput`: той проганяє введене через
          IEEE-754 і губить хвіст множника. Причина блокування — `description`,
          тобто видима й прив'язана до поля через `aria-describedby`. */}
      <TextInput
        label={t('units.factor')}
        inputMode="decimal"
        value={factor}
        onChange={(event) => set('factor', event.currentTarget.value)}
        disabled={lock !== null}
        description={lock ?? t('units.factorHint')}
        error={factorRejected ? t('units.factorMustBePositive') : undefined}
        data-field="factor"
      />

      <TextInput
        label={t('units.offset')}
        inputMode="decimal"
        value={offset}
        onChange={(event) => set('offset', event.currentTarget.value)}
        disabled={lock !== null}
        description={lock ?? t('units.offsetHint')}
        data-field="offset"
      />

      {inUse !== null && (
        <Text size="sm" c="statusError" data-unit-factor-in-use="">
          {t('err.ECR-UOM-0409.unitFactorInUse', { code: unit.code, total: inUse })}
        </Text>
      )}

      {fresh !== null && (
        <Stack gap="xs" data-unit-changed="">
          <Text size="sm" c="statusError">
            {t('err.ECR-UOM-0409.unitChanged', { code: unit.code })}
          </Text>
          <Group gap="xs">
            <Button size="xs" variant="default" onClick={() => takeFresh(fresh)} data-take-fresh="">
              {t('units.reloadCurrent')}
            </Button>
          </Group>
        </Stack>
      )}

      {general !== null && <ErrorAlert error={general} />}

      <Group justify="flex-end" gap="xs">
        <Button variant="default" onClick={onDone}>
          {t('common.cancel')}
        </Button>
        <Button
          loading={save.isPending}
          // ⛔ Поверх нерозв'язаного конфлікту не зберігаємо: та сама версія
          // дала б той самий `409`.
          disabled={incomplete || fresh !== null}
          onClick={() => save.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Stack>
  );
}
