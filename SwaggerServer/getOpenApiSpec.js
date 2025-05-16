const YAML = require('yamljs');
const path = require('path');
const fs = require('fs');

const yamlPath = path.join(__dirname,'OutputYAML','openapi.yaml'); 
let swaggerSpec;

try {
    if (fs.existsSync(yamlPath)) {
        swaggerSpec = YAML.load(yamlPath);
        console.log("Successfully loaded openapi.yaml for Swagger UI.");

        if (!swaggerSpec || typeof swaggerSpec !== 'object') {
             throw new Error("Loaded YAML content is not a valid OpenAPI object/specification.");
        }
        if (!swaggerSpec.openapi) {
             console.warn("Loaded openapi.yaml is missing the 'openapi' version field.");
        }
        if (!swaggerSpec.info) {
            console.warn("Loaded openapi.yaml is missing the 'info' object.");
        }
        if (!swaggerSpec.paths) {
            console.warn("Loaded openapi.yaml is missing the 'paths' object.");
        }

    } else {
        console.error(`Error: openapi.yaml not found at ${yamlPath}`);
        console.warn(`Expected YAML at: ${path.resolve(yamlPath)}`);


        swaggerSpec = {
            openapi: '3.0.4',
            info: {
                title: 'Error Loading API Specification',
                version: '0.0.0',
                description: `The openapi.yaml file was not found at ${yamlPath}..`
            },
            paths: {}
        };
    }
} catch (e) {
    console.error(`Error loading or parsing openapi.yaml: ${e.message}`);
    console.error(e.stack);

    swaggerSpec = {
        openapi: '3.0.4',
        info: {
            title: 'Error Parsing API Specification',
            version: '0.0.0',
            description: `There was an error parsing the openapi.yaml file: ${e.message}`
        },
        paths: {}
    };
}

module.exports = swaggerSpec;