/** Wire shapes for the catalogue, rooms, staff, roster, stock and governed settings. */

interface Versioned { readonly rowVersion: number; readonly eTag: string; }

export interface ServiceDto extends Versioned {
  readonly serviceId: string; readonly code: string; readonly name: string; readonly catalogType: string;
  readonly durationMinutes: number; readonly basePriceMinor: number; readonly currencyCode: string; readonly taxCode: string | null;
  readonly requiresIntake: boolean; readonly depositRequired: boolean; readonly onlineBookable: boolean;
  readonly requiredLicenseTypeCodes: readonly string[]; readonly status: 'Draft' | 'Active' | 'Inactive' | 'Retired';
}

export interface OfferingDto extends Versioned {
  readonly propertyServiceId: string; readonly serviceId: string; readonly priceMinor: number | null; readonly currencyCode: string | null;
  readonly onlineBookable: boolean | null; readonly status: 'Active' | 'Inactive';
}

export interface OfferingRowDto {
  readonly service: ServiceDto; readonly offering: OfferingDto | null; readonly offered: boolean; readonly effectivePriceMinor: number;
}

export interface RoomDto extends Versioned {
  readonly roomId: string; readonly code: string; readonly name: string; readonly resourceType: string; readonly locationId: string | null;
  readonly capacity: number; readonly accessible: boolean; readonly status: 'Active' | 'OutOfService' | 'Retired';
}

export interface ClosureDto extends Versioned {
  readonly maintenanceWindowId: string; readonly roomId: string; readonly startsUtc: string; readonly endsUtc: string;
  readonly reasonCode: string; readonly note: string | null; readonly status: string;
}

export interface LocationDto extends Versioned {
  readonly locationId: string; readonly locationCode: string; readonly locationName: string; readonly locationType: string; readonly status: string;
}

export interface RoleAssignmentDto extends Versioned {
  readonly assignmentId: string; readonly staffId: string; readonly roleCode: string; readonly propertyId: string | null; readonly tenantWide: boolean;
  readonly status: 'Proposed' | 'Active' | 'Revoked' | 'Expired'; readonly proposedBy: string | null; readonly approvedBy: string | null; readonly synced: boolean;
}

export interface StaffDto extends Versioned {
  readonly staffId: string; readonly preferredName: string; readonly principalId: string | null; readonly homePropertyId: string | null;
  readonly employmentStatus: string; readonly bookable: boolean; readonly hasSignIn: boolean;
  readonly roles?: readonly RoleAssignmentDto[]; readonly qualifiedServiceIds?: readonly string[];
}

export interface HrDto extends Versioned {
  readonly staffId: string; readonly employeeNumber: string; readonly firstName: string; readonly lastName: string; readonly workEmail: string | null;
  readonly personalEmail: string | null; readonly mobilePhone: string | null; readonly workerType: string; readonly jobTitle: string;
  readonly hireDate: string | null;
}

export interface QualificationDto extends Versioned {
  readonly qualificationId: string; readonly staffId: string; readonly serviceId: string; readonly credentialId: string | null; readonly status: string;
}

export interface CredentialDto extends Versioned {
  readonly credentialId: string; readonly staffId: string; readonly credentialKind: string; readonly licenseTypeCode: string | null;
  readonly jurisdiction: string | null; readonly numberMasked: string | null; readonly expiresAt: string | null; readonly status: string;
}

export interface RosterEntryDto extends Versioned {
  readonly workScheduleId: string; readonly staffId: string; readonly entryType: 'Shift' | 'OnCall' | 'Leave'; readonly leaveType: string | null;
  readonly startsUtc: string; readonly endsUtc: string; readonly status: string; readonly requestedBy: string | null;
}

export interface VariantDto extends Versioned {
  readonly variantId: string; readonly itemId: string; readonly variantCode: string; readonly barcode: string | null;
  readonly sellPriceMinor: number | null; readonly status: string;
}

export interface ItemDto extends Versioned {
  readonly itemId: string; readonly itemCode: string; readonly itemName: string; readonly itemKind: string; readonly reorderPoint: number;
  readonly status: string; readonly variants: readonly VariantDto[] | null;
}

export interface BalanceDto {
  readonly variantId: string; readonly locationId: string; readonly locationName: string; readonly itemName: string; readonly variantCode: string;
  readonly stockState: string; readonly onHand: number; readonly available: number; readonly reorderPoint: number;
}

export interface LedgerEntryDto {
  readonly entryId: string; readonly variantId: string; readonly locationId: string; readonly stockState: string; readonly movementType: string;
  readonly quantity: number; readonly reasonCode: string | null; readonly occurredUtc: string;
}

export interface CountDto extends Versioned {
  readonly stockCountId: string; readonly variantId: string; readonly locationId: string; readonly stockState: string;
  readonly expectedQuantity: number; readonly observedQuantity: number | null; readonly recountQuantity: number | null;
  readonly variance: number | null; readonly countedBy: string | null; readonly status: string;
}

export interface LaundryBatchDto extends Versioned {
  readonly laundryBatchId: string; readonly dispatchLocationId: string; readonly returnLocationId: string; readonly status: string;
  readonly dispatchedUtc: string | null;
}

export interface SettingDto extends Versioned {
  readonly settingId: string; readonly settingKey: string; readonly value: Record<string, unknown>; readonly propertyOnly: boolean;
  readonly deploymentScope: string; readonly reason: string | null; readonly status: 'Proposed' | 'Approved' | 'Active' | 'Superseded' | 'Rejected';
  readonly proposedBy: string | null; readonly approvedBy: string | null; readonly effectiveFrom: string; readonly effectiveTo: string | null;
}

export const ROLE_CODES = [
  'provider', 'front_desk', 'spa_manager', 'hr_compliance', 'finance', 'platform_admin', 'configuration_approver', 'scheduler',
  'housekeeping', 'inventory_manager', 'marketing', 'support', 'operations_analyst', 'executive', 'release_manager', 'security_admin',
] as const;

export const STOCK_STATES = ['Saleable', 'Clean', 'Soiled', 'InLaundry', 'Damaged', 'Quarantine'] as const;
