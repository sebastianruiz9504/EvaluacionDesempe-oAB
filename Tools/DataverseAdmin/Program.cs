using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

const string UsuarioTable = "crfb7_usuario";
const int SpanishLanguageCode = 3082;

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "metadata";
var repoRoot = ResolveRepositoryRoot();
var connectionString = BuildConnectionString(repoRoot);

using var client = new ServiceClient(connectionString);
if (!client.IsReady)
{
    Console.Error.WriteLine($"Dataverse no esta listo: {client.LastError}");
    return 1;
}

return command switch
{
    "metadata" => PrintMetadata(client),
    "ensure-schema" => EnsureSchema(client),
    "flows" => PrintFlows(client),
    "dump-flow" => DumpFlow(client, args.Skip(1).ToArray()),
    "update-approval-flow" => UpdateApprovalFlow(client, args.Skip(1).ToArray()),
    "ensure-scheduled-flow" => EnsureScheduledFlow(client),
    _ => PrintUsage()
};

static int PrintUsage()
{
    Console.WriteLine("Uso: dotnet run --project Tools/DataverseAdmin -- metadata|ensure-schema|flows|dump-flow <workflowId> <outputPath>|update-approval-flow <workflowId>|ensure-scheduled-flow");
    return 2;
}

static string ResolveRepositoryRoot()
{
    var current = new DirectoryInfo(System.AppContext.BaseDirectory);
    while (current != null && !File.Exists(Path.Combine(current.FullName, "appsettings.json")))
    {
        current = current.Parent;
    }

    if (current == null)
    {
        throw new InvalidOperationException("No se encontro appsettings.json.");
    }

    return current.FullName;
}

static string BuildConnectionString(string repoRoot)
{
    var appSettingsPath = Path.Combine(repoRoot, "appsettings.json");
    using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
    var root = document.RootElement;

    var rawConnectionString = root
        .GetProperty("ConnectionStrings")
        .GetProperty("Dataverse")
        .GetString();

    if (string.IsNullOrWhiteSpace(rawConnectionString))
    {
        throw new InvalidOperationException("No hay ConnectionStrings:Dataverse configurado.");
    }

    var builder = new DbConnectionStringBuilder { ConnectionString = rawConnectionString };
    if (string.Equals(Environment.GetEnvironmentVariable("DATAVERSE_ADMIN_AUTH"), "oauth", StringComparison.OrdinalIgnoreCase))
    {
        var url = builder.TryGetValue("Url", out var rawUrl) ? rawUrl?.ToString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("No hay Url en la cadena Dataverse.");
        }

        return "AuthType=OAuth;" +
               $"Url={url};" +
               "Username=digital@aguasdebogota.com.co;" +
               "ClientId=51f81489-12ee-4a9e-aaae-a2591f45987d;" +
               "RedirectUri=http://localhost;" +
               "LoginPrompt=Never";
    }

    if (!builder.ContainsKey("ClientSecret") &&
        root.TryGetProperty("AzureAd", out var azureAd) &&
        azureAd.TryGetProperty("ClientSecret", out var secret) &&
        !string.IsNullOrWhiteSpace(secret.GetString()))
    {
        builder["ClientSecret"] = secret.GetString();
    }

    builder.Remove("RedirectUri");
    builder.Remove("LoginPrompt");
    return builder.ConnectionString;
}

static int PrintMetadata(ServiceClient client)
{
    var request = new RetrieveEntityRequest
    {
        LogicalName = UsuarioTable,
        EntityFilters = EntityFilters.Attributes,
        RetrieveAsIfPublished = true
    };

    var response = (RetrieveEntityResponse)client.Execute(request);
    var attributes = response.EntityMetadata.Attributes
        .Where(a => a.LogicalName.StartsWith("crfb7_", StringComparison.OrdinalIgnoreCase) ||
                    a.LogicalName.StartsWith("cr3d2_", StringComparison.OrdinalIgnoreCase))
        .OrderBy(a => a.LogicalName)
        .Select(a => new
        {
            a.LogicalName,
            a.MetadataId,
            Schema = a.SchemaName,
            Type = a.AttributeTypeName?.Value ?? a.AttributeType?.ToString() ?? "",
            Label = a.DisplayName?.UserLocalizedLabel?.Label ?? ""
        });

    foreach (var attribute in attributes)
    {
        Console.WriteLine($"{attribute.LogicalName,-45} {attribute.MetadataId} {attribute.Type,-25} {attribute.Label}");
    }

    return 0;
}

static int EnsureSchema(ServiceClient client)
{
    var existing = GetExistingAttributes(client);

    EnsureBoolean(
        client,
        existing,
        "cr3d2_Habilitado",
        "Habilitado",
        "Si",
        "No",
        defaultValue: false);

    EnsureString(
        client,
        existing,
        "cr3d2_NombreEvaluadorSST",
        "Nombre evaluador SST",
        200);

    EnsureDateOnly(
        client,
        existing,
        "cr3d2_FechaActivacionProgramada",
        "Fecha activacion programada");

    client.Execute(new PublishXmlRequest
    {
        ParameterXml = $"<importexportxml><entities><entity>{UsuarioTable}</entity></entities><nodes/><securityroles/><settings/><workflows/></importexportxml>"
    });

    Console.WriteLine("Schema verificado y publicado.");
    return 0;
}

static HashSet<string> GetExistingAttributes(ServiceClient client)
{
    var request = new RetrieveEntityRequest
    {
        LogicalName = UsuarioTable,
        EntityFilters = EntityFilters.Attributes,
        RetrieveAsIfPublished = true
    };

    var response = (RetrieveEntityResponse)client.Execute(request);
    return response.EntityMetadata.Attributes
        .Select(a => a.LogicalName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static void EnsureBoolean(
    ServiceClient client,
    HashSet<string> existing,
    string schemaName,
    string displayName,
    string trueLabel,
    string falseLabel,
    bool defaultValue)
{
    var logicalName = schemaName.ToLowerInvariant();
    if (existing.Contains(logicalName))
    {
        Console.WriteLine($"Existe: {logicalName}");
        return;
    }

    var request = new CreateAttributeRequest
    {
        EntityName = UsuarioTable,
        Attribute = new BooleanAttributeMetadata
        {
            SchemaName = schemaName,
            DisplayName = new Label(displayName, SpanishLanguageCode),
            Description = new Label(displayName, SpanishLanguageCode),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            DefaultValue = defaultValue,
            OptionSet = new BooleanOptionSetMetadata(
                new OptionMetadata(new Label(trueLabel, SpanishLanguageCode), 1),
                new OptionMetadata(new Label(falseLabel, SpanishLanguageCode), 0))
        }
    };

    client.Execute(request);
    existing.Add(logicalName);
    Console.WriteLine($"Creada: {logicalName}");
}

static void EnsureString(ServiceClient client, HashSet<string> existing, string schemaName, string displayName, int maxLength)
{
    var logicalName = schemaName.ToLowerInvariant();
    if (existing.Contains(logicalName))
    {
        Console.WriteLine($"Existe: {logicalName}");
        return;
    }

    var request = new CreateAttributeRequest
    {
        EntityName = UsuarioTable,
        Attribute = new StringAttributeMetadata
        {
            SchemaName = schemaName,
            DisplayName = new Label(displayName, SpanishLanguageCode),
            Description = new Label(displayName, SpanishLanguageCode),
            MaxLength = maxLength,
            FormatName = StringFormatName.Text,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
        }
    };

    client.Execute(request);
    existing.Add(logicalName);
    Console.WriteLine($"Creada: {logicalName}");
}

static void EnsureDateOnly(ServiceClient client, HashSet<string> existing, string schemaName, string displayName)
{
    var logicalName = schemaName.ToLowerInvariant();
    if (existing.Contains(logicalName))
    {
        Console.WriteLine($"Existe: {logicalName}");
        return;
    }

    var request = new CreateAttributeRequest
    {
        EntityName = UsuarioTable,
        Attribute = new DateTimeAttributeMetadata
        {
            SchemaName = schemaName,
            DisplayName = new Label(displayName, SpanishLanguageCode),
            Description = new Label(displayName, SpanishLanguageCode),
            Format = DateTimeFormat.DateOnly,
            DateTimeBehavior = DateTimeBehavior.DateOnly,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
        }
    };

    client.Execute(request);
    existing.Add(logicalName);
    Console.WriteLine($"Creada: {logicalName}");
}

static int PrintFlows(ServiceClient client)
{
    var query = new QueryExpression("workflow")
    {
        ColumnSet = new ColumnSet("workflowid", "name", "category", "statecode", "statuscode", "clientdata", "createdon", "modifiedon"),
        TopCount = 100
    };
    query.Criteria.AddCondition("category", ConditionOperator.Equal, 5);
    query.Orders.Add(new OrderExpression("modifiedon", OrderType.Descending));

    var result = client.RetrieveMultiple(query);
    foreach (var flow in result.Entities)
    {
        Console.WriteLine($"{flow.Id} | {flow.GetAttributeValue<string>("name")} | state={flow.GetAttributeValue<OptionSetValue>("statecode")?.Value} status={flow.GetAttributeValue<OptionSetValue>("statuscode")?.Value}");
    }

    return 0;
}

static int DumpFlow(ServiceClient client, string[] args)
{
    if (args.Length < 2 || !Guid.TryParse(args[0], out var workflowId))
    {
        Console.Error.WriteLine("Uso: dump-flow <workflowId> <outputPath>");
        return 2;
    }

    var outputPath = Path.GetFullPath(args[1]);
    var flow = client.Retrieve(
        "workflow",
        workflowId,
        new ColumnSet(true));

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var lines = flow.Attributes
        .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kvp => $"{kvp.Key}: {FormatValue(kvp.Value)}");

    File.WriteAllLines(outputPath, lines);
    Console.WriteLine($"Flujo exportado a {outputPath}");
    return 0;
}

static int UpdateApprovalFlow(ServiceClient client, string[] args)
{
    if (args.Length < 1 || !Guid.TryParse(args[0], out var workflowId))
    {
        Console.Error.WriteLine("Uso: update-approval-flow <workflowId>");
        return 2;
    }

    var flow = client.Retrieve("workflow", workflowId, new ColumnSet("name", "clientdata", "statecode", "statuscode"));
    var clientData = flow.GetAttributeValue<string>("clientdata");
    if (string.IsNullOrWhiteSpace(clientData))
    {
        throw new InvalidOperationException("El flujo no tiene clientdata.");
    }

    var root = JsonNode.Parse(clientData)?.AsObject()
        ?? throw new InvalidOperationException("El clientdata del flujo no es JSON valido.");

    var definition = root["properties"]?["definition"]?.AsObject()
        ?? throw new InvalidOperationException("No se encontro properties.definition.");
    var actions = definition["actions"]?.AsObject()
        ?? throw new InvalidOperationException("No se encontraron acciones del flujo.");
    var approval = actions["Iniciar_y_esperar_una_aprobación"]?.AsObject()
        ?? throw new InvalidOperationException("No se encontro la accion de aprobacion.");
    actions["Responder_solicitud_recibida"] = new JsonObject
    {
        ["runAfter"] = new JsonObject(),
        ["type"] = "Response",
        ["kind"] = "Http",
        ["inputs"] = new JsonObject
        {
            ["statusCode"] = 202,
            ["body"] = new JsonObject
            {
                ["status"] = "pending"
            }
        }
    };
    approval["runAfter"] = new JsonObject
    {
        ["Responder_solicitud_recibida"] = new JsonArray("Succeeded")
    };

    var approvalParameters = approval["inputs"]?["parameters"]?.AsObject()
        ?? throw new InvalidOperationException("No se encontraron parametros de aprobacion.");

    approvalParameters["WebhookApprovalCreationInput/title"] =
        "Solicitud de habilitacion de evaluacion para @{triggerBody()?['usuarioNombre']} (@{triggerBody()?['usuarioCedula']})";
    approvalParameters["WebhookApprovalCreationInput/assignedTo"] = "jully.pinto@aguasdebogota.com.co";
    approvalParameters["WebhookApprovalCreationInput/details"] =
        "Evaluador: @{triggerBody()?['evaluadorNombre']} (@{triggerBody()?['evaluadorCorreo']})\n" +
        "Usuario: @{triggerBody()?['usuarioNombre']} (@{triggerBody()?['usuarioCedula']})\n" +
        "Correo usuario: @{triggerBody()?['usuarioCorreo']}\n\n" +
        "Si apruebas, se cambiara la columna Habilitado a Si para esta fila de Dataverse.\n";

    var updateParameters = actions["Condición"]?["actions"]?["Actualizar_una_fila"]?["inputs"]?["parameters"]?.AsObject()
        ?? throw new InvalidOperationException("No se encontraron parametros de actualizacion de Dataverse.");
    updateParameters.Remove("item/cr3d2_fechadeactivacionevaluacion");
    updateParameters["item/cr3d2_habilitado"] = true;
    actions["Condición"]?["actions"]?.AsObject().Remove("Respuesta");

    SetFlowState(client, workflowId, 0, 1);
    var update = new Entity("workflow", workflowId)
    {
        ["name"] = "App- Desempeño - Solicitud Aprobación Habilitado",
        ["clientdata"] = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
    };
    client.Update(update);
    SetFlowState(client, workflowId, 1, 2);

    Console.WriteLine($"Flujo de aprobacion actualizado: {workflowId}");
    return 0;
}

static int EnsureScheduledFlow(ServiceClient client)
{
    const string flowName = "App- Desempeño - Activación programada habilitado";
    var existing = FindCloudFlowByName(client, flowName);
    var referenceFlow = FindCloudFlowByName(client, "App- Desempeño - Solicitud Aprobación Habilitado")
        ?? FindCloudFlowByName(client, "App- Desempeño - Solicitud Aprobación Desempeño")
        ?? throw new InvalidOperationException("No se encontro un flujo base para reutilizar la conexion de Dataverse.");
    var owner = referenceFlow.GetAttributeValue<EntityReference>("ownerid");

    var baseClientData = referenceFlow.GetAttributeValue<string>("clientdata")
        ?? throw new InvalidOperationException("El flujo base no tiene clientdata.");
    var baseRoot = JsonNode.Parse(baseClientData)?.AsObject()
        ?? throw new InvalidOperationException("El clientdata base no es JSON valido.");
    var connectionReference = baseRoot["properties"]?["connectionReferences"]?["shared_commondataserviceforapps"]?.DeepClone()
        ?? throw new InvalidOperationException("No se encontro la referencia de conexion de Dataverse.");

    var root = new JsonObject
    {
        ["properties"] = new JsonObject
        {
            ["connectionReferences"] = new JsonObject
            {
                ["shared_commondataserviceforapps"] = connectionReference
            },
            ["definition"] = BuildScheduledFlowDefinition()
        },
        ["schemaVersion"] = "1.0.0.0"
    };

    var clientData = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    Guid workflowId;
    if (existing == null)
    {
        var create = new Entity("workflow")
        {
            ["name"] = flowName,
            ["category"] = new OptionSetValue(5),
            ["type"] = new OptionSetValue(1),
            ["mode"] = new OptionSetValue(0),
            ["scope"] = new OptionSetValue(4),
            ["runas"] = new OptionSetValue(1),
            ["ondemand"] = false,
            ["primaryentity"] = "none",
            ["clientdata"] = clientData,
            ["clientdataiscompressed"] = false
        };

        workflowId = client.Create(create);
        AssignFlowOwner(client, workflowId, owner);
        Console.WriteLine($"Flujo programado creado: {workflowId}");
    }
    else
    {
        workflowId = existing.Id;
        SetFlowState(client, workflowId, 0, 1);
        AssignFlowOwner(client, workflowId, owner);
        var update = new Entity("workflow", workflowId)
        {
            ["clientdata"] = clientData
        };
        client.Update(update);
        Console.WriteLine($"Flujo programado actualizado: {workflowId}");
    }

    SetFlowState(client, workflowId, 1, 2);
    return 0;
}

static Entity? FindCloudFlowByName(ServiceClient client, string name)
{
    var query = new QueryExpression("workflow")
    {
        ColumnSet = new ColumnSet("workflowid", "name", "clientdata", "statecode", "statuscode", "ownerid"),
        TopCount = 1
    };
    query.Criteria.AddCondition("category", ConditionOperator.Equal, 5);
    query.Criteria.AddCondition("name", ConditionOperator.Equal, name);

    return client.RetrieveMultiple(query).Entities.FirstOrDefault();
}

static JsonObject BuildScheduledFlowDefinition()
{
    return new JsonObject
    {
        ["$schema"] = "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#",
        ["contentVersion"] = "1.0.0.0",
        ["parameters"] = new JsonObject
        {
            ["$authentication"] = new JsonObject
            {
                ["defaultValue"] = new JsonObject(),
                ["type"] = "SecureObject"
            },
            ["$connections"] = new JsonObject
            {
                ["defaultValue"] = new JsonObject(),
                ["type"] = "Object"
            }
        },
        ["triggers"] = new JsonObject
        {
            ["Recurrence"] = new JsonObject
            {
                ["type"] = "Recurrence",
                ["recurrence"] = new JsonObject
                {
                    ["frequency"] = "Day",
                    ["interval"] = 1,
                    ["timeZone"] = "SA Pacific Standard Time",
                    ["schedule"] = new JsonObject
                    {
                        ["hours"] = new JsonArray(6),
                        ["minutes"] = new JsonArray(0)
                    }
                }
            }
        },
        ["actions"] = new JsonObject
        {
            ["Listar_usuarios_programados"] = new JsonObject
            {
                ["runAfter"] = new JsonObject(),
                ["type"] = "OpenApiConnection",
                ["inputs"] = new JsonObject
                {
                    ["parameters"] = new JsonObject
                    {
                        ["entityName"] = "crfb7_usuarios",
                        ["$filter"] = "cr3d2_habilitado eq false and cr3d2_fechaactivacionprogramada le '@{formatDateTime(convertTimeZone(utcNow(),'UTC','SA Pacific Standard Time'),'yyyy-MM-dd')}'"
                    },
                    ["host"] = new JsonObject
                    {
                        ["apiId"] = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps",
                        ["operationId"] = "ListRecords",
                        ["connectionName"] = "shared_commondataserviceforapps"
                    }
                }
            },
            ["Por_cada_usuario"] = new JsonObject
            {
                ["foreach"] = "@outputs('Listar_usuarios_programados')?['body/value']",
                ["actions"] = new JsonObject
                {
                    ["Habilitar_usuario"] = new JsonObject
                    {
                        ["type"] = "OpenApiConnection",
                        ["inputs"] = new JsonObject
                        {
                            ["parameters"] = new JsonObject
                            {
                                ["entityName"] = "crfb7_usuarios",
                                ["recordId"] = "@items('Por_cada_usuario')?['crfb7_usuarioid']",
                                ["item/cr3d2_habilitado"] = true
                            },
                            ["host"] = new JsonObject
                            {
                                ["apiId"] = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps",
                                ["operationId"] = "UpdateOnlyRecord",
                                ["connectionName"] = "shared_commondataserviceforapps"
                            }
                        }
                    }
                },
                ["runAfter"] = new JsonObject
                {
                    ["Listar_usuarios_programados"] = new JsonArray("Succeeded")
                },
                ["type"] = "Foreach"
            }
        },
        ["outputs"] = new JsonObject()
    };
}

static void AssignFlowOwner(ServiceClient client, Guid workflowId, EntityReference? owner)
{
    if (owner == null)
    {
        return;
    }

    try
    {
        client.Execute(new AssignRequest
        {
            Target = new EntityReference("workflow", workflowId),
            Assignee = owner
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"No se pudo reasignar el flujo {workflowId}: {ex.Message}");
    }
}

static void SetFlowState(ServiceClient client, Guid workflowId, int state, int status)
{
    try
    {
        client.Execute(new SetStateRequest
        {
            EntityMoniker = new EntityReference("workflow", workflowId),
            State = new OptionSetValue(state),
            Status = new OptionSetValue(status)
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"No se pudo cambiar el estado del flujo {workflowId} a {state}/{status}: {ex.Message}");
    }
}

static string FormatValue(object? value)
{
    return value switch
    {
        null => "",
        OptionSetValue option => option.Value.ToString(),
        EntityReference reference => $"{reference.LogicalName}:{reference.Id}:{reference.Name}",
        Money money => money.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTime date => date.ToString("O"),
        _ => value.ToString() ?? ""
    };
}
