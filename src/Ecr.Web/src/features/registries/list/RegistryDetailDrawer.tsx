import type { JSX } from 'react';
import { Badge, Button, Stack, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import type { RegistryDefDto } from '@/api/types';
import { RegistryUsagePanel } from '@/features/registries/RegistryUsage';
import { t } from '@/shared/i18n';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { KeyValue } from '@/shared/ui/KeyValue';
import { isSynced, sourceKindLabel, registryName } from './registryList';

interface RegistryDetailDrawerProps {
  readonly registry: RegistryDefDto;

  /** «Де використано» — лише з `Registry.EditDefinition` (право `GET …/usage`, `BE-24`). */
  readonly canSeeUsage: boolean;

  /** Повернути фокус на рядок, з якого шторку відкрили. */
  readonly onClose: () => void;
}

/**
 * Шторка довідника (`UI-35`; макет `screens-data.js`, `drawer` переліку `/admin/registries`).
 *
 * ⚠ Окремий модуль навмисно: його вантажить `RegistriesList` лінивим чанком, тож
 * `Drawer` і «де використано» не потрапляють у чанк маршруту, доки шторку не
 * відкрили.
 *
 * ⛔ З макетних рядків лишилися ті, що мають дані: «Fields», ознаки, master
 * (D-211) і «Used in» (`GET {code}/usage`). «Entries», «Definition vN» і
 * «Updated by» — поля, яких у `RegistryDefDto` немає (`D15-06`).
 */
export function RegistryDetailDrawer({
  registry,
  canSeeUsage,
  onClose,
}: RegistryDetailDrawerProps): JSX.Element {
  const code = encodeURIComponent(registry.code);

  const traits = [
    registry.isTemporal ? t('registries.temporal') : null,
    registry.isHierarchical ? t('registries.hierarchical') : null,
  ].filter((trait): trait is string => trait !== null);

  return (
    <DetailDrawer
      panelId={registry.code}
      title={registryName(registry)}
      subtitle={registry.code}
      badge={
        isSynced(registry) ? (
          <Badge variant="light" size="sm" tt="none">
            {sourceKindLabel(registry.sourceKind)}
          </Badge>
        ) : undefined
      }
      closeLabel={t('common.close')}
      onClose={onClose}
      footer={
        <>
          <Button component={Link} to={`/admin/registries/${code}/definition`} variant="default">
            {t('registries.constructor')}
          </Button>
          <Button component={Link} to={`/admin/registries?code=${code}`} variant="default">
            {t('registries.list.manage')}
          </Button>
          <Button component={Link} to={`/admin/registries/${code}/entries`}>
            {t('registries.data.open')}
          </Button>
        </>
      }
    >
      <KeyValue
        items={[
          {
            label: t('registries.fields'),
            value: String(registry.fields.length),
            hint:
              registry.fields.length === 0
                ? undefined
                : registry.fields.map((field) => field.code).join(', '),
          },
          {
            label: t('registries.list.traits'),
            value: traits.length === 0 ? null : traits.join(' · '),
          },
          {
            label: t('registries.list.source'),
            value: sourceKindLabel(registry.sourceKind),
            // D-211: правити записи не можна лише в External; Hybrid правиться й тут.
            hint: registry.sourceKind === 'External' ? t('registries.externalReadOnly') : undefined,
          },
        ]}
      />

      {canSeeUsage && (
        <Stack gap="xs" data-registry-drawer-usage="">
          <Title order={4} size="h6">
            {t('registries.list.usedIn')}
          </Title>
          <RegistryUsagePanel code={registry.code} />
        </Stack>
      )}
    </DetailDrawer>
  );
}
