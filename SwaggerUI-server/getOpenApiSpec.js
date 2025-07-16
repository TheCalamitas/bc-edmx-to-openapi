const YAML = require('yamljs');
const path = require('path');
const fs = require('fs');

const yamlDir = path.join(__dirname, 'OutputYAML');

// Loads and parses a single YAML specification file.
function loadSpecification(filePath) {
    try {
        if (fs.existsSync(filePath)) {
            const spec = YAML.load(filePath);
            if (!spec || typeof spec !== 'object') {
                throw new Error("Loaded YAML content is not a valid OpenAPI object.");
            }
            return spec;
        }
        return null;
    } catch (e) {
        console.error(`Error loading or parsing ${filePath}: ${e.message}`);
        return null;
    }
}

// Scans the YAML directory and returns an array of all available specifications.
function getAvailableSpecs() {
    try {
        const files = fs.readdirSync(yamlDir);
        return files
            .filter(file => file.endsWith('.yaml') || file.endsWith('.yml'))
            .map(file => ({
                name: path.basename(file, path.extname(file)),
                url: `/specs/${encodeURIComponent(file)}`,
                filePath: path.join(yamlDir, file)
            }));
    } catch (e) {
        console.error(`Error reading specs directory: ${e.message}`);
        return [];
    }
}

module.exports = {
    loadSpecification,
    getAvailableSpecs,
};