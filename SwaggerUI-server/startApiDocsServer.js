const express = require('express');
const swaggerUi = require('swagger-ui-express');
const axios = require('axios');
const path = require('path');
const { loadSpecification, getAvailableSpecs } = require('./getOpenApiSpec');

const app = express();
const port = process.env.PORT || 3000;

app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// Proxies OAuth2 token requests to Microsoft's identity platform.
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

// Load the first available spec at server startup.
const availableSpecs = getAvailableSpecs();
let primarySpec;
let primarySpecName = '';

if (availableSpecs.length > 0) {
    availableSpecs.sort((a, b) => a.name.localeCompare(b.name));
    primarySpec = loadSpecification(availableSpecs[0].filePath);
    primarySpecName = availableSpecs[0].name;
} else {
    console.warn("Warning: No OpenAPI specifications found in 'OutputYAML' directory.");
    primarySpec = {
        openapi: '3.0.0',
        info: { title: 'No Specifications Found', version: '1.0.0' },
        paths: {}
    };
    primarySpecName = 'No Specifications Found';
}

// Setup the main Swagger UI endpoint
app.use('/api-docs', swaggerUi.serve, swaggerUi.setup(primarySpec, {
    swaggerOptions: {
        filter: true,
    }
}));

// A simple root page to link to the single documentation.
app.get('/', (req, res) => {
    res.send(`<!DOCTYPE html><html lang="en"><head><title>API Endpoints</title></head><body>
            <h1>API Documentation</h1>
            <ul><li><a href="/api-docs">${primarySpecName}</a></li></ul>
            </body></html>`);
});

// Start the server and handle port-in-use error
app.listen(port, () => {
    console.log(`Server is running!`);
    console.log(`API Endpoint is available at http://localhost:${port}/api-docs`);
}).on('error', (err) => {
    if (err.code === 'EADDRINUSE') {
        console.error(`Error! : Port ${port} is already in use.`);
    } else {
        console.error('Server failed to start:', err);
    }
});