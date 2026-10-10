using Attendance.Application.Abstractions;
using Attendance.Application.Features.Attendance;
using Attendance.Application.Features.Configuration;
using Attendance.Application.Features.Devices;
using Attendance.Application.Features.Locations;
using Attendance.Application.Services;
using Attendance.Infrastructure.Deployment;
using Attendance.Infrastructure.Persistence.Connection;
using Attendance.Infrastructure.Persistence.Repositories;
using Attendance.Infrastructure.Security;
using Attendance.Infrastructure.Security.Attestation;
using Attendance.Infrastructure.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Attendance.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the infrastructure and application services.
/// </summary>
/// <remarks>
/// <para>
/// One registration point, so the API and the administration portal cannot drift
/// into composing the same services differently — which is how one host ends up
/// with a cached policy provider and the other without, and nobody notices until
/// a setting change fails to take effect on half the nodes.
/// </para>
/// <para>
/// <b>Options are validated at startup</b> (<c>ValidateOnStart</c>), so a
/// misconfigured deployment fails immediately and visibly rather than at the
/// first clock-in of the morning.
/// </para>
/// </remarks>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers persistence, security and the mobile use cases.</summary>
    public static IServiceCollection AddAttendanceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<SqlServerOptions>, SqlServerOptionsValidator>());

        services.AddOptions<AttestationOptions>()
            .Bind(configuration.GetSection(AttestationOptions.SectionName));

        AddDatabaseDeployment(services, configuration, environment);

        services.TryAddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.TryAddSingleton<IClock, SystemClock>();

        AddDataProtection(services, configuration, environment);

        AddPersistence(services);
        AddSecurity(services);
        AddUseCases(services);

        return services;
    }

    /// <summary>
    /// Automatic database deployment, registered identically for both hosts.
    /// </summary>
    /// <remarks>
    /// Shared deliberately. Either host may be the first to start and find no
    /// database, so both need this; registering it in one place is what stops
    /// the API growing the ability and the portal silently losing it.
    /// <para>
    /// The default is ON in Development and OFF elsewhere. <c>Configure</c> runs
    /// before <c>Bind</c>, so an explicit <c>Database:AutoDeploy</c> still wins.
    /// See <see cref="DatabaseDeploymentOptions"/> for why the defaults differ.
    /// </para>
    /// </remarks>
    private static void AddDatabaseDeployment(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<DatabaseDeploymentOptions>()
            .Configure(options => options.AutoDeploy = environment.IsDevelopment())
            .Bind(configuration.GetSection(DatabaseDeploymentOptions.SectionName));

        services.TryAddSingleton(provider => new DatabaseDeployer(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseDeploymentOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SqlServerOptions>>()
                .Value.ConnectionString,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseDeployer>>()));
    }

    /// <summary>
    /// The shared, encrypted key ring that protects every TOTP secret; see
    /// <see cref="KeyRingConfiguration"/> for why a non-development host refuses
    /// to start without one.
    /// </summary>
    private static void AddDataProtection(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        KeyRingConfiguration.AddSharedKeyRing(services, configuration, environment);
        services.TryAddSingleton<ISecretProtector, DataProtectionSecretProtector>();
    }

    /// <summary>
    /// Registers the shared services plus the administration portal's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the mobile registration because the two applications run as
    /// <b>different database principals against different schemas</b>. The portal
    /// is denied the <c>mobile</c> schema and the API is denied <c>admin</c>, so a
    /// single registration would hand each application services it cannot
    /// actually execute — and the failure would appear as a permission error at
    /// the moment somebody used the feature, not at startup.
    /// </para>
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Host configuration.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="protectSecrets">
    /// Whether to configure the Data Protection key ring. <c>false</c> is for a
    /// command that only applies SQL scripts and then exits.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why the key ring is optional.</b> Configuring it loads the
    /// certificate that protects every authenticator secret, which the portal
    /// needs and <c>--deploy-database</c> does not: that command runs schema
    /// scripts and exits. Loading it anyway would mean the deployment identity
    /// had to be able to read that private key — and a deployment identity
    /// is the one an automated pipeline holds. Skipping it keeps the key
    /// readable by the service account alone.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAttendanceAdministration(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool protectSecrets = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<SqlServerOptions>, SqlServerOptionsValidator>());

        services.TryAddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.TryAddSingleton<IClock, SystemClock>();

        if (protectSecrets)
        {
            AddDataProtection(services, configuration, environment);
        }

        AddDatabaseDeployment(services, configuration, environment);

        // Shared with the API: hashing, TOTP, lockout counters and the security
        // event trail are the same mechanisms for both audiences.
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<ITotpVerifier, TotpVerifier>();
        services.TryAddSingleton<ISecurityEventRecorder, SecurityEventRecorder>();
        services.TryAddSingleton<IAuthenticationAttemptRepository, AuthenticationAttemptRepository>();

        services.TryAddSingleton<IAdministratorRepository, AdministratorRepository>();
        services.TryAddSingleton<AdministratorPolicyProvider>();
        services.TryAddSingleton<IAdministratorPolicyProvider>(
            provider => provider.GetRequiredService<AdministratorPolicyProvider>());

        services.TryAddSingleton<AdministratorAuthenticator>();
        services.TryAddSingleton<AdministratorPasswordChanger>();
        services.TryAddSingleton<IAdministratorManagementRepository, AdministratorManagementRepository>();

        services.TryAddSingleton<IDeviceAdministrationRepository, DeviceAdministrationRepository>();
        services.TryAddSingleton<ISettingAdministrationRepository, SettingAdministrationRepository>();
        services.TryAddSingleton<IMfaEnrolmentRepository, MfaEnrolmentRepository>();
        services.TryAddSingleton<IMobileUserAdministrationRepository, MobileUserAdministrationRepository>();
        services.TryAddSingleton<IReferenceListRepository, ReferenceListRepository>();
        services.TryAddSingleton<IOfficeLocationAdministrationRepository, OfficeLocationAdministrationRepository>();
        services.TryAddSingleton<IAttendanceCorrectionRepository, AttendanceCorrectionRepository>();
        services.TryAddSingleton<IReportingRepository, ReportingRepository>();

        // Enrolment protects a new secret and verifies the code that activates it.
        services.TryAddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        // Stateless repositories: a singleton is correct, and avoids allocating
        // one per request on the attendance path.
        services.TryAddSingleton<IAttendanceRepository, AttendanceRepository>();
        services.TryAddSingleton<IMobileUserRepository, MobileUserRepository>();
        services.TryAddSingleton<IDeviceRepository, DeviceRepository>();
        services.TryAddSingleton<IIdempotencyStore, IdempotencyStore>();
        services.TryAddSingleton<ISecurityEventRecorder, SecurityEventRecorder>();
        services.TryAddSingleton<IAuthenticationAttemptRepository, AuthenticationAttemptRepository>();
        services.TryAddSingleton<IMfaCredentialRepository, MfaCredentialRepository>();
        services.TryAddSingleton<IMobileConfigurationRepository, MobileConfigurationRepository>();
        services.TryAddSingleton<INonceStore, RequestNonceStore>();

        // These two hold a cache, so the singleton lifetime is what makes the
        // cache worth having. Both are registered by their concrete type as well,
        // so an administrative action can invalidate them directly.
        services.TryAddSingleton<OfficeLocationRepository>();
        services.TryAddSingleton<IOfficeLocationRepository>(
            provider => provider.GetRequiredService<OfficeLocationRepository>());

        services.TryAddSingleton<AttendancePolicyProvider>();
        services.TryAddSingleton<IAttendancePolicyProvider>(
            provider => provider.GetRequiredService<AttendancePolicyProvider>());
    }

    private static void AddSecurity(IServiceCollection services)
    {
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<ITotpVerifier, TotpVerifier>();
        services.TryAddSingleton<IRequestSignatureVerifier, EcdsaRequestSignatureVerifier>();
        services.TryAddSingleton<IEmployeeCredentialValidator, LocalEmployeeCredentialValidator>();

        services.TryAddSingleton<IAttestationRevocationList, FileAttestationRevocationList>();
        services.TryAddSingleton<AndroidKeyAttestationVerifier>();
        services.TryAddSingleton<AppleAppAttestVerifier>();
        services.TryAddSingleton<IDeviceAttestationVerifier, DeviceAttestationVerifier>();
    }

    private static void AddUseCases(IServiceCollection services)
    {
        services.TryAddSingleton<EmployeeAuthenticator>();

        services.TryAddSingleton<ClockInHandler>();
        services.TryAddSingleton<ClockOutHandler>();
        services.TryAddSingleton<UserStatusHandler>();
        services.TryAddSingleton<DeviceRegistrationHandler>();
        services.TryAddSingleton<DeviceStatusHandler>();
        services.TryAddSingleton<ValidateLocationHandler>();
        services.TryAddSingleton<GetMobileConfigurationHandler>();
    }
}
