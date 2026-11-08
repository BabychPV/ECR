import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { Table } from '@mantine/core';
import { DocumentVersionCell, DocumentVersionTh } from '@/features/documents/DocumentVersionColumn';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Колонка «Version» переліку документів в обох темах: заголовок з `aria-sort`, типова версія і «—» - без блокуючих
 * порушень axe. (✎ 08.11: помісячної версії з підказкою «за період» у переліку року більше немає.)
 */
describe('DocumentVersionColumn — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: заголовок, типова версія й «—» без порушень`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <DocumentVersionTh sort="asc" onSort={() => {}} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              <Table.Tr>
                <Table.Td>
                  <DocumentVersionCell source={{ templateVersion: '1.0' }} />
                </Table.Td>
              </Table.Tr>
              <Table.Tr>
                <Table.Td>
                  <DocumentVersionCell source={{ templateVersion: null }} />
                </Table.Td>
              </Table.Tr>
            </Table.Tbody>
          </Table>
        </Shell>,
      );

      expect(container.querySelector('[data-column="version"]')?.getAttribute('aria-sort')).toBe('ascending');
      expect(container.querySelector('[data-document-version="1.0"]')).not.toBeNull();
      expect(container.querySelector('[data-document-version="none"]')).not.toBeNull();

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
