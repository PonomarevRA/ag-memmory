import type { TreeNode } from './api.js';
import { ui } from '../../ui-kit/index.js';
import type { StreamStatus } from './view-state.js';

export type TreeView = { tree: TreeNode[]; treeToken: string | null; treeStatus: StreamStatus; collapsed: Set<string>; railOpen: boolean };
type Branch = { node: TreeNode; key: string; children: Branch[]; root: boolean };

/** Flattens recursive and paged payloads once in hierarchy order without recursive stack growth. */
export function flattenTree(nodes: TreeNode[]): TreeNode[] {
  const result: TreeNode[] = [];
  const pending = [...nodes].reverse();
  while (pending.length) {
    const node = pending.pop()!;
    result.push(node);
    for (let i = node.children.length - 1; i >= 0; i--) pending.push(node.children[i]);
  }
  return result;
}

/** Builds native disclosure branches. Missing paged parents remain visible until their context is available. */
function branches(nodes: TreeNode[]): Branch[] {
  const roots: Branch[] = [];
  const known = new Map<string, Branch>();
  const ancestors: Branch[] = [];
  for (const [index, node] of flattenTree(nodes).entries()) {
    const key = node.nodeKey || `row-${index}`;
    const branch: Branch = { node, key, children: [], root: true };
    while (ancestors.length && ancestors[ancestors.length - 1].node.depth >= node.depth) ancestors.pop();
    const parent = node.parentKey ? known.get(node.parentKey) : ancestors[ancestors.length - 1];
    if (parent && parent.node.depth < node.depth) { branch.root = false; parent.children.push(branch); }
    else roots.push(branch);
    known.set(key, branch);
    ancestors.push(branch);
  }
  return roots;
}

/** Expands only the active document's ancestors, retaining all other user collapse choices. */
export function revealCurrent(root: HTMLElement, state: TreeView): void {
  const active = root.querySelector<HTMLElement>('.memory-reader-tree__link.is-active');
  if (!active) return;
  let parent = active.parentElement;
  while (parent && parent !== root) {
    if (parent instanceof HTMLDetailsElement && parent.dataset.readerBranch) {
      parent.open = true;
      state.collapsed.delete(parent.dataset.readerBranch);
    }
    parent = parent.parentElement;
  }
  active.scrollIntoView?.({ block: 'nearest' });
}

/** Renders nested lists and native details instead of an ARIA tree with incomplete keyboard handling. */
export function treeMarkup(state: TreeView, activeRouteKey: string, href: (path: string) => string, status: string): string {
  const e = ui.escape;
  const activeHref = activeRouteKey ? `/memory-reader/${encodeURIComponent(activeRouteKey)}` : '';
  const rows: string[] = [];
  const pending: Array<Branch | string> = [...branches(state.tree)].reverse();
  while (pending.length) {
    const value = pending.pop()!;
    if (typeof value === 'string') { rows.push(value); continue; }
    const { node, key, children } = value;
    const active = node.href?.split('?')[0] === activeHref;
    const indent = value.root ? Math.min(node.depth, 8) * .75 : 0;
    const label = node.href
      ? `<a class="memory-reader-tree__link${active ? ' is-active' : ''}" href="${e(href(node.href))}"${active ? ' aria-current="page"' : ''}>${e(node.label)}${node.linkWeight !== null ? ` (${node.linkWeight})` : ''}</a>`
      : `<span class="memory-reader-tree__folder">${e(node.label)}</span>`;
    rows.push(`<li data-parent-key="${e(node.parentKey || '')}" data-depth="${node.depth}" style="margin-inline-start:${indent}rem">`);
    if (children.length || !node.href) {
      rows.push(`<details class="memory-reader-tree__branch" data-reader-branch="${e(key)}"${state.collapsed.has(key) ? '' : ' open'}><summary>${label}</summary><ul class="memory-reader-tree__children">`);
      pending.push('</ul></details></li>');
      for (let i = children.length - 1; i >= 0; i--) pending.push(children[i]);
    } else rows.push(`${label}</li>`);
  }
  const refresh = state.treeStatus === 'changed';
  const retry = refresh || state.treeStatus === 'unavailable';
  const more = retry || state.treeToken ? ui.button(refresh ? 'Обновить дерево' : retry ? 'Повторить загрузку дерева' : 'Загрузить ещё дерево', { id: 'reader-more-tree', secondary: true, pending: state.treeStatus === 'loading' }) : '';
  return `<aside class="memory-reader-tree"><details id="reader-tree-panel"${state.railOpen ? ' open' : ''}><summary class="memory-reader-tree__header">Дерево страниц</summary><button type="button" id="reader-reveal-current" class="ui-button ui-button--secondary">Показать текущую страницу</button><nav class="memory-reader-tree__nav" aria-label="Дерево страниц"><ul>${rows.join('')}</ul></nav><div id="reader-tree-status" role="status">${status}</div><div id="reader-tree-sentinel"></div>${more}</details></aside>`;
}
