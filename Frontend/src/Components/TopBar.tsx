import { ProfileMenu } from './ProfileMenu';

export type Page = 'play' | 'settings' | 'diagnostics';

const tabs: { id: Page; label: string }[] = [
  { id: 'play', label: 'Play' },
  { id: 'settings', label: 'Settings' },
  { id: 'diagnostics', label: 'Diagnostics' },
];

export function TopBar({ page, onNavigate }: { page: Page; onNavigate: (p: Page) => void }) {
  return (
    <header className="relative z-20 grid h-[76px] shrink-0 grid-cols-[1fr_auto_1fr] items-center gap-4 pr-12 pl-9">
      <div className="flex items-center gap-2.5">
        <AppMark />
        <span className="font-display text-[1.375rem] leading-6 font-bold tracking-[0.02em] text-chalk">TretioDLSS</span>
      </div>

      <nav className="flex items-center gap-1 rounded-full border border-edge bg-white/6 p-1" aria-label="Pages">
        {tabs.map(({ id, label }) => {
          const active = id === page;
          return (
            <button
              key={id}
              type="button"
              onClick={() => onNavigate(id)}
              aria-current={active ? 'page' : undefined}
              className={`h-9 rounded-full px-[18px] text-[0.9375rem] leading-none font-medium transition-colors duration-200 ${
                active ? 'bg-chalk text-night' : 'text-dusk hover:text-chalk'
              }`}
            >
              {label}
            </button>
          );
        })}
      </nav>

      <ProfileMenu />
    </header>
  );
}

/** The app icon's two frames without its tile, so it reads on the dark bar. */
function AppMark() {
  return (
    <svg viewBox="37 67 182 130" className="h-[26px] w-auto shrink-0" aria-hidden>
      <rect x="44" y="74" width="120" height="86" rx="14" fill="none" stroke="#4d5565" strokeWidth="14" />
      <rect x="92" y="104" width="120" height="86" rx="14" fill="none" stroke="var(--color-flamingo)" strokeWidth="14" />
    </svg>
  );
}
