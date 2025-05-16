const express = require('express');
const swaggerUi = require('swagger-ui-express');
const swaggerSpec = require('./getOpenApiSpec');
const axios = require('axios');

const app = express();
const port = 3000;

app.use(express.urlencoded({ extended: true }));
app.use(express.json());

app.post('/GetAuthorizationToken', async (req, res) => {
    const { clientId, clientSecret, tenantId, grant_type, scope } = req.body;

    if (!clientId || !clientSecret || !tenantId || !grant_type || !scope) {
        const missing = [
            !clientId && 'clientId',
            !clientSecret && 'clientSecret',
            !tenantId && 'tenantId',
            !grant_type && 'grant_type',
            !scope && 'scope'
        ].filter(Boolean).join(', ');

        return res.status(400).json({
            error: 'Bad Request',
            message: `The following required form parameters are missing: ${missing}`
        });
    }

    const tokenUrl = `https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token`;

    const params = new URLSearchParams();
    params.append('grant_type', grant_type);
    params.append('client_id', clientId);
    params.append('client_secret', clientSecret);
    params.append('scope', scope);

    try {
        console.log(`Proxying token request for tenant ${tenantId} and client ${clientId} to ${tokenUrl}`);
        const tokenResponse = await axios.post(tokenUrl, params, {
            headers: {}
        });
        console.log('Token successfully retrieved from Microsoft identity platform.');
        res.json(tokenResponse.data);
    } catch (error) {
        console.error('Error acquiring token from Microsoft identity platform:');
        if (error.response) {
            console.error('Status:', error.response.status);
            console.error('Data:', error.response.data);
            res.status(error.response.status).json({
                error: 'Token Acquisition Failed',
                message: 'Failed to acquire token from the identity provider.',
                details: error.response.data
            });
        } else if (error.request) {
            console.error('No response received:', error.request);
            res.status(500).json({
                error: 'Proxy Error',
                message: 'No response received from the identity provider.'
            });
        } else {
            console.error('Error setting up request:', error.message);
            res.status(500).json({
                error: 'Proxy Setup Error',
                message: 'Error setting up the request to the identity provider.',
                details: error.message
            });
        }
    }
});

app.use('/api-docs', swaggerUi.serve, swaggerUi.setup(swaggerSpec, {
    swaggerOptions: {}
}));

app.get('/', (req, res) => {
  res.send('Server is running. Access API documentation at <a href="/api-docs">/api-docs</a>');
});

app.listen(port, () => {
  console.log(`Swagger UI server running on http://localhost:${port}`);
  console.log(`API Docs available at http://localhost:${port}/api-docs`);
  console.log(`OAuth2 Token Proxy available at POST http://localhost:${port}/GetAuthorizationToken`);
});