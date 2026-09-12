import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { api } from './endpoints';
import { apiBaseUrl } from './client';
import type { ApiKeyResponse } from './models';

const organizationId = '10000000-0000-4000-8000-000000000001';
const unitId = '10000000-0000-4000-8000-000000000002';
const areaId = '10000000-0000-4000-8000-000000000003';
const groupId = '10000000-0000-4000-8000-000000000005';
const roleId = '10000000-0000-4000-8000-000000000006';
const scopeId = '10000000-0000-4000-8000-000000000007';
const entryId = '10000000-0000-4000-8000-000000000008';
const alertId = '10000000-0000-4000-8000-000000000009';
const apiKeyId = '10000000-0000-4000-8000-000000000010';

function fetchCall(path: string, init: RequestInit = {}): [string, RequestInit] {
  return [`${apiBaseUrl()}${path}`, expect.objectContaining(init) as RequestInit];
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('admin organization endpoints', () => {
  it('maps organization and unit operations to the hierarchy routes', async () => {
    const organization = {
      code: 'POLICE', name: 'Police', organizationType: 'AGENCY', description: null,
    };
    const unit = {
      organizationId, code: 'HQ', name: 'Headquarters', unitType: 'DEPARTMENT', parentUnitId: null,
    };

    await api.admin.organizations.list();
    await api.admin.organizations.get(organizationId);
    await api.admin.organizations.create(organization);
    await api.admin.organizations.update(organizationId, organization);
    await api.admin.organizations.listUnits(organizationId);
    await api.admin.organizations.createUnit(organizationId, unit);
    await api.admin.organizations.updateUnit(unitId, unit);
    await api.admin.organizations.activateUnit(unitId);
    await api.admin.organizations.deactivateUnit(unitId);

    expect(fetch).toHaveBeenNthCalledWith(1, ...fetchCall('/api/v1/organizations'));
    expect(fetch).toHaveBeenNthCalledWith(2, ...fetchCall(`/api/v1/organizations/${organizationId}`));
    expect(fetch).toHaveBeenNthCalledWith(3, ...fetchCall('/api/v1/organizations', {
      method: 'POST', body: JSON.stringify(organization),
    }));
    expect(fetch).toHaveBeenNthCalledWith(4, ...fetchCall(`/api/v1/organizations/${organizationId}`, {
      method: 'PUT', body: JSON.stringify(organization),
    }));
    expect(fetch).toHaveBeenNthCalledWith(5, ...fetchCall(`/api/v1/organizations/${organizationId}/units`));
    expect(fetch).toHaveBeenNthCalledWith(6, ...fetchCall(`/api/v1/organizations/${organizationId}/units`, {
      method: 'POST', body: JSON.stringify(unit),
    }));
    expect(fetch).toHaveBeenNthCalledWith(7, ...fetchCall(`/api/v1/organization-units/${unitId}`, {
      method: 'PUT', body: JSON.stringify(unit),
    }));
    expect(fetch).toHaveBeenNthCalledWith(8, ...fetchCall(`/api/v1/organization-units/${unitId}/activate`, {
      method: 'POST',
    }));
    expect(fetch).toHaveBeenNthCalledWith(9, ...fetchCall(`/api/v1/organization-units/${unitId}/deactivate`, {
      method: 'POST', body: '{}',
    }));
  });
});

describe('admin geography endpoints', () => {
  it('maps area traversal and area mutation to hierarchy routes', async () => {
    const area = { code: 'AMD', name: 'Ahmedabad', areaType: 'DISTRICT', parentAreaId: null };
    const deactivation = { childStrategy: 'reparent' as const, newParentId: '10000000-0000-4000-8000-000000000011' };

    await api.admin.geography.listAreas({ rootsOnly: true, parentId: areaId });
    await api.admin.geography.getArea(areaId);
    await api.admin.geography.listAreaChildren(areaId);
    await api.admin.geography.listAreaAncestors(areaId);
    await api.admin.geography.listAreaTypes();
    await api.admin.geography.createArea(area);
    await api.admin.geography.updateArea(areaId, area);
    await api.admin.geography.activateArea(areaId);
    await api.admin.geography.deactivateArea(areaId, deactivation);

    expect(fetch).toHaveBeenNthCalledWith(1, ...fetchCall(`/api/v1/geographic-areas?rootsOnly=true&parentId=${areaId}`));
    expect(fetch).toHaveBeenNthCalledWith(2, ...fetchCall(`/api/v1/geographic-areas/${areaId}`));
    expect(fetch).toHaveBeenNthCalledWith(3, ...fetchCall(`/api/v1/geographic-areas/${areaId}/children`));
    expect(fetch).toHaveBeenNthCalledWith(4, ...fetchCall(`/api/v1/geographic-areas/${areaId}/ancestors`));
    expect(fetch).toHaveBeenNthCalledWith(5, ...fetchCall('/api/v1/geographic-areas/types'));
    expect(fetch).toHaveBeenNthCalledWith(6, ...fetchCall('/api/v1/geographic-areas', {
      method: 'POST', body: JSON.stringify(area),
    }));
    expect(fetch).toHaveBeenNthCalledWith(7, ...fetchCall(`/api/v1/geographic-areas/${areaId}`, {
      method: 'PUT', body: JSON.stringify(area),
    }));
    expect(fetch).toHaveBeenNthCalledWith(8, ...fetchCall(`/api/v1/geographic-areas/${areaId}/activate`, {
      method: 'POST',
    }));
    expect(fetch).toHaveBeenNthCalledWith(9, ...fetchCall(`/api/v1/geographic-areas/${areaId}/deactivate`, {
      method: 'POST', body: JSON.stringify(deactivation),
    }));
  });
});

describe('admin access-control endpoints', () => {
  it('maps access-group reads, creation, update, activation, disable, and scope changes', async () => {
    const group = { code: 'OPERATORS', name: 'Operators', roleId, description: 'Camera operators' };
    const updateGroup = { code: 'OPERATORS', name: 'Senior Operators', roleId, description: 'Senior camera operators' };
    const scope = { scopeType: 'ORGANIZATION', organizationUnitId: unitId, description: 'Headquarters' };

    await api.admin.groups.list();
    await api.admin.groups.get(groupId);
    await api.admin.groups.members(groupId);
    await api.admin.groups.create(group);
    await api.admin.groups.update(groupId, updateGroup);
    await api.admin.groups.activate(groupId, { confirmUnscoped: true });
    await api.admin.groups.disable(groupId);
    await api.admin.groups.addScope(groupId, scope);
    await api.admin.groups.removeScope(groupId, scopeId);

    expect(fetch).toHaveBeenNthCalledWith(1, ...fetchCall('/api/v1/access-groups'));
    expect(fetch).toHaveBeenNthCalledWith(2, ...fetchCall(`/api/v1/access-groups/${groupId}`));
    expect(fetch).toHaveBeenNthCalledWith(3, ...fetchCall(`/api/v1/access-groups/${groupId}/members`));
    expect(fetch).toHaveBeenNthCalledWith(4, ...fetchCall('/api/v1/access-groups', {
      method: 'POST', body: JSON.stringify(group),
    }));
    expect(fetch).toHaveBeenNthCalledWith(5, ...fetchCall(`/api/v1/access-groups/${groupId}`, {
      method: 'PUT', body: JSON.stringify(updateGroup),
    }));
    expect(fetch).toHaveBeenNthCalledWith(6, ...fetchCall(`/api/v1/access-groups/${groupId}/activate`, {
      method: 'POST', body: JSON.stringify({ confirmUnscoped: true }),
    }));
    expect(fetch).toHaveBeenNthCalledWith(7, ...fetchCall(`/api/v1/access-groups/${groupId}/disable`, {
      method: 'POST',
    }));
    expect(fetch).toHaveBeenNthCalledWith(8, ...fetchCall(`/api/v1/access-groups/${groupId}/scopes`, {
      method: 'POST', body: JSON.stringify(scope),
    }));
    expect(fetch).toHaveBeenNthCalledWith(9, ...fetchCall(`/api/v1/access-groups/${groupId}/scopes/${scopeId}`, {
      method: 'DELETE',
    }));
  });

  it('exposes role catalogue operations according to backend specification', async () => {
    const role = { code: 'DISPATCHER', name: 'Dispatcher', permissions: ['camera.read'], description: 'Dispatch staff' };

    await api.admin.roles.list(true);
    await api.admin.roles.get(roleId);
    await api.admin.roles.create(role);
    await api.admin.roles.update(roleId, role);
    await api.admin.roles.delete(roleId);
    await api.admin.roles.permissions();

    expect(fetch).toHaveBeenNthCalledWith(1, ...fetchCall('/api/v1/roles?includeInactive=true'));
    expect(fetch).toHaveBeenNthCalledWith(2, ...fetchCall(`/api/v1/roles/${roleId}`));
    expect(fetch).toHaveBeenNthCalledWith(3, ...fetchCall('/api/v1/roles', {
      method: 'POST', body: JSON.stringify(role),
    }));
    expect(fetch).toHaveBeenNthCalledWith(4, ...fetchCall(`/api/v1/roles/${roleId}`, {
      method: 'PUT', body: JSON.stringify(role),
    }));
    expect(fetch).toHaveBeenNthCalledWith(5, ...fetchCall(`/api/v1/roles/${roleId}`, {
      method: 'DELETE',
    }));
    expect(fetch).toHaveBeenNthCalledWith(6, ...fetchCall('/api/v1/permissions'));
  });
});

describe('admin watchlist endpoints', () => {
  it('maps plate entries and alert acknowledgement to the supported routes', async () => {
    const entry = { organizationUnitId: unitId, plateNumber: 'MH-12-AB-1234', reason: 'Stolen', severity: 'High' };

    await api.admin.watchlist.list();
    await api.admin.watchlist.create(entry);
    await api.admin.watchlist.listAlerts({ limit: 25 });
    await api.admin.watchlist.acknowledgeAlert(alertId);

    expect(fetch).toHaveBeenNthCalledWith(1, ...fetchCall('/api/v1/watchlist'));
    expect(fetch).toHaveBeenNthCalledWith(2, ...fetchCall('/api/v1/watchlist', {
      method: 'POST', body: JSON.stringify(entry),
    }));
    expect(fetch).toHaveBeenNthCalledWith(3, ...fetchCall('/api/v1/watchlist/alerts?limit=25'));
    expect(fetch).toHaveBeenNthCalledWith(4, ...fetchCall(`/api/v1/watchlist/alerts/${alertId}/acknowledge`, {
      method: 'POST',
    }));
  });

  it('deactivates a watchlist entry instead of deleting local state', async () => {
    await api.admin.watchlist.deactivate(entryId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/watchlist/${entryId}`),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });
});

describe('admin API-key endpoints', () => {
  it('creates an API key with the selected group and expiry', async () => {
    const request = { displayName: 'AI worker', groupId, expiresAt: '2027-01-01T00:00:00Z' };

    await api.admin.apiKeys.create(request);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining('/api/v1/api-keys'),
      expect.objectContaining({ method: 'POST', body: JSON.stringify(request) }),
    );
  });

  it('lists secret-free key metadata and revokes a key through DELETE', async () => {
    await api.admin.apiKeys.list();
    await api.admin.apiKeys.revoke(apiKeyId);

    expect(fetch).toHaveBeenNthCalledWith(1, ...fetchCall('/api/v1/api-keys'));
    expect(fetch).toHaveBeenNthCalledWith(2, ...fetchCall(`/api/v1/api-keys/${apiKeyId}`, {
      method: 'DELETE',
    }));

    const listedKey: ApiKeyResponse = {
      id: apiKeyId,
      keyId: 'ak_123',
      displayName: 'AI worker',
      groupId,
      groupCode: 'AI_WORKERS',
      createdAt: '2026-09-04T00:00:00Z',
      expiresAt: null,
      lastUsedAt: null,
      revokedAt: null,
    };
    // @ts-expect-error Raw material exists only on ApiKeyCreatedResponse.
    const leakedSecret = listedKey.rawKey;
    expect(leakedSecret).toBeUndefined();
  });
});
