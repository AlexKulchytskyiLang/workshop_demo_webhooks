import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { useAuth } from 'react-oidc-context';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { ApiError } from '../api/client';
import { tenancyApi, type Me } from '../api/tenancy';
import { clearSelectedTenant } from '../auth/selectedTenant';
import { AuthLayout } from './AuthLayout';

/**
 * Guards everything behind sign-in. After the identity provider says who you are, the API says which organisation you belong to
 * (/api/me). If that is not the one you picked, you get a clear stop screen instead of someone else's data.
 */
export function RequireSession() {
  const auth = useAuth();
  const location = useLocation();
  const me = useQuery<Me, ApiError>({
    queryKey: ['me'],
    queryFn: ({ signal }) => tenancyApi.me(signal),
    enabled: auth.isAuthenticated,
    retry: (count, error) => error.status >= 500 || error.status === 0 ? count < 2 : false,
    staleTime: 60_000,
  });

  // an expired or rejected token is not recoverable from here: drop the session and start again
  useEffect(() => {
    if (me.error?.status === 401) void auth.removeUser();
  }, [me.error, auth]);

  const signOut = () => {
    clearSelectedTenant();
    void auth.signoutRedirect();
  };

  if (auth.isLoading) {
    return <AuthLayout title="Loading…"><p className="help" role="status">Checking your session.</p></AuthLayout>;
  }
  if (!auth.isAuthenticated) return <Navigate to="/select-tenant" replace state={{ from: location.pathname }} />;
  if (me.isPending) {
    return <AuthLayout title="Loading your workspace…"><p className="help" role="status">One moment.</p></AuthLayout>;
  }

  if (me.error?.code === 'tenant_mismatch') {
    return (
      <AuthLayout title="This is not your organisation">
        <div className="banner amber" role="alert"><div className="grow">{me.error.message}</div></div>
        <p className="help">Sign out, then choose the organisation your account belongs to.</p>
        <button type="button" className="btn pri wide" onClick={signOut}>Sign out and choose again</button>
      </AuthLayout>
    );
  }

  if (me.error && me.error.status !== 401) {
    return (
      <AuthLayout title="We could not open your workspace">
        <div className="banner red" role="alert"><div className="grow">{me.error.message}</div></div>
        <div className="row">
          <button type="button" className="btn pri" onClick={() => void me.refetch()}>Try again</button>
          <button type="button" className="btn" onClick={signOut}>Sign out</button>
        </div>
      </AuthLayout>
    );
  }

  if (!me.data) return <Navigate to="/select-tenant" replace />;
  return <Outlet context={me.data} />;
}
