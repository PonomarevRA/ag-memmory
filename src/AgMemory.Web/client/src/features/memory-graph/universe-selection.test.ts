import { afterEach, expect, it, vi } from 'vitest';
import type { GraphSnapshot } from './sanitize.js';

const { scenes } = vi.hoisted(() => ({ scenes: [] as Array<{ children: Array<{ userData: { nodeId?: string }; children: Array<{ material: { color: { getHexString(): string } } }> }> }> }));
vi.mock('@vendor/three/three.module.js', async importOriginal => {
  const actual = await importOriginal<Record<string, unknown>>();
  return { ...actual, WebGLRenderer: class {
    setPixelRatio() {} setClearColor() {} setSize() {} dispose() {}
    render(scene: typeof scenes[number]) { scenes.push(scene); }
  } };
});
import { createMemoryUniverse } from '@vendor/memory-graph-universe.js';

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); scenes.length = 0; document.body.replaceChildren(); });

it('mirrors changed 3D selection and reset to the shared inspector without mutating readonly nodes', () => {
  vi.stubGlobal('WebGL2RenderingContext', class {});
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: true }));
  const canvas = document.createElement('canvas');
  Object.defineProperties(canvas, { clientWidth: { value: 1000 }, clientHeight: { value: 600 } });
  const summary = document.createElement('div');
  const selected = vi.fn();
  const renderer = createMemoryUniverse(canvas, summary, vi.fn(), selected);
  const node = Object.freeze({ id: 'local-node', href: '/memory-reader/local', type: 'Fact', label: 'Факт №1',
    degree: 0, importanceBand: 3, confidenceBand: 4, color: '#12a594', layoutKey: 'local-node' });
  renderer.render(Object.freeze({ status: 'available', nodes: Object.freeze([node]), edges: Object.freeze([]), nextToken: null }) as unknown as GraphSnapshot);
  const group = scenes.at(-1)?.children.find(object => object.userData.nodeId === node.id);
  expect(group?.children[0].material.color.getHexString()).toBe('12a594');
  renderer.selectNode(node.id); renderer.selectNode(node.id);
  expect(selected).toHaveBeenCalledTimes(1);
  expect(selected.mock.lastCall?.[0]).toEqual(node);
  expect(selected.mock.lastCall?.[0]).not.toBe(node);
  renderer.selectNode(undefined);
  expect(selected.mock.lastCall?.[0]).toBeUndefined();
  renderer.selectNode(node.id); renderer.reset();
  expect(selected.mock.lastCall?.[0]).toBeUndefined();
  expect(node.label).toBe('Факт №1');
  renderer.dispose(); renderer.dispose();
});
