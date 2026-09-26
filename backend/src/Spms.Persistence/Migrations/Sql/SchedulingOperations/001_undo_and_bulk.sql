-- CON-006 undo: where the appointment was before a committed reassign, and
-- when (once) it was put back.
ALTER TABLE scheduling.schedule_change_proposal
    ADD COLUMN previous_start timestamptz,
    ADD COLUMN previous_provider_id uuid,
    ADD COLUMN previous_room_id uuid,
    ADD COLUMN undone_at timestamptz,
    ADD CONSTRAINT schedule_change_proposal_undo_after_commit
        CHECK (undone_at IS NULL OR (committed_at IS NOT NULL AND undone_at >= committed_at));
COMMENT ON COLUMN scheduling.schedule_change_proposal.previous_start IS 'where the appointment was before the commit: what an undo restores';
COMMENT ON COLUMN scheduling.schedule_change_proposal.undone_at IS 'set once: a move is undone at most once';
ALTER TABLE scheduling.schedule_change_proposal ADD CONSTRAINT schedule_change_proposal_previous_provider_id_fk FOREIGN KEY (tenant_id, previous_provider_id) REFERENCES workforce.staff (tenant_id, staff_id) ON DELETE RESTRICT;
CREATE INDEX schedule_change_proposal_previous_provider_id_ix ON scheduling.schedule_change_proposal (tenant_id, previous_provider_id);
ALTER TABLE scheduling.schedule_change_proposal ADD CONSTRAINT schedule_change_proposal_previous_room_id_fk FOREIGN KEY (tenant_id, property_id, previous_room_id) REFERENCES resources.resource (tenant_id, property_id, resource_id) ON DELETE RESTRICT;
CREATE INDEX schedule_change_proposal_previous_room_id_ix ON scheduling.schedule_change_proposal (tenant_id, property_id, previous_room_id);

-- Bulk move swaps rooms inside one transaction: the room exclusion becomes
-- deferrable, still checked per statement unless a bulk move defers it.
ALTER TABLE scheduling.appointment DROP CONSTRAINT appointment_room_no_overlap;
ALTER TABLE scheduling.appointment ADD CONSTRAINT appointment_room_no_overlap
    EXCLUDE USING gist (tenant_id WITH =, property_id WITH =, room_id WITH =,
                        tstzrange(start_at, end_at, '[)') WITH &&)
    WHERE (room_id IS NOT NULL AND status NOT IN ('Cancelled', 'NoShow'))
    DEFERRABLE INITIALLY IMMEDIATE;
