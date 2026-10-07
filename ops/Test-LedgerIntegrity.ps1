<#
.SYNOPSIS
    Verifies the ClockInXtra audit ledger against exported digests.

.DESCRIPTION
    Reads every digest exported by Export-LedgerDigest.ps1 from
    -DigestDirectory and asks SQL Server to verify the ledger against all of
    them (job.usp_Maintenance_VerifyLedger).

    A pass means no audit or security-event row has been altered, removed or
    inserted out of order since those digests were taken. A failure means the
    ledger was changed outside the application: treat it as a security
    incident, preserve the database and the digests, and do not attempt a
    repair (threat TH-40).

    Run it on a schedule, and before audit evidence is handed to anyone.

    Exit codes: 0 verified, 2 VERIFICATION FAILED, 1 could not verify (error,
    no digests, unreachable server). A scheduler should alert on anything but 0.

.PARAMETER Server
    SQL Server instance.

.PARAMETER Database
    Database name. Defaults to ClockInXtra.

.PARAMETER DigestDirectory
    Directory holding the exported digest files.

.PARAMETER Credential
    SQL login for the maintenance account (app_jobs). Omit for Windows
    authentication.

.PARAMETER TrustServerCertificate
    Development only; see Export-LedgerDigest.ps1.

.EXAMPLE
    .\Test-LedgerIntegrity.ps1 -Server sql01.corp.local -DigestDirectory \\worm01\clockinxtra-ledger
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Server,
    [string] $Database = 'ClockInXtra',
    [Parameter(Mandatory = $true)] [string] $DigestDirectory,
    [System.Management.Automation.PSCredential] $Credential,
    [switch] $TrustServerCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'LedgerCommon.ps1')

try {
    $files = @(Get-ChildItem -LiteralPath $DigestDirectory -Filter 'clockinxtra-ledger-block*.json' -File |
        Sort-Object Name)

    if ($files.Count -eq 0) {
        throw "No digests found in '$DigestDirectory'. Nothing can be verified without them."
    }

    # Every digest must at least be a JSON object naming this database; a file
    # that is not is reported rather than skipped, since a damaged digest store
    # is itself worth knowing about.
    $digests = foreach ($file in $files) {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $parsed = $text | ConvertFrom-Json
        if ($parsed.database_name -ne $Database) {
            throw "Digest '$($file.Name)' is for database '$($parsed.database_name)', not '$Database'."
        }
        $text.Trim()
    }

    $array = '[' + ($digests -join ',') + ']'

    $connection = New-LedgerConnection -Server $Server -Database $Database `
        -Credential $Credential -TrustServerCertificate:$TrustServerCertificate

    try {
        $command = $connection.CreateCommand()
        $command.CommandType = [System.Data.CommandType]::StoredProcedure
        $command.CommandText = 'job.usp_Maintenance_VerifyLedger'

        # Verification rehashes the whole ledger; on a large database it takes
        # a while, and a timeout would be misread as a failure.
        $command.CommandTimeout = 3600

        [void] $command.Parameters.Add('@Digests', [System.Data.SqlDbType]::NVarChar, -1)
        $command.Parameters['@Digests'].Value = $array
        $message = $command.Parameters.Add('@FailureMessage', [System.Data.SqlDbType]::NVarChar, 4000)
        $message.Direction = [System.Data.ParameterDirection]::Output
        $result = $command.Parameters.Add('@ResultCode', [System.Data.SqlDbType]::Int)
        $result.Direction = [System.Data.ParameterDirection]::Output

        [void] $command.ExecuteNonQuery()

        switch ([int] $result.Value) {
            0 {
                Write-Output "Ledger VERIFIED against $($files.Count) digest(s), newest $($files[-1].Name)."
                exit 0
            }
            1080 {
                Write-Error ("LEDGER VERIFICATION FAILED against $($files.Count) digest(s): " +
                    "$($message.Value) Treat this as a security incident: preserve the database " +
                    "and the digest store, and do not attempt a repair.") -ErrorAction Continue
                exit 2
            }
            default {
                throw "job.usp_Maintenance_VerifyLedger returned result code $($result.Value)."
            }
        }
    }
    finally {
        $connection.Dispose()
    }
}
catch {
    Write-Error "Ledger verification could not be completed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
