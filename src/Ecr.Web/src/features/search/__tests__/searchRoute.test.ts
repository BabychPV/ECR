import { describe, expect, it } from 'vitest';
import { matchPath } from 'react-router-dom';
import { routes } from '@/app/routes';
import { searchHitRoute } from '@/features/search/searchRoute';

/** Маршрут збігу пошуку даних будує клієнт (BE-19): сервер віддає лише kind/id/code. */
describe('маршрут збігу палітри', () => {
  it('документ веде на сторінку документа', () => {
    const route = searchHitRoute({ kind: 'document', id: 42, code: 'DOC-42', title: 'Permit' });

    expect(route).toBe('/documents/42');
    expect(matchPath(routes.documentDetail.path, route ?? '')?.params.id).toBe('42');
  });

  it('шаблон веде на КАРТКУ шаблону, а не на перелік', () => {
    const route = searchHitRoute({ kind: 'template', id: 7, code: 'T-7', title: 'Emissions' });

    expect(route).toBe('/admin/templates/7');
    expect(matchPath(routes.adminTemplateSection.path, route ?? '')?.params.id).toBe('7');
  });

  it('довідник веде в конструктор за кодом, код кодується', () => {
    const route = searchHitRoute({ kind: 'registry', id: 3, code: 'FUEL/A B', title: 'Fuel' });

    expect(route).toBe('/admin/registries/FUEL%2FA%20B/definition');
    const code = matchPath(routes.adminRegistryDefinition.path, route ?? '')?.params.code;
    expect(decodeURIComponent(code ?? '')).toBe('FUEL/A B');
  });

  it('невідомий вид збігу нікуди не веде', () => {
    expect(searchHitRoute({ kind: 'unit', id: 1, code: 'kg', title: 'kg' })).toBeNull();
  });
});
