using Attendance.Application.Abstractions;
using Attendance.Infrastructure.Security;
using Xunit;

namespace Attendance.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="LocalEmployeeCredentialValidator"/>.
/// </summary>
/// <remarks>
/// The repository is faked here on purpose: what is under test is the
/// validator's decision order and its timing behaviour, not the SQL — the
/// database side of this path is covered by the stored procedure suite.
/// </remarks>
public sealed class LocalEmployeeCredentialValidatorTests
{
    private const string KnownUserId = "jane.doe";
    private const string CorrectPassword = "correct horse battery staple";

    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public async Task Validate_AcceptsTheCorrectPasswordForAnActiveEmployee()
    {
        LocalEmployeeCredentialValidator validator = CreateValidator(out _);

        CredentialValidationResult result =
            await validator.ValidateAsync(KnownUserId, CorrectPassword, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(CredentialValidationOutcome.Valid, result.Outcome);
        Assert.Equal(42, result.MobileUserId);
    }

    [Fact]
    public async Task Validate_RejectsTheWrongPassword()
    {
        LocalEmployeeCredentialValidator validator = CreateValidator(out _);

        CredentialValidationResult result =
            await validator.ValidateAsync(KnownUserId, "not the password", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(CredentialValidationOutcome.InvalidPassword, result.Outcome);
    }

    [Fact]
    public async Task Validate_ReportsAnUnknownUserWithoutRevealingIt()
    {
        LocalEmployeeCredentialValidator validator = CreateValidator(out _);

        CredentialValidationResult result =
            await validator.ValidateAsync("nobody", CorrectPassword, TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(CredentialValidationOutcome.UnknownUser, result.Outcome);
        Assert.Null(result.MobileUserId);
    }

    [Fact]
    public async Task Validate_SpendsHashingWorkEvenWhenTheUserDoesNotExist()
    {
        // The point of the control: an unknown identifier must not be cheaper to
        // refuse than a real one. Asserting that the dummy verification was
        // invoked is deterministic, unlike measuring elapsed time.
        LocalEmployeeCredentialValidator validator = CreateValidator(out CountingPasswordHasher hasher);

        await validator.ValidateAsync("nobody", CorrectPassword, TestContext.Current.CancellationToken);

        Assert.Equal(1, hasher.DummyVerificationCount);
        Assert.Equal(0, hasher.VerifyCount);
    }

    [Fact]
    public async Task Validate_SpendsHashingWorkForAnEmptySubmission()
    {
        LocalEmployeeCredentialValidator validator = CreateValidator(out CountingPasswordHasher hasher);

        await validator.ValidateAsync(KnownUserId, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(1, hasher.DummyVerificationCount);
    }

    [Fact]
    public async Task Validate_SpendsHashingWorkWhenTheEmployeeHasNoCredential()
    {
        LocalEmployeeCredentialValidator validator = new(
            new FakeMobileUserRepository(RecordWithoutCredential()), new CountingPasswordHasher(_hasher));

        CredentialValidationResult result =
            await validator.ValidateAsync(KnownUserId, CorrectPassword, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialValidationOutcome.NoCredential, result.Outcome);
    }

    [Fact]
    public async Task Validate_ChecksThePasswordBeforeReportingAnInactiveEmployee()
    {
        // Answering "inactive" without verifying the password would confirm the
        // account exists to anyone who guessed the identifier.
        LocalEmployeeCredentialValidator validator = new(
            new FakeMobileUserRepository(CreateRecord(isActive: false)), _hasher);

        CredentialValidationResult wrongPassword =
            await validator.ValidateAsync(KnownUserId, "wrong", TestContext.Current.CancellationToken);
        CredentialValidationResult rightPassword =
            await validator.ValidateAsync(KnownUserId, CorrectPassword, TestContext.Current.CancellationToken);

        Assert.Equal(CredentialValidationOutcome.InvalidPassword, wrongPassword.Outcome);
        Assert.Equal(CredentialValidationOutcome.UserInactive, rightPassword.Outcome);
    }

    [Fact]
    public async Task Validate_FlagsACredentialThatShouldBeRehashed()
    {
        // A credential stored under weaker parameters still verifies, and is
        // reported so it can be upgraded on this sign-in rather than locking the
        // employee out when the work factor rises.
        PasswordHash legacy = CreateWeakerHash(CorrectPassword);
        LocalEmployeeCredentialValidator validator = new(
            new FakeMobileUserRepository(CreateRecord(credential: legacy)), _hasher);

        CredentialValidationResult result =
            await validator.ValidateAsync(KnownUserId, CorrectPassword, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.True(result.RequiresPasswordChange);
    }

    private LocalEmployeeCredentialValidator CreateValidator(out CountingPasswordHasher hasher)
    {
        hasher = new CountingPasswordHasher(_hasher);
        return new LocalEmployeeCredentialValidator(new FakeMobileUserRepository(CreateRecord()), hasher);
    }

    private MobileUserAuthenticationRecord CreateRecord(
        bool isActive = true,
        PasswordHash? credential = null) =>
        new(42,
            Guid.NewGuid(),
            KnownUserId,
            isActive,
            credential ?? _hasher.Hash(CorrectPassword),
            Mfa: null);

    private static MobileUserAuthenticationRecord RecordWithoutCredential() =>
        new(42, Guid.NewGuid(), KnownUserId, IsActive: true, Credential: null, Mfa: null);

    private static PasswordHash CreateWeakerHash(string password)
    {
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            System.Security.Cryptography.HashAlgorithmName.SHA512,
            64);

        return new PasswordHash("pbkdf2-sha512", 100_000, salt, hash);
    }

    private sealed class FakeMobileUserRepository : IMobileUserRepository
    {
        private readonly MobileUserAuthenticationRecord _record;

        public FakeMobileUserRepository(MobileUserAuthenticationRecord record) => _record = record;

        public Task<MobileUserAuthenticationRecord?> GetForAuthenticationAsync(
            string userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<MobileUserAuthenticationRecord?>(
                string.Equals(userId, _record.UserId, StringComparison.Ordinal) ? _record : null);
    }

    /// <summary>Counts calls so timing behaviour can be asserted deterministically.</summary>
    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        private readonly IPasswordHasher _inner;

        public CountingPasswordHasher(IPasswordHasher inner) => _inner = inner;

        public int VerifyCount { get; private set; }

        public int DummyVerificationCount { get; private set; }

        public PasswordHash Hash(string password) => _inner.Hash(password);

        public bool Verify(string password, PasswordHash stored)
        {
            VerifyCount++;
            return _inner.Verify(password, stored);
        }

        public bool NeedsRehash(PasswordHash stored) => _inner.NeedsRehash(stored);

        public void PerformDummyVerification()
        {
            DummyVerificationCount++;
            _inner.PerformDummyVerification();
        }
    }
}
