import { useEffect, useState } from 'react';

/** The value, but only after it has stopped changing for a moment (used for checks that call the server while the person types). */
export function useDebounced<T>(value: T, delayMs = 450): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}
