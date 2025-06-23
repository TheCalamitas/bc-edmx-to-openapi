using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Vocabularies;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace EdmxToYaml
{
    public class OpenApiGenerator
    {
        private const string CompanyIdParamNamePath = "companyId";
        private const string IfMatchHeaderParamId = "IfMatchHeaderParam";
        private const string CompanyIdPathParamId = "CompanyIdPathParam";

        private readonly IEdmModel _model;
        private readonly string _baseApiUrl;
        private readonly OpenApiDocument _openApiDoc;
        private string? _securitySchemeName;
        private readonly string _companyEntitySetName;
        private readonly string _authenticationType;
        private int _actionsCount = 0;
        private int _functionsCount = 0;

        private IEdmEntitySet? _companyEntitySet;
        private IEdmEntityType? _companyEntityType;

        public int ActionsCount => _actionsCount;
        public int FunctionsCount => _functionsCount;

        static readonly Dictionary<EdmPrimitiveTypeKind, (string Type, string? Format)> EdmToOpenApiTypeMap =
            new()
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
            ProcessUnboundOperations(entityContainer);

            AddCompanyRootPaths();

            Console.WriteLine("Starting recursive path generation...");

            var companyEntityPath = $"/{_companyEntitySetName}({{{CompanyIdParamNamePath}}})";
            var rootParameters = new List<OpenApiParameter>
            {
                new OpenApiParameter { Reference = new OpenApiReference { Type = ReferenceType.Parameter, Id = CompanyIdPathParamId } }
            };

            GeneratePathsForEntity(_companyEntitySet, companyEntityPath, rootParameters, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Console.WriteLine("OpenAPI document generation complete.");
            return _openApiDoc;
        }

        private void GeneratePathsForEntity(IEdmEntitySet parentSet, string parentBasePath, List<OpenApiParameter> inheritedParameters, HashSet<string> processedPaths)
        {
            string indent = new string(' ', (inheritedParameters.Count) * 2);

            if (!processedPaths.Add(parentBasePath))
            {
                return;
            }

            Console.WriteLine($"{indent}Processing Entity: '{parentSet.Name}' at path '{parentBasePath}'");

            ProcessBoundOperations(parentSet.EntityType(), parentBasePath, inheritedParameters, isCollectionContext: false);

            foreach (var navProp in parentSet.EntityType().NavigationProperties())
            {
                var childEntityType = navProp.ToEntityType();
                var childSet = _model.EntityContainer.FindEntitySet(navProp.Name) ??
                               _model.EntityContainer.EntitySets().FirstOrDefault(es => es.EntityType().IsOrInheritsFrom(childEntityType));

                if (childSet == null)
                {
                    continue;
                }

                bool isLogicalJump = navProp.Partner != null && navProp.Partner.DeclaringType != parentSet.EntityType();

                string childSchemaKey = GetSchemaKey(childEntityType);
                EnsureSchemaExists(childEntityType, childSchemaKey);

                if (navProp.TargetMultiplicity() == EdmMultiplicity.Many)
                {
                    string collPathKey = $"{parentBasePath}/{navProp.Name}";
                    if (_openApiDoc.Paths.TryAdd(collPathKey, new OpenApiPathItem()))
                    {
                        var collPathItem = _openApiDoc.Paths[collPathKey];
                        CreateOperationsForCollectionPath(collPathItem, childSet, childSchemaKey, childSet.Name,
                            $"{childSet.Name}-from-{parentSet.Name}", $"under {parentSet.Name}",
                            inheritedParameters, true, true);
                    }
                    ProcessBoundOperations(childEntityType, collPathKey, inheritedParameters, isCollectionContext: true);

                    var (childKeySegment, childKeyDefs) = GenerateEntityKeyPathSegmentAndParameters(childSet.EntityType(), childSet.Name);

                    if (!string.IsNullOrEmpty(childKeySegment) && childKeyDefs.Any())
                    {
                        string entityPathKey = $"{collPathKey}{childKeySegment}";
                        var childKeyParamRefs = childKeyDefs.Select(p => new OpenApiParameter
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.Parameter, Id = CreateOrGetComponentParameter(p, childSet.Name) }
                        }).ToList();

                        if (_openApiDoc.Paths.TryAdd(entityPathKey, new OpenApiPathItem()))
                        {
                            var entityPathItem = _openApiDoc.Paths[entityPathKey];
                            CreateOperationsForEntityPath(entityPathItem, childSet, childSchemaKey, childSet.Name,
                                $"{childSet.Name}ByKey-from-{parentSet.Name}", $"by key, under {parentSet.Name}",
                                inheritedParameters, childKeyParamRefs, true, true, true);
                        }

                        if (!isLogicalJump)
                        {
                            var nextInheritedParams = inheritedParameters.Concat(childKeyParamRefs)
                                .GroupBy(p => p.Reference.Id)
                                .Select(g => g.First())
                                .ToList();
                            GeneratePathsForEntity(childSet, entityPathKey, nextInheritedParams, new HashSet<string>(processedPaths));
                        }
                    }
                }
                else
                {
                    string toOnePathKey = $"{parentBasePath}/{navProp.Name}";
                    if (_openApiDoc.Paths.TryAdd(toOnePathKey, new OpenApiPathItem()))
                    {
                        var pathItem = _openApiDoc.Paths[toOnePathKey];
                        var getOp = new OpenApiOperation
                        {
                            Tags = new List<OpenApiTag> { new OpenApiTag { Name = parentSet.Name } },
                            Summary = $"Get related {navProp.Name} for {parentSet.Name}",
                            OperationId = $"Get-Related-{parentSet.Name}-{navProp.Name}",
                            Parameters = new List<OpenApiParameter>(inheritedParameters),
                            Responses = new OpenApiResponses
                            {
                                ["200"] = new OpenApiResponse
                                {
                                    Description = $"Related {childEntityType.Name} retrieved.",
                                    Content = new Dictionary<string, OpenApiMediaType>
                                    {
                                        ["application/json"] = new OpenApiMediaType
                                        {
                                            Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = childSchemaKey } }
                                        }
                                    }
                                },
                                ["404"] = new OpenApiResponse { Description = "Not Found" }
                            }
                        };
                        AddODataSingleQueryParams(getOp);
                        pathItem.AddOperation(OperationType.Get, getOp);

                        if (!isLogicalJump)
                        {
                            GeneratePathsForEntity(childSet, toOnePathKey, inheritedParameters, new HashSet<string>(processedPaths));
                        }
                    }
                }
            }
        }

        private string CreateOrGetComponentParameter(OpenApiParameter parameter, string entitySetName)
        {
            string componentParamId = $"{entitySetName}_{parameter.Name}_KeyPathParam";
            if (!_openApiDoc.Components.Parameters.ContainsKey(componentParamId))
            {
                _openApiDoc.Components.Parameters.Add(componentParamId, parameter);
            }
            return componentParamId;
        }

        private void AddCompanyRootPaths()
        {
            if (_companyEntityType is null || _companyEntitySet is null) return;

            string companySchemaKey = GetSchemaKey(_companyEntityType);
            EnsureSchemaExists(_companyEntityType, companySchemaKey);

            string collectionPathKey = $"/{_companyEntitySetName}";
            var collectionPathItem = new OpenApiPathItem();
            CreateOperationsForCollectionPath(collectionPathItem, _companyEntitySet, companySchemaKey,
                _companyEntitySetName, _companyEntitySetName, $"list of {_companyEntitySetName}",
                new List<OpenApiParameter>(), true, false);
            _openApiDoc.Paths.Add(collectionPathKey, collectionPathItem);
            Console.WriteLine($"Added root collection path: {collectionPathKey}");

            string entityPathKey = $"/{_companyEntitySetName}({{{CompanyIdParamNamePath}}})";
            var entityPathItem = new OpenApiPathItem();
            var companyIdParamRef = new List<OpenApiParameter>
            {
                new OpenApiParameter { Reference = new OpenApiReference {Type = ReferenceType.Parameter, Id = CompanyIdPathParamId}}
            };

            CreateOperationsForEntityPath(entityPathItem, _companyEntitySet, companySchemaKey,
                _companyEntitySetName, $"{_companyEntitySetName}ById", $"single company by ID",
                new List<OpenApiParameter>(), companyIdParamRef, true, false, false);
            _openApiDoc.Paths.Add(entityPathKey, entityPathItem);
            Console.WriteLine($"Added root entity path: {entityPathKey}");
        }

        private (string? pathSegment, List<OpenApiParameter> keyParameters)
            GenerateEntityKeyPathSegmentAndParameters(IEdmEntityType entityType, string entitySetNameCtx)
        {
            var keyProps = entityType.Key().ToList();
            if (!keyProps.Any())
            {
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
                        new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = _securitySchemeName! }
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
                OpenApiSchema? generatedSchema = null;
                if (schemaType is IEdmEntityType entityType)
                    generatedSchema = GenerateSchemaForEntityType(entityType);
                else if (schemaType is IEdmComplexType complexType)
                    generatedSchema = GenerateSchemaForComplexType(complexType);

                if (generatedSchema != null)
                {
                    _openApiDoc.Components.Schemas.Add(schemaKey, generatedSchema);
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

            foreach (var prop in entityType.Properties().OfType<IEdmStructuralProperty>())
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

            foreach (var prop in complexType.Properties().OfType<IEdmStructuralProperty>())
            {
                ProcessStructuralProperty(prop, schema);
                if (!prop.Type.IsNullable) requiredProps.Add(prop.Name);
            }

            if (requiredProps.Any()) schema.Required = requiredProps;
            return schema;
        }

        private void ProcessStructuralProperty(IEdmStructuralProperty prop, OpenApiSchema parentSchema)
        {
            OpenApiSchema? propSchema = null;
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
                propSchema = new OpenApiSchema { Type = "array", Items = new OpenApiSchema() };
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

        private OpenApiSchema? MapEdmPrimitiveType(IEdmPrimitiveTypeReference primitiveRef)
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

        private void ProcessBoundOperations(IEdmEntityType bindingEntityType, string basePath, List<OpenApiParameter> inheritedParameters, bool isCollectionContext)
        {
            var allOperations = _model.SchemaElements.OfType<IEdmOperation>();

            foreach (var operation in allOperations)
            {
                if (!operation.IsBound || !operation.Parameters.Any())
                {
                    continue;
                }

                var bindingParameter = operation.Parameters.First();
                var bindingParamType = bindingParameter.Type;

                bool operationIsForCollection = bindingParamType.IsCollection();

                var operationBindingEntityType = (operationIsForCollection
                    ? bindingParamType.AsCollection().ElementType().Definition
                    : bindingParamType.Definition) as IEdmEntityType;

                if (operationBindingEntityType?.FullTypeName() != bindingEntityType.FullTypeName())
                {
                    continue;
                }

                if (isCollectionContext != operationIsForCollection)
                {
                    continue;
                }

                var pathKey = $"{basePath}/{operation.FullName()}";
                if (_openApiDoc.Paths.ContainsKey(pathKey))
                {
                    continue;
                }

                var pathItem = new OpenApiPathItem();

                if (operation is IEdmAction action)
                {
                    var postOp = CreateOperationForAction(action, inheritedParameters);
                    pathItem.AddOperation(OperationType.Post, postOp);
                }
                else if (operation is IEdmFunction function)
                {
                    var getOp = CreateOperationForFunction(function, inheritedParameters);
                    pathItem.AddOperation(OperationType.Get, getOp);
                }

                _openApiDoc.Paths.Add(pathKey, pathItem);
                Console.WriteLine($"      Added bound operation: {pathKey}");
            }
        }

        private void ProcessUnboundOperations(IEdmEntityContainer container)
        {
            if (container.OperationImports() == null) return;

            foreach (var operationImport in container.OperationImports())
            {
                var pathKey = $"/{operationImport.Name}";
                var pathItem = _openApiDoc.Paths.ContainsKey(pathKey) ? _openApiDoc.Paths[pathKey] : new OpenApiPathItem();

                if (operationImport is IEdmActionImport actionImport)
                {
                    var postOp = CreateOperationForAction(actionImport.Action, new List<OpenApiParameter>());
                    pathItem.AddOperation(OperationType.Post, postOp);
                }
                else if (operationImport is IEdmFunctionImport functionImport)
                {
                    var getOp = CreateOperationForFunction(functionImport.Function, new List<OpenApiParameter>());
                    pathItem.AddOperation(OperationType.Get, getOp);
                }

                if (!_openApiDoc.Paths.ContainsKey(pathKey))
                {
                    _openApiDoc.Paths.Add(pathKey, pathItem);
                    Console.WriteLine($"Added unbound operation: {pathKey}");
                }
            }
        }

        private OpenApiOperation CreateOperationForAction(IEdmAction action, List<OpenApiParameter> inheritedParams)
        {
            _actionsCount++;
            var operation = new OpenApiOperation
            {
                Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Actions" } },
                Summary = $"Execute action {action.Name}",
                OperationId = $"Action-{action.Name}-{Guid.NewGuid()}",
                Parameters = new List<OpenApiParameter>(inheritedParams),
                Responses = new OpenApiResponses()
            };

            var requestBodySchema = new OpenApiSchema { Type = "object", Properties = new Dictionary<string, OpenApiSchema>() };
            var requiredParams = new HashSet<string>();

            foreach (var param in action.Parameters.Skip(1))
            {
                var paramSchema = MapEdmType(param.Type);
                if (paramSchema is not null)
                {
                    requestBodySchema.Properties.Add(param.Name, paramSchema);
                    if (!param.Type.IsNullable)
                    {
                        requiredParams.Add(param.Name);
                    }
                }
            }

            if (requestBodySchema.Properties.Any())
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = requestBodySchema
                        }
                    }
                };
                if (requiredParams.Any()) requestBodySchema.Required = requiredParams;
            }

            OpenApiResponse response;
            if (action.ReturnType == null)
            {
                response = new OpenApiResponse { Description = "Action executed successfully with no return content." };
                operation.Responses.Add("204", response);
            }
            else
            {
                var responseSchema = MapEdmType(action.ReturnType);
                if (responseSchema is not null)
                {
                    response = new OpenApiResponse { Description = "Action executed successfully.", Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = responseSchema } } };
                    operation.Responses.Add("200", response);
                }
            }

            operation.Responses.Add("default", new OpenApiResponse { Description = "Error response" });

            return operation;
        }

        private OpenApiOperation CreateOperationForFunction(IEdmFunction function, List<OpenApiParameter> inheritedParams)
        {
            _functionsCount++;
            var operation = new OpenApiOperation
            {
                Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Functions" } },
                Summary = $"Execute function {function.Name}",
                OperationId = $"Function-{function.Name}-{Guid.NewGuid()}",
                Parameters = new List<OpenApiParameter>(inheritedParams),
                Responses = new OpenApiResponses()
            };

            foreach (var param in function.Parameters.Skip(1))
            {
                var paramSchema = MapEdmType(param.Type);
                if (paramSchema is not null)
                {
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = param.Name,
                        In = ParameterLocation.Query,
                        Required = !param.Type.IsNullable,
                        Schema = paramSchema
                    });
                }
            }

            var responseSchema = MapEdmType(function.ReturnType);
            if (responseSchema is not null)
            {
                operation.Responses.Add("200", new OpenApiResponse { Description = "Function executed successfully.", Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = responseSchema } } });
            }

            operation.Responses.Add("default", new OpenApiResponse { Description = "Error response" });

            return operation;
        }

        private OpenApiSchema? MapEdmType(IEdmTypeReference? edmType)
        {
            if (edmType == null) return new OpenApiSchema { Type = "object", Nullable = true, Description = "No content" };

            switch (edmType.TypeKind())
            {
                case EdmTypeKind.Primitive:
                    return MapEdmPrimitiveType(edmType.AsPrimitive());
                case EdmTypeKind.Entity:
                case EdmTypeKind.Complex:
                    var schemaType = (IEdmSchemaType)edmType.Definition;
                    var schemaKey = GetSchemaKey(schemaType);
                    EnsureSchemaExists(schemaType, schemaKey);
                    return new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaKey } };
                case EdmTypeKind.Collection:
                    var collectionType = edmType.AsCollection();
                    var items = MapEdmType(collectionType.ElementType());
                    if (items is null) return null;
                    return new OpenApiSchema
                    {
                        Type = "array",
                        Items = items
                    };
                case EdmTypeKind.Enum:
                    var enumType = (IEdmEnumType)edmType.Definition;
                    return new OpenApiSchema
                    {
                        Type = "string",
                        Enum = enumType.Members.Select(m => (IOpenApiAny)new OpenApiString(m.Name)).ToList()
                    };
                default:
                    return new OpenApiSchema { Type = "object", Description = $"Unsupported type: {edmType.FullName()}" };
            }
        }
    }
}