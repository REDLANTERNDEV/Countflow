import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';

/** ASCII 29 — what a scanner actually transmits for FNC1. */
const GS = '';

export function ScanPage() {
  const [barcode, setBarcode] = useState('');

  // Examples are built from a real seeded item, so they always resolve. Static
  // sample barcodes rot the moment the catalogue changes.
  const { data: sample } = useQuery({
    queryKey: ['scan-sample'],
    queryFn: async () => {
      const { data } = await api.GET('/api/items', {
        params: { query: { temperature: 'Chilled', pageSize: 1 } },
      });

      const item = data?.items[0];
      const caseGtin = item?.packConfigurations.find((p) => p.level === 'Case')?.gtin;
      return caseGtin ? { sku: item!.sku, gtin: caseGtin } : null;
    },
  });

  const resolve = useMutation({
    mutationFn: async (raw: string) => {
      const { data, error } = await api.POST('/api/scan/resolve', {
        body: { barcode: raw },
      });

      if (error) {
        const problem = error as { detail?: string; code?: string };
        throw new Error(problem.detail ?? 'Could not decode that barcode.');
      }

      return data;
    },
  });

  const examples = sample
    ? [
        {
          label: 'Case label — GTIN, batch, expiry, count',
          value: `01${sample.gtin}10LOT-4711${GS}1727011537000012`,
        },
        {
          label: 'Bracketed form, as printed in a supplier spec',
          value: `(01)${sample.gtin}(10)LOT-4711(17)270115`,
        },
        {
          label: 'Missing batch on a lot-tracked item',
          value: `01${sample.gtin}`,
        },
        {
          label: 'Expired stock',
          value: `01${sample.gtin}10OLD-1${GS}17200101`,
        },
        {
          label: 'GTIN not in the catalogue',
          value: '0109501101530003',
        },
      ]
    : [];

  const result = resolve.data;

  return (
    <>
      <div className="page-head">
        <h2>Scan test</h2>
      </div>

      <p className="cf-muted detail-note">
        Decodes a GS1-128 or GS1 DataMatrix element string and resolves it against
        the catalogue. One scan of a logistics label carries the item, its
        packaging level, batch, expiry and quantity — which is what lets receiving
        verify a delivery instead of trusting a typed number.
      </p>

      <div className="scan-input-row">
        <input
          className="cf-input cf-mono"
          placeholder="Paste or scan a GS1 barcode"
          value={barcode}
          onChange={(event) => setBarcode(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && barcode.trim()) {
              resolve.mutate(barcode.trim());
            }
          }}
        />
        <button
          className="cf-button"
          type="button"
          disabled={!barcode.trim() || resolve.isPending}
          onClick={() => resolve.mutate(barcode.trim())}
        >
          {resolve.isPending ? 'Decoding…' : 'Decode'}
        </button>
      </div>

      {examples.length > 0 ? (
        <div className="scan-examples">
          <span className="cf-label">Try one:</span>
          {examples.map((example) => (
            <button
              key={example.label}
              type="button"
              className="example-chip"
              onClick={() => {
                setBarcode(example.value);
                resolve.mutate(example.value);
              }}
            >
              {example.label}
            </button>
          ))}
        </div>
      ) : null}

      {resolve.isError ? (
        <div className="cf-card scan-result">
          <p className="cf-error">{(resolve.error as Error).message}</p>
        </div>
      ) : null}

      {result ? (
        <div className="scan-result-grid">
          <div className="cf-card">
            <h3 className="card-title">Resolution</h3>

            {result.itemRecognised && result.item ? (
              <>
                <div className="resolved-item">
                  <span className="cf-mono resolved-sku">{result.item.sku}</span>
                  <span>{result.item.description}</span>
                </div>
                <div className="detail-grid">
                  <Fact label="Packaging level" value={result.item.packagingLevel} />
                  <Fact label="Units per pack" value={String(result.item.baseUnitQuantity)} />
                  <Fact
                    label="Scanned quantity"
                    value={result.scannedQuantity ? String(result.scannedQuantity) : '—'}
                  />
                  <Fact
                    label="Total base units"
                    value={result.baseUnitQuantity ? String(result.baseUnitQuantity) : '—'}
                  />
                  <Fact label="Batch / lot" value={result.lotNumber ?? '—'} />
                  <Fact label="Expiry" value={result.expiryDate ?? '—'} />
                  <Fact label="Serial" value={result.serialNumber ?? '—'} />
                  <Fact label="SSCC" value={result.sscc ?? '—'} />
                </div>
              </>
            ) : (
              <p className="cf-muted">No catalogue item matched this barcode.</p>
            )}

            {result.warnings.length > 0 ? (
              <ul className="warning-list">
                {result.warnings.map((warning) => (
                  <li key={warning}>{warning}</li>
                ))}
              </ul>
            ) : null}
          </div>

          <div className="cf-card">
            <h3 className="card-title">Decoded elements</h3>
            <table className="cf-table compact">
              <thead>
                <tr>
                  <th>AI</th>
                  <th>Meaning</th>
                  <th>Value</th>
                </tr>
              </thead>
              <tbody>
                {result.elements.map((element) => (
                  <tr key={`${element.ai}-${element.value}`}>
                    <td className="cf-mono">{element.ai}</td>
                    <td>{element.title}</td>
                    <td className="cf-mono">{element.value}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : null}
    </>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="fact">
      <span className="cf-label">{label}</span>
      <span className="cf-mono">{value}</span>
    </div>
  );
}
