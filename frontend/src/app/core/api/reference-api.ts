import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  BalanceDto, ClosureDto, CountDto, CredentialDto, HrDto, ItemDto, LaundryBatchDto, LedgerEntryDto, LocationDto, OfferingDto, OfferingRowDto,
  QualificationDto, RoleAssignmentDto, RoomDto, RosterEntryDto, ServiceDto, SettingDto, StaffDto, VariantDto,
} from '../models/reference';

type Q = Record<string, string | number | boolean | undefined | null>;
type Opts = { rowVersion?: number; key?: string };

/**
 * Reference data and operations: the catalogue, rooms and closures, staff
 * with HR, roles, qualifications, credentials and the roster, stock, and
 * governed settings. One method per endpoint. A write that asserts a version
 * sends it as If-Match; a stock movement carries its Idempotency-Key.
 */
@Injectable({ providedIn: 'root' })
export class ReferenceApi {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  /* catalogue */
  services(status?: string): Promise<readonly ServiceDto[]> { return this.get('/catalog/services', { status }); }
  createService(body: Record<string, unknown>): Promise<ServiceDto> { return this.send('POST', '/catalog/services', body); }
  updateService(id: string, v: number, body: Record<string, unknown>): Promise<ServiceDto> { return this.send('PATCH', `/catalog/services/${id}`, body, { rowVersion: v }); }
  transitionService(id: string, v: number, to: string, reason?: string): Promise<ServiceDto> {
    return this.send('POST', `/catalog/services/${id}/transitions`, { to, reason }, { rowVersion: v });
  }
  offering(): Promise<readonly OfferingRowDto[]> { return this.get('/catalog/offering'); }
  offer(serviceId: string, body: { priceMinor?: number | null; currencyCode?: string | null; status?: string }, v?: number): Promise<OfferingDto> {
    return this.send('PUT', `/catalog/offering/${serviceId}`, body, v === undefined ? {} : { rowVersion: v });
  }

  /* rooms */
  rooms(): Promise<readonly RoomDto[]> { return this.get('/rooms'); }
  createRoom(body: { code: string; name: string; resourceType: string; capacity?: number }): Promise<RoomDto> { return this.send('POST', '/rooms', body); }
  updateRoom(id: string, v: number, body: Record<string, unknown>): Promise<RoomDto> { return this.send('PATCH', `/rooms/${id}`, body, { rowVersion: v }); }
  closures(): Promise<readonly ClosureDto[]> { return this.get('/rooms/closures'); }
  closeRoom(body: { resourceId: string; startsUtc: string; endsUtc: string; reasonCode: string; note?: string }): Promise<ClosureDto> {
    return this.send('POST', '/rooms/closures', body);
  }
  cancelClosure(id: string, v: number): Promise<ClosureDto> { return this.send('POST', `/rooms/closures/${id}/cancel`, {}, { rowVersion: v }); }
  locations(): Promise<readonly LocationDto[]> { return this.get('/locations'); }

  /* staff */
  staff(): Promise<readonly StaffDto[]> { return this.get('/staff'); }
  createStaff(body: { preferredName: string; bookable?: boolean }): Promise<StaffDto> { return this.send('POST', '/staff', body); }
  updateStaff(id: string, v: number, body: Record<string, unknown>): Promise<StaffDto> { return this.send('PATCH', `/staff/${id}`, body, { rowVersion: v }); }
  /** Links the person to their Entra account (a security administrator's act). */
  linkSignIn(id: string, v: number, body: { objectId: string; email?: string }): Promise<unknown> {
    return this.send('POST', `/staff/${id}/sign-in`, body, { rowVersion: v });
  }
  hr(id: string): Promise<HrDto> { return this.get(`/staff/${id}/hr`); }
  saveHr(id: string, body: Record<string, unknown>, v?: number): Promise<HrDto> {
    return this.send('PUT', `/staff/${id}/hr`, body, v === undefined ? {} : { rowVersion: v });
  }
  roles(id: string): Promise<readonly RoleAssignmentDto[]> { return this.get(`/staff/${id}/roles`); }
  proposeRole(id: string, roleCode: string, tenantWide: boolean): Promise<RoleAssignmentDto> {
    return this.send('POST', `/staff/${id}/roles`, { roleCode, tenantWide });
  }
  approveRole(id: string, v: number): Promise<RoleAssignmentDto> { return this.send('POST', `/role-assignments/${id}/approve`, {}, { rowVersion: v }); }
  revokeRole(id: string, v: number, reason: string): Promise<RoleAssignmentDto> {
    return this.send('POST', `/role-assignments/${id}/revoke`, { reason }, { rowVersion: v });
  }
  qualifications(id: string): Promise<readonly QualificationDto[]> { return this.get(`/staff/${id}/qualifications`); }
  grant(id: string, serviceId: string): Promise<QualificationDto> { return this.send('POST', `/staff/${id}/qualifications`, { serviceId }); }
  revokeQualification(id: string, v: number, reason: string): Promise<QualificationDto> {
    return this.send('POST', `/qualifications/${id}/revoke`, { reason }, { rowVersion: v });
  }
  credentials(id: string): Promise<readonly CredentialDto[]> { return this.get(`/staff/${id}/credentials`); }
  addCredential(id: string, body: Record<string, unknown>): Promise<CredentialDto> { return this.send('POST', `/staff/${id}/credentials`, body); }
  verifyCredential(id: string, v: number, verify: boolean): Promise<CredentialDto> {
    return this.send('POST', `/credentials/${id}/${verify ? 'verify' : 'reject'}`, {}, { rowVersion: v });
  }
  roster(fromUtc: string, toUtc: string): Promise<readonly RosterEntryDto[]> { return this.get('/roster', { from: fromUtc, to: toUtc }); }
  addRoster(body: { staffId: string; entryType: string; leaveType?: string; startsUtc: string; endsUtc: string }): Promise<RosterEntryDto> {
    return this.send('POST', '/roster', body);
  }
  moveRoster(id: string, v: number, action: 'publish' | 'approve' | 'reject' | 'cancel', reason?: string): Promise<RosterEntryDto> {
    return this.send('POST', `/roster/${id}/${action}`, { reason }, { rowVersion: v });
  }

  /* stock */
  items(): Promise<readonly ItemDto[]> { return this.get('/inventory/items'); }
  balances(query: { locationId?: string; variantId?: string; low?: boolean } = {}): Promise<readonly BalanceDto[]> { return this.get('/inventory/balances', query); }
  ledger(variantId?: string): Promise<readonly LedgerEntryDto[]> { return this.get('/inventory/ledger', { variantId, limit: 50 }); }
  move(key: string, body: { variantId: string; locationId: string; movementType: string; quantity: number; stockState?: string; reasonCode?: string }): Promise<{ entries: readonly LedgerEntryDto[] }> {
    return this.send('POST', '/inventory/movements', body, { key });
  }
  transfer(key: string, body: { variantId: string; fromLocationId: string; toLocationId: string; quantity: number; stockState?: string }): Promise<{ entries: readonly LedgerEntryDto[] }> {
    return this.send('POST', '/inventory/transfers', body, { key });
  }
  changeState(key: string, body: { variantId: string; locationId: string; fromState: string; toState: string; quantity: number }): Promise<{ entries: readonly LedgerEntryDto[] }> {
    return this.send('POST', '/inventory/state-changes', body, { key });
  }
  counts(open = true): Promise<readonly CountDto[]> { return this.get('/inventory/counts', { open }); }
  openCount(variantId: string, locationId: string, stockState: string): Promise<CountDto> {
    return this.send('POST', '/inventory/counts', { variantId, locationId, stockState });
  }
  recordCount(id: string, v: number, quantity: number, recount: boolean, reasonCode?: string): Promise<CountDto> {
    return this.send('POST', `/inventory/counts/${id}/${recount ? 'recount' : 'record'}`, { quantity, reasonCode }, { rowVersion: v });
  }
  approveCount(id: string, v: number): Promise<CountDto> { return this.send('POST', `/inventory/counts/${id}/approve`, {}, { rowVersion: v }); }
  laundry(): Promise<readonly LaundryBatchDto[]> { return this.get('/inventory/laundry'); }
  dispatchLaundry(key: string, body: { dispatchLocationId: string; returnLocationId?: string; lines: { variantId: string; quantity: number }[] }): Promise<LaundryBatchDto> {
    return this.send('POST', '/inventory/laundry', body, { key });
  }
  receiveLaundry(id: string, v: number, lines: { variantId: string; quantity: number; lost?: number }[]): Promise<LaundryBatchDto> {
    return this.send('POST', `/inventory/laundry/${id}/receive`, { lines }, { rowVersion: v });
  }
  variantsOf(items: readonly ItemDto[]): readonly (VariantDto & { itemName: string })[] {
    return items.flatMap((i) => (i.variants ?? []).map((v) => ({ ...v, itemName: i.itemName })));
  }

  /* governed settings */
  settings(key?: string): Promise<readonly SettingDto[]> { return this.get('/settings', { key }); }
  proposeSetting(body: { settingKey: string; value: unknown; reason: string; propertyOnly?: boolean; effectiveFrom?: string }): Promise<SettingDto> {
    return this.send('POST', '/settings', body);
  }
  decideSetting(id: string, v: number, approve: boolean, reason?: string): Promise<SettingDto> {
    return this.send('POST', `/settings/${id}/${approve ? 'approve' : 'reject'}`, { reason }, { rowVersion: v });
  }

  private get<T>(path: string, q: Q = {}): Promise<T> {
    const params = Object.fromEntries(Object.entries(q).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => [k, String(v)]));
    return firstValueFrom(this.http.get<T>(`${this.base}${path}`, { params }));
  }

  private send<T>(method: 'POST' | 'PUT' | 'PATCH', path: string, body: unknown, o: Opts = {}): Promise<T> {
    const headers: Record<string, string> = {};
    if (o.rowVersion !== undefined) headers['If-Match'] = `"${o.rowVersion}"`;
    if (o.key) headers['Idempotency-Key'] = o.key;
    return firstValueFrom(this.http.request<T>(method, `${this.base}${path}`, { body, headers }));
  }
}

export const newKey = (): string => `web-${crypto.randomUUID()}`;
