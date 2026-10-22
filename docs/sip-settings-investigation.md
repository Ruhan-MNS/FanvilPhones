# SIP settings investigation — 22 October 2026

## Findings

The previous implementation submitted the handset's complete `CheckBoxManager` while omitting every checked option except activation. Fanvil interprets those missing checkboxes as unchecked. This could disable authentication, NAT/rport, VPN, encryption and other configured behavior during an identity-only push. It also replaced Authentication User whenever an extension was supplied, forced every request onto line 1, and could accept an HTTP success without verifying the actual saved configuration or registration.

A read-only capture from the authorized test phone, `192.168.55.136`, showed populated input values in `lines.htm`. Its magicMark endpoint supplies registration status, rather than the SIP input values. The previous comment claiming that all SIP values were JavaScript-populated was incorrect for this firmware. The selected line belongs to a separate selector form; the SIP Apply form contains an empty JavaScript line-switch helper. Combining them or selecting the first option is unsafe.

A minimal partial Apply was tested with a temporary display-name edit. The phone returned HTTP 200 and silently left the name unchanged. The new read-back check caught this. A complete, populated form with all checked options retained successfully applied the same edit while omitting masked password fields.

## Implemented behavior

- Push opens an editor populated from the actual handset, with independent Username and Authentication User fields.
- Existing passwords remain write-only: a blank replacement preserves the secret. Replacement encoding follows `formPswdEncryption` in `comm.js` and the single-byte `base64encode` helper in `dialog.js`. Unsupported password characters are rejected before any write.
- Session-only line selection is verified before reading and writing; unsupported or incomplete responses stop the operation.
- A fresh pre-write capture must match the editor's configuration fingerprint. Same-IP sync-service operations are serialized within the application process.
- Apply preserves captured fields, codecs, and checkbox states. Password masks are never echoed. The Apply POST is sent once.
- Read-back must match the requested values and preserved non-password settings. Active lines must report Registered twice, a second apart, within the bounded polling period.
- Read, push, cancellation and failure results use existing authorization, timeout, cleanup and history infrastructure. An uncertain UI outcome disables another push until a reload.

## Live verification

The authorized phone started with line 1 Registered. Its display name was temporarily changed and read back successfully. The original display name was restored. The complete captured non-password configuration fingerprint matched its starting value, and the line remained Registered. Existing SIP credentials were not re-entered or replaced.

Line 2 was inactive. A temporary display-name edit was successfully applied to that line, then restored. Its complete captured non-password fingerprint matched its starting value, and it remained inactive. No line was deliberately activated, deactivated, or assigned a different account during live tests.

Automated tests use isolated databases and a synthetic multi-line handset. They exercise checkbox and codec retention, independent authentication identity, explicit password encoding, enabled/disabled lines, physical configuration drift, silent HTTP-200 rejection, unexpected unrelated changes, malformed/login responses, no-op pushes, registration failures, authorization, cancellation, audit privacy and preservation of concurrent fleet edits. The browser workflow is checked against an isolated preview using synthetic handset data.

## Limits

The handset does not expose its SIP secret; password retention is evidenced by continued successful registration after identity-only writes. A real password replacement was not performed because the original SIP secret is unavailable for restoration. Replacement encoding is covered with synthetic protocol tests. No changes were made to the PBX.

The handset protocol has no atomic compare-and-swap. The application checks for drift immediately before Apply and verifies the result afterward; another application instance or the physical web UI could still race that interval. Registration is observed over a bounded period and can change later due to PBX or network conditions. A stopped or failed verification does not roll back settings that the handset already applied.

Final verification: the Release build completed without warnings or errors, 163 automated tests passed (two explicit live-database checks were skipped), and all four loading-feedback checks passed. The isolated browser test verified auto-fill, editing and applying, verified values after success, and the reload requirement after changing lines.

Source changes require deployment/restart before they affect the running production application. No database migration is required.
