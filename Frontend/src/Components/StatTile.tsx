import { AlertTriangle } from 'lucide-react';

export function StatTile({
  label,
  value,
  unit,
  estimated,
  warning,
  spark,
  sparkColor = 'var(--color-lagoon)',
}: {
  label: string;
  value: string;
  unit?: string;
  estimated?: boolean;
  warning?: boolean;
  spark?: number[];
  sparkColor?: string;
}) {
  return (
    <div className="relative flex min-w-0 flex-col justify-between overflow-hidden rounded-[20px] border border-edge bg-boulevard px-[18px] pt-4 pb-3.5">
      <div className="flex h-4 items-center gap-1.5">
        <span className="eyebrow truncate leading-4">{label}</span>
        {estimated && <span className="text-[0.6875rem] leading-none font-medium text-dusk">est.</span>}
      </div>
      <div className="mt-2 flex items-baseline gap-1.5">
        {warning && <AlertTriangle size={18} className="shrink-0 self-center text-warning" aria-hidden />}
        <span
          className={`font-display tabular text-[2.25rem] leading-none font-semibold ${warning ? 'text-warning' : 'text-chalk'}`}
        >
          {value}
        </span>
        {unit && <span className="text-xs font-medium text-dusk">{unit}</span>}
      </div>
      <div className="mt-1.5 h-4">{spark && spark.length > 1 && <Sparkline values={spark} color={sparkColor} />}</div>
    </div>
  );
}

function Sparkline({ values, color }: { values: number[]; color: string }) {
  const max = Math.max(...values, 1) * 1.15;
  const min = Math.min(...values) * 0.85;
  const span = Math.max(max - min, 1);
  const w = 100;
  const h = 16;
  const step = w / (values.length - 1);
  const d = values
    .map((v, i) => `${i === 0 ? 'M' : 'L'}${(i * step).toFixed(2)},${(h - ((v - min) / span) * (h - 2) - 1).toFixed(2)}`)
    .join(' ');
  return (
    <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" className="h-full w-full" aria-hidden>
      <path d={d} fill="none" stroke={color} strokeWidth={1.5} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}
