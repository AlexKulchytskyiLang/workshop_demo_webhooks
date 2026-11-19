import { useMutation } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useAuth } from 'react-oidc-context';
import { Link } from 'react-router-dom';
import { ApiError } from '../api/client';
import { tenancyApi, type RegistrationRequest, type RegistrationResponse } from '../api/tenancy';
import { setSelectedTenant } from '../auth/selectedTenant';
import { AuthLayout } from '../shell/AuthLayout';
import { suggestSlug, validateRegistration, type FieldErrors } from './registrationValidation';

type Values = RegistrationRequest & { confirmPassword: string };

const EMPTY: Values = { tenantName: '', slug: '', adminFirstName: '', adminLastName: '', adminEmail: '', password: '', confirmPassword: '' };

const FIELD_ORDER: (keyof Values)[] = ['tenantName', 'slug', 'adminFirstName', 'adminLastName', 'adminEmail', 'password', 'confirmPassword'];

export function Register() {
  const auth = useAuth();
  const [values, setValues] = useState<Values>(EMPTY);
  const [slugTouched, setSlugTouched] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [created, setCreated] = useState<RegistrationResponse | null>(null);
  const [showPassword, setShowPassword] = useState(false);
  const form = useRef<HTMLFormElement>(null);

  const register = useMutation({ mutationFn: (body: RegistrationRequest) => tenancyApi.register(body) });

  // focus the first invalid field so keyboard and screen reader users land on the problem
  useEffect(() => {
    const first = FIELD_ORDER.find((f) => errors[f]);
    if (first) form.current?.querySelector<HTMLElement>(`[name="${first}"]`)?.focus();
  }, [errors]);

  function set<K extends keyof Values>(key: K, value: Values[K]) {
    setValues((v) => {
      const next = { ...v, [key]: value };
      if (key === 'tenantName' && !slugTouched) next.slug = suggestSlug(String(value));
      return next;
    });
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setFormError(null);
    const found = validateRegistration(values);
    setErrors(found);
    if (Object.keys(found).length > 0) return;

    const { confirmPassword: _ignored, ...body } = values;
    try {
      setCreated(await register.mutateAsync({ ...body, tenantName: body.tenantName.trim(), adminEmail: body.adminEmail.trim() }));
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.code === 'slug_taken') setErrors({ slug: 'This address is already taken. Choose another one.' });
        else if (err.code === 'email_taken') setErrors({ adminEmail: 'An account with this email already exists.' });
        else if (err.errors) setErrors(Object.fromEntries(Object.entries(err.errors).map(([k, v]) => [k, v.join(' ')])) as FieldErrors);
        else setFormError(err.message);
      } else setFormError('Something went wrong. Nothing was created; please try again.');
    }
  }

  if (created) {
    return (
      <AuthLayout title="Your organisation is ready">
        <div className="banner green" role="status">
          <div className="grow"><b>{created.name}</b> was created and you are its owner.</div>
        </div>
        <p className="help">Sign in with the email and password you just chose. You can invite colleagues later.</p>
        <button
          type="button"
          className="btn pri wide"
          onClick={() => {
            setSelectedTenant(created.slug, created.name);
            void auth.signinRedirect();
          }}
        >
          Sign in to {created.name}
        </button>
      </AuthLayout>
    );
  }

  const field = (name: keyof Values, label: string, props: React.InputHTMLAttributes<HTMLInputElement> = {}, help?: string) => (
    <div className="field">
      <label className="f" htmlFor={`f-${name}`}>{label}</label>
      <input
        id={`f-${name}`}
        name={name}
        className={`input${errors[name] ? ' bad' : ''}`}
        value={values[name]}
        onChange={(e) => set(name, e.target.value)}
        aria-invalid={errors[name] ? true : undefined}
        aria-describedby={`d-${name}`}
        {...props}
      />
      <div id={`d-${name}`} className={errors[name] ? 'error-text' : 'help'} role={errors[name] ? 'alert' : undefined}>
        {errors[name] ?? help}
      </div>
    </div>
  );

  return (
    <AuthLayout title="Register your organisation" subtitle="You become the owner. Sign-in uses the same identity service as every other account.">
      <form ref={form} onSubmit={(e) => void submit(e)} noValidate>
        {field('tenantName', 'Organisation name', { autoComplete: 'organization', autoFocus: true })}
        {field('slug', 'Organisation address', {
          autoComplete: 'off',
          spellCheck: false,
          onChange: (e) => {
            setSlugTouched(true);
            set('slug', e.target.value.toLowerCase());
          },
        }, 'Lowercase letters, digits and hyphens. It cannot be changed later.')}
        <div className="row two">
          {field('adminFirstName', 'First name', { autoComplete: 'given-name' })}
          {field('adminLastName', 'Last name', { autoComplete: 'family-name' })}
        </div>
        {field('adminEmail', 'Work email', { type: 'email', autoComplete: 'email' }, 'This is your sign-in name.')}
        {field('password', 'Password', { type: showPassword ? 'text' : 'password', autoComplete: 'new-password' },
          'At least 12 characters with upper and lower case letters, a digit and a symbol.')}
        {field('confirmPassword', 'Repeat the password', { type: showPassword ? 'text' : 'password', autoComplete: 'new-password' })}

        <label className="row check">
          <input type="checkbox" checked={showPassword} onChange={(e) => setShowPassword(e.target.checked)} /> Show passwords
        </label>

        {formError && <div className="banner red" role="alert"><div className="grow">{formError}</div></div>}

        <button type="submit" className="btn pri wide" disabled={register.isPending}>
          {register.isPending ? 'Creating…' : 'Create organisation'}
        </button>
        <p className="help center">Already registered? <Link to="/select-tenant">Sign in</Link></p>
      </form>
    </AuthLayout>
  );
}
