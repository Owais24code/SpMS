import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { OpsApi, deviceKeySpki } from '../../core/api/ops-api';
import { ToastService } from '../../core/services/toast.service';
import type { DeviceDto } from '../../core/models/ops';
import type { ApiProblem } from '../../core/models/api-problem';

/**
 * Devices against the API (SEC-013). A tablet, kiosk or desk terminal is
 * registered with a key generated in this browser (only its public half is
 * sent), activated by the manager, and revoked — immediately and for good —
 * when it is lost.
 */
@Component({
  selector: 'app-devices-live',
  standalone: true,
  imports: [PageHeader, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Operations" title="Devices" subtitle="Registered devices act only at their property, and only while active." />
    <section class="panel">
      <div class="panel__body stack">
        <div class="row">
          <select [(ngModel)]="kind" name="k" aria-label="Device kind"><option>ProviderTablet</option><option>Kiosk</option><option>FrontDesk</option></select>
          <input [(ngModel)]="name" name="n" aria-label="Device name" placeholder="Tablet — Suite 3" />
          <button type="button" class="btn btn--secondary" [disabled]="!name.trim()" (click)="register()">Register this device</button>
        </div>
        <table class="table" data-testid="devices">
          <tbody>
            @for (d of devices(); track d.deviceId) {
              <tr>
                <td>{{ d.deviceName }}</td><td>{{ d.deviceKind }}</td><td class="numeric subtle">{{ d.keyFingerprint }}</td>
                <td class="subtle">{{ d.lastSeenUtc ? 'seen ' + local(d.lastSeenUtc) : 'never seen' }}</td>
                <td><span class="badge" [class.badge--ok]="d.status === 'Active'" [class.badge--warn]="d.status === 'Pending'" [class.badge--danger]="d.status === 'Revoked'">{{ d.status }}</span></td>
                <td class="row row--end">
                  @if (d.status === 'Pending') { <button type="button" class="btn btn--ghost" (click)="decide(d, true)">Activate</button> }
                  @if (d.status !== 'Revoked') { <button type="button" class="btn btn--ghost" (click)="decide(d, false)">Revoke</button> }
                </td>
              </tr>
            } @empty { <tr><td class="subtle">No devices registered.</td></tr> }
          </tbody>
        </table>
      </div>
    </section>
  `,
  styles: [`:host { display: block; }`],
})
export class DevicesLive implements OnInit {
  private readonly api = inject(OpsApi);
  private readonly toast = inject(ToastService);
  protected readonly devices = signal<readonly DeviceDto[]>([]);
  protected kind = 'ProviderTablet';
  protected name = '';

  ngOnInit(): void { void this.load(); }
  async load(): Promise<void> { try { this.devices.set(await this.api.devices()); } catch (e) { this.fail(e); } }
  protected local(utc: string): string { return new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }); }

  protected async register(): Promise<void> {
    try {
      const d = await this.api.registerDevice({ deviceKind: this.kind, deviceName: this.name.trim(), publicKeySpki: await deviceKeySpki() });
      this.toast.success('Device registered', `${d.deviceName} waits for activation.`);
      this.name = '';
      await this.load();
    } catch (e) { this.fail(e); }
  }

  protected async decide(d: DeviceDto, activate: boolean): Promise<void> {
    try {
      await this.api.decideDevice(d.deviceId, d.rowVersion, activate);
      this.toast.success(activate ? 'Device activated' : 'Device revoked', d.deviceName);
      await this.load();
    } catch (e) { this.fail(e); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
