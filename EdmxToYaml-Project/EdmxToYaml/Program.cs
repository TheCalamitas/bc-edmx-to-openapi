using System;
using System.IO;
using System.Xml;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Validation;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using System.Collections.Generic;
using System.Linq;
using Microsoft.OData.Edm.Vocabularies;

namespace EdmxToYaml.AutoNested.Final
{
    public static class EdmExtensions
    {
        public static bool GetBooleanCapability(
            this IEdmVocabularyAnnotatable target,
            IEdmModel model,
            string termName,
            string propertyName,
            bool defaultValue)
        {
            var term = model.FindTerm(termName);
            if (term == null) return defaultValue;

            var annotation = model.FindVocabularyAnnotations<IEdmVocabularyAnnotation>(target, term)
                                  .FirstOrDefault();
            if (annotation?.Value is IEdmRecordExpression record)
            {
                var propertyValue = record.FindProperty(propertyName)?.Value;
                if (propertyValue is IEdmBooleanConstantExpression boolConst)
                {
                    return boolConst.Value;
                }
            }
            return defaultValue;
        }
    }

    public class OpenApiGenerator
    {
        private const string CompanyIdParamNamePath = "companyId";
        private const string IfMatchHeaderParamId = "IfMatchHeaderParam";
        private const string CompanyIdPathParamId = "CompanyIdPathParam";

        private readonly IEdmModel _model;
        private readonly string _baseApiUrl;
        private readonly OpenApiDocument _openApiDoc;
        private string _securitySchemeName;
        private readonly string _companyEntitySetName;
        private readonly string _authenticationType;

        private IEdmEntitySet _companyEntitySet;
        private IEdmEntityType _companyEntityType;

        private readonly HashSet<string> _processedEntitySetNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _nestedChildSetNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<EdmPrimitiveTypeKind, (string Type, string Format)> EdmToOpenApiTypeMap =
            new Dictionary<EdmPrimitiveTypeKind, (string Type, string Format)>()
        {
            { EdmPrimitiveTypeKind.Boolean, ("boolean", null) },
            { EdmPrimitiveTypeKind.Byte, ("integer", "int32") },
            { EdmPrimitiveTypeKind.Date, ("string", "date") },
            { EdmPrimitiveTypeKind.DateTimeOffset, ("string", "date-time") },
            { EdmPrimitiveTypeKind.Decimal, ("number", "double") },
            { EdmPrimitiveTypeKind.Double, ("number", "double") },
            { EdmPrimitiveTypeKind.Duration, ("string", "duration") },
            { EdmPrimitiveTypeKind.Guid, ("string", "uuid") },
            { EdmPrimitiveTypeKind.Int16, ("integer", "int32") },
            { EdmPrimitiveTypeKind.Int32, ("integer", "int32") },
            { EdmPrimitiveTypeKind.Int64, ("integer", "int64") },
            { EdmPrimitiveTypeKind.SByte, ("integer", "int32") },
            { EdmPrimitiveTypeKind.Single, ("number", "float") },
            { EdmPrimitiveTypeKind.String, ("string", null) },
            { EdmPrimitiveTypeKind.Stream, ("string", "binary") },
            { EdmPrimitiveTypeKind.TimeOfDay, ("string", "time") },
            { EdmPrimitiveTypeKind.Binary, ("string", "byte") }
        };

        public OpenApiGenerator(
            IEdmModel model,
            string baseApiUrl,
            string companyEntitySetName,
            string authenticationType)
        {
            _model = model;
            _baseApiUrl = baseApiUrl;
            _companyEntitySetName = companyEntitySetName;
            _authenticationType = authenticationType;

            _openApiDoc = new OpenApiDocument
            {
                Info = new OpenApiInfo
                {
                    Title = "Business Central API",
                    Version = "1.0.0",
                    Description = "OpenAPI specification from OData V4 EDMX"
                },
                Servers = new List<OpenApiServer> { new OpenApiServer { Url = _baseApiUrl } },
                Components = new OpenApiComponents
                {
                    Schemas = new Dictionary<string, OpenApiSchema>(),
                    Parameters = new Dictionary<string, OpenApiParameter>(),
                    SecuritySchemes = new Dictionary<string, OpenApiSecurityScheme>()
                },
                SecurityRequirements = new List<OpenApiSecurityRequirement>(),
                Paths = new OpenApiPaths()
            };
        }

        private void ConfigureSecuritySchemes()
        {
            _openApiDoc.Components.SecuritySchemes.Clear();
            _openApiDoc.SecurityRequirements.Clear();

            if (_authenticationType.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            {
                _securitySchemeName = "NavUserPasswordAuth";
                _openApiDoc.Components.SecuritySchemes[_securitySchemeName] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic",
                    Description = "Business Central NavUserPassword (Basic) Authentication."
                };
            }
            else if (_authenticationType.Equals("OAuth2.0", StringComparison.OrdinalIgnoreCase))
            {
                _securitySchemeName = "BearerAuth";
                _openApiDoc.Components.SecuritySchemes[_securitySchemeName] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Bearer token from /GetAuthorizationToken utility endpoint."
                };
                AddGetAuthorizationTokenPath();
            }
            else
            {
                Console.WriteLine($"Warning: Unknown auth type: {_authenticationType}. No security applied.");
                return;
            }

            _openApiDoc.SecurityRequirements.Add(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme { Reference =
                        new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = _securitySchemeName }
                    },
                    new List<string>()
                }
            });
            Console.WriteLine($"Security scheme '{_securitySchemeName}' configured.");
        }

        private void AddGetAuthorizationTokenPath()
        {
            var pathItem = new OpenApiPathItem();
            var postOperation = new OpenApiOperation
            {
                Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Authentication Utility" } },
                Summary = "Get OAuth2.0 Access Token (via proxy)",
                Description = "Exchanges client credentials for an access token (client_credentials grant).",
                OperationId = "Util-GetAuthorizationToken",
                RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/x-www-form-urlencoded"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Required = new HashSet<string> { "clientId", "clientSecret", "tenantId", "grant_type", "scope" },
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["grant_type"] = new OpenApiSchema { Type = "string", Default = new OpenApiString("client_credentials") },
                                    ["clientId"] = new OpenApiSchema { Type = "string" },
                                    ["clientSecret"] = new OpenApiSchema { Type = "string", Format = "password" },
                                    ["tenantId"] = new OpenApiSchema { Type = "string" },
                                    ["scope"] = new OpenApiSchema { Type = "string", Default = new OpenApiString("https://api.businesscentral.dynamics.com/.default") }
                                }
                            }
                        }
                    }
                },
                Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse
                    {
                        Description = "Access token retrieved.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = "object",
                                    Properties = new Dictionary<string, OpenApiSchema>
                                    {
                                        ["token_type"] = new OpenApiSchema { Type = "string" },
                                        ["expires_in"] = new OpenApiSchema { Type = "integer", Format = "int32" },
                                        ["access_token"] = new OpenApiSchema { Type = "string" }
                                    }
                                }
                            }
                        }
                    },
                    ["400"] = new OpenApiResponse { Description = "Bad Request." },
                    ["401"] = new OpenApiResponse { Description = "Unauthorized." },
                    ["500"] = new OpenApiResponse { Description = "Internal Server Error." }
                },
                Security = new List<OpenApiSecurityRequirement>()
            };
            pathItem.AddOperation(OperationType.Post, postOperation);
            _openApiDoc.Paths.Add("/GetAuthorizationToken", pathItem);
            Console.WriteLine("Added utility path: /GetAuthorizationToken");
        }

        public OpenApiDocument GenerateOpenApiDocument()
        {
            var entityContainer = _model.EntityContainer;
            if (entityContainer == null)
            {
                Console.WriteLine("Error: EntityContainer not found in EDMX.");
                return _openApiDoc;
            }

            ConfigureSecuritySchemes();

            _companyEntitySet = entityContainer.FindEntitySet(_companyEntitySetName) ??
                                entityContainer.EntitySets().FirstOrDefault(es =>
                                    es.Name.Equals(_companyEntitySetName, StringComparison.OrdinalIgnoreCase));

            if (_companyEntitySet == null)
            {
                Console.WriteLine($"Error: Company EntitySet '{_companyEntitySetName}' not found.");
                return _openApiDoc;
            }
            _companyEntityType = _companyEntitySet.EntityType();
            Console.WriteLine($"Identified Company EntitySet: '{_companyEntitySet.Name}'");

            InitializeCommonParameters();
            AddCompanyRootPaths();
            _processedEntitySetNames.Add(_companyEntitySetName);

            IdentifyPotentialNestedChildren(entityContainer);
            ProcessCompanyScopedAndNestedPaths(entityContainer);

            Console.WriteLine("OpenAPI document generation complete.");
            return _openApiDoc;
        }

        private void InitializeCommonParameters()
        {
            var companyIdParameter = new OpenApiParameter
            {
                Name = CompanyIdParamNamePath,
                In = ParameterLocation.Path,
                Required = true,
                Description = "ID of the company (GUID).",
                Schema = new OpenApiSchema { Type = "string", Format = "uuid" }
            };
            _openApiDoc.Components.Parameters.Add(CompanyIdPathParamId, companyIdParameter);

            var ifMatchParameter = new OpenApiParameter
            {
                Name = "If-Match",
                In = ParameterLocation.Header,
                Required = true,
                Description = "ETag for concurrency control.",
                Schema = new OpenApiSchema { Type = "string" }
            };
            _openApiDoc.Components.Parameters.Add(IfMatchHeaderParamId, ifMatchParameter);
            Console.WriteLine("Initialized common OpenAPI parameters (CompanyId, If-Match).");
        }

        private (string pathSegment, List<OpenApiParameter> keyParameters)
            GenerateEntityKeyPathSegmentAndParameters(IEdmEntityType entityType, string entitySetNameCtx)
        {
            var keyProps = entityType.Key().ToList();
            if (!keyProps.Any())
            {
                Console.WriteLine($"Warning: No keys for EntityType '{entityType.FullName()}' ('{entitySetNameCtx}').");
                return (null, new List<OpenApiParameter>());
            }

            var keyParamDefs = new List<OpenApiParameter>();
            var pathPlaceholders = new List<string>();

            foreach (var keyProp in keyProps)
            {
                string keyParamName = keyProp.Name;
                var keyParamSchema = MapEdmPrimitiveType(keyProp.Type.AsPrimitive());

                if (keyParamSchema == null)
                {
                    Console.WriteLine($"Error: Cannot map key type '{keyProp.Type.FullName()}' for '{keyParamName}' in '{entitySetNameCtx}'.");
                    return (null, new List<OpenApiParameter>());
                }

                keyParamDefs.Add(new OpenApiParameter
                {
                    Name = keyParamName,
                    In = ParameterLocation.Path,
                    Required = true,
                    Description = $"Key: {keyProp.Name} for {entitySetNameCtx}",
                    Schema = keyParamSchema
                });

                pathPlaceholders.Add(keyProps.Count == 1 ?
                    $"{{{keyParamName}}}" :
                    $"{keyProp.Name}={{{keyParamName}}}");
            }
            return ($"({string.Join(",", pathPlaceholders)})", keyParamDefs);
        }

        private void AddCompanyRootPaths()
        {
            string companySchemaKey = GetSchemaKey(_companyEntityType);
            EnsureSchemaExists(_companyEntityType, companySchemaKey);

            string collectionPathKey = $"/{_companyEntitySetName}";
            var collectionPathItem = new OpenApiPathItem();
            CreateOperationsForCollectionPath(collectionPathItem, _companyEntitySet, companySchemaKey,
                _companyEntitySetName, "Get", $"list of {_companyEntitySetName}",
                new List<OpenApiParameter>(), true, false);
            _openApiDoc.Paths.Add(collectionPathKey, collectionPathItem);
            Console.WriteLine($"Added root collection path: {collectionPathKey}");

            var (keySegment, keyDefs) =
                GenerateEntityKeyPathSegmentAndParameters(_companyEntityType, _companyEntitySetName);

            if (!string.IsNullOrEmpty(keySegment) && keyDefs.Any())
            {
                string entityPathKey = $"/{_companyEntitySetName}{keySegment}";
                var entityPathItem = new OpenApiPathItem();

                CreateOperationsForEntityPath(entityPathItem, _companyEntitySet, companySchemaKey,
                    _companyEntitySetName, "GetById", $"single company by ID",
                    new List<OpenApiParameter>(), keyDefs, true, false, false);
                _openApiDoc.Paths.Add(entityPathKey, entityPathItem);
                Console.WriteLine($"Added root entity path: {entityPathKey}");
            }
            else
            {
                Console.WriteLine($"Warning: Cannot generate root entity path for '{_companyEntitySetName}'.");
            }
        }

        private void IdentifyPotentialNestedChildren(IEdmEntityContainer entityContainer)
        {
            Console.WriteLine("Identifying potential nested children...");
            var companyNavProps = _companyEntityType.NavigationProperties()
                .Where(np => np.TargetMultiplicity() == EdmMultiplicity.Many).ToList();

            foreach (var L0NavProp in companyNavProps)
            {
                var L1EntityType = L0NavProp.ToEntityType();
                var L1EntitySet = entityContainer.EntitySets()
                    .FirstOrDefault(es => es.EntityType().IsOrInheritsFrom(L1EntityType));
                if (L1EntitySet == null) continue;

                foreach (var L1NavProp in L1EntityType.NavigationProperties()
                    .Where(np => np.TargetMultiplicity() == EdmMultiplicity.Many))
                {
                    var L2EntityType = L1NavProp.ToEntityType();
                    var L2EntitySet = entityContainer.EntitySets()
                        .FirstOrDefault(es => es.EntityType().IsOrInheritsFrom(L2EntityType));

                    if (L2EntitySet != null && L2EntitySet != L1EntitySet && L2EntitySet != _companyEntitySet)
                    {
                        if (_nestedChildSetNames.Add(L2EntitySet.Name))
                        {
                            Console.WriteLine($"  Marked '{L2EntitySet.Name}' as potential nested child of '{L1EntitySet.Name}'.");
                        }
                    }
                }
            }
        }

        private void ProcessCompanyScopedAndNestedPaths(IEdmEntityContainer entityContainer)
        {
            Console.WriteLine("Processing company-scoped and nested paths...");
            var companyNavProps = _companyEntityType.NavigationProperties()
                .Where(np => np.TargetMultiplicity() == EdmMultiplicity.Many).ToList();

            foreach (var navProp in companyNavProps)
            {
                var L1EntityType = navProp.ToEntityType();
                var L1EntitySet = entityContainer.EntitySets()
                    .FirstOrDefault(es => es.EntityType().IsOrInheritsFrom(L1EntityType));

                if (L1EntitySet == null)
                {
                    Console.WriteLine($"Warning: No EntitySet for NavProp '{navProp.Name}' -> '{L1EntityType.FullName()}'.");
                    continue;
                }
                string L1EntitySetName = L1EntitySet.Name;
                if (_processedEntitySetNames.Contains(L1EntitySetName)) continue;

                string L1SchemaKey = GetSchemaKey(L1EntityType);
                EnsureSchemaExists(L1EntityType, L1SchemaKey);

                if (!_nestedChildSetNames.Contains(L1EntitySetName))
                {
                    GenerateCompanyScopedPathsForEntitySet(L1EntitySet, L1SchemaKey);
                }
                else
                {
                    Console.WriteLine($"Skipping flat paths for '{L1EntitySetName}' (marked as nested).");
                }
                GenerateNestedPathsForParentEntitySet(L1EntitySet, L1SchemaKey, L1EntitySetName);
                _processedEntitySetNames.Add(L1EntitySetName);
            }
        }

        private void GenerateCompanyScopedPathsForEntitySet(IEdmEntitySet entitySet, string schemaKey)
        {
            string entitySetName = entitySet.Name;
            Console.WriteLine($"Generating company-scoped paths for: {entitySetName}");
            var baseParams = new List<OpenApiParameter>
            {
                new OpenApiParameter { Reference =
                    new OpenApiReference { Type = ReferenceType.Parameter, Id = CompanyIdPathParamId } }
            };

            string collPathKey = $"/{_companyEntitySetName}({{{CompanyIdParamNamePath}}})/{entitySetName}";
            var collPathItem = new OpenApiPathItem();
            CreateOperationsForCollectionPath(collPathItem, entitySet, schemaKey, entitySetName,
                $"{entitySetName}-ByCompany", $"for company", baseParams, true, true);
            _openApiDoc.Paths.Add(collPathKey, collPathItem);
            Console.WriteLine($"  Added company-scoped collection path: {collPathKey}");

            var (keySegment, keyDefs) =
                GenerateEntityKeyPathSegmentAndParameters(entitySet.EntityType(), entitySetName);

            if (!string.IsNullOrEmpty(keySegment) && keyDefs.Any())
            {
                string entityPathKey = $"/{_companyEntitySetName}({{{CompanyIdParamNamePath}}})/{entitySetName}{keySegment}";
                var entityPathItem = new OpenApiPathItem();
                CreateOperationsForEntityPath(entityPathItem, entitySet, schemaKey, entitySetName,
                    $"{entitySetName}-ByCompAndKey", $"by key for company",
                    baseParams, keyDefs, true, true, true);
                _openApiDoc.Paths.Add(entityPathKey, entityPathItem);
                Console.WriteLine($"  Added company-scoped entity path: {entityPathKey}");
            }
            else
            {
                Console.WriteLine($"Warning: No company-scoped entity path for '{entitySetName}' (key issue).");
            }
        }

        private void GenerateNestedPathsForParentEntitySet(
            IEdmEntitySet parentSet, string parentSchemaKey, string parentSetNameCtx)
        {
            var parentEntityType = parentSet.EntityType();
            var (parentKeySegment, parentKeyDefs) =
                GenerateEntityKeyPathSegmentAndParameters(parentEntityType, parentSet.Name);

            if (string.IsNullOrEmpty(parentKeySegment) || !parentKeyDefs.Any())
            {
                Console.WriteLine($"Skipping nested children for '{parentSetNameCtx}' (parent key issue).");
                return;
            }

            var parentKeyParamRefs = new List<OpenApiParameter>();
            foreach (var keyDef in parentKeyDefs)
            {
                string componentParamId = $"{parentSet.Name}_{keyDef.Name}_KeyPathParam";
                if (!_openApiDoc.Components.Parameters.ContainsKey(componentParamId))
                {
                    _openApiDoc.Components.Parameters.Add(componentParamId, keyDef);
                }
                parentKeyParamRefs.Add(new OpenApiParameter
                {
                    Reference =
                    new OpenApiReference { Type = ReferenceType.Parameter, Id = componentParamId }
                });
            }

            string parentBasePath = $"/{_companyEntitySetName}({{{CompanyIdParamNamePath}}})/{parentSet.Name}{parentKeySegment}";
            Console.WriteLine($"Checking for nested children of '{parentSetNameCtx}' (under {parentSet.Name}{parentKeySegment})");

            foreach (var childNavProp in parentEntityType.NavigationProperties()
                .Where(np => np.TargetMultiplicity() == EdmMultiplicity.Many))
            {
                var childEntityType = childNavProp.ToEntityType();
                var childEntitySet = _model.EntityContainer.EntitySets()
                    .FirstOrDefault(es => es.EntityType().IsOrInheritsFrom(childEntityType));

                if (childEntitySet != null && _nestedChildSetNames.Contains(childEntitySet.Name))
                {
                    Console.WriteLine($"  Generating nested paths for '{childEntitySet.Name}' under '{parentSet.Name}'.");
                    string childSchemaKey = GetSchemaKey(childEntityType);
                    EnsureSchemaExists(childEntityType, childSchemaKey);
                    GeneratePathsForNestedChild(parentBasePath, parentKeyParamRefs,
                        parentSet.Name, childEntitySet, childSchemaKey);
                }
            }
        }

        private void GeneratePathsForNestedChild(
            string parentBasePath, List<OpenApiParameter> parentKeyParamRefs,
            string parentTagCtx, IEdmEntitySet childSet, string childSchemaKey)
        {
            string childSetName = childSet.Name;
            var baseParamsForChild = new List<OpenApiParameter>
            {
                new OpenApiParameter { Reference =
                    new OpenApiReference { Type = ReferenceType.Parameter, Id = CompanyIdPathParamId } }
            };
            baseParamsForChild.AddRange(parentKeyParamRefs);

            string collPathKey = $"{parentBasePath}/{childSetName}";
            var collPathItem = new OpenApiPathItem();
            CreateOperationsForCollectionPath(collPathItem, childSet, childSchemaKey, childSetName,
                $"{childSetName}-Nested-{parentTagCtx}", $"for {parentTagCtx}",
                baseParamsForChild, true, true);
            _openApiDoc.Paths.Add(collPathKey, collPathItem);
            Console.WriteLine($"    Added nested collection path: {collPathKey}");

            var (childKeySegment, childKeyDefs) =
                GenerateEntityKeyPathSegmentAndParameters(childSet.EntityType(), childSetName);

            if (!string.IsNullOrEmpty(childKeySegment) && childKeyDefs.Any())
            {
                string entityPathKey = $"{parentBasePath}/{childSetName}{childKeySegment}";
                var entityPathItem = new OpenApiPathItem();
                CreateOperationsForEntityPath(entityPathItem, childSet, childSchemaKey, childSetName,
                    $"{childSetName}-NestedByKey-{parentTagCtx}", $"by key for {parentTagCtx}",
                    baseParamsForChild, childKeyDefs, true, true, true);
                _openApiDoc.Paths.Add(entityPathKey, entityPathItem);
                Console.WriteLine($"    Added nested entity path: {entityPathKey}");
            }
            else
            {
                Console.WriteLine($"Warning: No nested entity path for '{childSetName}' (key issue).");
            }
        }

        private void CreateOperationsForCollectionPath(
            OpenApiPathItem pathItem, IEdmEntitySet entitySet, string schemaKey, string tag,
            string opIdRoot, string summarySuffix, List<OpenApiParameter> baseParams,
            bool addGet, bool addPost)
        {
            bool insertable = addPost && entitySet.GetBooleanCapability(_model,
                "Org.OData.Capabilities.V1.InsertRestrictions", "Insertable", defaultValue: true);

            if (addGet)
            {
                var getListOp = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = tag } },
                    Summary = $"Retrieve {entitySet.Name} {summarySuffix}",
                    OperationId = $"Get-{opIdRoot}",
                    Parameters = new List<OpenApiParameter>(baseParams),
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = $"List of {entitySet.Name}",
                            Content =
                            new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, OpenApiSchema>
                                        {
                                            ["@odata.context"] = new OpenApiSchema { Type = "string" },
                                            ["value"] = new OpenApiSchema
                                            {
                                                Type = "array",
                                                Items =
                                        new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } }
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        ["default"] = new OpenApiResponse { Description = "Error response" }
                    }
                };
                AddODataCollectionQueryParams(getListOp);
                pathItem.AddOperation(OperationType.Get, getListOp);
            }

            if (insertable)
            {
                var postOp = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = tag } },
                    Summary = $"Create new {entitySet.Name} {summarySuffix}",
                    OperationId = $"Post-{opIdRoot}",
                    Parameters = new List<OpenApiParameter>(baseParams),
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Description = $"New {entitySet.Name} object.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } }
                            }
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["201"] = new OpenApiResponse
                        {
                            Description = $"{entitySet.Name} created.",
                            Content =
                            new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } }
                                }
                            }
                        },
                        ["default"] = new OpenApiResponse { Description = "Error response" }
                    }
                };
                pathItem.AddOperation(OperationType.Post, postOp);
            }
        }

        private void CreateOperationsForEntityPath(
            OpenApiPathItem pathItem, IEdmEntitySet entitySet, string schemaKey, string tag,
            string opIdRoot, string summarySuffix, List<OpenApiParameter> baseParams,
            List<OpenApiParameter> entityKeyParams, bool addGet, bool addPatch, bool addDelete)
        {
            bool updatable = addPatch && entitySet.GetBooleanCapability(_model,
                "Org.OData.Capabilities.V1.UpdateRestrictions", "Updatable", defaultValue: true);
            bool deletable = addDelete && entitySet.GetBooleanCapability(_model,
                "Org.OData.Capabilities.V1.DeleteRestrictions", "Deletable", defaultValue: true);

            var currentOpParams = new List<OpenApiParameter>(baseParams);
            currentOpParams.AddRange(entityKeyParams);

            if (addGet)
            {
                var getByIdOp = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = tag } },
                    Summary = $"Retrieve specific {entitySet.Name} {summarySuffix}",
                    OperationId = $"Get-{opIdRoot}",
                    Parameters = new List<OpenApiParameter>(currentOpParams),
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = $"{entitySet.Name} retrieved.",
                            Content =
                            new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } }
                                }
                            }
                        },
                        ["404"] = new OpenApiResponse { Description = "Not Found" },
                        ["default"] = new OpenApiResponse { Description = "Error response" }
                    }
                };
                AddODataSingleQueryParams(getByIdOp);
                pathItem.AddOperation(OperationType.Get, getByIdOp);
            }

            if (updatable)
            {
                var patchParamsList = new List<OpenApiParameter>(currentOpParams);
                patchParamsList.Add(new OpenApiParameter
                {
                    Reference =
                    new OpenApiReference { Type = ReferenceType.Parameter, Id = IfMatchHeaderParamId }
                });
                var patchOp = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = tag } },
                    Summary = $"Update specific {entitySet.Name} {summarySuffix}",
                    OperationId = $"Patch-{opIdRoot}",
                    Parameters = patchParamsList,
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Description = $"Properties of {entitySet.Name} to update.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } }
                            }
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = $"{entitySet.Name} updated.",
                            Content =
                            new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } }
                                }
                            }
                        },
                        ["400"] = new OpenApiResponse { Description = "Bad Request" },
                        ["404"] = new OpenApiResponse { Description = "Not Found" },
                        ["412"] = new OpenApiResponse { Description = "Precondition Failed" },
                        ["default"] = new OpenApiResponse { Description = "Error response" }
                    }
                };
                pathItem.AddOperation(OperationType.Patch, patchOp);
            }

            if (deletable)
            {
                var deleteParamsList = new List<OpenApiParameter>(currentOpParams);
                deleteParamsList.Add(new OpenApiParameter
                {
                    Reference =
                    new OpenApiReference { Type = ReferenceType.Parameter, Id = IfMatchHeaderParamId }
                });
                var deleteOp = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = tag } },
                    Summary = $"Delete specific {entitySet.Name} {summarySuffix}",
                    OperationId = $"Delete-{opIdRoot}",
                    Parameters = deleteParamsList,
                    Responses = new OpenApiResponses
                    {
                        ["204"] = new OpenApiResponse { Description = $"{entitySet.Name} deleted." },
                        ["404"] = new OpenApiResponse { Description = "Not Found" },
                        ["412"] = new OpenApiResponse { Description = "Precondition Failed" },
                        ["default"] = new OpenApiResponse { Description = "Error response" }
                    }
                };
                pathItem.AddOperation(OperationType.Delete, deleteOp);
            }
        }

        private void AddODataCollectionQueryParams(OpenApiOperation operation)
        {
            if (operation.Parameters == null) operation.Parameters = new List<OpenApiParameter>();
            operation.Parameters.Add(new OpenApiParameter { Name = "$top", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "integer" } });
            operation.Parameters.Add(new OpenApiParameter { Name = "$skip", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "integer" } });
            operation.Parameters.Add(new OpenApiParameter { Name = "$filter", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "string" } });
            operation.Parameters.Add(new OpenApiParameter { Name = "$select", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "string" } });
            operation.Parameters.Add(new OpenApiParameter { Name = "$orderby", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "string" } });
            operation.Parameters.Add(new OpenApiParameter { Name = "$expand", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "string" } });
        }

        private void AddODataSingleQueryParams(OpenApiOperation operation)
        {
            if (operation.Parameters == null) operation.Parameters = new List<OpenApiParameter>();
            operation.Parameters.Add(new OpenApiParameter { Name = "$select", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "string" } });
            operation.Parameters.Add(new OpenApiParameter { Name = "$expand", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = "string" } });
        }

        private void EnsureSchemaExists(IEdmSchemaType schemaType, string schemaKey)
        {
            if (!_openApiDoc.Components.Schemas.ContainsKey(schemaKey))
            {
                OpenApiSchema generatedSchema = null;
                if (schemaType is IEdmEntityType entityType)
                    generatedSchema = GenerateSchemaForEntityType(entityType);
                else if (schemaType is IEdmComplexType complexType)
                    generatedSchema = GenerateSchemaForComplexType(complexType);

                if (generatedSchema != null)
                {
                    _openApiDoc.Components.Schemas.Add(schemaKey, generatedSchema);
                    Console.WriteLine($"Added schema: '{schemaKey}'");
                }
                else
                {
                    Console.WriteLine($"Warning: Could not generate schema for type '{schemaType.FullName()}' (key: {schemaKey}).");
                }
            }
        }

        private OpenApiSchema GenerateSchemaForEntityType(IEdmEntityType entityType)
        {
            var schema = new OpenApiSchema { Type = "object", Properties = new Dictionary<string, OpenApiSchema>() };
            var requiredProps = entityType.Key().Select(k => k.Name).ToHashSet();
            foreach (var prop in entityType.DeclaredProperties.OfType<IEdmStructuralProperty>())
            {
                ProcessStructuralProperty(prop, schema);
                if (!prop.Type.IsNullable && !requiredProps.Contains(prop.Name))
                    requiredProps.Add(prop.Name);
            }
            if (requiredProps.Any()) schema.Required = requiredProps;
            return schema;
        }

        private OpenApiSchema GenerateSchemaForComplexType(IEdmComplexType complexType)
        {
            var schema = new OpenApiSchema { Type = "object", Properties = new Dictionary<string, OpenApiSchema>() };
            var requiredProps = new HashSet<string>();
            foreach (var prop in complexType.DeclaredProperties.OfType<IEdmStructuralProperty>())
            {
                ProcessStructuralProperty(prop, schema);
                if (!prop.Type.IsNullable) requiredProps.Add(prop.Name);
            }
            if (requiredProps.Any()) schema.Required = requiredProps;
            return schema;
        }

        private void ProcessStructuralProperty(IEdmStructuralProperty prop, OpenApiSchema parentSchema)
        {
            OpenApiSchema propSchema = null;
            if (prop.Type.IsPrimitive())
                propSchema = MapEdmPrimitiveType(prop.Type.AsPrimitive());
            else if (prop.Type.IsEnum())
            {
                var enumType = (IEdmEnumType)prop.Type.Definition;
                propSchema = new OpenApiSchema
                {
                    Type = "string",
                    Enum = enumType.Members.Select(m => (IOpenApiAny)new OpenApiString(m.Name)).ToList()
                };
            }
            else if (prop.Type.IsComplex())
            {
                var complexDef = prop.Type.AsComplex().ComplexDefinition();
                string complexSchemaKey = GetSchemaKey(complexDef);
                if (!_openApiDoc.Components.Schemas.ContainsKey(complexSchemaKey))
                {
                    _openApiDoc.Components.Schemas.Add(complexSchemaKey, new OpenApiSchema { Description = "Recursion placeholder" });
                    _openApiDoc.Components.Schemas[complexSchemaKey] = GenerateSchemaForComplexType(complexDef);
                }
                propSchema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = complexSchemaKey } };
            }
            else if (prop.Type.IsCollection())
            {
                var collType = prop.Type.AsCollection();
                var elemType = collType.ElementType();
                propSchema = new OpenApiSchema { Type = "array", Items = null };
                if (elemType.IsPrimitive())
                    propSchema.Items = MapEdmPrimitiveType(elemType.AsPrimitive());
                else if (elemType.IsComplex())
                {
                    var elemComplexDef = elemType.AsComplex().ComplexDefinition();
                    string elemSchemaKey = GetSchemaKey(elemComplexDef);
                    if (!_openApiDoc.Components.Schemas.ContainsKey(elemSchemaKey))
                    {
                        _openApiDoc.Components.Schemas.Add(elemSchemaKey, new OpenApiSchema { Description = "Placeholder" });
                        _openApiDoc.Components.Schemas[elemSchemaKey] = GenerateSchemaForComplexType(elemComplexDef);
                    }
                    propSchema.Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = elemSchemaKey } };
                }
                else
                    propSchema.Items = new OpenApiSchema { Type = "object", Description = "Unsupported collection element" };
            }
            if (propSchema != null) parentSchema.Properties.Add(prop.Name, propSchema);
        }

        private OpenApiSchema MapEdmPrimitiveType(IEdmPrimitiveTypeReference primitiveRef)
        {
            if (EdmToOpenApiTypeMap.TryGetValue(primitiveRef.PrimitiveKind(), out var typeInfo))
            {
                var schema = new OpenApiSchema { Type = typeInfo.Type, Format = typeInfo.Format };
                if (primitiveRef is IEdmStringTypeReference strRef && strRef.MaxLength.HasValue)
                    schema.MaxLength = strRef.MaxLength.Value;
                return schema;
            }
            Console.WriteLine($"Warning: Unmapped EdmPrimitiveType: {primitiveRef.PrimitiveKind()}");
            return new OpenApiSchema { Type = "string", Description = $"Unknown: {primitiveRef.PrimitiveKind()}" };
        }

        private string GetSchemaKey(IEdmSchemaType schemaType) => schemaType.FullName().Replace(".", "_");
    }

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
                    Console.WriteLine("Error: Missing arguments. Usage: <program>.exe \"<BaseApiUrl>\" \"<AuthType>\"");
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
                Console.WriteLine($"OpenAPI document: {openApiDoc.Paths.Count} paths, {openApiDoc.Components.Schemas.Count} schemas.");

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
                    if (errors?.Any() == true)
                    {
                        Console.WriteLine("EDMX parsing encountered issues:");
                        foreach (var error in errors)
                        {
                            bool isWarning = error.ErrorCode == EdmErrorCode.BadUnresolvedType ||
                                             error.ErrorMessage.Contains("facet is specified");
                            string level = isWarning ? "Warning" : "ERROR";
                            Console.WriteLine($"  - [{level}] ({error.ErrorCode}): {error.ErrorMessage} at {error.ErrorLocation}");
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
                var yamlWriter = new Microsoft.OpenApi.Writers.OpenApiYamlWriter(streamWriter);
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