
### 8. Foundational FDA CSV (21 CFR Part 11) Compliance — Sprint 1

Add FDA Computer System Validation (CSV) audit trail support to ensure the system meets regulatory requirements for computerized systems in drug/device manufacturing. This sprint focuses on immutable audit logs, electronic signature metadata, and traceability fields required for CSV readiness.

**Exit gate:** system captures and retains audit-quality metadata on all critical state changes; audit log is append-only and verified; core entities include creator/modifier identity and change rationale; compliance metadata integrates with existing job/run/planning workflows without breaking changes.

#### Sprint 1 Tasks:

**8.1 Enhance Domain Models with CSV Metadata**
- Add `CreatedBy`, `ModifiedBy`, and `ChangeReason` string fields to: `QualityJob`, `TestPlan`, `TestRun`, `AcceptanceCriterion`, `AgentDecision`
- These fields support 21 CFR Part 11.100 (electronic signatures) and 11.10 (audit trails) requirements
- Fields are optional/stub-compatible for backward compatibility

**8.2 Create Audit Log Table for Event Sourcing**
- Add `quality_audit_log` PostgreSQL table with columns:
  - `id` (uuid, primary key, default gen_random_uuid())
  - `job_id` (text, foreign key to quality_jobs)
  - `run_id` (text, foreign key to quality_test_runs, nullable)
  - `event_type` (text, check: JobCreated/StatusChanged/PlanGenerated/ManifestApproved/ExecutionStarted/ExecutionCompleted/FailureClassified)
  - `actor` (text, the user/system that caused the event)
  - `timestamp` (timestamptz, default now())
  - `details` (jsonb, additional event-specific data)
  - ` CONSTRAINT uq_audit_log_job_timestamp UNIQUE (job_id, timestamp)` for immutability

**8.3 Populate Audit Log on Key State Changes**
- Modify `QualityJob.Create()` to log `JobCreated` event
- Modify `QualityJob.TransitionTo()` to log `StatusChanged` event on all state transitions
- Modify planning workflow to log `PlanGenerated` event when TestPlan is created
- Modify execution workflow to log `ExecutionStarted`/`ExecutionCompleted` events
- Modify failure classification to log `FailureClassified` event

**8.4 Integrate CSV Metadata with Existing Workflows**
- Update API endpoints to accept and propagate `CreatedBy`/`ModifiedBy` values
- Update Command Center to display CSV metadata in traceability views
- Ensure existing job/revision tracking continues to work alongside audit log
- Verify stub mode still functions without requiring CSV fields

**8.5 CSV Compliance Verification**
- Run existing test suite (`dotnet test -c Release`) to ensure no regressions
- Verify PostgreSQL migration scripts apply cleanly
- Confirm MinIO artifact retention policies remain compatible
- Test that CLI `get --id`, `execution-approve`, and `classify-failure` all propagate actor identity where available

**Sprint 1 Exit Criteria:**
- [ ] Domain model enhancements merged and types compiled without errors
- [ ] Audit log table migration applied to development PostgreSQL instance
- [ ] Audit events logged on: job creation, status transitions, plan generation, execution start/completion, failure classification
- [ ] CreatedBy/ModifiedBy fields present on core entities and propagate through API
- [ ] Existing test suite passes (242 passed, 1 skipped baseline maintained)
- [ ] No breaking changes to stub mode or existing workflows
