import type { JSX } from 'react';
import { Badge, Button, Stack } from '@mantine/core';
import { Link } from 'react-router-dom';
import { RegistryUsagePanel } from '@/features/registries/RegistryUsage';
import { t } from '@/shared/i18n';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { Banner } from '@/shared/ui/Banner';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Timestamp } from '@/shared/ui/Timestamp';
import { isSynced, registryName, sourceKindLabel, type RegistryListItem } from './registryList';

interface RegistryDetailDrawerProps {
  readonly registry: RegistryListItem;

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
 * ⛔ Рядки макета — лише ті, що мають дані: Entries, Fields, Used in (колонки й
 * шаблони + перелік `GET {code}/usage`), Definition vN, дата зміни записів,
 * ознаки, master (D-211). «Updated by» і «cells» сервер не віддає (`D15-06`).
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

  // «N колонок у M шаблонах»; нуль — «ніде» (дані), `null` — рядка немає (без права).
  const usedIn =
    registry.usedInColumns === null || registry.usedInColumns === undefined
      ? null
      : registry.usedInColumns === 0
        ? t('registries.usageNone')
        : t('registries.list.usedInValue', {
            columns: registry.usedInColumns,
            templates: registry.usedInTemplates ?? 0,
          });

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
            label: t('registries.list.entries'),
            // ⚠ `null`/відсутнє поле — рядка немає (`KeyValue` не малює порожнє значення).
            value: registry.entryCount === null || registry.entryCount === undefined ? null : String(registry.entryCount),
            hint: t('registries.list.entriesHint'),
          },
          {
            label: t('registries.fields'),
            value: String(registry.fields.length),
            hint:
              registry.fields.length === 0
                ? undefined
                : registry.fields.map((field) => field.code).join(', '),
          },
          {
            label: t('registries.list.usedIn'),
            value: usedIn,
          },
          {
            label: t('registries.list.definition'),
            value:
              registry.definitionVersion === null || registry.definitionVersion === undefined
                ? null
                : registry.hasDraft === true
                  ? t('registries.list.definitionDraft', { version: registry.definitionVersion })
                  : t('registries.list.definitionValue', { version: registry.definitionVersion }),
          },
          {
            label: t('registries.list.updated'),
            value: registry.dataChangedAt === null || registry.dataChangedAt === undefined ? null : (
              <Timestamp value={registry.dataChangedAt} />
            ),
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

      {registry.hasDraft === true && (
        <Banner
          tone="info"
          title={t('registries.list.draftTitle')}
          text={t('registries.list.draftText')}
        />
      )}

      {canSeeUsage && (
        // ⚠ Без власного заголовка: підпис «Used in» уже стоїть рядком вище в `KeyValue`.
        <Stack gap="xs" data-registry-drawer-usage="">
          <RegistryUsagePanel code={registry.code} />
        </Stack>
      )}
    </DetailDrawer>
  );
}
