export function ProgressBar({ value, max }: { value: number; max: number }) {
  const percent = max > 0 ? Math.min(100, (value / max) * 100) : 0
  return (
    <div className="progress" role="progressbar" aria-valuenow={Math.round(percent)} aria-valuemin={0} aria-valuemax={100}>
      <div className="progress-fill" style={{ width: `${percent}%` }} />
      <span className="progress-text">
        {value}s / {max}s
      </span>
    </div>
  )
}
