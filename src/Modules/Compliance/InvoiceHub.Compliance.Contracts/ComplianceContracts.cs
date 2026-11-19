namespace InvoiceHub.Compliance.Contracts;

/// <summary>The outcome of an identity check, with the personal data it was about. The personal data stays in the Compliance module; the event says only that a check finished.</summary>
public sealed record CompleteKyc(string SubjectName, DateOnly DateOfBirth, string DocumentNumber, string Country, string Decision = "approved", int RiskScore = 12);

public sealed record KycRef(Guid VerificationId, string PublicId, string Decision);

/// <summary>Identity verification (KYC). The demo's stand-in for a screening provider: it records the result and announces it.</summary>
public interface IKycCommands
{
    /// <summary>Stores the verification and publishes <c>kyc.completed</c> in one transaction. The event carries the verification id and the decision, never a name or a document number.</summary>
    Task<KycRef> CompleteAsync(CompleteKyc command, CancellationToken ct = default);
}
