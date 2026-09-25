-- 900 security: row-level security and grants. Generated; do not edit.

GRANT USAGE ON SCHEMA core TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA catalog TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA resources TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA workforce TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA guest TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA scheduling TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA intake TO spms_intake, spms_erasure;
GRANT USAGE ON SCHEMA inventory TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA commerce TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA messaging TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA reporting TO spms_app, spms_erasure;
GRANT USAGE ON SCHEMA reporting TO spms_reporting;
GRANT USAGE ON SCHEMA core, guest, catalog, workforce, scheduling TO spms_intake;

ALTER TABLE core.property ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.property FORCE ROW LEVEL SECURITY;
CREATE POLICY property_scope ON core.property USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE core.principal ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.principal FORCE ROW LEVEL SECURITY;
CREATE POLICY principal_scope ON core.principal USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE core.principal_login ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.principal_login FORCE ROW LEVEL SECURITY;
CREATE POLICY principal_login_scope ON core.principal_login USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE core.device_registration ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.device_registration FORCE ROW LEVEL SECURITY;
CREATE POLICY device_registration_scope ON core.device_registration USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE core.capability_ownership ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.capability_ownership FORCE ROW LEVEL SECURITY;
CREATE POLICY capability_ownership_scope ON core.capability_ownership USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE core.setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.setting FORCE ROW LEVEL SECURITY;
CREATE POLICY setting_scope ON core.setting USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE core.code_list ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.code_list FORCE ROW LEVEL SECURITY;
CREATE POLICY code_list_scope ON core.code_list USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE core.audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.audit_event FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_event_scope ON core.audit_event USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE core.audit_seal ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.audit_seal FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_seal_scope ON core.audit_seal USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE core.event_outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.event_outbox FORCE ROW LEVEL SECURITY;
CREATE POLICY event_outbox_scope ON core.event_outbox USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE core.event_inbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.event_inbox FORCE ROW LEVEL SECURITY;
CREATE POLICY event_inbox_scope ON core.event_inbox USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE core.idempotency_record ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.idempotency_record FORCE ROW LEVEL SECURITY;
CREATE POLICY idempotency_record_scope ON core.idempotency_record USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE core.external_mapping ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.external_mapping FORCE ROW LEVEL SECURITY;
CREATE POLICY external_mapping_scope ON core.external_mapping USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE core.legal_hold ENABLE ROW LEVEL SECURITY;
ALTER TABLE core.legal_hold FORCE ROW LEVEL SECURITY;
CREATE POLICY legal_hold_scope ON core.legal_hold USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE catalog.service ENABLE ROW LEVEL SECURITY;
ALTER TABLE catalog.service FORCE ROW LEVEL SECURITY;
CREATE POLICY service_scope ON catalog.service USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE catalog.property_service ENABLE ROW LEVEL SECURITY;
ALTER TABLE catalog.property_service FORCE ROW LEVEL SECURITY;
CREATE POLICY property_service_scope ON catalog.property_service USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE catalog.service_option_rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE catalog.service_option_rule FORCE ROW LEVEL SECURITY;
CREATE POLICY service_option_rule_scope ON catalog.service_option_rule USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE catalog.tax_rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE catalog.tax_rule FORCE ROW LEVEL SECURITY;
CREATE POLICY tax_rule_scope ON catalog.tax_rule USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE resources.location ENABLE ROW LEVEL SECURITY;
ALTER TABLE resources.location FORCE ROW LEVEL SECURITY;
CREATE POLICY location_scope ON resources.location USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE resources.resource ENABLE ROW LEVEL SECURITY;
ALTER TABLE resources.resource FORCE ROW LEVEL SECURITY;
CREATE POLICY resource_scope ON resources.resource USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE resources.maintenance_window ENABLE ROW LEVEL SECURITY;
ALTER TABLE resources.maintenance_window FORCE ROW LEVEL SECURITY;
CREATE POLICY maintenance_window_scope ON resources.maintenance_window USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE workforce.staff ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.staff FORCE ROW LEVEL SECURITY;
CREATE POLICY staff_scope ON workforce.staff USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE workforce.spa_service_provider ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.spa_service_provider FORCE ROW LEVEL SECURITY;
CREATE POLICY spa_service_provider_scope ON workforce.spa_service_provider USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE workforce.staff_role_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.staff_role_assignment FORCE ROW LEVEL SECURITY;
CREATE POLICY staff_role_assignment_scope ON workforce.staff_role_assignment USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE workforce.credential ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.credential FORCE ROW LEVEL SECURITY;
CREATE POLICY credential_scope ON workforce.credential USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE workforce.staff_document ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.staff_document FORCE ROW LEVEL SECURITY;
CREATE POLICY staff_document_scope ON workforce.staff_document USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE workforce.staff_qualification ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.staff_qualification FORCE ROW LEVEL SECURITY;
CREATE POLICY staff_qualification_scope ON workforce.staff_qualification USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE workforce.work_schedule ENABLE ROW LEVEL SECURITY;
ALTER TABLE workforce.work_schedule FORCE ROW LEVEL SECURITY;
CREATE POLICY work_schedule_scope ON workforce.work_schedule USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE guest.guest ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.guest FORCE ROW LEVEL SECURITY;
CREATE POLICY guest_scope ON guest.guest USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE guest.guest_contact_point ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.guest_contact_point FORCE ROW LEVEL SECURITY;
CREATE POLICY guest_contact_point_scope ON guest.guest_contact_point USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE guest.guest_relationship ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.guest_relationship FORCE ROW LEVEL SECURITY;
CREATE POLICY guest_relationship_scope ON guest.guest_relationship USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE guest.guest_merge_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.guest_merge_case FORCE ROW LEVEL SECURITY;
CREATE POLICY guest_merge_case_scope ON guest.guest_merge_case USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE guest.delegated_authority ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.delegated_authority FORCE ROW LEVEL SECURITY;
CREATE POLICY delegated_authority_scope ON guest.delegated_authority USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE guest.consent_record ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.consent_record FORCE ROW LEVEL SECURITY;
CREATE POLICY consent_record_scope ON guest.consent_record USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE guest.privacy_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.privacy_request FORCE ROW LEVEL SECURITY;
CREATE POLICY privacy_request_scope ON guest.privacy_request USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE guest.guest_magic_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE guest.guest_magic_link FORCE ROW LEVEL SECURITY;
CREATE POLICY guest_magic_link_scope ON guest.guest_magic_link USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE scheduling.visit ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.visit FORCE ROW LEVEL SECURITY;
CREATE POLICY visit_scope ON scheduling.visit USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE scheduling.appointment ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.appointment FORCE ROW LEVEL SECURITY;
CREATE POLICY appointment_scope ON scheduling.appointment USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE scheduling.schedule_change_proposal ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.schedule_change_proposal FORCE ROW LEVEL SECURITY;
CREATE POLICY schedule_change_proposal_scope ON scheduling.schedule_change_proposal USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE scheduling.waitlist_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.waitlist_entry FORCE ROW LEVEL SECURITY;
CREATE POLICY waitlist_entry_scope ON scheduling.waitlist_entry USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE scheduling.turnaround_task ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.turnaround_task FORCE ROW LEVEL SECURITY;
CREATE POLICY turnaround_task_scope ON scheduling.turnaround_task USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE scheduling.visit_exception ENABLE ROW LEVEL SECURITY;
ALTER TABLE scheduling.visit_exception FORCE ROW LEVEL SECURITY;
CREATE POLICY visit_exception_scope ON scheduling.visit_exception USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE intake.form_definition ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.form_definition FORCE ROW LEVEL SECURITY;
CREATE POLICY form_definition_scope ON intake.form_definition USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE intake.intake_submission ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.intake_submission FORCE ROW LEVEL SECURITY;
CREATE POLICY intake_submission_scope ON intake.intake_submission USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE intake.treatment_note ENABLE ROW LEVEL SECURITY;
ALTER TABLE intake.treatment_note FORCE ROW LEVEL SECURITY;
CREATE POLICY treatment_note_scope ON intake.treatment_note USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE inventory.inventory_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.inventory_item FORCE ROW LEVEL SECURITY;
CREATE POLICY inventory_item_scope ON inventory.inventory_item USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE inventory.inventory_item_variant ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.inventory_item_variant FORCE ROW LEVEL SECURITY;
CREATE POLICY inventory_item_variant_scope ON inventory.inventory_item_variant USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE inventory.inventory_location_balance ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.inventory_location_balance FORCE ROW LEVEL SECURITY;
CREATE POLICY inventory_location_balance_scope ON inventory.inventory_location_balance USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE inventory.inventory_ledger_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.inventory_ledger_entry FORCE ROW LEVEL SECURITY;
CREATE POLICY inventory_ledger_entry_scope ON inventory.inventory_ledger_entry USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE inventory.laundry_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.laundry_batch FORCE ROW LEVEL SECURITY;
CREATE POLICY laundry_batch_scope ON inventory.laundry_batch USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE inventory.service_supply ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.service_supply FORCE ROW LEVEL SECURITY;
CREATE POLICY service_supply_scope ON inventory.service_supply USING (tenant_id = (SELECT core.current_tenant_id())) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()));
ALTER TABLE inventory.stock_count ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory.stock_count FORCE ROW LEVEL SECURITY;
CREATE POLICY stock_count_scope ON inventory.stock_count USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE commerce.commerce_order ENABLE ROW LEVEL SECURITY;
ALTER TABLE commerce.commerce_order FORCE ROW LEVEL SECURITY;
CREATE POLICY commerce_order_scope ON commerce.commerce_order USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE commerce.order_line ENABLE ROW LEVEL SECURITY;
ALTER TABLE commerce.order_line FORCE ROW LEVEL SECURITY;
CREATE POLICY order_line_scope ON commerce.order_line USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE commerce.payment_intent ENABLE ROW LEVEL SECURITY;
ALTER TABLE commerce.payment_intent FORCE ROW LEVEL SECURITY;
CREATE POLICY payment_intent_scope ON commerce.payment_intent USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE commerce.payment_transaction ENABLE ROW LEVEL SECURITY;
ALTER TABLE commerce.payment_transaction FORCE ROW LEVEL SECURITY;
CREATE POLICY payment_transaction_scope ON commerce.payment_transaction USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE commerce.commerce_reference ENABLE ROW LEVEL SECURITY;
ALTER TABLE commerce.commerce_reference FORCE ROW LEVEL SECURITY;
CREATE POLICY commerce_reference_scope ON commerce.commerce_reference USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE messaging.message_template ENABLE ROW LEVEL SECURITY;
ALTER TABLE messaging.message_template FORCE ROW LEVEL SECURITY;
CREATE POLICY message_template_scope ON messaging.message_template USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));
ALTER TABLE messaging.scheduled_message ENABLE ROW LEVEL SECURITY;
ALTER TABLE messaging.scheduled_message FORCE ROW LEVEL SECURITY;
CREATE POLICY scheduled_message_scope ON messaging.scheduled_message USING (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND property_id = ANY ((SELECT core.current_property_ids())::uuid[]));
ALTER TABLE reporting.report_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE reporting.report_run FORCE ROW LEVEL SECURITY;
CREATE POLICY report_run_scope ON reporting.report_run USING (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))) WITH CHECK (tenant_id = (SELECT core.current_tenant_id()) AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[])));

GRANT SELECT ON core.tenant TO spms_app;
GRANT SELECT, INSERT, UPDATE ON core.property TO spms_app;
GRANT SELECT ON core.property TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.principal TO spms_app;
GRANT SELECT ON core.principal TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.principal_login TO spms_app;
GRANT SELECT ON core.principal_login TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.device_registration TO spms_app;
GRANT SELECT ON core.device_registration TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.capability_ownership TO spms_app;
GRANT SELECT ON core.capability_ownership TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.setting TO spms_app;
GRANT SELECT ON core.setting TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.code_list TO spms_app;
GRANT SELECT ON core.code_list TO spms_erasure;
GRANT SELECT, INSERT ON core.audit_event TO spms_app;
GRANT SELECT ON core.audit_event TO spms_erasure;
GRANT SELECT, INSERT ON core.audit_seal TO spms_app;
GRANT SELECT ON core.audit_seal TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.event_outbox TO spms_app;
GRANT SELECT ON core.event_outbox TO spms_erasure;
GRANT SELECT, INSERT ON core.event_inbox TO spms_app;
GRANT SELECT ON core.event_inbox TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.idempotency_record TO spms_app;
GRANT SELECT ON core.idempotency_record TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.external_mapping TO spms_app;
GRANT SELECT ON core.external_mapping TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON core.legal_hold TO spms_app;
GRANT SELECT ON core.legal_hold TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON catalog.service TO spms_app;
GRANT SELECT ON catalog.service TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON catalog.property_service TO spms_app;
GRANT SELECT ON catalog.property_service TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON catalog.service_option_rule TO spms_app;
GRANT SELECT ON catalog.service_option_rule TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON catalog.tax_rule TO spms_app;
GRANT SELECT ON catalog.tax_rule TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON resources.location TO spms_app;
GRANT SELECT ON resources.location TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON resources.resource TO spms_app;
GRANT SELECT ON resources.resource TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON resources.maintenance_window TO spms_app;
GRANT SELECT ON resources.maintenance_window TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.staff TO spms_app;
GRANT SELECT ON workforce.staff TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.spa_service_provider TO spms_app;
GRANT SELECT ON workforce.spa_service_provider TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.staff_role_assignment TO spms_app;
GRANT SELECT ON workforce.staff_role_assignment TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.credential TO spms_app;
GRANT SELECT ON workforce.credential TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.staff_document TO spms_app;
GRANT SELECT ON workforce.staff_document TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.staff_qualification TO spms_app;
GRANT SELECT ON workforce.staff_qualification TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON workforce.work_schedule TO spms_app;
GRANT SELECT ON workforce.work_schedule TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.guest TO spms_app;
GRANT SELECT ON guest.guest TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.guest_contact_point TO spms_app;
GRANT SELECT ON guest.guest_contact_point TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.guest_relationship TO spms_app;
GRANT SELECT ON guest.guest_relationship TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.guest_merge_case TO spms_app;
GRANT SELECT ON guest.guest_merge_case TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.delegated_authority TO spms_app;
GRANT SELECT ON guest.delegated_authority TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.consent_record TO spms_app;
GRANT SELECT ON guest.consent_record TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.privacy_request TO spms_app;
GRANT SELECT ON guest.privacy_request TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON guest.guest_magic_link TO spms_app;
GRANT SELECT ON guest.guest_magic_link TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON scheduling.visit TO spms_app;
GRANT SELECT ON scheduling.visit TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON scheduling.appointment TO spms_app;
GRANT SELECT ON scheduling.appointment TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON scheduling.schedule_change_proposal TO spms_app;
GRANT SELECT ON scheduling.schedule_change_proposal TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON scheduling.waitlist_entry TO spms_app;
GRANT SELECT ON scheduling.waitlist_entry TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON scheduling.turnaround_task TO spms_app;
GRANT SELECT ON scheduling.turnaround_task TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON scheduling.visit_exception TO spms_app;
GRANT SELECT ON scheduling.visit_exception TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON intake.form_definition TO spms_intake;
GRANT SELECT, INSERT, UPDATE ON intake.intake_submission TO spms_intake;
GRANT SELECT, INSERT ON intake.treatment_note TO spms_intake;
GRANT SELECT, INSERT, UPDATE ON inventory.inventory_item TO spms_app;
GRANT SELECT ON inventory.inventory_item TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON inventory.inventory_item_variant TO spms_app;
GRANT SELECT ON inventory.inventory_item_variant TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON inventory.inventory_location_balance TO spms_app;
GRANT SELECT ON inventory.inventory_location_balance TO spms_erasure;
GRANT SELECT, INSERT ON inventory.inventory_ledger_entry TO spms_app;
GRANT SELECT ON inventory.inventory_ledger_entry TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON inventory.laundry_batch TO spms_app;
GRANT SELECT ON inventory.laundry_batch TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON inventory.service_supply TO spms_app;
GRANT SELECT ON inventory.service_supply TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON inventory.stock_count TO spms_app;
GRANT SELECT ON inventory.stock_count TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON commerce.commerce_order TO spms_app;
GRANT SELECT ON commerce.commerce_order TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON commerce.order_line TO spms_app;
GRANT SELECT ON commerce.order_line TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON commerce.payment_intent TO spms_app;
GRANT SELECT ON commerce.payment_intent TO spms_erasure;
GRANT SELECT, INSERT ON commerce.payment_transaction TO spms_app;
GRANT SELECT ON commerce.payment_transaction TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON commerce.commerce_reference TO spms_app;
GRANT SELECT ON commerce.commerce_reference TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON messaging.message_template TO spms_app;
GRANT SELECT ON messaging.message_template TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON messaging.scheduled_message TO spms_app;
GRANT SELECT ON messaging.scheduled_message TO spms_erasure;
GRANT SELECT, INSERT, UPDATE ON reporting.report_run TO spms_app;
GRANT SELECT ON reporting.report_run TO spms_reporting;
GRANT SELECT ON reporting.report_run TO spms_erasure;

-- ---------------------------------------------------------------------------
-- Identity resolution before a tenant is known.
-- ---------------------------------------------------------------------------
-- Authentication yields (issuer, subject) or a magic-link hash; nothing yet says
-- which tenant the caller belongs to, so RLS would hide every row. These narrow
-- SECURITY DEFINER functions are owned by spms_definer (BYPASSRLS, and
-- SELECT on the identity tables only), pin search_path, and return ids only.
-- Resolve an authenticated IdP subject to a principal before any tenant is set.
CREATE FUNCTION core.resolve_principal(p_issuer text, p_subject text)
RETURNS TABLE (principal_id uuid, tenant_id uuid, principal_type text, status text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT p.principal_id, p.tenant_id, p.principal_type, p.status
      FROM core.principal_login l
      JOIN core.principal p ON p.tenant_id = l.tenant_id AND p.principal_id = l.principal_id
     WHERE l.idp_issuer = p_issuer AND l.idp_subject = p_subject AND l.status = 'Active'
$$;

-- Consume a guest magic link: single use, unexpired, unrevoked. Atomic, so two
-- concurrent clicks cannot both succeed.
CREATE FUNCTION guest.resolve_magic_link(p_token_hash bytea)
RETURNS TABLE (guest_id uuid, tenant_id uuid, principal_id uuid, purpose text,
               scope_entity_type text, scope_entity_id uuid)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    UPDATE guest.guest_magic_link l
       SET consumed_at = now()
     WHERE l.token_hash = p_token_hash
       AND l.consumed_at IS NULL AND l.revoked_at IS NULL AND l.expires_at > now()
    RETURNING l.guest_id, l.tenant_id, l.principal_id, l.purpose, l.scope_entity_type, l.scope_entity_id
$$;

-- CON-005 is tenant-wide: a guest cannot be in two treatments at once at any of
-- the tenant's properties. Request scope covers one property, so this function
-- answers for the whole CURRENT tenant - intervals only, no appointment detail.
CREATE FUNCTION scheduling.guest_busy_intervals(p_guest_id uuid, p_from timestamptz, p_to timestamptz)
RETURNS TABLE (property_id uuid, appointment_id uuid, start_at timestamptz, end_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
    SELECT a.property_id, a.appointment_id, a.start_at, a.end_at
      FROM scheduling.appointment a
     WHERE a.tenant_id = core.current_tenant_id()
       AND a.guest_id = p_guest_id
       AND a.status NOT IN ('Cancelled', 'NoShow')
       AND tstzrange(a.start_at, a.end_at, '[)') && tstzrange(p_from, p_to, '[)')
$$;

GRANT USAGE ON SCHEMA core, guest, scheduling TO spms_definer;
GRANT SELECT ON core.principal, core.principal_login TO spms_definer;
GRANT SELECT, UPDATE (consumed_at) ON guest.guest_magic_link TO spms_definer;
GRANT SELECT ON scheduling.appointment TO spms_definer;
REVOKE ALL ON FUNCTION core.resolve_principal(text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION guest.resolve_magic_link(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.resolve_principal(text, text) TO spms_app;
GRANT EXECUTE ON FUNCTION guest.resolve_magic_link(bytea) TO spms_app;
REVOKE ALL ON FUNCTION scheduling.guest_busy_intervals(uuid, timestamptz, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION scheduling.guest_busy_intervals(uuid, timestamptz, timestamptz) TO spms_app;

-- Ownership moves last: ACL entries granted above transfer to the new owner.
GRANT CREATE ON SCHEMA core, guest, scheduling TO spms_definer;
ALTER FUNCTION core.resolve_principal(text, text) OWNER TO spms_definer;
ALTER FUNCTION guest.resolve_magic_link(bytea) OWNER TO spms_definer;
ALTER FUNCTION scheduling.guest_busy_intervals(uuid, timestamptz, timestamptz) OWNER TO spms_definer;
REVOKE CREATE ON SCHEMA core, guest, scheduling FROM spms_definer;

-- ---------------------------------------------------------------------------
-- Outbox publisher: one table, across tenants.
-- ---------------------------------------------------------------------------
GRANT USAGE ON SCHEMA core TO spms_outbox;
GRANT SELECT, UPDATE (published_at, attempt_count, next_attempt_at, last_error) ON core.event_outbox TO spms_outbox;

-- ---------------------------------------------------------------------------
-- Redaction (guest erasure). The only path that may change append-only rows.
-- ---------------------------------------------------------------------------
CREATE FUNCTION core.redact_audit_subject(p_entity_type text, p_entity_id uuid) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE n bigint;
BEGIN
    PERFORM set_config('spms.redacting', 'on', true);
    UPDATE core.audit_event
       SET before_data = NULL, after_data = NULL, reason_text = NULL
     WHERE tenant_id = core.current_tenant_id()
       AND entity_type = p_entity_type AND entity_id = p_entity_id;
    GET DIAGNOSTICS n = ROW_COUNT;
    PERFORM set_config('spms.redacting', 'off', true);
    RETURN n;
END $$;
REVOKE ALL ON FUNCTION core.redact_audit_subject(text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.redact_audit_subject(text, uuid) TO spms_erasure;

-- Controlled deletion of transient rows: lines of a Draft order (a cart) and
-- expired idempotency claims. order_line refuses DELETE once its order leaves Draft. Everything else is status + audit, never DELETE (no hard delete).
GRANT DELETE ON commerce.order_line, core.idempotency_record TO spms_app;

-- Partitions are reached only through their parent, whose grants and policies
-- apply; no role other than the owner holds privileges on a partition itself.

