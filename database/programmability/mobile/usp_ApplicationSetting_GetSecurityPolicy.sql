/*==============================================================================
  mobile.usp_ApplicationSetting_GetSecurityPolicy
  Phase : 6 (added during Phase 9, when the gap became visible)
  Called by: the API's policy provider, which caches the result briefly.

  Purpose
  -------
  Return the settings the API itself needs in order to apply policy before a
  request ever reaches the database: lockout thresholds, the signature skew
  window, the attestation requirements, the registration challenge lifetime and
  the location accuracy policy.

  WHY THIS IS NOT usp_ApplicationSetting_GetMobileRuntime
  ------------------------------------------------------
  That procedure answers the *device*, and returns only rows flagged
  IsMobileVisible. These settings are deliberately NOT mobile-visible: an
  attacker who learns the lockout threshold knows how many guesses are free, and
  one who learns the skew window knows how long a captured request stays
  replayable. This procedure answers the *server*, which already needs the values
  in order to enforce them.

  The two must stay separate. Widening the mobile procedure to cover these would
  put them on every handset in the organisation.

  WHY AN EXPLICIT KEY LIST
  ------------------------
  The IN list below is an allow-list, exactly like the IsMobileVisible flag is
  for the client. Returning "everything in the Security category" would silently
  start exporting any future setting somebody files under that category —
  including, one day, something that should not leave the database at all.

  Values may be NULL. A NULL means the business has not decided (see the seed
  script's header), and the application layer must treat it as undecided rather
  than substituting a default.

  Result codes
      0 Success
==============================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE mobile.usp_ApplicationSetting_GetSecurityPolicy
    @ResultCode INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        s.SettingKey,
        s.SettingValue,
        s.DataType
    FROM core.ApplicationSetting AS s
    WHERE s.SettingKey IN
    (
        'Location.AccuracyPolicy',
        'Location.MaxAcceptedAccuracyMeters',
        'Location.RejectMockedLocations',
        'Security.MobileLockoutThreshold',
        'Security.MobileLockoutMinutes',
        'Security.SignatureSkewSeconds',
        'Security.CollapseCredentialErrorCodes',
        'Security.RequireHardwareAttestationAndroid',
        'Security.RequireHardwareAttestationIos',
        'Security.DeviceRegistrationRequiresApproval',
        'Security.ChallengeLifetimeSeconds',
        'Security.TotpStepTolerance',
        'Mobile.MinimumAppVersion'
    )
    ORDER BY s.SettingKey;

    SET @ResultCode = 0;
END;
GO

PRINT 'mobile.usp_ApplicationSetting_GetSecurityPolicy deployed.';
GO
