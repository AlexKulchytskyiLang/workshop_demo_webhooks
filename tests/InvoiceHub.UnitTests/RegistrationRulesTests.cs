using InvoiceHub.Tenancy.Registration;

namespace InvoiceHub.UnitTests;

public class RegistrationRulesTests
{
    private static RegistrationRequest Valid() => new("Northwind Traders", "northwind", "owner@northwind.test", "Nancy", "Davolio", "Str0ng!Passw0rd");

    [Fact]
    public void A_complete_request_is_valid() => Assert.Empty(RegistrationRules.Validate(Valid()));

    [Theory]
    [InlineData("ab")]                       // too short
    [InlineData("-bad")]                     // starts with a hyphen
    [InlineData("bad-")]                     // ends with a hyphen
    [InlineData("Has Space")]
    [InlineData("under_score")]
    [InlineData("ünicode")]
    public void Malformed_slugs_are_rejected(string slug) =>
        Assert.Contains("slug", RegistrationRules.Validate(Valid() with { Slug = slug }).Keys);

    [Theory]
    [InlineData("admin")]
    [InlineData("API")]
    [InlineData("keycloak")]
    public void Reserved_slugs_are_rejected_case_insensitively(string slug) =>
        Assert.Contains("slug", RegistrationRules.Validate(Valid() with { Slug = slug }).Keys);

    [Fact]
    public void Slugs_are_normalised_before_validation() =>
        Assert.Empty(RegistrationRules.Validate(Valid() with { Slug = "  NorthWind  " }));

    [Theory]
    [InlineData("short1!A")]                 // under 12 characters
    [InlineData("alllowercase12345!")]       // no upper case
    [InlineData("ALLUPPERCASE12345!")]       // no lower case
    [InlineData("NoDigitsHere!!!!!")]        // no digit
    [InlineData("NoSymbolsHere12345")]       // no symbol
    public void Weak_passwords_are_rejected(string password) =>
        Assert.Contains("password", RegistrationRules.Validate(Valid() with { Password = password }).Keys);

    [Fact]
    public void A_password_equal_to_the_email_is_rejected() =>
        Assert.Contains("password", RegistrationRules.Validate(Valid() with { AdminEmail = "Aa1!Aa1!Aa1!@x.io", Password = "Aa1!Aa1!Aa1!@x.io" }).Keys);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("two@@example.com")]
    [InlineData("missingdot@localhost")]
    public void Invalid_emails_are_rejected(string? email) =>
        Assert.Contains("adminEmail", RegistrationRules.Validate(Valid() with { AdminEmail = email }).Keys);

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var errors = RegistrationRules.Validate(new RegistrationRequest("", "x", "nope", "", "", "weak"));
        Assert.Equal(["adminEmail", "adminFirstName", "adminLastName", "password", "slug", "tenantName"], errors.Keys.Order().ToArray());
    }

    [Fact]
    public void The_demo_password_satisfies_the_registration_policy()
    {
        var seed = InvoiceHub.Tenancy.Seeding.SeedData.Load();
        Assert.Empty(RegistrationRules.Validate(Valid() with { Password = seed.DemoPassword }));
    }
}
