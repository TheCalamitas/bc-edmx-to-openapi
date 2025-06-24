# Limitations

1. This project most likely works only with **Business Central** due to the way its metadata is structured. It does not define which primary roots are valid for API calls. The `companies` root is used as the default, and all other child endpoints are nested under it.

   ```
   /companies
   /companies({companyId})
   /companies({companyId})/customers
   ...
   ```

2. Currently there is implemented only **2** authorization options **Basic** and **OAuth2.0**, you must include authorization option as a parameter to the C# executable, because of some OAuth2.0 issues, to bypass most of the problems **C# executable** will include one more **proxy API endpoint** for the user to obtain the **bearer token** so he can Authorize it in **SwaggerUI** afterwards

   ```
   /GetAuthorizationToken
   required:
     - clientId
     - clientSecret
     - tenantId
     - grant_type
     - scope
   ```

# About

This project was developed during my first internship in the company **Arquiconsult (Odivelas)**.

Its goal is to automatically generate OpenAPI specifications with API endpoints from the OData metadata fetched during the pipeline run.

The intended usage is for it to be integrated into a pipeline, although it can still be easily modified to work in other environments.

The project consists of three parts:

1. A **PowerShell** script that fetches metadata, saves it to the **OutputEDMX** folder, and triggers the next step.
2. A **C# executable** that converts the obtained metadata into an OpenAPI YAML specification and saves it to the **OutputYAML** folder.
3. A **Node.js** server that allows users to authorize and make **API** calls using **SwaggerUI**.

# Usage Manual

## PREPARATION

Ensure that `Get-EdmxAndConvertToYaml.ps1`, `EdmxToYaml.exe`, `Node.JS server files and packages` are all in the same directory.

## STEP 1

First step is to obtain the metadata of your **Business Central** application with `Get-EdmxAndConvertToYaml.ps1`

`Get-EdmxAndConvertToYaml.ps1` receives the following parameters:

```powershell
[Parameter(Mandatory = $true)]
[string]$BuildAPIBaseURL,        # The base URL required to authorize and fetch metadata.

[Parameter(Mandatory = $true)]   # Used for Basic authentication.
[string]$BuildAPIBaseUsername,   # Your username.

[Parameter(Mandatory = $true)]
[SecureString]$BuildAPIPassword, # Your password.

[Parameter(Mandatory = $true)]   # The base API URL to include in the OpenAPI specification.
[string]$DeploymentAPIBaseURL,

[Parameter(Mandatory = $true)]
[string]$DeploymentAuthenticationType,  # Authorization type: "Basic" or "OAuth2.0". This is passed to the executable.

[string]$EdmxFilePath = "OutputEDMX/edmx.xml"  # Default output path. Do not change unless you have modified the C# executable.
```

If you use this application in some different environment you probably will have to modify metadata tenant path.

```powershell
$MetadataUrl = "$($BuildAPIBaseURL.TrimEnd('/'))/" + "`$metadata?tenant=True"
```

**Note:** *True* in my case was the name of the tenant for some reason...

## STEP 2

*This step is only necessary if you obtained the metadata using a different method. Otherwise, it is handled automatically by the PowerShell script*

The C# converter, `EdmxToYaml.exe`, searches for an `edmx.xml` file inside the **OutputEDMX** folder. The executable accepts **2** parameters: the base API URL to be included in the specification and the authorization type ("Basic" or "OAuth2.0").

```bash
./EdmxToYaml.exe "https://BusinessCentral/api/v2.0" "OAuth2.0"
```

**Executable** will create the folder **OutputYAML** and put the created file in it `openapi.yaml`

## STEP 3

Ensure that the **OutputYAML** folder (containing your OpenAPI specifications) is in the same directory as your Node.js server files.

You can start the server from your command line:

```bash
npm start
```

This will start a server at `http://localhost:3000/api-docs`. The server will fail to start if port 3000 is already in use.
