<#
    Shared by Export-LedgerDigest.ps1 and Test-LedgerIntegrity.ps1.

    Uses System.Data.SqlClient, which ships with the .NET Framework, so the
    scripts run on a stock Windows Server with Windows PowerShell 5.1 and need
    nothing installed.
#>

function New-LedgerConnection {
    param(
        [Parameter(Mandatory = $true)] [string] $Server,
        [Parameter(Mandatory = $true)] [string] $Database,
        [System.Management.Automation.PSCredential] $Credential,
        [switch] $TrustServerCertificate
    )

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source'] = $Server
    $builder['Initial Catalog'] = $Database
    $builder['Encrypt'] = $true
    $builder['TrustServerCertificate'] = [bool] $TrustServerCertificate
    $builder['Application Name'] = 'ClockInXtra.LedgerMaintenance'

    if ($null -eq $Credential) {
        $builder['Integrated Security'] = $true
        $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString)
    }
    else {
        # SqlCredential keeps the password in a SecureString rather than in the
        # connection string, where it could surface in a log or an exception.
        $password = $Credential.Password.Copy()
        $password.MakeReadOnly()
        $sqlCredential = New-Object System.Data.SqlClient.SqlCredential($Credential.UserName, $password)
        $connection = New-Object System.Data.SqlClient.SqlConnection($builder.ConnectionString, $sqlCredential)
    }

    $connection.Open()
    return $connection
}
