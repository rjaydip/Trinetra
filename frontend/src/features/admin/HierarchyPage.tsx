import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';

import { errorDetail, isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type {
  DeactivateRequest,
  GeographicAreaRequest,
  GeographicAreaResponse,
  OrganizationRequest,
  OrganizationResponse,
  OrganizationUnitRequest,
  OrganizationUnitResponse,
} from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { Button, PageState, StatusBadge } from '../../components/ui';
import { buildTree, type TreeNode } from '../../lib/tree';
import './admin.css';

function value(form: FormData, name: string) {
  return String(form.get(name) ?? '').trim();
}

function optional(valueToCheck: string) {
  return valueToCheck || undefined;
}

interface ParentOption { id: string; name: string }

/** Every id reachable by walking down from `rootId` — the node's whole subtree, never itself. */
function descendantIds<T extends { id: string }>(
  rootId: string,
  items: T[],
  getParentId: (item: T) => string | null | undefined,
): Set<string> {
  const childrenByParent = new Map<string, string[]>();
  items.forEach((item) => {
    const parentId = getParentId(item);
    if (!parentId) return;
    const list = childrenByParent.get(parentId) ?? [];
    list.push(item.id);
    childrenByParent.set(parentId, list);
  });
  const result = new Set<string>();
  const stack = [...(childrenByParent.get(rootId) ?? [])];
  while (stack.length > 0) {
    const id = stack.pop()!;
    if (result.has(id)) continue;
    result.add(id);
    stack.push(...(childrenByParent.get(id) ?? []));
  }
  return result;
}

/**
 * Candidates for a "new parent" picker: `ACTIVE` only (every re-parent operation — edit, move,
 * deactivate-with-reparent — refuses an inactive parent server-side), excluding the node itself
 * and its own subtree (re-parenting under a descendant is refused too — it would create a cycle).
 * Pass `excludeId: undefined` for a picker with no "self" yet (e.g. creating a new node).
 */
function validParentOptions<T extends { id: string; status: string }>(
  items: T[],
  excludeId: string | undefined,
  getParentId: (item: T) => string | null | undefined,
): T[] {
  const excluded = excludeId ? descendantIds(excludeId, items, getParentId) : new Set<string>();
  return items.filter((item) => item.status === 'ACTIVE' && item.id !== excludeId && !excluded.has(item.id));
}

/**
 * `validParentOptions` filtered for a `<select defaultValue>` picker, plus the node's *current*
 * parent even when that parent no longer qualifies (inactive — possible via the still-open 5-M8
 * gap, or a status change since this list loaded). Dropping it would desync `defaultValue` from
 * every rendered `<option>`; the browser falls back to whatever option happens to render first,
 * so submitting the form *unchanged* would silently re-parent the unit. Labelled distinctly so
 * the admin knows to pick a real destination rather than resubmit it as-is.
 */
function parentSelectOptions<T extends { id: string; name: string; status: string }>(
  items: T[],
  excludeId: string,
  currentParentId: string | null | undefined,
  getParentId: (item: T) => string | null | undefined,
): Array<{ id: string; label: string }> {
  const valid = validParentOptions(items, excludeId, getParentId);
  const options = valid.map((item) => ({ id: item.id, label: item.name }));
  if (currentParentId && !valid.some((item) => item.id === currentParentId)) {
    const current = items.find((item) => item.id === currentParentId);
    if (current) options.unshift({ id: current.id, label: `${current.name} (inactive — choose a different parent)` });
  }
  return options;
}

/** Levels shown before a node's children collapse behind its own expand toggle — "2 or 3 levels"
 * at a glance (depths 0 and 1 auto-expand, so depths 0/1/2 are visible; depth 2's own children
 * start collapsed). */
const AUTO_EXPAND_DEPTH = 2;

/** Whether a node's children render, given which nodes the admin has manually toggled away from
 * their depth-based default (a single toggle set, XORed against the default, means clicking a
 * node always flips what the admin currently sees rather than needing separate "collapsed below
 * the fold" and "expanded past the fold" tracking). */
function isNodeExpanded(id: string, depth: number, toggled: Set<string>): boolean {
  const defaultExpanded = depth < AUTO_EXPAND_DEPTH;
  return toggled.has(id) ? !defaultExpanded : defaultExpanded;
}

function toggleSetMembership(set: Set<string>, id: string): Set<string> {
  const next = new Set(set);
  if (next.has(id)) next.delete(id); else next.add(id);
  return next;
}

/** Inline "+" form to add a unit directly under a given parent (or at the root) from the tree,
 * without leaving the tree or hunting for the right dropdown in the create-form further down. */
function InlineAddUnit({ organizationId, parentUnitId, onCancel, onCreate, pending, error }: {
  organizationId: string;
  parentUnitId: string | null;
  onCancel(): void;
  onCreate(body: { code: string; name: string; unitType: string; parentUnitId?: string }): void;
  pending: boolean;
  error: unknown;
}) {
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    onCreate({
      code: value(data, 'code'), name: value(data, 'name'), unitType: value(data, 'unitType'),
      parentUnitId: parentUnitId ?? undefined,
    });
  }

  return <form aria-label={parentUnitId ? 'Add child unit' : 'Add root unit'} className="admin-form admin-form--inline tree-inline-add" onSubmit={submit}>
    <label>Code<input name="code" required autoFocus /></label>
    <label>Name<input name="name" required /></label>
    <label>Unit type<input name="unitType" required /></label>
    <input name="organizationId" type="hidden" value={organizationId} />
    {Boolean(error) && <p className="form-error" role="alert">{errorDetail(error, 'The organization unit could not be created.')}</p>}
    <div className="form-actions">
      <Button disabled={pending} type="submit">Add unit</Button>
      <button className="button button--secondary" type="button" onClick={onCancel}>Cancel</button>
    </div>
  </form>;
}

/** Inline "+" form to add a geographic area directly under a given parent (or at the root). */
function InlineAddArea({ parentAreaId, areaTypes, onCancel, onCreate, pending, error }: {
  parentAreaId: string | null;
  areaTypes: Array<{ code: string; name: string }>;
  onCancel(): void;
  onCreate(body: { code: string; name: string; areaType: string; parentAreaId?: string }): void;
  pending: boolean;
  error: unknown;
}) {
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    onCreate({
      code: value(data, 'code'), name: value(data, 'name'), areaType: value(data, 'areaType'),
      parentAreaId: parentAreaId ?? undefined,
    });
  }

  return <form aria-label={parentAreaId ? 'Add child geographic area' : 'Add root geographic area'} className="admin-form admin-form--inline tree-inline-add" onSubmit={submit}>
    <label>Code<input name="code" required autoFocus /></label>
    <label>Name<input name="name" required /></label>
    <label>Area type
      <select name="areaType" required>
        <option value="">Select an area type</option>
        {areaTypes.map((type) => <option key={type.code} value={type.code}>{type.name}</option>)}
      </select>
    </label>
    {Boolean(error) && <p className="form-error" role="alert">{errorDetail(error, 'The geographic area could not be created.')}</p>}
    <div className="form-actions">
      <Button disabled={pending} type="submit">Add area</Button>
      <button className="button button--secondary" type="button" onClick={onCancel}>Cancel</button>
    </div>
  </form>;
}

function buildBreadcrumbs<T extends { id: string; name: string }>(
  targetId: string,
  items: T[],
  getParentId: (item: T) => string | null | undefined,
): T[] {
  const itemMap = new Map<string, T>();
  items.forEach((item) => itemMap.set(item.id, item));
  const trail: T[] = [];
  let curr = itemMap.get(targetId);
  while (curr) {
    trail.unshift(curr);
    const parentId = getParentId(curr);
    curr = parentId ? itemMap.get(parentId) : undefined;
  }
  return trail;
}

function DeactivationControl({
  kind, itemId, itemName, parentLabel, parentOptions, deactivate, onSuccess,
}: {
  kind: 'area' | 'unit';
  itemId: string;
  itemName: string;
  parentLabel: string;
  parentOptions: ParentOption[];
  deactivate(request: DeactivateRequest): Promise<void>;
  onSuccess(): Promise<unknown> | void;
}) {
  const [conflict, setConflict] = useState('');
  const [strategy, setStrategy] = useState<'' | 'cascade' | 'reparent'>('');
  const [newParentId, setNewParentId] = useState('');
  const mutation = useMutation({
    mutationFn: deactivate,
    onSuccess: async () => {
      setConflict('');
      setStrategy('');
      setNewParentId('');
      await onSuccess();
    },
    onError: (error) => {
      if (isApiProblem(error) && error.status === 409) {
        setConflict(error.detail);
        setStrategy('');
        setNewParentId('');
      }
    },
  });
  const confirmDisabled = mutation.isPending || !strategy || (strategy === 'reparent' && !newParentId);

  return <div className="deactivation-control">
    <button className="admin-action-link" disabled={mutation.isPending || Boolean(conflict)} type="button" onClick={() => {
      setConflict('');
      setStrategy('');
      setNewParentId('');
      mutation.mutate({});
    }}>Deactivate {kind} {itemName}</button>
    {mutation.isError && !conflict && <p className="form-error" role="alert">{errorDetail(mutation.error, `The ${kind} could not be deactivated.`)}</p>}
    {conflict && <fieldset className="deactivation-strategy">
      <legend>Children require a decision</legend>
      <p role="alert">{conflict}</p>
      <label className="strategy-option"><input checked={strategy === 'cascade'} name={`${kind}-${itemId}-strategy`} onChange={() => { setStrategy('cascade'); setNewParentId(''); }} type="radio" /> Cascade</label>
      <label className="strategy-option"><input checked={strategy === 'reparent'} name={`${kind}-${itemId}-strategy`} onChange={() => setStrategy('reparent')} type="radio" /> Reparent</label>
      {strategy === 'reparent' && <label className="strategy-parent">{parentLabel}
        <select value={newParentId} onChange={(event) => setNewParentId(event.target.value)} required>
          <option value="">Select a new parent</option>
          {parentOptions.filter((parent) => parent.id !== itemId).map((parent) => <option key={parent.id} value={parent.id}>{parent.name}</option>)}
        </select>
      </label>}
      <Button disabled={confirmDisabled} type="button" onClick={() => mutation.mutate(strategy === 'cascade'
        ? { childStrategy: 'cascade' }
        : { childStrategy: 'reparent', newParentId })}>Confirm deactivation</Button>
    </fieldset>}
  </div>;
}

/**
 * Cross-organization move (v1.12 `POST /organization-units/{id}/move`) — re-parents a unit under
 * a different organization and rewrites the whole subtree's organization to match. Limited to
 * organization.manage held unscoped (403 otherwise, surfaced as the request's own error). A scope
 * conflict (409) lists which access-group scopes would follow the moved units into the new
 * organization; the admin re-confirms explicitly rather than that happening silently.
 */
function MoveUnitControl({
  unit, organizations, onSuccess,
}: {
  unit: OrganizationUnitResponse;
  organizations: OrganizationResponse[];
  onSuccess(): Promise<unknown> | void;
}) {
  const [open, setOpen] = useState(false);
  const [targetOrganizationId, setTargetOrganizationId] = useState('');
  const [newParentUnitId, setNewParentUnitId] = useState('');
  const [conflict, setConflict] = useState('');
  const [confirmScopeImpact, setConfirmScopeImpact] = useState(false);

  const targetUnits = useQuery({
    queryKey: queryKeys.admin.organizationUnits(targetOrganizationId),
    queryFn: () => api.admin.organizations.listUnits(targetOrganizationId),
    enabled: Boolean(targetOrganizationId),
  });

  const mutation = useMutation({
    mutationFn: (confirm: boolean) => api.admin.organizations.moveUnit(unit.id, { newParentUnitId, confirmScopeImpact: confirm }),
    onSuccess: async () => {
      reset();
      await onSuccess();
    },
    onError: (error) => {
      setConflict(isApiProblem(error) && error.status === 409 ? error.detail : '');
    },
  });

  function reset() {
    setOpen(false);
    setTargetOrganizationId('');
    setNewParentUnitId('');
    setConflict('');
    setConfirmScopeImpact(false);
  }

  if (!open) {
    return <button className="admin-action-link" type="button" onClick={() => setOpen(true)}>
      Move {unit.name} to another organization
    </button>;
  }

  const otherOrganizations = organizations.filter((organization) => organization.id !== unit.organizationId);
  const confirmDisabled = mutation.isPending || !newParentUnitId || (Boolean(conflict) && !confirmScopeImpact);

  return <fieldset className="move-unit-control">
    <legend>Move {unit.name} to another organization</legend>
    <label>Destination organization
      <select value={targetOrganizationId} onChange={(event) => {
        setTargetOrganizationId(event.target.value);
        setNewParentUnitId('');
        setConflict('');
      }}>
        <option value="">Select an organization</option>
        {otherOrganizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}
      </select>
    </label>
    {targetOrganizationId && <label>New parent unit
      <select disabled={targetUnits.isPending} value={newParentUnitId} onChange={(event) => {
        setNewParentUnitId(event.target.value);
        setConflict('');
      }}>
        <option value="">{targetUnits.isPending ? 'Loading units…' : 'Select a parent unit'}</option>
        {(targetUnits.data ?? []).filter((candidate) => candidate.status === 'ACTIVE').map((candidate) => <option key={candidate.id} value={candidate.id}>{candidate.name} ({candidate.code})</option>)}
      </select>
    </label>}
    {mutation.isError && !conflict && <p className="form-error" role="alert">{errorDetail(mutation.error, 'The unit could not be moved.')}</p>}
    {conflict && <div className="deactivation-strategy">
      <p role="alert">{conflict}</p>
      <label className="strategy-option">
        <input checked={confirmScopeImpact} onChange={(event) => setConfirmScopeImpact(event.target.checked)} type="checkbox" />
        I understand — move anyway
      </label>
    </div>}
    <div className="form-actions">
      <Button disabled={confirmDisabled} type="button" onClick={() => mutation.mutate(Boolean(conflict) && confirmScopeImpact)}>
        {conflict ? 'Confirm move' : 'Move unit'}
      </Button>
      <button className="button button--secondary" type="button" onClick={reset}>Cancel</button>
    </div>
  </fieldset>;
}

export function HierarchyPage() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const canReadOrganizations = hasPermission(session, 'organization.read');
  const canManageOrganizations = hasPermission(session, 'organization.manage');
  const canReadGeography = hasPermission(session, 'geography.read');
  const canManageGeography = hasPermission(session, 'geography.manage');
  const showOrganizationDomain = canReadOrganizations || canManageOrganizations;
  const showGeographyDomain = canReadGeography || canManageGeography;

  const [activeDomain, setActiveDomain] = useState<'organization' | 'geography'>(
    canReadOrganizations || canManageOrganizations ? 'organization' : 'geography',
  );

  const [organizationId, setOrganizationId] = useState('');
  const [selectedUnitId, setSelectedUnitId] = useState('');
  const [editingOrg, setEditingOrg] = useState(false);
  const [editingUnitId, setEditingUnitId] = useState('');
  const [unitTreeToggled, setUnitTreeToggled] = useState<Set<string>>(new Set());
  const [addUnitParentId, setAddUnitParentId] = useState<string | 'root' | null>(null);

  const [selectedAreaId, setSelectedAreaId] = useState('');
  const [editingAreaId, setEditingAreaId] = useState('');
  const [areaTreeToggled, setAreaTreeToggled] = useState<Set<string>>(new Set());
  const [addAreaParentId, setAddAreaParentId] = useState<string | 'root' | null>(null);

  const organizations = useQuery({
    queryKey: queryKeys.admin.organizations, queryFn: api.admin.organizations.list, enabled: showOrganizationDomain,
  });
  const effectiveOrganizationId = organizationId || organizations.data?.[0]?.id || '';
  const currentOrg = organizations.data?.find((o) => o.id === effectiveOrganizationId);

  const units = useQuery({
    queryKey: queryKeys.admin.organizationUnits(effectiveOrganizationId),
    queryFn: () => api.admin.organizations.listUnits(effectiveOrganizationId),
    enabled: showOrganizationDomain && Boolean(effectiveOrganizationId),
  });
  const unitList = units.data ?? [];
  const effectiveUnitId = selectedUnitId || unitList[0]?.id || '';
  const currentUnit = unitList.find((u) => u.id === effectiveUnitId);

  const areas = useQuery({
    queryKey: queryKeys.admin.geographicAreas, queryFn: () => api.admin.geography.listAreas(), enabled: showGeographyDomain,
  });
  const areaList = areas.data ?? [];
  const effectiveAreaId = selectedAreaId || areaList[0]?.id || '';
  const currentArea = areaList.find((a) => a.id === effectiveAreaId);

  const areaTypes = useQuery({
    queryKey: queryKeys.admin.geographicAreaTypes, queryFn: api.admin.geography.listAreaTypes, enabled: showGeographyDomain && canManageGeography,
  });

  // Mutations for creation
  const createOrganization = useMutation({
    mutationFn: api.admin.organizations.create,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizations }),
  });
  const createUnit = useMutation({
    mutationFn: ({ id, body }: { id: string; body: Parameters<typeof api.admin.organizations.createUnit>[1] }) => api.admin.organizations.createUnit(id, body),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizationUnitsAll }),
  });
  const createArea = useMutation({
    mutationFn: api.admin.geography.createArea,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.admin.geographicAreas }),
  });

  // Mutations for updates & activation
  const updateOrganization = useMutation({
    mutationFn: ({ id, body }: { id: string; body: OrganizationRequest }) => api.admin.organizations.update(id, body),
    onSuccess: () => {
      setEditingOrg(false);
      return queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizations });
    },
  });
  const updateUnit = useMutation({
    mutationFn: ({ id, body }: { id: string; body: OrganizationUnitRequest }) => api.admin.organizations.updateUnit(id, body),
    onSuccess: () => {
      setEditingUnitId('');
      return queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizationUnitsAll });
    },
  });
  const activateUnit = useMutation({
    mutationFn: (id: string) => api.admin.organizations.activateUnit(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizationUnitsAll }),
  });
  const updateArea = useMutation({
    mutationFn: ({ id, body }: { id: string; body: GeographicAreaRequest }) => api.admin.geography.updateArea(id, body),
    onSuccess: () => {
      setEditingAreaId('');
      return queryClient.invalidateQueries({ queryKey: queryKeys.admin.geographicAreas });
    },
  });
  const activateArea = useMutation({
    mutationFn: (id: string) => api.admin.geography.activateArea(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.admin.geographicAreas }),
  });

  function submitOrganization(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    createOrganization.mutate({ code: value(data, 'code'), name: value(data, 'name'), organizationType: value(data, 'organizationType'), description: optional(value(data, 'description')) }, { onSuccess: () => form.reset() });
  }

  function submitUnit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const selectedOrganizationId = value(data, 'organizationId');
    createUnit.mutate({ id: selectedOrganizationId, body: { organizationId: selectedOrganizationId, code: value(data, 'code'), name: value(data, 'name'), unitType: value(data, 'unitType'), parentUnitId: optional(value(data, 'parentUnitId')) } }, { onSuccess: () => form.reset() });
  }

  function submitArea(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    createArea.mutate({ code: value(data, 'code'), name: value(data, 'name'), areaType: value(data, 'areaType'), parentAreaId: optional(value(data, 'parentAreaId')) }, { onSuccess: () => form.reset() });
  }

  function submitEditOrg(event: FormEvent<HTMLFormElement>, org: OrganizationResponse) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    updateOrganization.mutate({
      id: org.id,
      body: {
        code: value(data, 'code'),
        name: value(data, 'name'),
        organizationType: value(data, 'organizationType'),
        description: optional(value(data, 'description')),
      },
    });
  }

  function submitEditUnit(event: FormEvent<HTMLFormElement>, unit: OrganizationUnitResponse) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    updateUnit.mutate({
      id: unit.id,
      body: {
        organizationId: unit.organizationId,
        code: value(data, 'code'),
        name: value(data, 'name'),
        unitType: value(data, 'unitType'),
        parentUnitId: optional(value(data, 'parentUnitId')),
      },
    });
  }

  function submitEditArea(event: FormEvent<HTMLFormElement>, area: GeographicAreaResponse) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    updateArea.mutate({
      id: area.id,
      body: {
        code: value(data, 'code'),
        name: value(data, 'name'),
        areaType: value(data, 'areaType'),
        parentAreaId: optional(value(data, 'parentAreaId')),
      },
    });
  }

  // Tree and breadcrumb computations
  const unitTree = buildTree(unitList, (u) => u.parentUnitId);
  const unitBreadcrumbs = effectiveUnitId ? buildBreadcrumbs(effectiveUnitId, unitList, (u) => u.parentUnitId) : [];

  const areaTree = buildTree(areaList, (a) => a.parentAreaId);
  const areaBreadcrumbs = effectiveAreaId ? buildBreadcrumbs(effectiveAreaId, areaList, (a) => a.parentAreaId) : [];

  function renderUnitTreeNode(node: TreeNode<OrganizationUnitResponse>) {
    const isSelected = node.item.id === effectiveUnitId;
    const isEditing = editingUnitId === node.item.id;
    const hasChildren = node.children.length > 0;
    const expanded = isNodeExpanded(node.item.id, node.depth, unitTreeToggled);
    const isAdding = addUnitParentId === node.item.id;
    return (
      <div key={node.item.id} className="tree-node-wrapper">
        <div
          className={`tree-node ${isSelected ? 'tree-node--selected' : ''}`}
          style={{ paddingLeft: `${node.depth * 1.5 + 0.5}rem` }}
        >
          {hasChildren ? (
            <button
              type="button"
              className="tree-node__toggle"
              aria-expanded={expanded}
              aria-label={expanded ? `Collapse ${node.item.name}` : `Expand ${node.item.name}`}
              onClick={() => setUnitTreeToggled((prev) => toggleSetMembership(prev, node.item.id))}
            >
              {expanded ? '▾' : '▸'}
            </button>
          ) : <span className="tree-branch" aria-hidden="true">{node.depth > 0 ? '├─ ' : '• '}</span>}
          <div className="tree-node__info">
            <button
              type="button"
              className="tree-node__select-btn"
              onClick={() => setSelectedUnitId(node.item.id)}
            >
              <strong>{node.item.name}</strong>
              <span className="tree-node__meta"><code>{node.item.code}</code> · {node.item.unitType}</span>
            </button>
          </div>
          <div className="tree-node__actions">
            <StatusBadge tone={node.item.status === 'ACTIVE' ? 'success' : 'warning'}>{node.item.status}</StatusBadge>
            {canManageOrganizations && node.item.status === 'ACTIVE' && (
              <button
                className="admin-action-link tree-node__add-btn"
                type="button"
                aria-label={`Add a unit under ${node.item.name}`}
                title={`Add a unit under ${node.item.name}`}
                onClick={() => setAddUnitParentId(isAdding ? null : node.item.id)}
              >
                +
              </button>
            )}
            {canManageOrganizations && node.item.status === 'ACTIVE' && (
              <button
                className="admin-action-link"
                type="button"
                onClick={() => {
                  setSelectedUnitId(node.item.id);
                  setEditingUnitId(isEditing ? '' : node.item.id);
                }}
              >
                {isEditing ? 'Close' : 'Edit'}
              </button>
            )}
          </div>
        </div>
        {isAdding && (
          <div style={{ paddingLeft: `${(node.depth + 1) * 1.5 + 0.5}rem` }}>
            <InlineAddUnit
              organizationId={node.item.organizationId}
              parentUnitId={node.item.id}
              pending={createUnit.isPending}
              error={createUnit.isError ? createUnit.error : null}
              onCancel={() => setAddUnitParentId(null)}
              onCreate={(body) => createUnit.mutate({ id: node.item.organizationId, body: { ...body, organizationId: node.item.organizationId } }, {
                onSuccess: () => { setUnitTreeToggled((prev) => (isNodeExpanded(node.item.id, node.depth, prev) ? prev : toggleSetMembership(prev, node.item.id))); setAddUnitParentId(null); },
              })}
            />
          </div>
        )}
        {(expanded || isAdding) && node.children.map(renderUnitTreeNode)}
      </div>
    );
  }

  function renderAreaTreeNode(node: TreeNode<GeographicAreaResponse>) {
    const isSelected = node.item.id === effectiveAreaId;
    const isEditing = editingAreaId === node.item.id;
    const hasChildren = node.children.length > 0;
    const expanded = isNodeExpanded(node.item.id, node.depth, areaTreeToggled);
    const isAdding = addAreaParentId === node.item.id;
    return (
      <div key={node.item.id} className="tree-node-wrapper">
        <div
          className={`tree-node ${isSelected ? 'tree-node--selected' : ''}`}
          style={{ paddingLeft: `${node.depth * 1.5 + 0.5}rem` }}
        >
          {hasChildren ? (
            <button
              type="button"
              className="tree-node__toggle"
              aria-expanded={expanded}
              aria-label={expanded ? `Collapse ${node.item.name}` : `Expand ${node.item.name}`}
              onClick={() => setAreaTreeToggled((prev) => toggleSetMembership(prev, node.item.id))}
            >
              {expanded ? '▾' : '▸'}
            </button>
          ) : <span className="tree-branch" aria-hidden="true">{node.depth > 0 ? '├─ ' : '• '}</span>}
          <div className="tree-node__info">
            <button
              type="button"
              className="tree-node__select-btn"
              onClick={() => setSelectedAreaId(node.item.id)}
            >
              <strong>{node.item.name}</strong>
              <span className="tree-node__meta"><code>{node.item.code}</code> · {node.item.areaType}</span>
            </button>
          </div>
          <div className="tree-node__actions">
            <StatusBadge tone={node.item.status === 'ACTIVE' ? 'success' : 'warning'}>{node.item.status}</StatusBadge>
            {canManageGeography && node.item.status === 'ACTIVE' && (
              <button
                className="admin-action-link tree-node__add-btn"
                type="button"
                aria-label={`Add an area under ${node.item.name}`}
                title={`Add an area under ${node.item.name}`}
                onClick={() => setAddAreaParentId(isAdding ? null : node.item.id)}
              >
                +
              </button>
            )}
            {canManageGeography && node.item.status === 'ACTIVE' && (
              <button
                className="admin-action-link"
                type="button"
                onClick={() => {
                  setSelectedAreaId(node.item.id);
                  setEditingAreaId(isEditing ? '' : node.item.id);
                }}
              >
                {isEditing ? 'Close' : 'Edit'}
              </button>
            )}
          </div>
        </div>
        {isAdding && (
          <div style={{ paddingLeft: `${(node.depth + 1) * 1.5 + 0.5}rem` }}>
            <InlineAddArea
              parentAreaId={node.item.id}
              areaTypes={areaTypes.data ?? []}
              pending={createArea.isPending}
              error={createArea.isError ? createArea.error : null}
              onCancel={() => setAddAreaParentId(null)}
              onCreate={(body) => createArea.mutate(body, {
                onSuccess: () => { setAreaTreeToggled((prev) => (isNodeExpanded(node.item.id, node.depth, prev) ? prev : toggleSetMembership(prev, node.item.id))); setAddAreaParentId(null); },
              })}
            />
          </div>
        )}
        {(expanded || isAdding) && node.children.map(renderAreaTreeNode)}
      </div>
    );
  }

  return (
    <section className="admin-workspace hierarchy-page" aria-labelledby="hierarchy-title">
      <header>
        <div>
          <p className="eyebrow">Owned structures</p>
          <h2 id="hierarchy-title">Organization &amp; geography</h2>
        </div>
        <p>Build and maintain the organizational units and physical territories that define operational reach.</p>
      </header>

      {showOrganizationDomain && showGeographyDomain && (
        <div className="hierarchy-tabs" role="tablist" aria-label="Hierarchy domain">
          <button
            role="tab"
            aria-selected={activeDomain === 'organization'}
            className={`hierarchy-tab ${activeDomain === 'organization' ? 'hierarchy-tab--active' : ''}`}
            type="button"
            onClick={() => setActiveDomain('organization')}
          >
            Organization
          </button>
          <button
            role="tab"
            aria-selected={activeDomain === 'geography'}
            className={`hierarchy-tab ${activeDomain === 'geography' ? 'hierarchy-tab--active' : ''}`}
            type="button"
            onClick={() => setActiveDomain('geography')}
          >
            Geographic
          </button>
        </div>
      )}

      {showOrganizationDomain && activeDomain === 'organization' && (
        <section className="admin-domain" aria-labelledby="organizations-title">
          <header>
            <div>
              <h3 id="organizations-title">Organizations &amp; Units</h3>
              <p>Agencies, departments, and operational teams arranged in a clear hierarchical structure.</p>
            </div>
          </header>

          {organizations.isPending ? (
            <PageState title="Loading organizations">Retrieving authorized organizations…</PageState>
          ) : organizations.isError ? (
            <PageState title="Couldn’t load organizations">{errorDetail(organizations.error, 'Organizations could not be loaded.')}</PageState>
          ) : organizations.data.length === 0 ? (
            <p className="admin-empty">No organizations are available.</p>
          ) : (
            <div className="hierarchy-layout">
              {/* Organization Panel */}
              <div className="admin-panel">
                <header className="panel-header">
                  <h4>Organizations</h4>
                  {canManageOrganizations && currentOrg && (
                    <button
                      className="button button--secondary button--small"
                      type="button"
                      onClick={() => setEditingOrg((prev) => !prev)}
                    >
                      {editingOrg ? 'Cancel editing' : 'Edit organization'}
                    </button>
                  )}
                </header>

                <label className="admin-selector">
                  Select organization
                  <select
                    value={effectiveOrganizationId}
                    onChange={(event) => {
                      setOrganizationId(event.target.value);
                      setSelectedUnitId('');
                      setEditingOrg(false);
                      setEditingUnitId('');
                    }}
                  >
                    {organizations.data.map((organization) => (
                      <option key={organization.id} value={organization.id}>
                        {organization.name} ({organization.code})
                      </option>
                    ))}
                  </select>
                </label>

                {currentOrg && (
                  <div className="hierarchy-card">
                    <div className="hierarchy-card__header">
                      <div>
                        <h5>{currentOrg.name}</h5>
                        <p className="hierarchy-card__meta">
                          <code>{currentOrg.code}</code> · Type: <strong>{currentOrg.organizationType}</strong>
                        </p>
                      </div>
                      <StatusBadge tone={currentOrg.status === 'ACTIVE' ? 'success' : 'warning'}>
                        {currentOrg.status}
                      </StatusBadge>
                    </div>
                    {currentOrg.description && <p className="hierarchy-card__desc">{currentOrg.description}</p>}
                  </div>
                )}

                {editingOrg && currentOrg && (
                  <form
                    aria-label="Edit organization"
                    className="admin-form admin-form--inline"
                    onSubmit={(e) => submitEditOrg(e, currentOrg)}
                  >
                    <h5>Edit {currentOrg.name}</h5>
                    <label>
                      Code
                      <input name="code" defaultValue={currentOrg.code} required />
                    </label>
                    <label>
                      Name
                      <input name="name" defaultValue={currentOrg.name} required />
                    </label>
                    <label>
                      Organization type
                      <input name="organizationType" defaultValue={currentOrg.organizationType} required />
                    </label>
                    <label>
                      Description
                      <textarea name="description" defaultValue={currentOrg.description ?? ''} />
                    </label>
                    {updateOrganization.isError && (
                      <p className="form-error" role="alert">
                        {errorDetail(updateOrganization.error, 'The organization could not be updated.')}
                      </p>
                    )}
                    <div className="form-actions">
                      <Button disabled={updateOrganization.isPending} type="submit">
                        Save changes
                      </Button>
                      <button className="button button--secondary" type="button" onClick={() => setEditingOrg(false)}>
                        Cancel
                      </button>
                    </div>
                  </form>
                )}

                <ul className="admin-record-list" style={{ marginTop: '1rem' }}>
                  {organizations.data.map((organization) => (
                    <li
                      key={organization.id}
                      className={organization.id === effectiveOrganizationId ? 'admin-record--active' : ''}
                    >
                      <div>
                        <strong>{organization.name}</strong>
                        <span>{organization.code} · {organization.organizationType}</span>
                        <button
                          className="admin-action-link"
                          type="button"
                          onClick={() => {
                            setOrganizationId(organization.id);
                            setSelectedUnitId('');
                            setEditingOrg(false);
                          }}
                        >
                          Select {organization.name}
                        </button>
                      </div>
                      <StatusBadge tone={organization.status === 'ACTIVE' ? 'success' : 'warning'}>
                        {organization.status}
                      </StatusBadge>
                    </li>
                  ))}
                </ul>
              </div>

              {/* Units Panel */}
              <div className="admin-panel">
                <header className="panel-header">
                  <h4>Units Hierarchy</h4>
                  {canManageOrganizations && effectiveOrganizationId && (
                    <button
                      className="button button--secondary button--small"
                      type="button"
                      onClick={() => setAddUnitParentId(addUnitParentId === 'root' ? null : 'root')}
                    >
                      + Add root unit
                    </button>
                  )}
                </header>

                {addUnitParentId === 'root' && (
                  <InlineAddUnit
                    organizationId={effectiveOrganizationId}
                    parentUnitId={null}
                    pending={createUnit.isPending}
                    error={createUnit.isError ? createUnit.error : null}
                    onCancel={() => setAddUnitParentId(null)}
                    onCreate={(body) => createUnit.mutate({ id: effectiveOrganizationId, body: { ...body, organizationId: effectiveOrganizationId } }, {
                      onSuccess: () => setAddUnitParentId(null),
                    })}
                  />
                )}

                <label className="admin-selector">
                  Select unit
                  <select
                    value={effectiveUnitId}
                    onChange={(event) => setSelectedUnitId(event.target.value)}
                    disabled={!unitList.length}
                  >
                    <option value="">{unitList.length ? 'Select a unit to inspect' : 'No units in this organization'}</option>
                    {unitList.map((unit) => (
                      <option key={unit.id} value={unit.id}>
                        {unit.name} ({unit.code})
                      </option>
                    ))}
                  </select>
                </label>

                {unitBreadcrumbs.length > 0 && (
                  <nav aria-label="Unit hierarchy trail" className="hierarchy-breadcrumb">
                    <span className="breadcrumb-root">{currentOrg?.name ?? 'Organization'}</span>
                    {unitBreadcrumbs.map((crumb) => (
                      <span key={crumb.id} className="breadcrumb-step">
                        <span className="breadcrumb-sep" aria-hidden="true"> / </span>
                        <button
                          type="button"
                          className={`breadcrumb-link ${crumb.id === effectiveUnitId ? 'breadcrumb-link--active' : ''}`}
                          onClick={() => setSelectedUnitId(crumb.id)}
                        >
                          › {crumb.name}
                        </button>
                      </span>
                    ))}
                  </nav>
                )}

                {units.isPending ? (
                  <p>Loading units…</p>
                ) : units.isError ? (
                  <p className="form-error">{errorDetail(units.error, 'Units could not be loaded.')}</p>
                ) : unitList.length === 0 ? (
                  <p className="admin-empty">No units are available for this organization.</p>
                ) : (
                  <div className="tree-container" role="tree" aria-label="Organization units tree">
                    {unitTree.map(renderUnitTreeNode)}
                  </div>
                )}

                {/* Selected Unit Details / Edit Panel */}
                {currentUnit && (
                  <div className="hierarchy-inspect-card">
                    <div className="hierarchy-inspect-card__header">
                      <div>
                        <h5>Unit: {currentUnit.name}</h5>
                        <p className="hierarchy-card__meta">
                          <code>{currentUnit.code}</code> · {currentUnit.unitType}
                        </p>
                      </div>
                      <StatusBadge tone={currentUnit.status === 'ACTIVE' ? 'success' : 'warning'}>
                        {currentUnit.status}
                      </StatusBadge>
                    </div>

                    {canManageOrganizations && (
                      <div className="hierarchy-actions">
                        {currentUnit.status === 'ACTIVE' && (
                          <button
                            className="button button--secondary button--small"
                            type="button"
                            onClick={() => setEditingUnitId((prev) => (prev === currentUnit.id ? '' : currentUnit.id))}
                          >
                            {editingUnitId === currentUnit.id ? 'Close edit' : 'Edit unit'}
                          </button>
                        )}
                        {currentUnit.status !== 'ACTIVE' && (
                          <Button
                            disabled={activateUnit.isPending}
                            type="button"
                            onClick={() => activateUnit.mutate(currentUnit.id)}
                          >
                            Activate unit
                          </Button>
                        )}
                        {currentUnit.status === 'ACTIVE' && (
                          <DeactivationControl
                            kind="unit"
                            itemId={currentUnit.id}
                            itemName={currentUnit.name}
                            parentLabel="New parent unit"
                            parentOptions={validParentOptions(unitList, currentUnit.id, (u) => u.parentUnitId).map(({ id, name }) => ({ id, name }))}
                            deactivate={(request) => api.admin.organizations.deactivateUnit(currentUnit.id, request)}
                            onSuccess={() => queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizationUnitsAll })}
                          />
                        )}
                        {currentUnit.status === 'ACTIVE' && (
                          <MoveUnitControl
                            unit={currentUnit}
                            organizations={organizations.data ?? []}
                            onSuccess={() => {
                              setSelectedUnitId('');
                              return queryClient.invalidateQueries({ queryKey: queryKeys.admin.organizationUnitsAll });
                            }}
                          />
                        )}
                      </div>
                    )}

                    {editingUnitId === currentUnit.id && currentUnit.status === 'ACTIVE' && canManageOrganizations && (
                      <form
                        aria-label={`Edit unit ${currentUnit.name}`}
                        className="admin-form admin-form--inline"
                        onSubmit={(e) => submitEditUnit(e, currentUnit)}
                      >
                        <h5>Edit unit {currentUnit.name}</h5>
                        <label>
                          Code
                          <input name="code" defaultValue={currentUnit.code} required />
                        </label>
                        <label>
                          Name
                          <input name="name" defaultValue={currentUnit.name} required />
                        </label>
                        <label>
                          Unit type
                          <input name="unitType" defaultValue={currentUnit.unitType} required />
                        </label>
                        <label>
                          Parent unit
                          <select name="parentUnitId" defaultValue={currentUnit.parentUnitId ?? ''}>
                            <option value="">No parent (Root unit)</option>
                            {parentSelectOptions(unitList, currentUnit.id, currentUnit.parentUnitId, (u) => u.parentUnitId)
                              .map((option) => (
                                <option key={option.id} value={option.id}>
                                  {option.label}
                                </option>
                              ))}
                          </select>
                        </label>
                        {updateUnit.isError && (
                          <p className="form-error" role="alert">
                            {errorDetail(updateUnit.error, 'The unit could not be updated.')}
                          </p>
                        )}
                        <div className="form-actions">
                          <Button disabled={updateUnit.isPending} type="submit">
                            Save changes
                          </Button>
                          <button
                            className="button button--secondary"
                            type="button"
                            onClick={() => setEditingUnitId('')}
                          >
                            Cancel
                          </button>
                        </div>
                      </form>
                    )}
                  </div>
                )}
              </div>
            </div>
          )}

          {canManageOrganizations && (
            <div className="admin-form-grid">
              <form aria-label="Create organization" className="admin-form" onSubmit={submitOrganization}>
                <h4>Create organization</h4>
                <label>
                  Code
                  <input name="code" required />
                </label>
                <label>
                  Name
                  <input name="name" required />
                </label>
                <label>
                  Organization type
                  <input name="organizationType" required />
                </label>
                <label>
                  Description
                  <textarea name="description" />
                </label>
                {createOrganization.isError && (
                  <p className="form-error" role="alert">
                    {errorDetail(createOrganization.error, 'The organization could not be created.')}
                  </p>
                )}
                <Button disabled={createOrganization.isPending} type="submit">
                  Create organization
                </Button>
              </form>

              <form aria-label="Create organization unit" className="admin-form" onSubmit={submitUnit}>
                <h4>Create organization unit</h4>
                <label>
                  Organization
                  <select
                    name="organizationId"
                    required
                    value={effectiveOrganizationId}
                    onChange={(event) => setOrganizationId(event.target.value)}
                  >
                    <option value="">Select an organization</option>
                    {organizations.data?.map((organization) => (
                      <option key={organization.id} value={organization.id}>
                        {organization.name}
                      </option>
                    ))}
                  </select>
                </label>
                <label>
                  Code
                  <input name="code" required />
                </label>
                <label>
                  Name
                  <input name="name" required />
                </label>
                <label>
                  Unit type
                  <input name="unitType" required />
                </label>
                <label>
                  Parent unit
                  <select name="parentUnitId">
                    <option value="">No parent</option>
                    {(units.data ?? []).filter((unit) => unit.status === 'ACTIVE').map((unit) => (
                      <option key={unit.id} value={unit.id}>
                        {unit.name}
                      </option>
                    ))}
                  </select>
                </label>
                {createUnit.isError && (
                  <p className="form-error" role="alert">
                    {errorDetail(createUnit.error, 'The organization unit could not be created.')}
                  </p>
                )}
                <Button disabled={createUnit.isPending || !effectiveOrganizationId} type="submit">
                  Create organization unit
                </Button>
              </form>
            </div>
          )}
        </section>
      )}

      {showGeographyDomain && activeDomain === 'geography' && (
        <section className="admin-domain" aria-labelledby="geography-title">
          <header>
            <div>
              <h3 id="geography-title">Geography</h3>
              <p>The territory hierarchy cameras, VMS targets, and events attach to directly.</p>
            </div>
          </header>

          {areas.isPending ? (
            <PageState title="Loading geographic areas">Retrieving authorized areas…</PageState>
          ) : areas.isError ? (
            <PageState title="Couldn’t load geographic areas">{errorDetail(areas.error, 'Geographic areas could not be loaded.')}</PageState>
          ) : (
            <div className="hierarchy-layout">
              {/* Geographic Areas Panel */}
              <div className="admin-panel">
                <header className="panel-header">
                  <h4>Geographic Areas Hierarchy</h4>
                  {canManageGeography && (
                    <button
                      className="button button--secondary button--small"
                      type="button"
                      onClick={() => setAddAreaParentId(addAreaParentId === 'root' ? null : 'root')}
                    >
                      + Add root area
                    </button>
                  )}
                </header>

                {addAreaParentId === 'root' && (
                  <InlineAddArea
                    parentAreaId={null}
                    areaTypes={areaTypes.data ?? []}
                    pending={createArea.isPending}
                    error={createArea.isError ? createArea.error : null}
                    onCancel={() => setAddAreaParentId(null)}
                    onCreate={(body) => createArea.mutate(body, { onSuccess: () => setAddAreaParentId(null) })}
                  />
                )}

                <label className="admin-selector">
                  Select geographic area
                  <select
                    value={effectiveAreaId}
                    onChange={(event) => setSelectedAreaId(event.target.value)}
                    disabled={!areaList.length}
                  >
                    <option value="">{areaList.length ? 'Select an area to inspect' : 'No areas available'}</option>
                    {areaList.map((area) => (
                      <option key={area.id} value={area.id}>
                        {area.name} ({area.code} · {area.areaType})
                      </option>
                    ))}
                  </select>
                </label>

                {areaBreadcrumbs.length > 0 && (
                  <nav aria-label="Geographic area hierarchy trail" className="hierarchy-breadcrumb">
                    <span className="breadcrumb-root">Territory</span>
                    {areaBreadcrumbs.map((crumb) => (
                      <span key={crumb.id} className="breadcrumb-step">
                        <span className="breadcrumb-sep" aria-hidden="true"> / </span>
                        <button
                          type="button"
                          className={`breadcrumb-link ${crumb.id === effectiveAreaId ? 'breadcrumb-link--active' : ''}`}
                          onClick={() => setSelectedAreaId(crumb.id)}
                        >
                          › {crumb.name}
                        </button>
                      </span>
                    ))}
                  </nav>
                )}

                {areaList.length === 0 ? (
                  <p className="admin-empty">No geographic areas are available.</p>
                ) : (
                  <div className="tree-container" role="tree" aria-label="Geographic areas tree">
                    {areaTree.map(renderAreaTreeNode)}
                  </div>
                )}

                {/* Selected Area Inspection / Edit */}
                {currentArea && (
                  <div className="hierarchy-inspect-card">
                    <div className="hierarchy-inspect-card__header">
                      <div>
                        <h5>Area: {currentArea.name}</h5>
                        <p className="hierarchy-card__meta">
                          <code>{currentArea.code}</code> · Level: <strong>{currentArea.areaType}</strong>
                        </p>
                      </div>
                      <StatusBadge tone={currentArea.status === 'ACTIVE' ? 'success' : 'warning'}>
                        {currentArea.status}
                      </StatusBadge>
                    </div>

                    {canManageGeography && (
                      <div className="hierarchy-actions">
                        {currentArea.status === 'ACTIVE' && (
                          <button
                            className="button button--secondary button--small"
                            type="button"
                            onClick={() => setEditingAreaId((prev) => (prev === currentArea.id ? '' : currentArea.id))}
                          >
                            {editingAreaId === currentArea.id ? 'Close edit' : 'Edit area'}
                          </button>
                        )}
                        {currentArea.status !== 'ACTIVE' && (
                          <Button
                            disabled={activateArea.isPending}
                            type="button"
                            onClick={() => activateArea.mutate(currentArea.id)}
                          >
                            Activate area
                          </Button>
                        )}
                        {currentArea.status === 'ACTIVE' && (
                          <DeactivationControl
                            kind="area"
                            itemId={currentArea.id}
                            itemName={currentArea.name}
                            parentLabel="New parent area"
                            parentOptions={validParentOptions(areaList, currentArea.id, (a) => a.parentAreaId).map(({ id, name }) => ({ id, name }))}
                            deactivate={(request) => api.admin.geography.deactivateArea(currentArea.id, request)}
                            onSuccess={() => queryClient.invalidateQueries({ queryKey: queryKeys.admin.geographicAreas })}
                          />
                        )}
                      </div>
                    )}

                    {editingAreaId === currentArea.id && currentArea.status === 'ACTIVE' && canManageGeography && (
                      <form
                        aria-label={`Edit geographic area ${currentArea.name}`}
                        className="admin-form admin-form--inline"
                        onSubmit={(e) => submitEditArea(e, currentArea)}
                      >
                        <h5>Edit area {currentArea.name}</h5>
                        <label>
                          Code
                          <input name="code" defaultValue={currentArea.code} required />
                        </label>
                        <label>
                          Name
                          <input name="name" defaultValue={currentArea.name} required />
                        </label>
                        <label>
                          Area type
                          <select name="areaType" defaultValue={currentArea.areaType} required>
                            {areaTypes.data?.map((type) => (
                              <option key={type.code} value={type.code}>
                                {type.name}
                              </option>
                            ))}
                          </select>
                        </label>
                        <label>
                          Parent area
                          <select name="parentAreaId" defaultValue={currentArea.parentAreaId ?? ''}>
                            <option value="">No parent (Root territory)</option>
                            {parentSelectOptions(areaList, currentArea.id, currentArea.parentAreaId, (a) => a.parentAreaId)
                              .map((option) => (
                                <option key={option.id} value={option.id}>
                                  {option.label}
                                </option>
                              ))}
                          </select>
                        </label>
                        {updateArea.isError && (
                          <p className="form-error" role="alert">
                            {errorDetail(updateArea.error, 'The geographic area could not be updated.')}
                          </p>
                        )}
                        <div className="form-actions">
                          <Button disabled={updateArea.isPending} type="submit">
                            Save changes
                          </Button>
                          <button
                            className="button button--secondary"
                            type="button"
                            onClick={() => setEditingAreaId('')}
                          >
                            Cancel
                          </button>
                        </div>
                      </form>
                    )}
                  </div>
                )}
              </div>

            </div>
          )}

          {canManageGeography && (
            <div className="admin-form-grid">
              <form aria-label="Create geographic area" className="admin-form" onSubmit={submitArea}>
                <h4>Create geographic area</h4>
                <label>
                  Code
                  <input name="code" required />
                </label>
                <label>
                  Name
                  <input name="name" required />
                </label>
                <label>
                  Area type
                  <select name="areaType" required>
                    <option value="">Select an area type</option>
                    {areaTypes.data?.map((type) => (
                      <option key={type.code} value={type.code}>
                        {type.name}
                      </option>
                    ))}
                  </select>
                </label>
                <label>
                  Parent area
                  <select name="parentAreaId">
                    <option value="">No parent</option>
                    {(areas.data ?? []).filter((area) => area.status === 'ACTIVE').map((area) => (
                      <option key={area.id} value={area.id}>
                        {area.name}
                      </option>
                    ))}
                  </select>
                </label>
                {createArea.isError && (
                  <p className="form-error" role="alert">
                    {errorDetail(createArea.error, 'The geographic area could not be created.')}
                  </p>
                )}
                <Button disabled={createArea.isPending} type="submit">
                  Create geographic area
                </Button>
              </form>
            </div>
          )}
        </section>
      )}
    </section>
  );
}

