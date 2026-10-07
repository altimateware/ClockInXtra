<#
.SYNOPSIS
    Builds the solution and runs every .NET test assembly, reporting a combined
    result.

.DESCRIPTION
    Test projects are executed DIRECTLY rather than through `dotnet test`.

    Why: xunit.v3 4.0.0 ships Microsoft.Testing.Platform 2.x, which dropped the
    legacy VSTest path on the .NET 10 SDK. Each test project therefore builds as
    a self-hosting executable, which is the runner it was built to use.

    `dotnet test` also works, in the MTP mode global.json selects, provided the
    project is passed with --project:

        dotnet test --project tests/Attendance.Domain.Tests

    The positional form (`dotnet test tests/Attendance.Domain.Tests`) is NOT
    equivalent in MTP mode: it runs zero tests and exits with code 5. An earlier
    version of this note blamed `dotnet test` itself; the positional argument
    was the cause (found 2026-09-18). The exit code is what CI should gate on.

    Projects with no test files yet are skipped rather than run, because MTP
    treats "zero tests" as an error and an empty placeholder project would fail
    the build for no useful reason. A directory under tests/ that holds no
    project file is skipped for a plainer reason: tests/Shared is source linked
    into other projects, not an assembly of its own.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.PARAMETER Filter
    Optional substring matched against project names, e.g. -Filter Domain.

.EXAMPLE
    ./scripts/run-tests.ps1
    ./scripts/run-tests.ps1 -Configuration Release -Filter Domain
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [string] $Filter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'ClockInXtra.slnx'

Write-Host "ClockInXtra test run" -ForegroundColor Cyan
Write-Host "Repository   : $repoRoot"
Write-Host "Configuration: $Configuration"
Write-Host ''

# ---------------------------------------------------------------- build ------
Write-Host 'Building solution...' -ForegroundColor Cyan
dotnet build $solution --configuration $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Build failed. No tests were run.' -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------------- discover ------
$testProjects = Get-ChildItem (Join-Path $repoRoot 'tests') -Directory |
    Where-Object { -not $Filter -or $_.Name -like "*$Filter*" }

$results = [System.Collections.Generic.List[pscustomobject]]::new()

foreach ($project in $testProjects) {
    # Not everything under tests/ is a test. Attendance.LoadTest is a capacity
    # harness: running it here would take minutes, write thousands of rows and
    # move the attendance window while it worked. Only projects that declare
    # themselves test projects are run.
    $projectFile = Join-Path $project.FullName "$($project.Name).csproj"

    # A directory with no project file is not a project at all. tests/Shared
    # holds source that is linked into several test projects, so it builds no
    # assembly of its own and there is nothing here to run.
    if (-not (Test-Path $projectFile)) {
        $results.Add([pscustomobject]@{ Project = $project.Name; Outcome = 'SKIPPED (not a project)'; ExitCode = 0 })
        continue
    }

    if (-not (Select-String -Path $projectFile -Pattern '<IsTestProject>\s*true' -Quiet)) {
        $results.Add([pscustomobject]@{ Project = $project.Name; Outcome = 'SKIPPED (not a test project)'; ExitCode = 0 })
        continue
    }

    # A project with no test source is scaffolding for a later phase. Skip it:
    # MTP reports zero tests as an error, which would be noise, not a finding.
    $sourceFiles = Get-ChildItem $project.FullName -Filter '*.cs' -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

    if ($sourceFiles.Count -eq 0) {
        $results.Add([pscustomobject]@{ Project = $project.Name; Outcome = 'SKIPPED (no tests yet)'; ExitCode = 0 })
        continue
    }

    $exe = Join-Path $project.FullName "bin\$Configuration\net10.0\$($project.Name).exe"
    if (-not (Test-Path $exe)) {
        $results.Add([pscustomobject]@{ Project = $project.Name; Outcome = 'MISSING EXECUTABLE'; ExitCode = 1 })
        continue
    }

    Write-Host ''
    Write-Host "Running $($project.Name)..." -ForegroundColor Cyan
    & $exe
    $code = $LASTEXITCODE

    $results.Add([pscustomobject]@{
        Project  = $project.Name
        Outcome  = if ($code -eq 0) { 'PASSED' } else { 'FAILED' }
        ExitCode = $code
    })
}

# --------------------------------------------------------------- report ------
Write-Host ''
Write-Host 'Summary' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

$failed = @($results | Where-Object { $_.ExitCode -ne 0 })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) test project(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All test projects passed.' -ForegroundColor Green
exit 0
