using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Validation;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;

namespace EdmxToYaml
{
    public class Program
    {
        const string EdmxInputPath = "OutputEDMX/edmx.xml";
        const string YamlOutputPath = "OutputYAML/openapi.yaml";
        const string CompanyEntitySetNameConst = "companies";

        static void Main(string[] args)
        {
            Console.WriteLine("--- EDMX to OpenAPI YAML Generation ---");
            try
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Error: Missing arguments.");
                    Console.WriteLine("Usage: <program>.exe \"<BaseApiUrl>\" \"<AuthType>\"");
                    return;
                }
                string baseApiUrl = args[0];
                string authType = args[1];

                Console.WriteLine($"Using API Base URL: {baseApiUrl}");
                Console.WriteLine($"Using Auth Type: {authType}");

                if (!File.Exists(EdmxInputPath))
                {
                    Console.WriteLine($"Error: Input EDMX not found: {Path.GetFullPath(EdmxInputPath)}");
                    return;
                }
                Console.WriteLine($"Reading EDMX: {Path.GetFullPath(EdmxInputPath)}");

                IEdmModel model = LoadEdmModel(EdmxInputPath);
                if (model == null)
                {
                    Console.WriteLine("EDMX model loading failed. Aborting.");
                    return;
                }
                Console.WriteLine("EDMX parsed into IEdmModel.");

                Console.WriteLine("Generating OpenAPI document...");
                var generator = new OpenApiGenerator(model, baseApiUrl, CompanyEntitySetNameConst, authType);
                OpenApiDocument openApiDoc = generator.GenerateOpenApiDocument();

                Console.WriteLine($"OpenAPI document: {openApiDoc.Paths.Count} paths, {openApiDoc.Components.Schemas.Count} schemas, {generator.ActionsCount} actions, {generator.FunctionsCount} functions.");

                string outputDir = Path.GetDirectoryName(Path.GetFullPath(YamlOutputPath));
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    Console.WriteLine($"Created output directory: {outputDir}");
                }
                SerializeToYaml(openApiDoc, YamlOutputPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL ERROR: {ex.Message}");
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                Console.WriteLine("--- Generation process finished. ---");
            }
        }

        static IEdmModel LoadEdmModel(string edmxPath)
        {
            try
            {
                using var stream = File.OpenRead(edmxPath);
                using var reader = XmlReader.Create(stream);
                if (CsdlReader.TryParse(reader, true, out IEdmModel model, out IEnumerable<EdmError> errors))
                {
                    var significantErrors = errors?.Where(e => e.ErrorCode != EdmErrorCode.BadUnresolvedType && !e.ErrorMessage.Contains("facet is specified")).ToList();

                    if (significantErrors?.Any() == true)
                    {
                        Console.WriteLine("EDMX parsing encountered significant errors:");
                        foreach (var error in significantErrors)
                        {
                            Console.WriteLine($"  - [ERROR] ({error.ErrorCode}): {error.ErrorMessage} at {error.ErrorLocation}");
                        }
                    }
                    return model;
                }
                else
                {
                    Console.WriteLine("Error: Failed to parse EDMX.");
                    if (errors != null) foreach (var err in errors)
                            Console.WriteLine($"  - {err.ErrorCode}: {err.ErrorMessage} at {err.ErrorLocation}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during EDMX loading: {ex.Message}");
                return null;
            }
        }

        static void SerializeToYaml(OpenApiDocument openApiDoc, string outputPath)
        {
            try
            {
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                using var streamWriter = new StreamWriter(fileStream);
                var yamlWriter = new OpenApiYamlWriter(streamWriter);
                openApiDoc.SerializeAsV3(yamlWriter);
                yamlWriter.Flush();
                Console.WriteLine($"OpenAPI YAML written to: {Path.GetFullPath(outputPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing YAML file: {ex.Message}");
            }
        }
    }
}