import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import type {
  DeviceDto, KioskBookingDto, SearchHitDto, MappingDto, MessageDto, OutboxHealthDto, OwnershipDto, ReportCatalogDto, ReportRunDto, TemplateDto,
} from '../models/ops';

/** Messaging, reports, devices, integrations and the kiosk. One method per endpoint. */
@Injectable({ providedIn: 'root' })
export class OpsApi {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  /* messaging */
  templates(): Promise<readonly TemplateDto[]> { return this.get('/messaging/templates'); }
  draftTemplate(body: Record<string, unknown>): Promise<TemplateDto> { return this.post('/messaging/templates', body); }
  approveTemplate(id: string, v: number): Promise<TemplateDto> { return this.post(`/messaging/templates/${id}/approve`, {}, v); }
  retireTemplate(id: string, v: number): Promise<TemplateDto> { return this.post(`/messaging/templates/${id}/retire`, {}, v); }
  messages(status?: string): Promise<readonly MessageDto[]> { return this.get('/messaging/messages', status ? { status } : {}); }
  cancelMessage(id: string, v: number): Promise<MessageDto> { return this.post(`/messaging/messages/${id}/cancel`, {}, v); }

  /* reports */
  reports(): Promise<readonly ReportCatalogDto[]> { return this.get('/reports'); }
  runReport(reportId: string, date: string): Promise<ReportRunDto> { return this.post(`/reports/${reportId}/runs`, { date }); }
  exportReport(runId: string): Promise<Blob> {
    return firstValueFrom(this.http.get(`${this.base}/reports/runs/${runId}/export`, { responseType: 'blob' }));
  }

  /* devices */
  devices(): Promise<readonly DeviceDto[]> { return this.get('/devices'); }
  registerDevice(body: { deviceKind: string; deviceName: string; publicKeySpki: string }): Promise<DeviceDto> { return this.post('/devices', body); }
  decideDevice(id: string, v: number, activate: boolean): Promise<DeviceDto> { return this.post(`/devices/${id}/${activate ? 'activate' : 'revoke'}`, {}, v); }

  /* integrations */
  ownership(): Promise<readonly OwnershipDto[]> { return this.get('/integrations/ownership'); }
  proposeOwnership(capabilityCode: string, ownerSystem: string): Promise<OwnershipDto> { return this.post('/integrations/ownership', { capabilityCode, ownerSystem }); }
  decideOwnership(id: string, v: number, approve: boolean): Promise<OwnershipDto> {
    return this.post(`/integrations/ownership/${id}/${approve ? 'approve' : 'reject'}`, {}, v);
  }
  outbox(): Promise<OutboxHealthDto> { return this.get('/integrations/outbox'); }
  replay(eventId: string): Promise<{ replayed: boolean }> { return this.post(`/integrations/outbox/${eventId}/replay`, {}); }
  mappings(): Promise<readonly MappingDto[]> { return this.get('/integrations/mappings'); }

  /* universal search */
  search(q: string): Promise<{ q: string; hits: readonly SearchHitDto[] }> { return this.get('/search', { q }); }

  /* kiosk */
  kioskLookup(confirmationNumber: string, lastName: string): Promise<KioskBookingDto> { return this.post('/kiosk/lookup', { confirmationNumber, lastName }); }
  kioskCheckIn(appointmentId: string, confirmationNumber: string, lastName: string): Promise<{ status: string; message: string }> {
    return this.post('/kiosk/check-in', { appointmentId, confirmationNumber, lastName });
  }

  private get<T>(path: string, params: Record<string, string> = {}): Promise<T> {
    return firstValueFrom(this.http.get<T>(`${this.base}${path}`, { params }));
  }

  private post<T>(path: string, body: unknown, rowVersion?: number): Promise<T> {
    const headers: Record<string, string> = rowVersion === undefined ? {} : { 'If-Match': `"${rowVersion}"` };
    return firstValueFrom(this.http.post<T>(`${this.base}${path}`, body, { headers }));
  }
}

/** A fresh ECDSA P-256 device key; the public half is registered, the private half never leaves the browser. */
export async function deviceKeySpki(): Promise<string> {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify']);
  const spki = new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey));
  return btoa(String.fromCharCode(...spki));
}
