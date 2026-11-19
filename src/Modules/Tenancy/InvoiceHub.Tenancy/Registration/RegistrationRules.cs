using System.Net.Mail;
using System.Text.RegularExpressions;

namespace InvoiceHub.Tenancy.Registration;

public sealed record RegistrationRequest(
    string? TenantName,
    string? Slug,
    string? AdminEmail,
    string? AdminFirstName,
    string? AdminLastName,
    string? Password);

public sealed record RegistrationResponse(Guid TenantId, string Slug, string Name);

/// <summary>Input rules for tenant registration. The password policy mirrors the realm's (length 12 and not the username).</summary>
public static partial class RegistrationRules
{
    public const int MinPasswordLength = 12;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "api", "www", "portal", "keycloak", "system", "root", "invoicehub", "receiver", "console", "test", "demo-admin",
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$")]
    private static partial Regex SlugPattern();

    public static string NormalizeSlug(string? slug) => (slug ?? "").Trim().ToLowerInvariant();

    /// <summary>Returns field name to message for every problem; empty means valid.</summary>
    public static Dictionary<string, string[]> Validate(RegistrationRequest r)
    {
        var errors = new Dictionary<string, string[]>();

        var name = r.TenantName?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 120)
            errors["tenantName"] = ["Use 2 to 120 characters."];

        var slug = NormalizeSlug(r.Slug);
        if (!SlugPattern().IsMatch(slug))
            errors["slug"] = ["Use 3 to 40 lowercase letters, digits or hyphens, starting and ending with a letter or digit."];
        else if (Reserved.Contains(slug))
            errors["slug"] = ["This name is reserved."];

        if (!IsEmail(r.AdminEmail))
            errors["adminEmail"] = ["Enter a valid email address."];
        if (string.IsNullOrWhiteSpace(r.AdminFirstName) || r.AdminFirstName.Trim().Length > 60)
            errors["adminFirstName"] = ["Required, up to 60 characters."];
        if (string.IsNullOrWhiteSpace(r.AdminLastName) || r.AdminLastName.Trim().Length > 60)
            errors["adminLastName"] = ["Required, up to 60 characters."];

        var password = r.Password ?? "";
        var passwordProblems = new List<string>();
        if (password.Length < MinPasswordLength)
            passwordProblems.Add($"At least {MinPasswordLength} characters.");
        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit) || password.All(char.IsLetterOrDigit))
            passwordProblems.Add("Mix upper and lower case letters, a digit and a symbol.");
        if (!string.IsNullOrEmpty(r.AdminEmail) && password.Equals(r.AdminEmail, StringComparison.OrdinalIgnoreCase))
            passwordProblems.Add("Must not equal the email.");
        if (passwordProblems.Count > 0)
            errors["password"] = [.. passwordProblems];

        return errors;
    }

    internal static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
            return false;

        return MailAddress.TryCreate(value.Trim(), out var address) &&
               address.Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) && value.Contains('.');
    }
}
