/**
 * Wire shapes for visits, the waitlist, room turnover, the desk's arrivals
 * list and bulk moves — mirrors of OperationsEndpoints.cs and the bulk-move
 * contract in Contracts.cs. Field names are the server's, camelCased.
 */
import type { AppointmentDto, ConflictDto } from './api';

export type VisitStatus = 'Planned' | 'Arrived' | 'InProgress' | 'Closed' | 'Cancelled' | 'NoShow';
export type VisitType = 'DayGuest' | 'HotelGuest' | 'Member' | 'Group' | 'WalkIn';

export interface VisitAppointmentDto {
  readonly appointmentId: string;
  readonly serviceName: string;
  readonly providerId: string | null;
  readonly roomId: string | null;
  readonly startUtc: string;
  readonly endUtc: string;
  readonly status: string;
  readonly rowVersion: number;
}

export interface VisitDto {
  readonly visitId: string;
  readonly visitType: VisitType;
  readonly guestAlias: string;
  readonly visitDate: string;
  readonly operatingMode: string;
  readonly status: VisitStatus;
  readonly allowedTransitions: readonly VisitStatus[];
  readonly scheduledArrivalUtc: string | null;
  readonly actualArrivalUtc: string | null;
  readonly closedUtc: string | null;
  readonly notes: string | null;
  readonly rowVersion: number;
  readonly eTag: string;
  readonly appointments: readonly VisitAppointmentDto[];
}

export type WaitlistStatus = 'Waiting' | 'Offered' | 'Accepted' | 'Expired' | 'Cancelled';

export interface WaitlistDto {
  readonly waitlistId: string;
  readonly guestAlias: string;
  readonly serviceId: string | null;
  readonly earliestUtc: string;
  readonly latestUtc: string;
  readonly providerId: string | null;
  readonly notes: string | null;
  readonly status: WaitlistStatus;
  readonly expiresUtc: string | null;
  readonly offerExpiresUtc: string | null;
  readonly acceptedAppointmentId: string | null;
  readonly createdUtc: string;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface CreateWaitlistRequest {
  readonly guestId: string;
  readonly serviceId?: string | null;
  readonly earliestUtc: string;
  readonly latestUtc: string;
  readonly providerId?: string | null;
  readonly notes?: string | null;
  readonly expiresUtc?: string | null;
}

export type TurnaroundStatus = 'Pending' | 'InProgress' | 'Completed' | 'Skipped';
export type TurnaroundResult = 'Pass' | 'Fail' | 'NeedsAttention';

export interface TurnaroundDto {
  readonly turnaroundTaskId: string;
  readonly roomId: string;
  readonly roomName: string;
  readonly appointmentId: string | null;
  readonly taskType: 'Turnover' | 'DeepClean' | 'Sanitation' | 'Restock';
  readonly dueUtc: string;
  readonly status: TurnaroundStatus;
  readonly result: TurnaroundResult | null;
  readonly checklistCode: string | null;
  readonly assignedStaffId: string | null;
  readonly completedUtc: string | null;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface ArrivalDto {
  readonly appointmentId: string;
  readonly guestAlias: string;
  readonly serviceName: string;
  readonly startUtc: string;
  readonly startLocal: string;
  readonly providerId: string | null;
  readonly roomId: string | null;
  readonly status: string;
  readonly rowVersion: number;
  readonly eTag: string;
  readonly visitId: string | null;
  readonly checkedInUtc: string | null;
  readonly roomReady: boolean;
  readonly intake: 'NotRequired' | 'Pending' | 'Complete';
  readonly deposit: 'NotTracked' | 'NotRequired' | 'Pending' | 'Settled';
}

export interface ArrivalsDto {
  readonly date: string;
  readonly timeZone: string;
  readonly items: readonly ArrivalDto[];
}

export interface BulkMoveItemRequest {
  readonly appointmentId: string;
  readonly startUtc: string;
  readonly providerId?: string | null;
  readonly roomId?: string | null;
  readonly fromRowVersion: number;
}

export interface BulkMoveItemDto {
  readonly appointmentId: string;
  readonly state: 'Ok' | 'NotFound' | 'NotReschedulable' | 'StaleVersion' | 'Duplicate';
  readonly conflicts: readonly ConflictDto[];
  readonly appointment: AppointmentDto | null;
}

export interface BulkMoveResponseDto {
  readonly outcome: 'Evaluated' | 'Committed';
  readonly commitAllowed: boolean;
  readonly requiresReason: boolean;
  readonly items: readonly BulkMoveItemDto[];
}
