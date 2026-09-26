/** Wire shapes for messaging, reports, devices, integrations and the kiosk. */

interface Versioned { readonly rowVersion: number; readonly eTag: string; }

export interface TemplateDto extends Versioned {
  readonly templateId: string; readonly templateCode: string; readonly versionNumber: number; readonly channel: string; readonly locale: string;
  readonly purpose: string; readonly subject: string | null; readonly bodyTemplate: string; readonly variables: readonly string[];
  readonly triggerEvent: string | null; readonly offsetMinutes: number | null; readonly status: string; readonly authoredBy: string | null;
}

export interface MessageDto extends Versioned {
  readonly messageId: string; readonly guestId: string; readonly appointmentId: string | null; readonly templateId: string; readonly channel: string;
  readonly sendAfterUtc: string; readonly sentUtc: string | null; readonly deliveredUtc: string | null; readonly attemptCount: number;
  readonly failureCode: string | null; readonly status: string;
}

export interface ReportCatalogDto { readonly reportId: string; readonly title: string; readonly description: string; readonly requires: string; }

export interface ReportTableDto { readonly name: string; readonly columns: readonly string[]; readonly rows: readonly (readonly unknown[])[]; }

export interface ReportRunDto {
  readonly runId: string; readonly reportId: string; readonly status: string; readonly resultSha256: string | null; readonly closed: boolean;
  readonly resultIntact: boolean; readonly definitionCurrent: boolean; readonly asOfUtc: string;
  readonly result: { readonly date: string; readonly timeZone: string; readonly tables: readonly ReportTableDto[] } | null;
}

export interface DeviceDto extends Versioned {
  readonly deviceId: string; readonly deviceKind: string; readonly deviceName: string; readonly status: string;
  readonly lastSeenUtc: string | null; readonly keyFingerprint: string | null;
}

export interface OwnershipDto extends Versioned {
  readonly ownershipId: string; readonly capabilityCode: string; readonly ownerSystem: string; readonly status: string;
  readonly proposedBy: string | null; readonly approvedBy: string | null; readonly effectiveFromUtc: string; readonly effectiveToUtc: string | null;
}

export interface OutboxHealthDto {
  readonly pending: number; readonly failing: number; readonly oldestPendingUtc: string | null;
  readonly problems: readonly { eventId: string; eventType: string; attemptCount: number; lastError: string | null; occurredUtc: string }[];
}

export interface MappingDto extends Versioned {
  readonly mappingId: string; readonly entityType: string; readonly localId: string; readonly sourceSystem: string; readonly sourceKey: string; readonly status: string;
}

export interface SearchHitDto { readonly type: string; readonly id: string; readonly title: string; readonly subtitle: string | null; readonly link: string; }

export interface KioskBookingDto {
  readonly appointmentId: string; readonly serviceName: string; readonly startUtc: string; readonly status: string;
  readonly greeting: string | null; readonly canCheckIn: boolean;
}
