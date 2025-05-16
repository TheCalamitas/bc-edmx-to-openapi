# BusinessCentral_EDMX-To-YAML_and_Swagger

This project lets you use Business Central default and custom APIs using interactive swagger documentation.

## What it does

* **Creates API docs:** Takes info about your Business Central API *EDMX* and turns it into a standard format for documentation *YAML*.
* **Shows interactive docs:** Lets you browse and try out the API directly in your web browser using a tool called Swagger UI.
* **Supports two authorization types:** Supports user authentication using OAuth2.0 bearer token and Basic Authentication.

## How it works

1. **Step 1: Get API info:** A PowerShell script connects to your Business Central and downloads its API definition file (called EDMX)
2. **Step 2: Convert to docs:** A separate C# program reads that file and creates the documentation file in a format called OpenAPI (YAML).
3. **Step 3: Show the docs:** A simple web server runs and displays this OpenAPI file using the interactive Swagger UI.

You run Step 1 & 2 when your API changes. You run Step 3 to view the docs whenever you want.

## Requirements to BUILD

* **Visual Studio 2022:** To build and publish EdmxToYaml-Project
* **Node.js:** To install npm packages for Swagger server

## Requirements to RUN

* **Node.js:** To run the web server that shows the docs.
* **PowerShell:** To run the script that starts the process and gets the API info.

You'll also need to download the project files.

## How to build it from repo

1. **Download repo from repository or run a git clone**
2. **Create a folder for your project**
3. **Put the PowerShell script in it that is located in the folder Powershell-script**
4. **Put Node.js scripts and packages into your project folder, they are located in the SwaggerServer folder**
5. **Open terminal in your project folder and run npm install**
6. **Open C# folder EdmxToYaml-Project and open project, publish it to your project folder**

## How to run it (Release version)

1. **Download the project files.**
2. **Install Node.js** if you don't have it.
3. **Open PowerShell** in the project folder.
4. **Run the setup script:** This script gets the API info from your Business Central and creates the documentation file. You'll need to give it your Business Central base URL, a username and password to connect, the main address where people will access your API, and the type of security your API uses (like "Basic" or "OAuth2.0").

Example:
```powershell
.\Get-EdmxAndConvertToYaml.ps1 `
  -BuildAPIBaseURL "https://api.businesscentral.dynamics.com/v2.0/{tenantid}/Sandbox/api/v2.0" `
  -BuildAPIBaseUsername "Calamity" `
  -BuildAPIPassword ("YOUR_PASSWORD_HERE" | ConvertTo-SecureString -AsPlainText -Force) `
  -DeploymentAPIBaseURL "https://api.businesscentral.dynamics.com/v2.0/{tenantid}/Sandbox/api/v2.0" `
  -DeploymentAuthenticationType Basic
```

5. **Start the web server:** Open a normal command prompt or terminal in the project folder and run:
```bash
npm start
```

6. **View the docs:** Open your web browser and go to the address the server shows you.
   * Go to http://localhost:3000/api-docs in your browser.

This shows you:
* All the different parts of your Business Central API you can talk to. *(Companies are always present and if OAuth2.0 authentication was chosen, you will have an extra post action for retrieving a bearer token, it was done like that to avoid CORS errors)*
* Authorize button.
* How to get lists of things, or just one specific thing.
* How to send data to create or update things.
* What data you need to send and what data you will get back.
