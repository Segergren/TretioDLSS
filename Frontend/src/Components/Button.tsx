import { ButtonHTMLAttributes, ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';

type Variant = 'default' | 'ghost' | 'danger';
type Size = 'md' | 'sm';

interface Props extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  icon?: LucideIcon;
  iconOnly?: boolean;
  children?: ReactNode;
}

const variants: Record<Variant, string> = {
  default: 'bg-white/8 border-white/10 text-chalk enabled:hover:bg-white/12',
  ghost: 'bg-transparent border-transparent text-haze enabled:hover:bg-white/8 enabled:hover:text-chalk',
  danger: 'bg-error/16 border-error text-error enabled:hover:bg-error/10',
};

export function Button({
  variant = 'default',
  size = 'md',
  icon: Icon,
  iconOnly,
  className = '',
  children,
  ...rest
}: Props) {
  const height = size === 'md' ? 'h-10' : 'h-8';
  const pad = iconOnly ? (size === 'md' ? 'w-10' : 'w-8') : size === 'md' ? 'px-4' : 'px-3';
  const shape = 'rounded-full';
  const text = size === 'md' ? 'text-[0.9375rem]' : 'text-[0.8125rem]';
  const iconSize = size === 'md' ? 18 : 16;
  return (
    <button
      type="button"
      className={`inline-flex shrink-0 items-center justify-center gap-2 border font-medium leading-none whitespace-nowrap transition-colors duration-200 disabled:opacity-50 ${height} ${pad} ${shape} ${text} ${variants[variant]} ${className}`}
      {...rest}
    >
      {Icon && <Icon size={iconSize} strokeWidth={2} className="shrink-0" aria-hidden />}
      {children}
    </button>
  );
}
