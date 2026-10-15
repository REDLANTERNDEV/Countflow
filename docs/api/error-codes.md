# Error codes

Every failure is an RFC 9457 problem document carrying a stable `code`:

```json
{ "title": "Conflict", "status": 409,
  "detail": "There is not enough unreserved stock in that holding.",
  "code": "inventory.insufficient_available_stock",
  "traceId": "00-0e18d…-d16c4…-01" }
```

**Branch on `code`.** `detail` is English prose for a human reading a log and is
reworded freely; the console never displays it, translating the code instead. New
codes appear without notice — treat one you do not recognise as a generic failure
of its HTTP status.

| Status | Meaning |
|---|---|
| `400` | The request is wrong. Fixing it means changing what you sent. |
| `401` | No usable credential. |
| `403` | A valid credential without the role for this. |
| `404` | Nothing with that identifier. |
| `409` | The request is well-formed and the warehouse's state refuses it. Retrying unchanged will not help; the state has to change first. |
| `429` | Over the rate limit. `Retry-After` says how long. |
| `500` | Ours. Quote the `traceId`. |

The `409`/`400` split is worth internalising: `400` is your bug, `409` is the
world. `inventory.insufficient_stock` is not a malformed request — it is the shelf
being empty.

## Transport

These come from the framework rather than from a handler, and they are the ones
you will meet first. They carry a `code` for the same reason everything else does
— a rule that holds everywhere except at the first failure an integrator hits is
not a rule they can write code against.

| Code | Status | Means |
|---|---|---|
| `auth.credential_required` | 401 | No credential, or one that did not check out. |
| `auth.role_required` | 403 | A valid credential without the role this endpoint needs. Getting a new credential will not help; the account needs the role. |
| `http.not_found` | 404 | No such path. Check the `/api/v1` prefix first — an unversioned path is a `404`, deliberately. |
| `http.method_not_allowed` | 405 | |
| `http.malformed_request` | 400 | The request would not bind: a type, an enum member sent as a number, a missing required field, an explicit `null` where the schema is not nullable, or a required query parameter that is absent or unparseable. Two rules cover the body. **A field listed under `required` must be present and non-null.** **A nullable field may be omitted or sent as `null`, interchangeably** — optional fields are nullable in the schema except the few given a single non-`null` type (`maxTasks`, say), which may be omitted but not nulled. |
| `http.unsupported_media_type` | 415 | Send `Content-Type: application/json`. |
| `http.payload_too_large` | 413 | |
| `http.unexpected_error` | 500 | Ours. Quote the `traceId`. |

## Cross-cutting

| Code | Status | Means |
|---|---|---|
| `rate_limit.exceeded` | 429 | This credential has made too many requests. |
| `*.idempotency_key_required` | 400 | A write arrived without an `Idempotency-Key` header. See [writing.md](writing.md). |
| `*.unknown_actor` | 401 | The credential carries no usable user identity. |

## Authentication and credentials

| Code | Status | Means |
|---|---|---|
| `auth.invalid_credentials` | 401 | Wrong password, no such user, or a service account trying to log in. Deliberately indistinguishable. |
| `auth.invalid_refresh_token` | 401 | Expired, already rotated, or revoked. Sign in again. |
| `auth.user_not_found` | 404 | The account behind a valid token no longer exists. |
| `identity.service_account_not_found` | 404 | No integration with that id. |
| `identity.api_key_not_found` | 404 | No key with that id. |
| `identity.not_a_service_account` | 400 | You asked for a key against a person's account. |
| `identity.account_name_taken` | 409 | |
| `identity.account_inactive` | 409 | The integration is disabled. |
| `identity.unknown_role` | 400 | Roles are `Admin`, `Supervisor`, `Operator`. |
| `identity.no_roles` | 400 | An integration with no roles could do nothing. |
| `identity.name_required` | 400 | |
| `identity.key_name_required` | 400 | Name the key, so somebody can tell later what it is for. |
| `identity.account_rejected` | 400 | The account itself was refused; `detail` says why. |
| `identity.human_credential_required` | 403 | Issuing credentials needs a signed-in administrator, not an API key. |

## Inventory — the ledger

| Code | Status | Means |
|---|---|---|
| `inventory.insufficient_stock` | 409 | Not enough on hand in that holding. |
| `inventory.insufficient_available_stock` | 409 | Enough on hand, but it is promised to somebody. |
| `inventory.idempotency_key_reused` | 409 | That key was used for a *different* command. |
| `inventory.idempotency_keys_not_distinct` | 400 | A batch reused a key within itself. |
| `inventory.lot_required` | 400 | The item is lot-tracked. |
| `inventory.lot_not_permitted` | 400 | The item is not lot-tracked and you sent a lot. |
| `inventory.lot_expiry_conflict` | 409 | Two expiry dates for one lot number. A lot *is* a batch. |
| `inventory.lot_ambiguous` | 400 | That lot number matches more than one batch. |
| `inventory.expiry_requires_lot` | 400 | An expiry date belongs to a batch. |
| `inventory.lot_not_found` | 404 | |
| `inventory.item_not_found` | 404 | |
| `inventory.item_inactive` | 400 | |
| `inventory.location_not_found` | 404 | |
| `inventory.location_blocked` | 400 | |
| `inventory.holding_not_found` | 404 | No stock of that item, lot, plate and status there. |
| `inventory.holdings_identical` | 400 | A movement from a holding to itself says nothing. |
| `inventory.quantity_not_positive` | 400 | Direction lives in the from/to pair, never in a sign. |
| `inventory.nothing_to_post` | 400 | An empty batch. |
| `inventory.movement_not_found` | 404 | |
| `inventory.already_reversed` | 409 | A movement may be reversed once. |
| `inventory.cannot_reverse_a_reversal` | 400 | Correct the original instead. |
| `inventory.invalid_counterparty` | 400 | |
| `inventory.counterparty_missing` | 500 | A virtual counterparty location is absent. Configuration. |
| `inventory.invalid_sscc` | 400 | Check digit. |
| `inventory.license_plate_not_found` | 404 | |
| `inventory.license_plate_empty` | 409 | |
| `inventory.license_plate_cycle` | 400 | A plate cannot nest inside itself. |
| `inventory.status_change_needs_new_status` | 400 | |
| `inventory.reservation_not_found` | 404 | |
| `inventory.reservation_not_open` | 409 | Already discharged or released — the claim was spent. |
| `inventory.counted_quantity_negative` | 400 | Finding nothing is `0`, which is an answer. |
| `inventory.cannot_count_counterparty` | 400 | Counting a virtual location means nothing. |
| `inventory.scrap_notes_required` | 400 | A write-off has to say why. |
| `inventory.nothing_to_despatch` | 400 | |
| `inventory.as_of_in_future` | 400 | |

## Inbound

| Code | Status | Means |
|---|---|---|
| `inbound.asn_not_found` | 404 | |
| `inbound.asn_reference_taken` | 409 | |
| `inbound.asn_not_open` | 409 | Already closed. |
| `inbound.asn_line_not_found` | 404 | |
| `inbound.item_not_on_asn` | 400 | Receive it blind, without `asnReference`, if it really arrived. |
| `inbound.not_a_receiving_location` | 400 | Goods cannot be booked straight into racking. |
| `inbound.no_lines` | 400 | |
| `inbound.item_not_found` · `inbound.location_not_found` | 404 | |
| `inbound.quantity_not_positive` | 400 | |

## Outbound

| Code | Status | Means |
|---|---|---|
| `outbound.order_not_found` · `outbound.wave_not_found` | 404 | |
| `outbound.order_reference_taken` · `outbound.wave_reference_taken` | 409 | |
| `outbound.order_not_releasable` | 409 | Cancelled or already despatched. |
| `outbound.order_not_packable` | 409 | Not released yet. |
| `outbound.picks_outstanding` | 409 | Packing asserts that picking is finished. |
| `outbound.order_not_despatchable` | 409 | Not packed. |
| `outbound.nothing_picked` | 409 | A despatch is built from what was picked. |
| `outbound.already_despatched` | 409 | |
| `outbound.order_not_cancellable` | 409 | |
| `outbound.picks_in_progress` | 409 | Somebody is holding a pick; a job cannot be cancelled from under them. |
| `outbound.order_not_waveable` | 409 | |
| `outbound.wave_not_releasable` | 409 | |
| `outbound.staging_required` | 400 | A pick with nowhere to go is not a job. |
| `outbound.not_a_staging_location` | 400 | |
| `outbound.no_lines` · `outbound.no_orders` | 400 | |
| `outbound.quantity_not_positive` | 400 | |
| `outbound.item_not_found` · `outbound.location_not_found` · `outbound.warehouse_not_found` | 404 | |

## Work queue

| Code | Status | Means |
|---|---|---|
| `work.no_work_available` | 404 | The queue is empty. Not an error. |
| `work.task_not_found` | 404 | |
| `work.task_already_claimed` | 409 | Somebody else got it. |
| `work.task_not_yours` | 403 | Claimed by another operator. |
| `work.task_already_completed` | 409 | A second confirmation under a new key. |
| `work.task_not_open` | 409 | Cancelled or withdrawn. |
| `work.enqueue_race_lost` | 409 | Two raisers, one plate. Retry. |
| `work.count_quantity_required` | 400 | A count is confirmed with a number, not a location. |
| `work.count_location_mismatch` | 400 | A count asks about one holding; a different code is a mis-scan. |
| `work.task_has_no_reservation` | 500 | A pick with no claim behind it. Report it. |
| `work.task_has_no_decision` | 500 | A directed movement with no trace. Report it. |
| `work.warehouse_required` | 400 | |
| `work.quantity_not_positive` | 400 | |
| `work.location_not_found` | 404 | |

## Counting

| Code | Status | Means |
|---|---|---|
| `counting.plan_not_found` | 404 | |
| `counting.schedule_budget_invalid` | 400 | Choose 1–200 tasks. |
| `counting.schedule_interval_invalid` | 400 | Choose 1–10080 minutes. |
| `counting.plan_incomplete` | 409 | Retry cancellation once all keyed tasks can be resolved. |
| `counting.plan_not_open` | 409 | |
| `counting.counts_in_progress` | 409 | A plan cannot be withdrawn from under a counter. |
| `counting.task_count_not_positive` · `counting.too_many_tasks` | 400 | |
| `counting.warehouse_not_found` | 404 | |

A plan that comes back with nothing to count is usually the quiet period: the
policy will not count a bay within four hours of anything moving in it, which is
right — counting mid-putaway measures the truck — and means a warehouse that was
built minutes ago has nothing admissible yet.

## Rules and replay

| Code | Status | Means |
|---|---|---|
| `rules.rule_set_not_found` · `rules.trace_not_found` | 404 | |
| `rules.rule_set_not_editable` | 409 | Only a draft. Clone it and edit the copy. |
| `rules.rule_set_not_promotable` | 409 | Only a draft can be promoted. |
| `rules.rule_set_empty` | 400 | A set with no rules would admit everything and rank nothing. |
| `rules.rule_not_in_vocabulary` | 400 | That rule belongs to another kind of decision. |
| `rules.rule_set_kind_mismatch` | 400 | |
| `rules.duplicate_rule` | 400 | |
| `rules.weight_out_of_range` | 400 | A preference weighs between 0 and 1. |
| `rules.no_vocabulary` | 400 | This build has no rules for that decision kind. |
| `rules.no_active_rule_set` | 500 | Configuration: nothing is on the floor for that decision. |
| `rules.unknown_rule` | 500 | A stored rule code this build does not implement. Never skipped — silently dropping a constraint is how a chilled pallet reaches an ambient aisle. |
| `rules.invalid_rule_parameters` | 500 | |
| `rules.quantity_not_positive` | 400 | |
| `rules.item_not_found` · `rules.location_not_found` · `rules.warehouse_not_found` | 404 | |
| `replay.run_not_found` · `replay.warehouse_not_found` | 404 | |
| `replay.window_inverted` | 400 | |

## Master data and import

| Code | Status | Means |
|---|---|---|
| `item.not_found` · `warehouse.not_found` | 404 | |
| `import.malformed_csv` | 400 | `detail` names the row. |
| `masterdata.warehouse_not_found` | 404 | |
| `masterdata.no_company_prefix` | 409 | The site has no GS1 company prefix, so it cannot issue an SSCC. Configuration, not your request. |
| `masterdata.invalid_company_prefix` · `masterdata.invalid_extension_digit` | 409 | The prefix on file is not six to ten digits, or the extension digit is outside 0–9. |
| `masterdata.sscc_serial_exhausted` | 409 | Every serial the prefix has room for has been issued. A further prefix has to be bought from GS1; serials are never reused. |

## Labels

Most of these arrive when a template is **saved**, not when a label is printed — a
template is validated by rendering it, so a layout that could not print is refused
in the editor rather than at a rack.

| Code | Status | Means |
|---|---|---|
| `labels.template_not_found` · `labels.warehouse_not_found` · `labels.item_not_found` | 404 | |
| `labels.template_not_editable` | 409 | Only a draft is editable. Clone, edit the copy, promote that. |
| `labels.template_not_promotable` | 409 | Only a draft can be promoted. |
| `labels.no_active_template` | 409 | Nothing is on the floor for that kind and site. Promote one. |
| `labels.template_empty` · `labels.template_has_no_extent` | 400 | |
| `labels.unknown_field` | 400 | The template names a field its kind does not publish. Never printed blank — see `GET /labels/kinds` for the list. |
| `labels.no_vocabulary` | 400 | This build has no fields for that label kind. |
| `labels.duplicate_element_order` | 400 | Draw order decides overlap, so two elements cannot share one. |
| `labels.element_off_label` | 400 | The element starts outside the label's extent. |
| `labels.invalid_rotation` | 400 | `N`, `R`, `I` or `B`. |
| `labels.text_is_both` · `labels.text_has_nothing` | 400 | A text element prints a caption or a field, exactly one. |
| `labels.barcode_has_no_symbology` · `labels.barcode_has_no_field` | 400 | |
| `labels.box_has_no_extent` · `labels.unknown_element_type` | 400 | |
| `labels.barcode_field_empty` | 409 | A plain Code 128 over a field this subject has no value for. A barcode encoding nothing scans as nothing while looking like one. |
| `labels.invalid_sscc` | 400 | Check digit. |
| `labels.idempotency_key_required` | 400 | |

## GS1 encoding

Raised while **building** a barcode's data, and reached through the label
endpoints. `gs1.empty_value`, `gs1.value_too_long` and `gs1.non_numeric` are the
same codes scanning raises, deliberately — one idea, one code.

| Code | Status | Means |
|---|---|---|
| `gs1.no_elements` | 400 | |
| `gs1.unknown_ai` | 400 | An application identifier this build cannot encode. |
| `gs1.duplicate_ai` | 400 | |
| `gs1.wrong_fixed_length` | 400 | A predefined-length AI given the wrong number of characters. Nothing terminates one, so a short value swallows the head of the next element. |


### Pick routing

| Code | Status | Meaning |
|---|---|---|
| `work.tour_not_found` | 404 | The tour does not exist. |
| `work.tour_not_yours` | 403 | Release is restricted to its operator. |
| `work.tour_nothing_to_release` | 409 | No unfinished stops remain; retry the original release key after a lost response. |
| `masterdata.zone_not_found` | 404 | The zone does not exist. |
| `masterdata.unknown_route_strategy` | 400 | This build has no registered strategy under that code. |
| `masterdata.route_strategy_unsupported` | 409 | The zone lacks geometry required by the strategy. |

Routing support and fallback fields contain reason codes: `route.no_aisles`,
`route.no_bay_depth`, `route.no_rear_cross_aisle`, `route.no_zone`, and
`route.unknown_strategy`. They are explanatory metadata, not separate HTTP
failures, and are translated by the client.

`counting.schedule_failed` is a persisted schedule diagnostic, not an HTTP error.
It indicates that the pending warehouse run will retry; its plan key is retained.

The scheduler logs `counting.schedule_warehouse_unavailable` when its warehouse
is inactive or missing, and `counting.schedule_incomplete` when a plan still has
unresolved task lines. Both are internal causes of `counting.schedule_failed`,
not separate HTTP refusals. Jobs persists `jobs.execution_failed` when a handler
fails; the same run retries after backoff. These diagnostics have English and
Turkish messages and cannot be returned by a handheld completion command.

### Replenishment

| Code | Status | Meaning |
|---|---|---|
| `replenishment.levels_invalid` | 400 | Positive minimum, greater maximum, at most four decimals, maximum up to 1,000,000,000. |
| `replenishment.capacity_exceeded` | 400 | Maximum exceeds known face weight or volume capacity. |
| `replenishment.face_invalid` | 400 | Requires an active replenishable Forward location and compatible temperature. |
| `replenishment.item_not_found` | 404 | Requires an active item. |
| `replenishment.stock_committed` | 409 | Finish or withdraw incoming work before lowering maximum below on-hand plus incoming stock. |
| `replenishment.in_progress` | 409 | Face stock or pending/incoming work prevents changing its item. |
| `replenishment.plan_not_found` | 404 | The replenishment plan does not exist. |
| `replenishment.retry_cancellation` | 409 | Resolve intended task acknowledgements before releasing stock; retry cancellation. |
| `work.replenishment_destination_mismatch` | 400 | Confirm only the task's fixed pick face. |
| `inventory.replenishment_locations_invalid` | 400 | Active Reserve source and replenishable Forward destination must share a warehouse. |

Persisted policy diagnostics `replenishment.no_source`,
`replenishment.face_unavailable`, `replenishment.capacity_exceeded`, and
`replenishment.retrying` report a shortage, changed face/capacity, or pending retry.
The pending plan and line keys survive retries. Allocation exclusion
`rules.allocation.source_role_required` explains why a holding is not a bulk source.

### Webhooks

| Code | Status | Meaning |
|---|---|---|
| `webhooks.receiver_invalid` | 400 | Public HTTPS URL without credentials, query, or fragment; internal origins need deployment allowlisting. |
| `webhooks.limit_reached` | 400 | Names up to 100 characters; up to 100 subscriptions or triggers. |
| `webhooks.key_not_configured` | 503 | Configure the persistent 32-byte base64 encryption key. |
| `webhooks.verification_failed` | 400 | Receiver did not echo the signed challenge with HTTP 200. |
| `webhooks.verification_required` | 400 | Verify before enabling. |
| `webhooks.retry_key_required` | 400 | Inbound calls require an Idempotency-Key up to 128 characters. |

Delivery diagnostics `webhooks.receiver_refused` and `webhooks.delivery_failed`
report a non-2xx acknowledgement or failed network/secret access. Eight failed
attempts enter DeadLetter and block later batches until manual retry. Invalid or
revoked capability URLs return 404; a full inbound queue returns 429.

`work.throughput_window_invalid` (400) requires a dashboard window of 1–90 days.
Dashboard throughput and density return 404 for an unknown or inactive warehouse.

### Blind bin counts

| Code | HTTP | Action |
| --- | --- | --- |
| `work.bin_count_reason_required` | 400 | Supply the supervisor's reason, up to 120 characters. |
| `work.idempotency_key_reused` | 409 | Use the original bin/reason or a new request key. |
| `inventory.bin_count_invalid` | 400 | Check identities, nonnegative quantities, conditions and the 200-row limit. |
| `inventory.bin_count_duplicate` | 400 | Combine identical item/lot/pallet/condition holdings. |
| `inventory.bin_changed` | 409 | Discard the old observations, restart, and count the entire bin again. |
| `work.bin_count_recorded` | 409 | Retry the saved confirmation; it was recorded and cannot be released or restarted. |

`work.task_not_yours` (403) also fences obsolete bin claim IDs after restart or
release/reclaim. Existing item tracking, lot expiry, SSCC, correct-bin and task
state errors still apply. `work.bin_draft_storage` is a device-only diagnostic:
keep the capture screen open when local draft persistence fails.
