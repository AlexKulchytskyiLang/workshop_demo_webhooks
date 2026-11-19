import { useState } from 'react';

type Theme = 'dark' | 'light';

function current(): Theme {
  return document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
}

/** Dark/light switch. The initial value is applied in index.html before the first paint; this only flips and remembers it. */
export function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>(current);
  const next = theme === 'dark' ? 'light' : 'dark';

  return (
    <button
      type="button"
      className="btn sm gh"
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
      onClick={() => {
        document.documentElement.dataset.theme = next;
        try {
          localStorage.setItem('ih.theme', next);
        } catch {
          /* not persisted */
        }
        setTheme(next);
      }}
    >
      <span aria-hidden="true">{theme === 'dark' ? '☀' : '☾'}</span> {theme === 'dark' ? 'Light' : 'Dark'}
    </button>
  );
}
