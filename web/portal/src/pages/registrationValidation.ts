import type { RegistrationRequest } from '../api/tenancy';

export type FieldErrors = Partial<Record<keyof RegistrationRequest | 'confirmPassword', string>>;

const SLUG = /^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Turns an organisation name into a suggested address: "Northwind Traders!" becomes "northwind-traders". */
export function suggestSlug(name: string): string {
  return name
    .toLowerCase()
    .replace(/ł/g, 'l').replace(/đ/g, 'd').replace(/ø/g, 'o').replace(/æ/g, 'ae').replace(/ß/g, 'ss') // letters that do not decompose
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40)
    .replace(/-+$/g, '');
}

/** Mirrors the API's rules so mistakes are caught before the request; the API stays the authority. */
export function validateRegistration(v: RegistrationRequest & { confirmPassword: string }): FieldErrors {
  const e: FieldErrors = {};
  if (v.tenantName.trim().length < 2) e.tenantName = 'Enter your organisation name (at least 2 characters).';
  if (!SLUG.test(v.slug)) e.slug = 'Use 3 to 40 lowercase letters, digits or hyphens, starting and ending with a letter or digit.';
  if (!v.adminFirstName.trim()) e.adminFirstName = 'Enter your first name.';
  if (!v.adminLastName.trim()) e.adminLastName = 'Enter your last name.';
  if (!EMAIL.test(v.adminEmail.trim())) e.adminEmail = 'Enter a valid email address.';

  const p = v.password;
  if (p.length < 12) e.password = 'Use at least 12 characters.';
  else if (!/[a-z]/.test(p) || !/[A-Z]/.test(p) || !/[0-9]/.test(p) || !/[^A-Za-z0-9]/.test(p))
    e.password = 'Mix upper and lower case letters, a digit and a symbol.';
  if (v.confirmPassword !== p) e.confirmPassword = 'The passwords do not match.';
  return e;
}
