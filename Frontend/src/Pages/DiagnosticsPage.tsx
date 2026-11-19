import { useEffect, useState } from 'react';
import { CheckCircle2, CircleSlash, FileText, RefreshCw, Timer } from 'lucide-react';
import { Button } from '../Components/Button';
import { Panel } from '../Components/Controls';
import { Spinner } from '../Components/StartButton';
import { onMessage } from '../lib/bridge';
import { useStore } from '../lib/store';

interface LatencyResult {
  samples: number;
  minMs: number;
  medianMs: number;
  maxMs: number;
  error?: string;
}

export function DiagnosticsPage() {
  const { state, stats, send, capabilities, capabilitiesChecking, refreshCapabilities } = useStore();
  const rows = capabilities?.rows ?? null;
  const [latency, setLatency] = useState<LatencyResult | null>(null);
  const [latencyRunning, setLatencyRunning] = useState(false);

  useEffect(() => {
    const off = onMessage((method, content) => {
      if (method === 'LatencyResult') {
        setLatency(content);
        setLatencyRunning(false);
      }
    });
    return off;
  }, []);

  if (!state) return null;
  const groups = rows ? [...new Set(rows.map((r) => r.group))] : [];
  const live = state.pipeline.status === 'running';

  return (
    <div className="mx-auto flex max-w-[1400px] flex-col gap-6 px-9 pt-1 pb-9">
      <header className="flex items-center justify-between">
        <h1 className="font-display text-[1.75rem] leading-9 font-bold text-chalk">Diagnostics</h1>
        <div className="flex items-center gap-2">
          <Button icon={FileText} onClick={() => send('OpenLogs')}>
            Open logs
          </Button>
          <Button icon={capabilitiesChecking ? undefined : RefreshCw} disabled={capabilitiesChecking} onClick={refreshCapabilities}>
            {capabilitiesChecking && <Spinner />}
            {capabilitiesChecking ? 'Checking' : 'Check again'}
          </Button>
        </div>
      </header>

      <div className="grid gap-6 min-[1100px]:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <Panel eyebrow="This machine" title="Capabilities">
          {!rows ? (
            <div className="flex h-24 items-center justify-center gap-3 text-dusk">
              <Spinner />
              <span>Checking</span>
            </div>
          ) : (
            groups.map((g) => (
              <div key={g} className="rounded-2xl border border-edge bg-night">
                <div className="eyebrow px-3 pt-2.5 pb-1">{g}</div>
                <ul>
                  {rows
                    .filter((r) => r.group === g)
                    .map((r) => (
                      <li key={r.name} className="flex items-start gap-2.5 border-t border-edge/60 px-3 py-2 first:border-t-0">
                        <span className="flex h-5 w-4 shrink-0 items-center justify-center">
                          {r.ok ? (
                            <CheckCircle2 size={16} className="text-success" aria-label="Available" />
                          ) : (
                            <CircleSlash size={16} className="text-dusk" aria-label="Not available" />
                          )}
                        </span>
                        <span className="w-40 shrink-0 text-[0.9375rem] leading-5 text-chalk">{r.name}</span>
                        <span className="min-w-0 flex-1 text-[0.8125rem] leading-5 text-dusk">{r.value}</span>
                      </li>
                    ))}
                </ul>
              </div>
            ))
          )}
        </Panel>

        <div className="flex flex-col gap-6">
          <Panel eyebrow="Measure" title="Delay test">
            <p className="text-[0.8125rem] leading-[18px] text-dusk">
              Connect a spare display output of the PC to the capture card's HDMI input. The card then shows up as a display. The test
              flashes a marker there and times how long it takes to come back through the card.
            </p>
            <div className="flex items-center gap-3">
              <Button
                icon={latencyRunning ? undefined : Timer}
                disabled={latencyRunning || live}
                onClick={() => {
                  setLatencyRunning(true);
                  setLatency(null);
                  send('RunLatencyTest');
                }}
              >
                {latencyRunning && <Spinner size={14} />}
                {latencyRunning ? 'Measuring' : 'Run delay test'}
              </Button>
              {live && <span className="text-[0.8125rem] text-dusk">Stop the pipeline first</span>}
            </div>
            {latency && (
              <div className="grid grid-cols-3 gap-2">
                {latency.error ? (
                  <div className="col-span-3 text-[0.9375rem] text-warning">{latency.error}</div>
                ) : (
                  (
                    [
                      ['Lowest', latency.minMs],
                      ['Median', latency.medianMs],
                      ['Highest', latency.maxMs],
                    ] as const
                  ).map(([l, v]) => (
                    <div key={l} className="rounded-2xl border border-edge bg-night p-3">
                      <div className="eyebrow leading-none">{l}</div>
                      <div className="mt-2 flex items-baseline gap-1.5">
                        <span className="font-display tabular text-[1.75rem] leading-none font-semibold text-chalk">{v.toFixed(1)}</span>
                        <span className="text-xs font-medium text-dusk">ms</span>
                      </div>
                    </div>
                  ))
                )}
              </div>
            )}
          </Panel>

          <Panel eyebrow="Live" title="Cadence">
            <CadenceGraph real={stats.history.real} generated={stats.history.generated} live={live} />
            <div className="flex items-center gap-5 text-[0.8125rem] leading-none text-dusk">
              <span className="inline-flex items-center gap-1.5">
                <span className="h-2 w-2 rounded-full bg-lagoon" aria-hidden />
                Real frames
              </span>
              <span className="inline-flex items-center gap-1.5">
                <span className="h-2 w-2 rounded-full bg-tangerine" aria-hidden />
                Generated frames
              </span>
              <span className="tabular ml-auto">{live ? (stats.cadence ? `Locked ${stats.cadence}` : 'Not locked') : 'Not running'}</span>
            </div>
          </Panel>
        </div>
      </div>
    </div>
  );
}

function CadenceGraph({ real, generated, live }: { real: number[]; generated: number[]; live: boolean }) {
  const n = Math.max(real.length, generated.length);
  const max = Math.max(1, ...real.map((r, i) => r + (generated[i] ?? 0)));
  return (
    <div className="flex h-28 items-end gap-[3px] rounded-2xl border border-edge bg-night p-3" role="img" aria-label="Real and generated frames per second">
      {!live || n === 0 ? (
        <div className="flex h-full w-full items-center justify-center text-[0.8125rem] text-dusk">Start the pipeline to see frame rates</div>
      ) : (
        Array.from({ length: n }).map((_, i) => {
          const r = real[i] ?? 0;
          const g = generated[i] ?? 0;
          return (
            <div key={i} className="flex h-full min-w-0 flex-1 flex-col justify-end gap-px">
              <div className="rounded-t-sm bg-tangerine" style={{ height: `${(g / max) * 100}%` }} />
              <div className={`bg-lagoon ${g === 0 ? 'rounded-t-sm' : ''}`} style={{ height: `${(r / max) * 100}%` }} />
            </div>
          );
        })
      )}
    </div>
  );
}
