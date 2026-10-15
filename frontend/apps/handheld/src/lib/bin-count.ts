import type { components } from '@countflow/api-client';

export interface ObservedHolding {
  sku: string;
  quantity: number;
  lotNumber?: string;
  expiryDate?: string;
  sscc?: string;
  condition: components['schemas']['StockCondition'];
  description?: string;
  uom?: string;
  warnings?: components['schemas']['ScanWarning'][];
}

export function validBinQuantity(quantity: number): boolean {
  return Number.isFinite(quantity) && quantity >= 0 && quantity < 100000000000000
    && Math.round(quantity * 10000) / 10000 === quantity;
}

export function addBinObservation(lines: ObservedHolding[], incoming: ObservedHolding): ObservedHolding[] | null {
  const { lotNumber, sscc, ...rest } = incoming;
  const line = { ...rest, sku: incoming.sku.trim(), ...(lotNumber?.trim() ? { lotNumber: lotNumber.trim() } : {}),
    ...(sscc?.trim() ? { sscc: sscc.trim() } : {}) };
  if (!line.sku || !validBinQuantity(line.quantity) || lines.length >= 200) return null;
  if (lines.some(existing => existing.sku === line.sku && existing.lotNumber === line.lotNumber
    && existing.sscc === line.sscc && existing.condition === line.condition)) return null;
  return [...lines, line];
}

export function binCountReady(scanned: string, bin: string, version: number | null | undefined,
  claim: string | null | undefined, lines: ObservedHolding[], emptyConfirmed: boolean): boolean {
  return scanned.trim().toUpperCase() === bin.trim().toUpperCase() && Boolean(scanned.trim())
    && version != null && Boolean(claim) && emptyConfirmed && lines.every(line => validBinQuantity(line.quantity));
}
