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

$MetadataUrl = "$($BuildAPIBaseURL.TrimEnd('/'))/" + "`$metadata?tenant=True"

# Create output directory
$EdmxOutputDir = Split-Path -Path $EdmxFilePath -Parent
if ($EdmxOutputDir) { 
    if (-not (Test-Path -Path $EdmxOutputDir)) {
        Write-Verbose "Creating EDMX output directory: $EdmxOutputDir"
        New-Item -ItemType Directory -Path $EdmxOutputDir -Force | Out-Null
    }
}

try {
    # Fetch EDMX
    Write-Host "Fetching EDMX from $MetadataUrl..."
    $Credential = New-Object PSCredential($BuildAPIBaseUsername, $BuildAPIPassword)
    $Response = Invoke-WebRequest -Uri $MetadataUrl -Credential $Credential -UseBasicParsing -TimeoutSec 500 -Headers @{Accept = "application/xml"}  -AllowUnencryptedAuthentication
    
    # Validate XML by attempting to parse it
    try {
        [xml]$XmlTest = $Response.Content
    }
    catch {
        throw "Response is not valid XML: $($_.Exception.Message)"
    }

    # Save EDMX
    Set-Content -Path $EdmxFilePath -Value $Response.Content -Encoding UTF8
    Write-Host "EDMX saved to $EdmxFilePath"

    # Execute EdmxToYaml.exe
    $ExePath = Join-Path $PSScriptRoot "EdmxToYaml.exe"
    if (-not (Test-Path $ExePath)) {
        throw "EdmxToYaml.exe not found at: $ExePath"
    }

    Write-Host "Executing EdmxToYaml.exe with parameters:"
    Write-Host "  Deployment API Base URL: $DeploymentAPIBaseURL"
    Write-Host "  Deployment Authentication Type: $DeploymentAuthenticationType"

    $ArgumentArray = @(
        $DeploymentAPIBaseURL,        
        $DeploymentAuthenticationType  
    )

    $Process = Start-Process -FilePath $ExePath -ArgumentList $ArgumentArray -Wait -PassThru -NoNewWindow

    if ($Process.ExitCode -ne 0) {
        throw "EdmxToYaml.exe failed with exit code $($Process.ExitCode)"
    }
    
    Write-Host "EdmxToYaml.exe executed successfully."
}
catch {
    Write-Error "Script failed: $($_.Exception.Message)"
}