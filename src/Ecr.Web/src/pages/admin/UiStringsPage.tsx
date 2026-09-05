import { useState, type JSX } from 'react';
import { Badge, Button, Group, Select, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SetUiStringRequest, UiStringCatalog, UiStringRevisionResponse } from '@/api/types';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
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
 */
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
  const keys = Object.keys(original)
    .filter((key) => key.toLowerCase().includes(filter.toLowerCase()))
    .sort((a, b) => a.localeCompare(b));

  const isDefault = lang === DefaultLanguage;

  return (
    <>
      <PageHeader
        title={t('uiStrings.title')}
        actions={
          <Group gap="xs" align="end">
            <Select
              size="xs"
              miw={160}
              label={t('uiStrings.language')}
              data={(languages.data ?? []).map((language) => ({
                value: language.code,
                label: language.nameNative,
              }))}
              value={lang}
              onChange={(value) => setLang(value)}
              allowDeselect={false}
            />

            <TextInput
              size="xs"
              miw={220}
              label={t('uiStrings.filter')}
              value={filter}
              onChange={(event) => setFilter(event.currentTarget.value)}
            />
          </Group>
        }
      />

      <AsyncBoundary<UiStringCatalog>
        isPending={catalog.isPending || reference.isPending}
        error={catalog.error ?? reference.error}
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
              {keys.map((key) => {
                const value = strings[key] ?? '';
                const source = original[key] ?? '';

                // ⛔ Ознака «немає перекладу»: значення дослівно збігається з
                // оригіналом. Сервер підміняє відсутній переклад мовою за
                // замовчуванням, тому в каталозі порожнеча не видно ніколи —
                // і саме тому неперекладений інтерфейс виглядає перекладеним.
                const untranslated = !isDefault && value === source;

                return (
                  <Table.Tr key={key}>
                    <Table.Td>
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
                            <Badge size="xs" color="orange" variant="light">
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
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </>
  );
}
