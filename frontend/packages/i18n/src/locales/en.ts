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
