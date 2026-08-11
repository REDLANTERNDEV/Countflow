/**
 * English catalogue.
 *
 * Keys are namespaced by area. Server-originated messages live under `errors.*`
 * and `scan.*` keyed by the code the API returns — the API never sends prose,
 * so adding a language is a catalogue rather than a backend change.
 */
export const en = {
  common: {
    signIn: 'Sign in',
    signOut: 'Sign out',
    signingIn: 'Signing in…',
    loading: 'Loading…',
    search: 'Search',
    cancel: 'Cancel',
    save: 'Save',
    previous: 'Previous',
    next: 'Next',
    page: 'Page {{page}} of {{total}}',
    total: '{{count}} total',
    none: '—',
    yes: 'Yes',
    no: 'No',
    all: 'All',
    theme: 'Theme',
    themeSystem: 'System',
    themeLight: 'Light',
    themeDark: 'Dark',
    language: 'Language',
    online: 'Online',
    offline: 'Offline — work is queued',
  },

  nav: {
    warehouses: 'Warehouses',
    locations: 'Locations',
    items: 'Items',
    stock: 'Stock',
    ledger: 'Ledger',
    scanTest: 'Scan test',
  },

  auth: {
    title: 'Countflow',
    subtitle: 'Warehouse console',
    identifier: 'Username or operator code',
    operatorCode: 'Operator code',
    password: 'Password',
    scanBadge: 'Scan your badge or key your operator code',
    failed: 'Sign in failed.',
  },

  locations: {
    title: 'Locations',
    searchPlaceholder: 'Search by code, e.g. A07-03',
    allTypes: 'All types',
    pickableOnly: 'Pickable only',
    code: 'Code',
    type: 'Type',
    zone: 'Zone',
    aisle: 'Aisle',
    bay: 'Bay',
    level: 'Level',
    sequence: 'Seq',
    maxVolume: 'Max m³',
    maxWeight: 'Max kg',
    flags: 'Flags',
    virtual: 'virtual',
    pick: 'pick',
    replen: 'replen',
    blocked: 'blocked',
    empty: 'No locations match these filters.',
  },

  items: {
    title: 'Items',
    searchPlaceholder: 'Search by SKU, description or GTIN',
    allAbc: 'All ABC classes',
    allTemperatures: 'All temperatures',
    sku: 'SKU',
    description: 'Description',
    abcClass: 'ABC',
    temperature: 'Temp',
    tracking: 'Tracking',
    packLevels: 'Pack levels',
    caseGtin: 'Case GTIN',
    lot: 'lot',
    expiry: 'expiry',
    serial: 'serial',
    hazard: 'hazard',
    baseUnit: 'Base unit',
    shelfLife: 'Shelf life',
    shelfLifeDays: '{{count}} days',
    hazardous: 'Hazardous',
    lotTracked: 'Lot tracked',
    expiryTracked: 'Expiry tracked',
    serialTracked: 'Serial tracked',
    packaging: 'Packaging hierarchy',
    packagingNote:
      'A GTIN identifies a packaging level, not the item. Scanning the case barcode is what tells receiving it is holding twelve units rather than one.',
    baseUnits: 'Base units',
    dimensions: 'L×W×H (mm)',
    volume: 'Volume m³',
    grossWeight: 'Gross kg',
    role: 'Role',
    receiving: 'receiving',
    empty: 'No items match these filters.',
  },

  stock: {
    title: 'Stock',
    intro:
      'Balances are a projection of the stock ledger, not a stored total. Set a date and time to rebuild them from the ledger as they stood at that instant.',
    skuPlaceholder: 'SKU',
    locationPlaceholder: 'Location code',
    lotPlaceholder: 'Batch / lot',
    ssccPlaceholder: 'SSCC',
    allStatuses: 'All statuses',
    includeEmpty: 'Include emptied holdings',
    asOf: 'As of',
    asOfClear: 'Now',
    asOfNotice:
      'Reconstructed from the ledger up to entry #{{sequence}}. These are historical figures.',
    sku: 'SKU',
    description: 'Description',
    lot: 'Batch',
    expiry: 'Expiry',
    location: 'Location',
    sscc: 'SSCC',
    status: 'Status',
    quantity: 'Quantity',
    uom: 'UoM',
    allocatable: 'Allocatable',
    held: 'Held',
    empty: 'No stock matches these filters.',

    status_Available: 'Available',
    status_Quarantine: 'Quarantine',
    status_Damaged: 'Damaged',
    status_OnHold: 'On hold',
    status_Expired: 'Expired',
  },

  ledger: {
    title: 'Stock ledger',
    intro:
      'Every stock change ever posted, in order. Entries are never edited or deleted — a correction is a compensating entry, so the record of the mistake survives alongside its fix.',
    allTypes: 'All movement types',
    allReasons: 'All reasons',
    entry: 'Entry',
    when: 'When',
    sku: 'SKU',
    lot: 'Batch',
    quantity: 'Qty',
    from: 'From',
    to: 'To',
    type: 'Type',
    reason: 'Reason',
    user: 'By',
    notes: 'Notes',
    reversed: 'reversed',
    reversal: 'correction',
    empty: 'No ledger entries match these filters.',
    reconcile: 'Verify against the ledger',
    reconciling: 'Replaying…',
    reconcileClean:
      'Clean: {{holdings}} holdings replayed from {{movements}} entries, and the ledger sums to zero.',
    reconcileDrift:
      'Drift on {{count}} holdings. The balance projection disagrees with the ledger.',
    reconcileNote:
      'Replays the whole ledger and compares the result with the stored balances. It reports drift and never repairs it: the fix is a compensating entry posted by someone who understands the cause.',

    type_Receipt: 'Receipt',
    type_Putaway: 'Putaway',
    type_Transfer: 'Transfer',
    type_Replenishment: 'Replenishment',
    type_Pick: 'Pick',
    type_Despatch: 'Despatch',
    type_Adjustment: 'Adjustment',
    type_CountAdjustment: 'Count adjustment',
    type_StatusChange: 'Status change',
    type_Scrap: 'Write-off',
    type_Reversal: 'Reversal',
  },

  scan: {
    title: 'Scan test',
    intro:
      'Decodes a GS1-128 or GS1 DataMatrix element string and resolves it against the catalogue. One scan of a logistics label carries the item, its packaging level, batch, expiry and quantity — which is what lets receiving verify a delivery instead of trusting a typed number.',
    placeholder: 'Paste or scan a GS1 barcode',
    decode: 'Decode',
    decoding: 'Decoding…',
    tryOne: 'Try one:',
    resolution: 'Resolution',
    elements: 'Decoded elements',
    ai: 'AI',
    meaning: 'Meaning',
    value: 'Value',
    packagingLevel: 'Packaging level',
    unitsPerPack: 'Units per pack',
    scannedQuantity: 'Scanned quantity',
    totalBaseUnits: 'Total base units',
    batch: 'Batch / lot',
    expiry: 'Expiry',
    serial: 'Serial',
    sscc: 'SSCC',
    notRecognised: 'No catalogue item matched this barcode.',
    exampleCase: 'Case label — GTIN, batch, expiry, count',
    exampleBracketed: 'Bracketed form, as printed in a supplier spec',
    exampleMissingLot: 'Missing batch on a lot-tracked item',
    exampleExpired: 'Expired stock',
    exampleUnknown: 'GTIN not in the catalogue',

    // Warning codes returned by /api/scan/resolve. The server sends a code and
    // arguments; the wording is entirely ours.
    warning: {
      missing_lot: '{{sku}} is lot-tracked but the scan carries no batch number (AI 10).',
      missing_expiry: '{{sku}} is expiry-tracked but the scan carries no expiry date (AI 17).',
      missing_serial: '{{sku}} is serial-tracked but the scan carries no serial (AI 21).',
      expired:
        'Stock is already expired ({{date}}). It must be quarantined, not put away.',
      gtin_not_found: 'GTIN {{gtin}} is not in the item catalogue.',
      gtin_check_digit: 'GTIN {{gtin}} failed its check digit and was not used for lookup.',
      sscc_check_digit: 'SSCC {{sscc}} failed its check digit.',
    },
  },

  errors: {
    'auth.invalid_credentials': 'The credentials provided are not valid.',
    'auth.invalid_refresh_token': 'Your session has expired. Please sign in again.',
    'auth.user_not_found': 'That user no longer exists.',
    'item.not_found': 'No item with that SKU.',
    'warehouse.not_found': 'No warehouse with that code.',
    'gs1.empty': 'Nothing was scanned.',
    'gs1.invalid_ai': 'That is not a valid GS1 application identifier.',
    'gs1.truncated_ai': 'The barcode ends mid-identifier.',
    'gs1.truncated_value': 'The barcode ends mid-value.',
    'gs1.empty_value': 'An application identifier carries no data.',
    'gs1.non_numeric': 'A numeric field contains non-digits.',
    'gs1.invalid_date': 'A date field is not a valid YYMMDD date.',
    'gs1.value_too_long': 'A field is longer than its application identifier allows.',
    'import.malformed_csv': 'The file could not be read as CSV.',

    // Inventory. The ledger returns codes for the same reason the scanner does:
    // an operator on a Turkish handheld must read Turkish, and a server that
    // sends English sentences makes that impossible.
    'inventory.item_not_found': 'No item with that SKU.',
    'inventory.item_inactive': 'That item is deactivated and cannot be moved.',
    'inventory.location_not_found': 'No location with that code.',
    'inventory.location_blocked': 'That location is blocked and cannot receive stock.',
    'inventory.lot_required': 'This item is batch-tracked, so a batch number is required.',
    'inventory.lot_not_permitted': 'This item is not batch-tracked, so it cannot carry a batch.',
    'inventory.lot_not_found': 'No such batch for this item.',
    'inventory.lot_ambiguous': 'That batch number exists on more than one item. Add a SKU.',
    'inventory.lot_expiry_conflict':
      'That batch is already recorded with a different expiry date. The same batch cannot expire twice.',
    'inventory.expiry_requires_lot': 'An expiry date belongs to a batch, so it needs a batch number.',
    'inventory.quantity_not_positive': 'Quantity must be greater than zero.',
    'inventory.holdings_identical': 'A movement must change location, licence plate or status.',
    'inventory.insufficient_stock': 'There is not that much stock in that location.',
    'inventory.idempotency_key_required': 'This request is missing its idempotency key.',
    'inventory.idempotency_key_reused':
      'That idempotency key was already used for a different movement.',
    'inventory.invalid_counterparty': 'That movement type cannot use those locations.',
    'inventory.status_change_needs_new_status': 'A status change must move stock to a different status.',
    'inventory.movement_not_found': 'No ledger entry with that identifier.',
    'inventory.already_reversed': 'That entry has already been corrected.',
    'inventory.cannot_reverse_a_reversal':
      'Correct the original entry, not the correction that already undid it.',
    'inventory.invalid_sscc': 'An SSCC is 18 digits and must satisfy its check digit.',
    'inventory.license_plate_not_found': 'No licence plate with that SSCC.',
    'inventory.license_plate_cycle': 'A licence plate cannot be nested inside itself.',
    'inventory.as_of_in_future': 'A point-in-time enquiry cannot be in the future.',
    'inventory.counterparty_missing':
      'This warehouse has no virtual counterparty location for that movement. The ledger cannot post without one.',
    'inventory.unknown_actor': 'Your session does not identify a user. Please sign in again.',

    unknown: 'Something went wrong.',
  },
};

/**
 * The catalogue shape, derived from English.
 *
 * Deliberately *not* `as const`: literal types would make every Turkish string a
 * type error, since `'Giriş yap'` is not assignable to `'Sign in'`. What we want
 * enforced is the *structure* — every key present, with a string value — so a
 * missing or misspelled translation fails the build. `typeof en` without the
 * const assertion gives exactly that.
 */
export type Catalogue = typeof en;
