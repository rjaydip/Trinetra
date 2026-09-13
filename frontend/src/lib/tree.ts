/** Shared by the admin hierarchy page and the camera registry's organization-unit tree filter —
 * builds a parent/child tree from a flat list keyed by id + parent-id. */
export interface TreeNode<T> {
  item: T;
  children: TreeNode<T>[];
  depth: number;
}

export function buildTree<T extends { id: string }>(
  items: T[],
  getParentId: (item: T) => string | null | undefined,
): TreeNode<T>[] {
  const itemMap = new Map<string, T>();
  items.forEach((item) => itemMap.set(item.id, item));

  const childrenMap = new Map<string, T[]>();
  const roots: T[] = [];

  items.forEach((item) => {
    const parentId = getParentId(item);
    if (parentId && itemMap.has(parentId)) {
      const list = childrenMap.get(parentId) ?? [];
      list.push(item);
      childrenMap.set(parentId, list);
    } else {
      roots.push(item);
    }
  });

  function makeNodes(list: T[], depth: number): TreeNode<T>[] {
    return list.map((item) => ({
      item,
      depth,
      children: makeNodes(childrenMap.get(item.id) ?? [], depth + 1),
    }));
  }

  return makeNodes(roots, 0);
}
