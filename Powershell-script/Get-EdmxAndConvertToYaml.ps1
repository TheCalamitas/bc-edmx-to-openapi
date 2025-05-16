[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$BuildAPIBaseURL,

    [Parameter(Mandatory = $true)]
    [string]$BuildAPIBaseUsername,

    [Parameter(Mandatory = $true)]
    [SecureString]$BuildAPIPassword,

    [Parameter(Mandatory = $true)]
    [string]$DeploymentAPIBaseURL,

    [Parameter(Mandatory = $true)]
    [string]$DeploymentAuthenticationType,

    [string]$EdmxFilePath = "OutputEDMX/edmx.xml"
)

$ErrorActionPreference = 'Stop'

# Use BuildAPIBaseURL for fetching metadata
$CleanedBuildAPIBaseURL = $BuildAPIBaseURL.TrimEnd('/')
$MetadataUrl = "$CleanedBuildAPIBaseURL/" + '$metadata?$schemaversion=2.0'

$EdmxOutputDir = Split-Path -Path $EdmxFilePath -Parent
if ($EdmxOutputDir -and -not (Test-Path -Path $EdmxOutputDir)) {
    Write-Verbose "Creating EDMX output directory: $EdmxOutputDir"
    New-Item -ItemType Directory -Path $EdmxOutputDir -Force | Out-Null
}

function Get-NavUserCredential {
    param (
        [string]$User,
        [SecureString]$Pass
    )
    return New-Object System.Management.Automation.PSCredential($User, $Pass)
}

function Fetch-AndSaveEdmx {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Url,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$Credential,
        [Parameter(Mandatory = $true)]
        [string]$LocalEdmxPath
    )

    $Headers = @{
        Accept = "application/xml"
    }

    try {
        Write-Host "Fetching EDMX from $Url..."
        $Response = Invoke-WebRequest -Method Get -Uri $Url -Headers $Headers -Credential $Credential -UseBasicParsing -AllowUnencryptedAuthentication
        $Content = $Response.Content

        if (-not ($Content.Trim().StartsWith('<') -and $Content.Trim().EndsWith('>'))) {
            Write-Error "Expected XML, but the response does not look like XML. Got: $($Content.Substring(0, [Math]::Min(200, $Content.Length)))..."
        }

        Set-Content -Path $LocalEdmxPath -Value $Content -Encoding UTF8
        Write-Host "EDMX saved to $LocalEdmxPath"
    }
    catch {
        $ErrorMessage = "Failed to fetch/save EDMX: $($_.Exception.Message)"
        if ($_.Exception.Response) {
            $ErrorMessage += "`nResponse Status: $($_.Exception.Response.StatusCode) - $($_.Exception.Response.StatusDescription)"
            try {
                $ErrorContent = $_.Exception.Response.GetResponseStream()
                $StreamReader = New-Object System.IO.StreamReader($ErrorContent)
                $ErrorBody = $StreamReader.ReadToEnd()
                $StreamReader.Close()
                $ErrorContent.Close()
                $ErrorMessage += "`nResponse Body: $ErrorBody"
            } catch {
                $ErrorMessage += "`nCould not read error response body."
            }
        }
        Write-Error $ErrorMessage
    }
}

try {
    $Credential = Get-NavUserCredential -User $BuildAPIBaseUsername -Pass $BuildAPIPassword
    Fetch-AndSaveEdmx -Url $MetadataUrl -Credential $Credential -LocalEdmxPath $EdmxFilePath

    $ExePath = Join-Path -Path $PSScriptRoot -ChildPath "EdmxToYaml.exe"
    if (-not (Test-Path -Path $ExePath)) {
        Write-Error "EdmxToYaml.exe not found at expected path: $ExePath"
    }

    Write-Host "Executing EdmxToYaml.exe with the following parameters:"
    Write-Host "  Deployment API Base URL: $DeploymentAPIBaseURL"
    Write-Host "  Deployment Authentication Type: $DeploymentAuthenticationType"

    $ArgumentList = @(
        """$DeploymentAPIBaseURL""",
        """$DeploymentAuthenticationType"""
    )

    $Process = Start-Process -FilePath $ExePath -ArgumentList $ArgumentList -Wait -PassThru -NoNewWindow

    if ($Process.ExitCode -ne 0) {
        Write-Error "EdmxToYaml.exe failed with exit code $($Process.ExitCode)."
    } else {
        Write-Host "EdmxToYaml.exe executed successfully."
    }
}
catch {
    Write-Error "An unexpected error occurred in the script: $($_.Exception.Message)"
}