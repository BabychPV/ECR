import { useState, type JSX } from 'react';
import { Badge, Button, Tabs } from '@mantine/core';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { localized } from '@/shared/i18n/localized';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { TestDataSourceModal } from './TestDataSourceModal';
import type { DataSource } from './dataSourceApi';

/**
 * Назва з'єднання мовою користувача; немає жодної — код.
 *
 * ⚠ `nameL10n` у `DataSourceView` — ПЛОСКА мапа мов, а не `{ values }`, як
 * у решті `…L10n`; тому обгортка тут, а не зміна `localized`.
 */
export function dataSourceName(source: DataSource): string {
  const name = localized({ values: source.nameL10n });

  return name.length > 0 ? name : source.code;
}

/**
 * Пари «підпис → значення» вкладки Connection.
 *
 * ⛔ Поля секрету немає й не буде: сервер його не віддає, а сховище секретів
 * прибрано рішенням людини (Q15-06, службовий обліковий запис). `hasSecret`
 * — лише ознака, і лише коли `true`: `false` — це норма для службового
 * запису, а не «бракує налаштування», і рядок «секрету немає» читався б як
 * попередження, якого немає (`D15-06`).
 *
 * ⚠ `null` у `secondaryEndpoint`/`catalog` рядка не дає: `KeyValue` сам не
 * малює пару без значення.
 */
export function connectionItems(source: DataSource): KeyValueItem[] {
  return [
    { label: t('sources.transport'), value: source.transport },
    { label: t('sources.endpoint'), value: source.endpoint, mono: true },
    { label: t('sources.secondaryEndpoint'), value: source.secondaryEndpoint, mono: true },
    { label: t('sources.catalog'), value: source.catalog, mono: true },
    { label: t('sources.maxParallel'), value: formatNumber(source.maxParallel) },
    { label: t('sources.entities'), value: formatNumber(source.sourceEntities) },
    { label: t('sources.schedules'), value: formatNumber(source.collectionSchedules) },
    { label: t('sources.hasSecret'), value: source.hasSecret ? t('sources.hasSecretYes') : null },
  ];
}

/**
 * Шухляда з'єднання (директива №15 §3 `UI-09`, шар 2).
 *
 * ⚠ Вкладка поки одна — Connection. Розклад збору прийде наступним кроком
 * окремим компонентом; вкладки заведені вже зараз, щоб він додав рядок, а не
 * переставляв розмітку шухляди.
 *
 * ⛔ Багатокрокової «Check configuration» із макета тут немає: сервер цих
 * кроків не віддає, а елемент без даних не малюється.
 */
export function DataSourceDrawer({
  source,
  canTest,
}: {
  readonly source: DataSource;

  /** `Integration.Manage`: без нього кнопки проби НЕМАЄ, а не вимкнена. */
  readonly canTest: boolean;
}): JSX.Element {
  const [testing, setTesting] = useState(false);

  return (
    <>
      <DetailDrawer
        panelId={source.code}
        title={dataSourceName(source)}
        subtitle={source.code}
        badge={
          source.isActive ? undefined : (
            <Badge variant="outline" color="gray">
              {t('sources.inactive')}
            </Badge>
          )
        }
        closeLabel={t('sources.closeDetails')}
        footer={
          canTest ? (
            <Button onClick={() => setTesting(true)} data-test-connection="">
              {t('sources.testConnection')}
            </Button>
          ) : undefined
        }
      >
        <Tabs defaultValue="connection" keepMounted={false}>
          <Tabs.List>
            <Tabs.Tab value="connection">{t('sources.connection')}</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="connection" pt="sm">
            <KeyValue items={connectionItems(source)} />
          </Tabs.Panel>
        </Tabs>
      </DetailDrawer>

      {canTest && (
        <TestDataSourceModal
          opened={testing}
          sourceId={source.id}
          sourceName={dataSourceName(source)}
          onClose={() => setTesting(false)}
        />
      )}
    </>
  );
}
