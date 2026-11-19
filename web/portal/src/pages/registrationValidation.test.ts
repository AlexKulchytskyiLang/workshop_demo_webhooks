import { suggestSlug, validateRegistration } from './registrationValidation';

const valid = {
  tenantName: 'Northwind Traders', slug: 'northwind', adminFirstName: 'Nancy', adminLastName: 'Davolio',
  adminEmail: 'nancy@northwind.test', password: 'Str0ng!Passw0rd', confirmPassword: 'Str0ng!Passw0rd',
};

describe('suggestSlug', () => {
  it.each([
    ['Northwind Traders', 'northwind-traders'],
    ['  Contoso, Ltd.  ', 'contoso-ltd'],
    ['Zażółć Gęślą', 'zazolc-gesla'],
    ['ÅÄÖ Café', 'aao-cafe'],
    ['A'.repeat(60), 'a'.repeat(40)],
  ])('turns %j into %j', (name, slug) => expect(suggestSlug(name)).toBe(slug));
});

describe('validateRegistration', () => {
  it('accepts a complete request', () => expect(validateRegistration(valid)).toEqual({}));

  it.each(['ab', '-bad', 'bad-', 'Has Space', 'under_score'])('rejects the address %j', (slug) =>
    expect(validateRegistration({ ...valid, slug }).slug).toBeTruthy());

  it.each(['short1!A', 'alllowercase12345!', 'ALLUPPERCASE12345!', 'NoDigitsHere!!!!!', 'NoSymbolsHere12345'])('rejects the weak password %j', (password) =>
    expect(validateRegistration({ ...valid, password, confirmPassword: password }).password).toBeTruthy());

  it('rejects a mismatching confirmation', () =>
    expect(validateRegistration({ ...valid, confirmPassword: 'Different1!Pass' }).confirmPassword).toBeTruthy());

  it.each(['', 'nope', 'a@b', 'a b@c.d'])('rejects the email %j', (adminEmail) =>
    expect(validateRegistration({ ...valid, adminEmail }).adminEmail).toBeTruthy());

  it('reports every problem at once', () => {
    const e = validateRegistration({ tenantName: '', slug: '', adminFirstName: '', adminLastName: '', adminEmail: '', password: '', confirmPassword: 'x' });
    expect(Object.keys(e).sort()).toEqual(['adminEmail', 'adminFirstName', 'adminLastName', 'confirmPassword', 'password', 'slug', 'tenantName']);
  });
});
