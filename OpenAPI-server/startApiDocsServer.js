const express = require('express');
const swaggerUi = require('swagger-ui-express');
const axios = require('axios');
const path = require('path');
const { loadSpecification, getAvailableSpecs } = require('./getOpenApiSpec');

const app = express();
const port = process.env.PORT || 3000;

app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// Securely proxies OAuth2 token requests to Microsoft's identity platform.
app.post('/GetAuthorizationToken', async (req, res) => {
    const { clientId, clientSecret, tenantId, grant_type, scope } = req.body;

    if (!clientId || !clientSecret || !tenantId || !grant_type || !scope) {
        const missing = [
            !clientId && 'clientId', !clientSecret && 'clientSecret', !tenantId && 'tenantId',
            !grant_type && 'grant_type', !scope && 'scope'
        ].filter(Boolean).join(', ');
        return res.status(400).json({ error: 'Bad Request', message: `Missing required form parameters: ${missing}` });
    }

    const tokenUrl = `https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token`;
    const params = new URLSearchParams({ grant_type, 'client_id': clientId, 'client_secret': clientSecret, scope });

    try {
        const tokenResponse = await axios.post(tokenUrl, params);
        res.json(tokenResponse.data);
    } catch (error) {
        const status = error.response ? error.response.status : 500;
        const details = error.response ? error.response.data : 'Proxy error connecting to identity provider.';
        console.error(`Token acquisition failed with status ${status}:`, details);
        res.status(status).json({ error: 'Token Acquisition Failed', details });
    }
});

// Load all available specs at server startup.
const availableSpecs = getAvailableSpecs();
let primarySpec;

if (availableSpecs.length > 0) {
    availableSpecs.sort((a, b) => a.name.localeCompare(b.name));
    primarySpec = loadSpecification(availableSpecs[0].filePath);
    console.log(`Found ${availableSpecs.length} specs, defaulting to '${availableSpecs[0].name}'.`);
} else {
    // If no specs are found, create a placeholder to prevent a crash.
    console.warn("Warning: No OpenAPI specifications found in 'OutputYAML' directory.");
    primarySpec = {
        openapi: '3.0.0',
        info: { title: 'No Specifications Found', version: '1.0.0' },
        paths: {}
    };
}

// Serves the list of available specifications.
app.get('/specs', (req, res) => {
    const specList = availableSpecs.map(s => ({ name: s.name, url: s.url }));
    res.json(specList);
});

// Serves the content of a specific specification file.
app.get('/specs/:filename', (req, res) => {
    const filePath = path.join(__dirname, 'OutputYAML', req.params.filename);
    const spec = loadSpecification(filePath);
    if (spec) {
        res.json(spec);
    } else {
        res.status(404).json({ error: 'Specification not found' });
    }
});

// Setup the main Swagger UI endpoint.
app.use('/api-docs', swaggerUi.serve, swaggerUi.setup(primarySpec, {
    explorer: true,
    swaggerOptions: {
        urls: availableSpecs.map(s => ({ name: s.name, url: s.url })),
        filter: true,
    }
}));

// A simple root page to list available documentation links.
app.get('/', (req, res) => {
    const specList = availableSpecs
        .map(spec => `<li><a href="/api-docs?urls.primaryName=${encodeURIComponent(spec.name)}">${spec.name}</a></li>`)
        .join('');
    
    res.send(`<!DOCTYPE html><html lang="en"><head><title>API Endpoints</title></head><body>
            <h1>API Documentations</h1>
            ${specList.length > 0 ? `<ul>${specList}</ul>` : '<h2>No specifications found.</h2>'}
            </body></html>`);
});

app.listen(port, () => {
    console.log(`Server is running!`);
    console.log(`API Endpoints are available at http://localhost:${port}/api-docs`);
});