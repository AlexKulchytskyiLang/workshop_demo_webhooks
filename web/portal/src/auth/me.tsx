import { createContext, useContext, type ReactNode } from 'react';
import type { Me } from '../api/tenancy';

const MeContext = createContext<Me | null>(null);

export function MeProvider({ me, children }: { me: Me; children: ReactNode }) {
  return <MeContext.Provider value={me}>{children}</MeContext.Provider>;
}

export function useMe(): Me {
  const me = useContext(MeContext);
  if (!me) throw new Error('useMe must be used inside the signed-in shell.');
  return me;
}

/** What the signed-in person's role allows. Buttons the role cannot use are hidden or explained; the API enforces it regardless. */
export function useCan() {
  const granted = new Set(useMe().permissions);
  return (permission: string) => granted.has(permission);
}
