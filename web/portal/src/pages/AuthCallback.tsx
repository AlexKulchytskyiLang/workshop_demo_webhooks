import { useAuth } from 'react-oidc-context';
import { Link, Navigate } from 'react-router-dom';
import { AuthLayout } from '../shell/AuthLayout';

/** The page Keycloak returns to. The auth provider completes the code exchange; this only shows progress or the failure. */
export function AuthCallback() {
  const auth = useAuth();

  if (auth.isAuthenticated) return <Navigate to="/" replace />;

  if (auth.error) {
    return (
      <AuthLayout title="Sign-in did not complete">
        <div className="banner red" role="alert"><div className="grow">{auth.error.message}</div></div>
        <p className="help">This can happen when the sign-in page was open for too long. Start again.</p>
        <Link className="btn pri wide" to="/select-tenant">Back to sign in</Link>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout title="Signing you in…">
      <p className="help" role="status">One moment, completing the secure sign-in.</p>
    </AuthLayout>
  );
}
