import { useState, type JSX } from 'react';
import { Alert, Button, Checkbox, Group, Modal, SegmentedControl, Stack, Text, TextInput } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { meetsGrant, type GrantLevelName } from '@/features/workflow/SheetActions';
import { t } from '@/shared/i18n';
import { can, type MeDto } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import {
  setRegistrySyncPolicy,
  type RegistryMissingPolicy,
  type RegistrySyncPolicy,
  type SourceEntityWithPolicy,
} from './registrySyncPolicyApi';

/**
 * Три відповіді синку на «елемент зник у джерелі» — від м'якої до жорсткої.
 * ⚠ Ключі літералами, не шаблоном: сторож каталогу бачить лише літерал.
 */
const MissingPolicies: readonly { value: RegistryMissingPolicy; label: () => string; hint: () => string }[] = [
  {
    value: 'MarkOrphaned',
    label: () => t('sources.syncPolicyMarkOrphaned'),
    hint: () => t('sources.syncPolicyMarkOrphanedHint'),
  },
  { value: 'Ignore', label: () => t('sources.syncPolicyIgnore'), hint: () => t('sources.syncPolicyIgnoreHint') },
  {
    value: 'Deactivate',
    label: () => t('sources.syncPolicyDeactivate'),
    hint: () => t('sources.syncPolicyDeactivateHint'),
  },
];

/** Політика, якою сервер заводить сутність (`SourceEntity`: типове `MarkOrphaned`, дат немає). */
export const DefaultRegistrySyncPolicy: RegistrySyncPolicy = {
  onMissingInSource: 'MarkOrphaned',
  validFromAttribute: null,
  validToAttribute: null,
  validToInclusive: false,
};

/**
 * Чи може користувач змінити політику синку довідника `registryDefId`.
 *
 * ⛔ Дзеркало `SetSourceEntityRegistryPolicyHandler`: `Integration.Manage` І
 * (`Registry.EditData` АБО грант `Write`+ на `Registry:{id}` — `RegistryAccess`).
 * Політика `Deactivate` вимикає записи довідника від імені синку — це зміна
 * його даних, одного права на інтеграцію мало. Заборона виграє (ФВ-6.6).
 */
export function canEditRegistrySyncPolicy(me: MeDto | undefined, registryDefId: number): boolean {
  if (me === undefined || !can(me, 'Integration.Manage')) return false;
  if (can(me, 'Registry.EditData')) return true;

  const key = `Registry:${String(registryDefId)}`;
  if ((me.denies ?? []).includes(key)) return false;

  const level = (me.grants ?? {})[key] as GrantLevelName | undefined;
  return level !== undefined && meetsGrant(level, 'Write');
}

/** Порожнє поле атрибута — «не синхронізувати» (`null`), а не порожнє ім'я. */
function attribute(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Політика синку довідника з AF для сутності збору (`D-212`,
 * `PUT /api/v1/sources/{id}/registry/policy`).
 *
 * ⚠ `current = null` — клієнт не знає чинної політики: перелік сутностей
 * (`GET /api/v1/sources`) її не віддає, а окремого читання немає. Форма тоді
 * стартує з типової і прямо про це попереджає: `PUT` — повна заміна, тож
 * мовчки показана типова затерла б чинні атрибути дат.
 *
 * ⚠ Чернетка живе стільки, скільки змонтована форма: викликач монтує її на
 * відкриття (з `key` сутності), тож кожне відкриття — з чинної політики.
 */
export function RegistrySyncPolicyModal({
  entityId,
  entityLabel,
  current,
  opened,
  onClose,
  onSaved,
}: {
  readonly entityId: number;
  readonly entityLabel: string;
  readonly current: RegistrySyncPolicy | null;
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly onSaved: (entity: SourceEntityWithPolicy) => void;
}): JSX.Element {
  const start = current ?? DefaultRegistrySyncPolicy;

  const [missing, setMissing] = useState<RegistryMissingPolicy>(start.onMissingInSource);
  const [validFrom, setValidFrom] = useState(start.validFromAttribute ?? '');
  const [validTo, setValidTo] = useState(start.validToAttribute ?? '');
  const [inclusive, setInclusive] = useState(start.validToInclusive);

  const save = useMutation({
    mutationFn: (policy: RegistrySyncPolicy) => setRegistrySyncPolicy(entityId, policy),
    onSuccess: (entity) => {
      showDone(t('sources.syncPolicySaved'));
      onSaved(entity);
      onClose();
    },
  });

  // Межа «до включно» без атрибута кінця не означає нічого — сервер відмовляє (422).
  const hasValidTo = validTo.trim().length > 0;

  return (
    <Modal opened={opened} onClose={onClose} title={t('sources.syncPolicyTitle', { entity: entityLabel })} size="lg">
      <Stack gap="sm" data-sync-policy={entityId}>
        <Text size="sm" c="dimmed">
          {t('sources.syncPolicyHint')}
        </Text>

        {current === null && (
          <Alert color="statusWarning" data-sync-policy-unknown="">
            {t('sources.syncPolicyUnknown')}
          </Alert>
        )}

        {/* ⚠ SegmentedControl, не Radio: стилі Radio відсічені (`mantineCssPrune.ts`),
            а повернути їх — +1 КБ у вхідний CSS і DocumentPage за межею D-132 (251/250). */}
        <Stack gap="xs">
          <Text size="sm" fw={500} id={`sync-policy-missing-${String(entityId)}`}>
            {t('sources.syncPolicyMissing')}
          </Text>
          <SegmentedControl
            aria-labelledby={`sync-policy-missing-${String(entityId)}`}
            value={missing}
            onChange={(value) => setMissing(value as RegistryMissingPolicy)}
            data={MissingPolicies.map((policy) => ({ value: policy.value, label: policy.label() }))}
            data-sync-policy-missing=""
          />
          <Text size="xs" c="dimmed" data-sync-policy-option={missing}>
            {MissingPolicies.find((policy) => policy.value === missing)?.hint()}
          </Text>
        </Stack>

        <Text size="xs" c="dimmed">
          {t('sources.syncPolicyAttributeHint')}
        </Text>
        <Group grow align="flex-start">
          <TextInput
            label={t('sources.syncPolicyValidFrom')}
            value={validFrom}
            onChange={(event) => setValidFrom(event.currentTarget.value)}
            data-sync-policy-valid-from=""
          />
          <TextInput
            label={t('sources.syncPolicyValidTo')}
            value={validTo}
            onChange={(event) => setValidTo(event.currentTarget.value)}
            data-sync-policy-valid-to=""
          />
        </Group>
        <Checkbox
          label={t('sources.syncPolicyValidToInclusive')}
          checked={hasValidTo && inclusive}
          disabled={!hasValidTo}
          onChange={(event) => setInclusive(event.currentTarget.checked)}
          data-sync-policy-inclusive=""
        />

        {save.error !== null && <ErrorAlert error={save.error} />}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            loading={save.isPending}
            onClick={() =>
              save.mutate({
                onMissingInSource: missing,
                validFromAttribute: attribute(validFrom),
                validToAttribute: attribute(validTo),
                validToInclusive: hasValidTo && inclusive,
              })
            }
            data-sync-policy-save=""
          >
            {t('common.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
