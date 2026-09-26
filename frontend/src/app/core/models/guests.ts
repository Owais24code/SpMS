/** Wire shapes for guests, merges, delegation, consent, privacy and intake (GuestProfileEndpoints.cs, IntakeEndpoints.cs). */

export interface ContactDto {
  readonly contactPointId: string;
  readonly contactType: 'Email' | 'Mobile' | 'Phone' | 'Address';
  readonly displayHint: string;
  readonly isPrimary: boolean;
  readonly verified: boolean;
  readonly status: string;
}

export interface GuestDto {
  readonly guestId: string;
  readonly displayAlias: string | null;
  readonly preferredName: string | null;
  readonly legalFirstName: string | null;
  readonly legalLastName: string | null;
  readonly publicQueueId: string | null;
  readonly locale: string | null;
  readonly homePropertyId: string | null;
  /** Whether the guest is under 18. The date of birth itself is never served. */
  readonly isMinor: boolean | null;
  readonly status: string;
  readonly mergedIntoGuestId: string | null;
  readonly preferences: Readonly<Record<string, string>>;
  readonly contacts: readonly ContactDto[];
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface GuestSearchHitDto {
  readonly guestId: string;
  readonly displayAlias: string | null;
  readonly preferredName: string | null;
  readonly legalLastName: string | null;
  readonly publicQueueId: string | null;
  readonly contacts: readonly ContactDto[];
}

export interface CreateGuestRequest {
  readonly legalFirstName?: string;
  readonly legalLastName?: string;
  readonly preferredName?: string;
  readonly locale?: string;
  readonly birthDate?: string;
  readonly email?: string;
  readonly mobile?: string;
  readonly contactsVerifiedInPerson?: boolean;
}

export interface CreateGuestResponse {
  readonly guest: GuestDto;
  readonly possibleDuplicates: readonly string[];
}

export interface MergeCaseDto {
  readonly mergeCaseId: string;
  readonly survivingGuestId: string;
  readonly duplicateGuestId: string;
  readonly confidence: number;
  readonly matchSignals: { readonly signals?: readonly string[] };
  readonly status: 'Candidate' | 'Approved' | 'Merged' | 'Rejected' | 'Split';
  readonly decisionReason: string | null;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface DelegationDto {
  readonly delegationId: string;
  readonly guestId: string;
  readonly delegateGuestId: string | null;
  readonly allowedActions: readonly string[];
  readonly informationVisibility: string;
  readonly evidenceReference: string;
  readonly effectiveToUtc: string | null;
  readonly status: string;
  readonly synced: boolean;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface ConsentDto {
  readonly consentId: string;
  readonly purpose: string;
  readonly channel: string | null;
  readonly templateId: string;
  readonly templateVersion: number;
  readonly effectiveUtc: string;
  readonly status: string;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface PrivacyRequestDto {
  readonly privacyRequestId: string;
  readonly guestId: string;
  readonly requestType: string;
  readonly dueUtc: string;
  readonly status: string;
  readonly allowedTransitions: readonly string[];
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface IntakeFieldDto {
  readonly key: string;
  readonly label: string;
  readonly type: 'boolean' | 'text' | 'select';
  readonly required: boolean;
  readonly mustBeTrue: boolean;
  readonly options: readonly string[] | null;
  readonly maxLength: number | null;
}

export interface GuestIntakeDto {
  readonly submissionId: string;
  readonly appointmentId: string | null;
  readonly serviceName: string;
  readonly appointmentStartUtc: string | null;
  readonly formTitle: string;
  readonly fields: readonly IntakeFieldDto[];
  readonly status: string;
  readonly answers: Readonly<Record<string, unknown>> | null;
  readonly submittedUtc: string | null;
  readonly editable: boolean;
  readonly rowVersion: number;
  readonly eTag: string;
}

export interface IntakeSummaryDto {
  readonly submissionId: string;
  readonly formTitle: string;
  readonly status: string;
  readonly requiresReview: boolean;
  readonly items: readonly { key: string; label: string; answer: unknown; review: boolean }[];
  readonly submittedUtc: string | null;
  readonly acknowledgedUtc: string | null;
}

export interface NoteDto {
  readonly noteId: string;
  readonly appointmentId: string;
  readonly providerStaffId: string;
  readonly templateCode: string | null;
  readonly content: string;
  readonly authoredUtc: string;
  readonly supersedesNoteId: string | null;
  readonly amendmentReason: string | null;
  readonly superseded: boolean;
}
