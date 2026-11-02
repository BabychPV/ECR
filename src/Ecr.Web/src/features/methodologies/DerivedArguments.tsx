import type { JSX } from 'react';
import { Badge, Group, Stack, Table, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { MethodologyFormulaArgumentDto } from './draft';

/** Підпис виду аргументу — літералами ключів (сторож ключів через змінну). */
export function argumentKindLabel(kind: MethodologyFormulaArgumentDto['kind']): string {
  switch (kind) {
    case 'Constant':
      return t('methodologies.argumentKind.constant');
    case 'Formula':
      return t('methodologies.argumentKind.formula');
    default:
      return t('methodologies.argumentKind.input');
  }
}

/**
 * Похідний список аргументів формули — лише читання (✎ 2026-11-02).
 *
 * ⛔ Поля «Declared arguments» більше немає: ручний список розходився з виразом. Список виводить сервер
 * із виразу при кожному записі; необов'язковий аргумент позначається у виразі `@Name?`. Тут видно рівно
 * те, що зберіг сервер, — тож для нової або щойно зміненої формули список оновиться після збереження.
 */
export function DerivedArguments({
  items,
}: {
  readonly items: readonly MethodologyFormulaArgumentDto[];
}): JSX.Element {
  return (
    <Stack gap="xs" data-testid="derived-arguments">
      <Text size="sm" fw={500} id="derived-arguments-label">
        {t('methodologies.derivedArguments')}
      </Text>
      <Text size="xs" c="dimmed">
        {t('methodologies.derivedArgumentsHint')}
      </Text>
      {items.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('methodologies.derivedArgumentsEmpty')}
        </Text>
      ) : (
        <Table withTableBorder aria-labelledby="derived-arguments-label">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('methodologies.derivedArgumentName')}</Table.Th>
              <Table.Th>{t('methodologies.derivedArgumentKind')}</Table.Th>
              <Table.Th>{t('methodologies.derivedArgumentText')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {items.map((argument) => (
              <Table.Tr key={argument.text} data-argument={argument.text}>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    <Text size="sm">{argument.name}</Text>
                    {argument.isOptional && (
                      <Badge
                        size="xs"
                        variant="outline"
                        color="gray"
                        title={t('methodologies.derivedArgumentOptionalHint')}
                      >
                        {`? ${t('methodologies.derivedArgumentOptional')}`}
                      </Badge>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td>{argumentKindLabel(argument.kind)}</Table.Td>
                <Table.Td>
                  <Text size="sm" ff="monospace">
                    {argument.text}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  );
}
