<#
.SYNOPSIS
    Exports a ClockInXtra audit-ledger digest to organisation-controlled storage.

.DESCRIPTION
    Calls job.usp_Maintenance_GenerateLedgerDigest and writes the digest it
    returns to -DigestDirectory as one JSON file per run.

    The digest is only worth anything if it is kept where the people who run
    the database server cannot change it. -DigestDirectory should therefore be
    write-once (WORM) storage, or at minimum a share on a different server
    that the SQL Server administrators and service account can write to but
    not modify or delete from. The audit ledger is tamper-evident only against
    someone who cannot also rewrite these files (threat TH-40).

    Schedule it (Task Scheduler) at an interval that bounds how much audit
    history could be rewritten undetected — hourly is a reasonable start.
    Nothing is written when the database has no ledger blocks yet.

    Exit codes: 0 digest written (or nothing to write), 1 error.

.PARAMETER Server
    SQL Server instance, e.g. sql01.corp.local or sql01\ATTENDANCE.

.PARAMETER Database
    Database name. Defaults to ClockInXtra.

.PARAMETER DigestDirectory
    Destination directory, typically a UNC path to write-once storage.

.PARAMETER Credential
    SQL login for the maintenance account (app_jobs). Omit to use the Windows
    identity the task runs as. The password is never written anywhere by this
    script.

.PARAMETER TrustServerCertificate
    Development only: accept a certificate that does not chain to a trusted
    root. Production must present a certificate from the organisation's PKI.

.EXAMPLE
    .\Export-LedgerDigest.ps1 -Server sql01.corp.local -DigestDirectory \\worm01\clockinxtra-ledger
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
    if (-not (Test-Path -LiteralPath $DigestDirectory -PathType Container)) {
        throw "Digest directory '$DigestDirectory' does not exist or is not reachable."
    }

    $connection = New-LedgerConnection -Server $Server -Database $Database `
        -Credential $Credential -TrustServerCertificate:$TrustServerCertificate

    try {
        $command = $connection.CreateCommand()
        $command.CommandType = [System.Data.CommandType]::StoredProcedure
        $command.CommandText = 'job.usp_Maintenance_GenerateLedgerDigest'
        $command.CommandTimeout = 120

        $digest = $command.Parameters.Add('@Digest', [System.Data.SqlDbType]::NVarChar, -1)
        $digest.Direction = [System.Data.ParameterDirection]::Output
        $result = $command.Parameters.Add('@ResultCode', [System.Data.SqlDbType]::Int)
        $result.Direction = [System.Data.ParameterDirection]::Output

        [void] $command.ExecuteNonQuery()

        if ($result.Value -ne 0) {
            throw "job.usp_Maintenance_GenerateLedgerDigest returned result code $($result.Value)."
        }

        if ($digest.Value -is [System.DBNull]) {
            Write-Output 'The database has no ledger blocks yet; nothing to export.'
            exit 0
        }

        $json = [string] $digest.Value
        $parsed = $json | ConvertFrom-Json

        # One file per digest, named so that a listing sorts by block. A file is
        # never overwritten: on write-once storage that would fail anyway, and
        # anywhere else it would destroy the evidence this exists to keep.
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
        $name = 'clockinxtra-ledger-block{0:D12}-{1}.json' -f [long] $parsed.block_id, $stamp
        $path = Join-Path $DigestDirectory $name

        if (Test-Path -LiteralPath $path) {
            throw "Refusing to overwrite existing digest '$path'."
        }

        [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))

        Write-Output "Ledger digest for block $($parsed.block_id) written to $path"
        exit 0
    }
    finally {
        $connection.Dispose()
    }
}
catch {
    Write-Error "Ledger digest export failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
