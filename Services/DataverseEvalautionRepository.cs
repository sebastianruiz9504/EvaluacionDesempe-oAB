using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EvaluacionDesempenoAB.Models;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace EvaluacionDesempenoAB.Services
{
    public class DataverseEvaluacionRepository : IEvaluacionRepository
    {
        private readonly ServiceClient _client;
        private readonly ConcurrentDictionary<string, IReadOnlyList<ChildRelationshipInfo>> _childRelationshipsCache = new(StringComparer.OrdinalIgnoreCase);

        // TABLAS DE CATÁLOGO
        private const string NivelTable          = "crfb7_nivel";
        private const string CompetenciaTable    = "crfb7_competencia";
        private const string ComportamientoTable = "crfb7_comportamiento";

        // TABLAS DE NEGOCIO
        private const string UsuarioTable    = "crfb7_usuario";
        private const string EvaluacionTable = "crfb7_evaluacion";
        private const string DetalleTable    = "crfb7_detalledeevaluacion";
        private const string PlanTable       = "crfb7_plandeaccion";
        private const string FotoUsuarioColumn = "cr3d2_foto";
        private const string FirmaUsuarioColumn = "cr3d2_firma";
        private const string ReporteFirmadoColumn = "cr3d2_reportefirmado";
        private const string ReporteFirmadoNombreColumn = "cr3d2_reportefirmado_name";

        private sealed class ChildRelationshipInfo
        {
            public string ReferencingEntity { get; init; } = string.Empty;
            public string ReferencingAttribute { get; init; } = string.Empty;
        }

        public bool IsDataverseBacked => true;

        public DataverseEvaluacionRepository(ServiceClient client)
        {
            _client = client;
        }

        // =====================================================
        // USUARIOS (crfb7_usuario)
        // =====================================================

        public async Task<UsuarioEvaluado?> GetUsuarioByCorreoAsync(string correo)
        {
            var q = new QueryExpression(UsuarioTable)
            {
                ColumnSet = new ColumnSet(true)
            };

            q.Criteria.AddCondition("crfb7_correoelectronico", ConditionOperator.Equal, correo);

            var result = await _client.RetrieveMultipleAsync(q);
            var entity = result.Entities.FirstOrDefault();

            return entity == null ? null : MapUsuario(entity);
        }

        public async Task<List<UsuarioEvaluado>> GetUsuariosByEvaluadorAsync(string evaluadorCorreo)
        {
            var q = new QueryExpression(UsuarioTable)
            {
                ColumnSet = new ColumnSet(true)
            };

            var filter = new FilterExpression(LogicalOperator.Or);
            filter.AddCondition("crfb7_evaluadorid", ConditionOperator.Equal, evaluadorCorreo);
            filter.AddCondition("cr3d2_correoevaluadorsst", ConditionOperator.Equal, evaluadorCorreo);
            q.Criteria.AddFilter(filter);

            var result = await _client.RetrieveMultipleAsync(q);
            return result.Entities.Select(MapUsuario).ToList();
        }

        public async Task<List<UsuarioEvaluado>> GetUsuariosAsync()
        {
            var q = new QueryExpression(UsuarioTable)
            {
                ColumnSet = new ColumnSet(true)
            };

            var result = await _client.RetrieveMultipleAsync(q);
            return result.Entities.Select(MapUsuario).ToList();
        }

        public async Task<UsuarioEvaluado?> GetUsuarioByIdAsync(Guid id)
        {
            var e = await _client.RetrieveAsync(UsuarioTable, id, new ColumnSet(true));
            return e == null ? null : MapUsuario(e);
        }

        public async Task<List<UsuarioEvaluado>> GetUsuariosByIdsAsync(IEnumerable<Guid> ids)
        {
            var usuarios = new List<UsuarioEvaluado>();
            var usuarioIds = ids
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();

            if (usuarioIds.Length == 0)
            {
                return usuarios;
            }

            foreach (var chunk in usuarioIds.Chunk(200))
            {
                var q = new QueryExpression(UsuarioTable)
                {
                    ColumnSet = new ColumnSet(true)
                };

                q.Criteria.AddCondition("crfb7_usuarioid", ConditionOperator.In, chunk.Cast<object>().ToArray());

                var result = await _client.RetrieveMultipleAsync(q);
                usuarios.AddRange(result.Entities.Select(MapUsuario));
            }

            return usuarios
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .ToList();
        }

        private UsuarioEvaluado MapUsuario(Entity e)
        {
            return new UsuarioEvaluado
            {
                Id = e.Id,
                NombreCompleto   = e.GetAttributeValue<string>("crfb7_nombredeusuario") ?? "",
                Cedula           = e.GetAttributeValue<string>("crfb7_cedula") ?? "",
                Cargo            = e.GetAttributeValue<string>("crfb7_cargo"),
                Gerencia         = e.GetAttributeValue<string>("crfb7_gerencia"),
                Proyecto         = e.GetAttributeValue<string>("cr3d2_proyecto"),
                FechaIngreso     = e.GetAttributeValue<DateTime?>("crfb7_fechaingreso"),
                FechaInicioContrato = e.GetAttributeValue<DateTime?>("crfb7_fechainiciocontrato"),
                FechaFinalizacionContrato = e.GetAttributeValue<DateTime?>("crfb7_fechafinalizacioncontrato"),
                FechaFinalizacionPeriodoPrueba =
                    e.GetAttributeValue<DateTime?>("crfb7_fechafinalizacionperiododeprueba"),
                FechaActivacionEvaluacion =
                    e.GetAttributeValue<DateTime?>("crfb7_fechaactivacionevaluacion"),
                CorreoElectronico = e.GetAttributeValue<string>("crfb7_correoelectronico"),
                EvaluadorNombre   = e.GetAttributeValue<string>("crfb7_evaluadorid"),
                CorreoEvaluador   = e.GetAttributeValue<string>("crfb7_correoevaluador"),
                CorreoEvaluadorSst = e.GetAttributeValue<string>("cr3d2_correoevaluadorsst"),
                CargoJefeInmediato = e.GetAttributeValue<string>("cr3d2_cargodeljefeinmediato"),
                CargoEvaluadorSst = e.GetAttributeValue<string>("cr3d2_cargosst"),
                TipoFormulario    = e.GetAttributeValue<OptionSetValue>("crfb7_tipoformulario")?.Value,
                EsSuperAdministrador = GetBoolOrOptionSet(e, "crfb7_superadministrador"),
                Novedades = e.GetAttributeValue<string>("crfb7_novedades")

            };
        }

        private static bool GetBoolOrOptionSet(Entity e, string attributeLogicalName)
        {
            // Dataverse "Two Options" can surface as bool, while legacy OptionSet returns OptionSetValue.
            var boolValue = e.GetAttributeValue<bool?>(attributeLogicalName);
            if (boolValue.HasValue)
            {
                return boolValue.Value;
            }

            var optionValue = e.GetAttributeValue<OptionSetValue>(attributeLogicalName);
            return optionValue?.Value == 1;
        }

        public async Task UpdateUsuarioNovedadesAsync(Guid usuarioId, string? novedades)
        {
            var entity = new Entity(UsuarioTable, usuarioId)
            {
                ["crfb7_novedades"] = novedades
            };

            await _client.UpdateAsync(entity);
        }

        public async Task UploadFotoUsuarioAsync(Guid usuarioId, string fileName, string? contentType, Stream content)
        {
            var metadata = GetAttributeMetadata(UsuarioTable, FotoUsuarioColumn);

            if (metadata is FileAttributeMetadata)
            {
                await UploadFileColumnAsync(UsuarioTable, usuarioId, FotoUsuarioColumn, fileName, contentType, content);
                return;
            }

            if (metadata is ImageAttributeMetadata imageMetadata)
            {
                await UploadImageColumnAsync(UsuarioTable, usuarioId, FotoUsuarioColumn, imageMetadata, content);
                return;
            }

            throw new InvalidPluginExecutionException($"{UsuarioTable}.{FotoUsuarioColumn} no es una columna de archivo ni de imagen valida.");
        }

        public async Task<ArchivoEvaluacion?> DownloadFotoUsuarioAsync(Guid usuarioId)
        {
            var metadata = GetAttributeMetadata(UsuarioTable, FotoUsuarioColumn);

            if (metadata is FileAttributeMetadata)
            {
                return await DownloadFileColumnAsync(UsuarioTable, usuarioId, FotoUsuarioColumn, "foto_usuario");
            }

            if (metadata is ImageAttributeMetadata)
            {
                return await DownloadImageColumnAsync(UsuarioTable, usuarioId, FotoUsuarioColumn, "foto_usuario");
            }

            throw new InvalidPluginExecutionException($"{UsuarioTable}.{FotoUsuarioColumn} no es una columna de archivo ni de imagen valida.");
        }

        public async Task UploadFirmaUsuarioAsync(Guid usuarioId, string fileName, string? contentType, Stream content)
        {
            var metadata = GetAttributeMetadata(UsuarioTable, FirmaUsuarioColumn);

            if (metadata is FileAttributeMetadata)
            {
                await UploadFileColumnAsync(UsuarioTable, usuarioId, FirmaUsuarioColumn, fileName, contentType, content);
                return;
            }

            if (metadata is ImageAttributeMetadata imageMetadata)
            {
                await UploadImageColumnAsync(UsuarioTable, usuarioId, FirmaUsuarioColumn, imageMetadata, content);
                return;
            }

            throw new InvalidPluginExecutionException($"{UsuarioTable}.{FirmaUsuarioColumn} no es una columna de archivo ni de imagen válida.");
        }

        public async Task<ArchivoEvaluacion?> DownloadFirmaUsuarioAsync(Guid usuarioId)
        {
            var metadata = GetAttributeMetadata(UsuarioTable, FirmaUsuarioColumn);

            if (metadata is FileAttributeMetadata)
            {
                return await DownloadFileColumnAsync(UsuarioTable, usuarioId, FirmaUsuarioColumn, "firma_evaluador");
            }

            if (metadata is ImageAttributeMetadata)
            {
                return await DownloadImageColumnAsync(UsuarioTable, usuarioId, FirmaUsuarioColumn, "firma_evaluador");
            }

            throw new InvalidPluginExecutionException($"{UsuarioTable}.{FirmaUsuarioColumn} no es una columna de archivo ni de imagen válida.");
        }

        // =====================================================
        // NIVELES (crfb7_nivel)
        // =====================================================

        public async Task<List<NivelEvaluacion>> GetNivelesActivosAsync()
        {
            var q = new QueryExpression(NivelTable)
            {
                ColumnSet = new ColumnSet("crfb7_nombrenivel", "crfb7_codigo")
            };

            var result = await _client.RetrieveMultipleAsync(q);
            return result.Entities.Select(MapNivel).ToList();
        }

        public async Task<NivelEvaluacion?> GetNivelByIdAsync(Guid id)
        {
            var e = await _client.RetrieveAsync(NivelTable, id,
                new ColumnSet("crfb7_nombrenivel", "crfb7_codigo"));

            return e == null ? null : MapNivel(e);
        }

        public async Task<List<NivelEvaluacion>> GetNivelesByIdsAsync(IEnumerable<Guid> ids)
        {
            var niveles = new List<NivelEvaluacion>();
            var nivelIds = ids
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();

            if (nivelIds.Length == 0)
            {
                return niveles;
            }

            foreach (var chunk in nivelIds.Chunk(200))
            {
                var q = new QueryExpression(NivelTable)
                {
                    ColumnSet = new ColumnSet("crfb7_nombrenivel", "crfb7_codigo")
                };

                q.Criteria.AddCondition("crfb7_nivelid", ConditionOperator.In, chunk.Cast<object>().ToArray());

                var result = await _client.RetrieveMultipleAsync(q);
                niveles.AddRange(result.Entities.Select(MapNivel));
            }

            return niveles
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .ToList();
        }

        private NivelEvaluacion MapNivel(Entity e)
        {
            return new NivelEvaluacion
            {
                Id     = e.Id,
                Nombre = e.GetAttributeValue<string>("crfb7_nombrenivel") ?? "",
                Codigo = e.GetAttributeValue<string>("crfb7_codigo")
            };
        }

        // =====================================================
        // COMPETENCIAS (crfb7_competencia)
        // =====================================================

        public async Task<List<Competencia>> GetCompetenciasAsync()
        {
            var q = new QueryExpression(CompetenciaTable)
            {
                ColumnSet = new ColumnSet("crfb7_competencia1", "crfb7_dt_orden")
            };

            q.AddOrder("crfb7_dt_orden", OrderType.Ascending);

            var result = await _client.RetrieveMultipleAsync(q);

            return result.Entities.Select(e => new Competencia
            {
                Id     = e.Id,
                Nombre = e.GetAttributeValue<string>("crfb7_competencia1") ?? "",
                Orden  = e.GetAttributeValue<int?>("crfb7_dt_orden") ?? 0
            }).ToList();
        }

        // =====================================================
        // COMPORTAMIENTOS (crfb7_comportamiento)
        // =====================================================

        public async Task<List<Comportamiento>> GetComportamientosByNivelAsync(Guid nivelId)
        {
            var q = new QueryExpression(ComportamientoTable)
            {
                ColumnSet = new ColumnSet(
                    "crfb7_comportamiento1",
                    "crfb7_descripciondelcomportamiento",
                    "crfb7_competencia",
                    "crfb7_niveldeevaluacion"
                )
            };

            q.Criteria.AddCondition("crfb7_niveldeevaluacion", ConditionOperator.Equal, nivelId);

            var result = await _client.RetrieveMultipleAsync(q);

            return result.Entities.Select(e => new Comportamiento
            {
                Id          = e.Id,
                Descripcion = e.GetAttributeValue<string>("crfb7_descripciondelcomportamiento")
                              ?? e.GetAttributeValue<string>("crfb7_comportamiento1")
                              ?? "",
                Orden        = 0, // si agregas un campo de orden lo conectamos aquí
                CompetenciaId = e.GetAttributeValue<EntityReference>("crfb7_competencia")?.Id ?? Guid.Empty,
                NivelId       = e.GetAttributeValue<EntityReference>("crfb7_niveldeevaluacion")?.Id ?? Guid.Empty
            }).ToList();
        }

        public async Task<List<Comportamiento>> GetComportamientosByNivelesAsync(IEnumerable<Guid> nivelIds)
        {
            var comportamientos = new List<Comportamiento>();
            var ids = nivelIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
            {
                return comportamientos;
            }

            foreach (var chunk in ids.Chunk(200))
            {
                var q = new QueryExpression(ComportamientoTable)
                {
                    ColumnSet = new ColumnSet(
                        "crfb7_comportamiento1",
                        "crfb7_descripciondelcomportamiento",
                        "crfb7_competencia",
                        "crfb7_niveldeevaluacion"
                    )
                };

                q.Criteria.AddCondition("crfb7_niveldeevaluacion", ConditionOperator.In, chunk.Cast<object>().ToArray());

                var result = await _client.RetrieveMultipleAsync(q);
                comportamientos.AddRange(result.Entities.Select(e => new Comportamiento
                {
                    Id = e.Id,
                    Descripcion = e.GetAttributeValue<string>("crfb7_descripciondelcomportamiento")
                                  ?? e.GetAttributeValue<string>("crfb7_comportamiento1")
                                  ?? "",
                    Orden = 0,
                    CompetenciaId = e.GetAttributeValue<EntityReference>("crfb7_competencia")?.Id ?? Guid.Empty,
                    NivelId = e.GetAttributeValue<EntityReference>("crfb7_niveldeevaluacion")?.Id ?? Guid.Empty
                }));
            }

            return comportamientos
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .ToList();
        }

        // =====================================================
        // EVALUACIONES (crfb7_evaluacion)
        // =====================================================

        public async Task<List<Evaluacion>> GetEvaluacionesByEvaluadorAsync(string evaluadorCorreo)
        {
            var usuarios = await GetUsuariosByEvaluadorAsync(evaluadorCorreo);
            var usuarioIds = usuarios
                .Select(u => u.Id)
                .Distinct()
                .ToList();

            if (!usuarioIds.Any())
            {
                return new List<Evaluacion>();
            }

            var q = new QueryExpression(EvaluacionTable)
            {
                ColumnSet = new ColumnSet(true)
            };

            q.Criteria.AddCondition(
                "crfb7_usuario",
                ConditionOperator.In,
                usuarioIds.Cast<object>().ToArray());
            q.AddOrder("createdon", OrderType.Descending);

            var result = await _client.RetrieveMultipleAsync(q);
            return result.Entities.Select(MapEvaluacion).ToList();
        }

        public async Task<List<Evaluacion>> GetEvaluacionesAsync()
        {
            var q = new QueryExpression(EvaluacionTable)
            {
                ColumnSet = new ColumnSet(true)
            };

            q.AddOrder("createdon", OrderType.Descending);

            var result = await _client.RetrieveMultipleAsync(q);
            return result.Entities.Select(MapEvaluacion).ToList();
        }

        public async Task<List<Evaluacion>> GetEvaluacionesByUsuarioAsync(Guid usuarioId)
        {
            var q = new QueryExpression(EvaluacionTable)
            {
                ColumnSet = new ColumnSet(true)
            };

            q.Criteria.AddCondition("crfb7_usuario", ConditionOperator.Equal, usuarioId);
            q.AddOrder("createdon", OrderType.Descending);

            var result = await _client.RetrieveMultipleAsync(q);
            return result.Entities.Select(MapEvaluacion).ToList();
        }

        public async Task<Evaluacion?> GetEvaluacionByIdAsync(Guid id)
        {
            var e = await _client.RetrieveAsync(EvaluacionTable, id, new ColumnSet(true));
            return e == null ? null : MapEvaluacion(e);
        }

        private Evaluacion MapEvaluacion(Entity e)
        {
            return new Evaluacion
            {
                Id             = e.Id,
                UsuarioId      = e.GetAttributeValue<EntityReference>("crfb7_usuario")?.Id ?? Guid.Empty,
                // No usamos EvaluadorId (Guid) en este modelo ahora
                EvaluadorId    = Guid.Empty,
                NivelId        = e.GetAttributeValue<EntityReference>("crfb7_nivel")?.Id ?? Guid.Empty,
                FechaEvaluacion = e.GetAttributeValue<DateTime?>("createdon") ?? DateTime.MinValue,
                TipoEvaluacion  = e.GetAttributeValue<string>("crfb7_tipo") ?? "",
                Total           = e.GetAttributeValue<decimal?>("crfb7_total"),
                Observaciones   = e.GetAttributeValue<string>("crfb7_observaciones"),
                FechaProximaEvaluacion = e.GetAttributeValue<DateTime?>("crfb7_fechaproxima"),
                EvaluacionOrigenId = e.GetAttributeValue<EntityReference>("crfb7_evaluacionorigen")?.Id,
                EvaluadorNombre = e.GetAttributeValue<string>("crfb7_evaluadorid"),
                Proyecto = e.GetAttributeValue<string>("cr3d2_proyecto"),
                Gerencia = e.GetAttributeValue<string>("cr3d2_gerencia"),
                ReporteFirmadoId = GetFileId(e, ReporteFirmadoColumn),
                ReporteFirmadoNombre = e.GetAttributeValue<string>(ReporteFirmadoNombreColumn)
            };
        }

        private static Guid? GetFileId(Entity entity, string attributeLogicalName)
        {
            if (!entity.Attributes.TryGetValue(attributeLogicalName, out var rawValue) || rawValue == null)
            {
                return null;
            }

            if (rawValue is Guid guidValue)
            {
                return guidValue;
            }

            if (rawValue is string stringValue && Guid.TryParse(stringValue, out var parsedGuid))
            {
                return parsedGuid;
            }

            return null;
        }

        public Task UploadReporteFirmadoAsync(Guid evaluacionId, string fileName, string? contentType, Stream content)
        {
            var maxSizeInKb = GetFileColumnMaxSizeInKb(EvaluacionTable, ReporteFirmadoColumn);
            if (content.CanSeek && content.Length > maxSizeInKb * 1024L)
            {
                throw new InvalidPluginExecutionException("El archivo supera el tamaño máximo permitido para la columna Reporte firmado.");
            }

            var target = new EntityReference(EvaluacionTable, evaluacionId);
            var initializeRequest = new InitializeFileBlocksUploadRequest
            {
                Target = target,
                FileAttributeName = ReporteFirmadoColumn,
                FileName = fileName
            };

            var initializeResponse = (InitializeFileBlocksUploadResponse)_client.Execute(initializeRequest);
            var token = initializeResponse.FileContinuationToken;
            var blockIds = new List<string>();
            var buffer = new byte[4 * 1024 * 1024];

            int bytesRead;
            while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[bytesRead];
                System.Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
                blockIds.Add(blockId);

                var uploadRequest = new UploadBlockRequest
                {
                    BlockData = chunk,
                    BlockId = blockId,
                    FileContinuationToken = token
                };

                _client.Execute(uploadRequest);
            }

            var commitRequest = new CommitFileBlocksUploadRequest
            {
                BlockList = blockIds.ToArray(),
                FileContinuationToken = token,
                FileName = fileName,
                MimeType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
            };

            _client.Execute(commitRequest);
            return Task.CompletedTask;
        }

        public Task<ArchivoEvaluacion?> DownloadReporteFirmadoAsync(Guid evaluacionId)
        {
            var target = new EntityReference(EvaluacionTable, evaluacionId);
            var initializeRequest = new InitializeFileBlocksDownloadRequest
            {
                Target = target,
                FileAttributeName = ReporteFirmadoColumn
            };

            var initializeResponse = (InitializeFileBlocksDownloadResponse)_client.Execute(initializeRequest);
            if (initializeResponse.FileSizeInBytes <= 0)
            {
                return Task.FromResult<ArchivoEvaluacion?>(null);
            }

            var bytes = new List<byte>((int)initializeResponse.FileSizeInBytes);
            long remaining = initializeResponse.FileSizeInBytes;
            long offset = 0;
            long blockSize = initializeResponse.IsChunkingSupported
                ? 4 * 1024 * 1024
                : initializeResponse.FileSizeInBytes;

            if (remaining < blockSize)
            {
                blockSize = remaining;
            }

            while (remaining > 0)
            {
                var downloadRequest = new DownloadBlockRequest
                {
                    BlockLength = blockSize,
                    FileContinuationToken = initializeResponse.FileContinuationToken,
                    Offset = offset
                };

                var downloadResponse = (DownloadBlockResponse)_client.Execute(downloadRequest);
                bytes.AddRange(downloadResponse.Data);
                remaining -= blockSize;
                offset += blockSize;
            }

            ArchivoEvaluacion archivo = new()
            {
                NombreArchivo = initializeResponse.FileName ?? "reporte_firmado",
                TipoContenido = InferContentType(initializeResponse.FileName, bytes.ToArray()),
                Contenido = bytes.ToArray()
            };

            return Task.FromResult<ArchivoEvaluacion?>(archivo);
        }

        private int GetFileColumnMaxSizeInKb(string entityLogicalName, string fileColumnLogicalName)
        {
            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityLogicalName,
                LogicalName = fileColumnLogicalName
            };

            var response = (RetrieveAttributeResponse)_client.Execute(request);
            if (response.AttributeMetadata is FileAttributeMetadata fileColumn && fileColumn.MaxSizeInKB.HasValue)
            {
                return fileColumn.MaxSizeInKB.Value;
            }

            throw new InvalidPluginExecutionException($"{entityLogicalName}.{fileColumnLogicalName} no es una columna de archivo válida.");
        }

        private AttributeMetadata GetAttributeMetadata(string entityLogicalName, string attributeLogicalName)
        {
            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityLogicalName,
                LogicalName = attributeLogicalName
            };

            var response = (RetrieveAttributeResponse)_client.Execute(request);
            return response.AttributeMetadata
                ?? throw new InvalidPluginExecutionException($"No fue posible recuperar la metadata de {entityLogicalName}.{attributeLogicalName}.");
        }

        private Task UploadFileColumnAsync(
            string entityLogicalName,
            Guid recordId,
            string fileColumnLogicalName,
            string fileName,
            string? contentType,
            Stream content)
        {
            var maxSizeInKb = GetFileColumnMaxSizeInKb(entityLogicalName, fileColumnLogicalName);
            if (content.CanSeek && content.Length > maxSizeInKb * 1024L)
            {
                throw new InvalidPluginExecutionException($"El archivo supera el tamaño máximo permitido para la columna {fileColumnLogicalName}.");
            }

            var target = new EntityReference(entityLogicalName, recordId);
            var initializeRequest = new InitializeFileBlocksUploadRequest
            {
                Target = target,
                FileAttributeName = fileColumnLogicalName,
                FileName = fileName
            };

            var initializeResponse = (InitializeFileBlocksUploadResponse)_client.Execute(initializeRequest);
            var token = initializeResponse.FileContinuationToken;
            var blockIds = new List<string>();
            var buffer = new byte[4 * 1024 * 1024];

            int bytesRead;
            while ((bytesRead = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);

                var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
                blockIds.Add(blockId);

                var uploadRequest = new UploadBlockRequest
                {
                    BlockData = chunk,
                    BlockId = blockId,
                    FileContinuationToken = token
                };

                _client.Execute(uploadRequest);
            }

            var commitRequest = new CommitFileBlocksUploadRequest
            {
                BlockList = blockIds.ToArray(),
                FileContinuationToken = token,
                FileName = fileName,
                MimeType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
            };

            _client.Execute(commitRequest);
            return Task.CompletedTask;
        }

        private Task<ArchivoEvaluacion?> DownloadFileColumnAsync(
            string entityLogicalName,
            Guid recordId,
            string fileColumnLogicalName,
            string defaultFileName)
        {
            var target = new EntityReference(entityLogicalName, recordId);
            var initializeRequest = new InitializeFileBlocksDownloadRequest
            {
                Target = target,
                FileAttributeName = fileColumnLogicalName
            };

            var initializeResponse = (InitializeFileBlocksDownloadResponse)_client.Execute(initializeRequest);
            if (initializeResponse.FileSizeInBytes <= 0)
            {
                return Task.FromResult<ArchivoEvaluacion?>(null);
            }

            var bytes = new List<byte>((int)initializeResponse.FileSizeInBytes);
            long remaining = initializeResponse.FileSizeInBytes;
            long offset = 0;
            long blockSize = initializeResponse.IsChunkingSupported
                ? 4 * 1024 * 1024
                : initializeResponse.FileSizeInBytes;

            if (remaining < blockSize)
            {
                blockSize = remaining;
            }

            while (remaining > 0)
            {
                var downloadRequest = new DownloadBlockRequest
                {
                    BlockLength = blockSize,
                    FileContinuationToken = initializeResponse.FileContinuationToken,
                    Offset = offset
                };

                var downloadResponse = (DownloadBlockResponse)_client.Execute(downloadRequest);
                bytes.AddRange(downloadResponse.Data);
                remaining -= blockSize;
                offset += blockSize;
            }

            return Task.FromResult<ArchivoEvaluacion?>(new ArchivoEvaluacion
            {
                NombreArchivo = initializeResponse.FileName ?? defaultFileName,
                TipoContenido = InferContentType(initializeResponse.FileName, bytes.ToArray()),
                Contenido = bytes.ToArray()
            });
        }

        private async Task UploadImageColumnAsync(
            string entityLogicalName,
            Guid recordId,
            string imageColumnLogicalName,
            ImageAttributeMetadata imageMetadata,
            Stream content)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory);

            var bytes = memory.ToArray();
            var maxSizeInKb = GetMaxSizeInKb(imageMetadata, entityLogicalName, imageColumnLogicalName);
            if (bytes.LongLength > maxSizeInKb * 1024L)
            {
                throw new InvalidPluginExecutionException($"La imagen supera el tamaño máximo permitido para la columna {imageColumnLogicalName}.");
            }

            var entity = new Entity(entityLogicalName, recordId)
            {
                [imageColumnLogicalName] = bytes
            };

            await _client.UpdateAsync(entity);
        }

        private async Task<ArchivoEvaluacion?> DownloadImageColumnAsync(
            string entityLogicalName,
            Guid recordId,
            string imageColumnLogicalName,
            string defaultFileName)
        {
            var entity = await _client.RetrieveAsync(entityLogicalName, recordId, new ColumnSet(imageColumnLogicalName));
            var bytes = entity?.GetAttributeValue<byte[]>(imageColumnLogicalName);
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            var contentType = InferImageContentType(bytes);
            return new ArchivoEvaluacion
            {
                NombreArchivo = defaultFileName + InferImageExtension(contentType),
                TipoContenido = contentType,
                Contenido = bytes
            };
        }

        private static int GetMaxSizeInKb(AttributeMetadata metadata, string entityLogicalName, string attributeLogicalName)
        {
            return metadata switch
            {
                FileAttributeMetadata fileColumn when fileColumn.MaxSizeInKB.HasValue => fileColumn.MaxSizeInKB.Value,
                ImageAttributeMetadata imageColumn when imageColumn.MaxSizeInKB.HasValue => imageColumn.MaxSizeInKB.Value,
                _ => throw new InvalidPluginExecutionException($"{entityLogicalName}.{attributeLogicalName} no expone un tamaño máximo válido.")
            };
        }

        private static string InferImageContentType(byte[] bytes)
        {
            if (bytes.Length >= 4 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47)
            {
                return "image/png";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xD8 &&
                bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            return "application/octet-stream";
        }

        private static string InferImageExtension(string contentType)
        {
            return contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                _ => string.Empty
            };
        }

        private static string InferContentType(string? fileName, byte[] bytes)
        {
            var contentTypeFromName = InferContentTypeFromFileName(fileName);
            if (!string.IsNullOrWhiteSpace(contentTypeFromName))
            {
                return contentTypeFromName;
            }

            return InferImageContentType(bytes);
        }

        private static string? InferContentTypeFromFileName(string? fileName)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            return extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                _ => null
            };
        }

        public async Task<Guid> CreateEvaluacionAsync(Evaluacion evaluacion,
                                                      List<EvaluacionDetalle> detalles,
                                                      List<PlanAccion> planAccion)
        {
            var e = new Entity(EvaluacionTable);

            e["crfb7_usuario"] = new EntityReference(UsuarioTable, evaluacion.UsuarioId);
            e["crfb7_nivel"]   = new EntityReference(NivelTable, evaluacion.NivelId);
            e["crfb7_tipo"]    = evaluacion.TipoEvaluacion;

            if (!string.IsNullOrWhiteSpace(evaluacion.EvaluadorNombre))
                e["crfb7_evaluadorid"] = evaluacion.EvaluadorNombre;
            if (!string.IsNullOrWhiteSpace(evaluacion.Proyecto))
                e["cr3d2_proyecto"] = evaluacion.Proyecto;
            if (!string.IsNullOrWhiteSpace(evaluacion.Gerencia))
                e["cr3d2_gerencia"] = evaluacion.Gerencia;

            if (evaluacion.Total.HasValue)
                e["crfb7_total"] = evaluacion.Total.Value;

            if (!string.IsNullOrWhiteSpace(evaluacion.Observaciones))
                e["crfb7_observaciones"] = evaluacion.Observaciones;

            if (evaluacion.FechaProximaEvaluacion.HasValue)
                e["crfb7_fechaproxima"] = evaluacion.FechaProximaEvaluacion.Value;

            if (evaluacion.EvaluacionOrigenId.HasValue)
                e["crfb7_evaluacionorigen"] =
                    new EntityReference(EvaluacionTable, evaluacion.EvaluacionOrigenId.Value);

            var id = await _client.CreateAsync(e);
            evaluacion.Id = id;

            await SaveDetallesYPlanesAsync(id, detalles, planAccion);

            return id;
        }

        public async Task UpdateEvaluacionAsync(Evaluacion evaluacion,
                                                List<EvaluacionDetalle> detalles,
                                                List<PlanAccion> planAccion)
        {
            var e = new Entity(EvaluacionTable)
            {
                Id = evaluacion.Id
            };

            e["crfb7_usuario"] = new EntityReference(UsuarioTable, evaluacion.UsuarioId);
            e["crfb7_nivel"]   = new EntityReference(NivelTable, evaluacion.NivelId);
            e["crfb7_tipo"]    = evaluacion.TipoEvaluacion;

            e["crfb7_evaluadorid"] = string.IsNullOrWhiteSpace(evaluacion.EvaluadorNombre)
                ? null
                : evaluacion.EvaluadorNombre;
            e["cr3d2_proyecto"] = string.IsNullOrWhiteSpace(evaluacion.Proyecto)
                ? null
                : evaluacion.Proyecto;
            e["cr3d2_gerencia"] = string.IsNullOrWhiteSpace(evaluacion.Gerencia)
                ? null
                : evaluacion.Gerencia;
            e["crfb7_total"] = evaluacion.Total;
            e["crfb7_observaciones"] = string.IsNullOrWhiteSpace(evaluacion.Observaciones)
                ? null
                : evaluacion.Observaciones;
            e["crfb7_fechaproxima"] = evaluacion.FechaProximaEvaluacion;
            e["crfb7_evaluacionorigen"] = evaluacion.EvaluacionOrigenId.HasValue
                ? new EntityReference(EvaluacionTable, evaluacion.EvaluacionOrigenId.Value)
                : null;

            await _client.UpdateAsync(e);

            await DeleteDetallesYPlanesAsync(evaluacion.Id);
            await SaveDetallesYPlanesAsync(evaluacion.Id, detalles, planAccion);
        }

        public Task DeleteEvaluacionAsync(Guid evaluacionId)
        {
            if (evaluacionId == Guid.Empty)
            {
                return Task.CompletedTask;
            }

            return DeleteEntityCascadeAsync(
                EvaluacionTable,
                evaluacionId,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        // =====================================================
        // DETALLES (crfb7_detalledeevaluacion) & PLANES (crfb7_plandeaccion)
        // =====================================================

        private async Task DeleteEntityCascadeAsync(
            string entityLogicalName,
            Guid recordId,
            HashSet<string> visitados)
        {
            if (recordId == Guid.Empty)
            {
                return;
            }

            var visitKey = $"{entityLogicalName}:{recordId:D}";
            if (!visitados.Add(visitKey))
            {
                return;
            }

            foreach (var childRelationship in GetChildRelationships(entityLogicalName))
            {
                var childIds = await GetChildRecordIdsAsync(
                    childRelationship.ReferencingEntity,
                    childRelationship.ReferencingAttribute,
                    recordId);

                foreach (var childId in childIds)
                {
                    await DeleteEntityCascadeAsync(
                        childRelationship.ReferencingEntity,
                        childId,
                        visitados);
                }
            }

            await _client.DeleteAsync(entityLogicalName, recordId);
        }

        private IReadOnlyList<ChildRelationshipInfo> GetChildRelationships(string entityLogicalName)
        {
            return _childRelationshipsCache.GetOrAdd(entityLogicalName, logicalName =>
            {
                var request = new RetrieveEntityRequest
                {
                    LogicalName = logicalName,
                    EntityFilters = EntityFilters.Relationships
                };

                var response = (RetrieveEntityResponse)_client.Execute(request);
                return response.EntityMetadata?.OneToManyRelationships?
                    .Where(relationship => ShouldCascadeDeleteRelationship(logicalName, relationship))
                    .Select(relationship => new ChildRelationshipInfo
                    {
                        ReferencingEntity = relationship.ReferencingEntity ?? string.Empty,
                        ReferencingAttribute = relationship.ReferencingAttribute ?? string.Empty
                    })
                    .GroupBy(
                        relationship => $"{relationship.ReferencingEntity}:{relationship.ReferencingAttribute}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList()
                    ?? new List<ChildRelationshipInfo>();
            });
        }

        private async Task<List<Guid>> GetChildRecordIdsAsync(
            string entityLogicalName,
            string referencingAttribute,
            Guid referencedRecordId)
        {
            var ids = new List<Guid>();
            var pageInfo = new PagingInfo
            {
                Count = 5000,
                PageNumber = 1
            };

            while (true)
            {
                var query = new QueryExpression(entityLogicalName)
                {
                    ColumnSet = new ColumnSet(false),
                    PageInfo = pageInfo
                };

                query.Criteria.AddCondition(referencingAttribute, ConditionOperator.Equal, referencedRecordId);

                var result = await _client.RetrieveMultipleAsync(query);
                ids.AddRange(result.Entities.Select(entity => entity.Id).Where(id => id != Guid.Empty));

                if (!result.MoreRecords)
                {
                    break;
                }

                pageInfo.PageNumber++;
                pageInfo.PagingCookie = result.PagingCookie;
            }

            return ids
                .Distinct()
                .ToList();
        }

        private static bool ShouldCascadeDeleteRelationship(
            string parentEntityLogicalName,
            OneToManyRelationshipMetadata relationship)
        {
            if (!string.Equals(
                    relationship.ReferencedEntity,
                    parentEntityLogicalName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(relationship.ReferencingEntity) ||
                string.IsNullOrWhiteSpace(relationship.ReferencingAttribute))
            {
                return false;
            }

            return IsAppManagedEntity(relationship.ReferencingEntity);
        }

        private static bool IsAppManagedEntity(string entityLogicalName)
        {
            return entityLogicalName.StartsWith("crfb7_", StringComparison.OrdinalIgnoreCase) ||
                   entityLogicalName.StartsWith("cr3d2_", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SaveDetallesYPlanesAsync(Guid evalId,
                                                    List<EvaluacionDetalle> detalles,
                                                    List<PlanAccion> planes)
        {
            // Detalles
            foreach (var d in detalles)
            {
                var det = new Entity(DetalleTable);
                det["crfb7_dt_evaluacionid"] =
                    new EntityReference(EvaluacionTable, evalId);
                det["crfb7_dt_comportamientoid"] =
                    new EntityReference(ComportamientoTable, d.ComportamientoId);
                det["crfb7_dt_puntaje"] = d.Puntaje;

                if (!string.IsNullOrWhiteSpace(d.Comentario))
                    det["crfb7_dt_comentario"] = d.Comentario;

                await _client.CreateAsync(det);
            }

            // Planes
            foreach (var p in planes)
            {
                var plan = new Entity(PlanTable);
                plan["crfb7_evaluacion"] =
                    new EntityReference(EvaluacionTable, evalId);
                plan["crfb7_descripciondelaaccion"] = p.DescripcionAccion;

                if (!string.IsNullOrWhiteSpace(p.Responsable))
                    plan["crfb7_responsable"] = p.Responsable;

                if (p.FechaCompromiso.HasValue)
                    plan["crfb7_fechacompromiso"] = p.FechaCompromiso.Value;

                await _client.CreateAsync(plan);
            }
        }

        private async Task DeleteDetallesYPlanesAsync(Guid evalId)
        {
            // Detalles
            var q1 = new QueryExpression(DetalleTable)
            {
                ColumnSet = new ColumnSet(false)
            };
            q1.Criteria.AddCondition("crfb7_dt_evaluacionid", ConditionOperator.Equal, evalId);

            var r1 = await _client.RetrieveMultipleAsync(q1);
            foreach (var d in r1.Entities)
                await _client.DeleteAsync(DetalleTable, d.Id);

            // Planes
            var q2 = new QueryExpression(PlanTable)
            {
                ColumnSet = new ColumnSet(false)
            };
            q2.Criteria.AddCondition("crfb7_evaluacion", ConditionOperator.Equal, evalId);

            var r2 = await _client.RetrieveMultipleAsync(q2);
            foreach (var p in r2.Entities)
                await _client.DeleteAsync(PlanTable, p.Id);
        }

        public async Task<List<EvaluacionDetalle>> GetDetallesByEvaluacionAsync(Guid evalId)
        {
            var q = new QueryExpression(DetalleTable)
            {
                ColumnSet = new ColumnSet(true)
            };
            q.Criteria.AddCondition("crfb7_dt_evaluacionid", ConditionOperator.Equal, evalId);

            var r = await _client.RetrieveMultipleAsync(q);
            return r.Entities.Select(e => new EvaluacionDetalle
            {
                Id              = e.Id,
                EvaluacionId    = evalId,
                ComportamientoId = e.GetAttributeValue<EntityReference>("crfb7_dt_comportamientoid")?.Id ?? Guid.Empty,
                Puntaje         = e.GetAttributeValue<int?>("crfb7_dt_puntaje") ?? 0,
                Comentario      = e.GetAttributeValue<string>("crfb7_dt_comentario")
            }).ToList();
        }

        public async Task<List<EvaluacionDetalle>> GetDetallesByEvaluacionesAsync(IEnumerable<Guid> evaluacionIds)
        {
            var detalles = new List<EvaluacionDetalle>();
            var ids = evaluacionIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
            {
                return detalles;
            }

            foreach (var chunk in ids.Chunk(200))
            {
                var q = new QueryExpression(DetalleTable)
                {
                    ColumnSet = new ColumnSet(true)
                };
                q.Criteria.AddCondition("crfb7_dt_evaluacionid", ConditionOperator.In, chunk.Cast<object>().ToArray());

                var result = await _client.RetrieveMultipleAsync(q);
                detalles.AddRange(result.Entities.Select(e => new EvaluacionDetalle
                {
                    Id = e.Id,
                    EvaluacionId = e.GetAttributeValue<EntityReference>("crfb7_dt_evaluacionid")?.Id ?? Guid.Empty,
                    ComportamientoId = e.GetAttributeValue<EntityReference>("crfb7_dt_comportamientoid")?.Id ?? Guid.Empty,
                    Puntaje = e.GetAttributeValue<int?>("crfb7_dt_puntaje") ?? 0,
                    Comentario = e.GetAttributeValue<string>("crfb7_dt_comentario")
                }));
            }

            return detalles
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .ToList();
        }

        public async Task<List<PlanAccion>> GetPlanesByEvaluacionAsync(Guid evalId)
        {
            var q = new QueryExpression(PlanTable)
            {
                ColumnSet = new ColumnSet(true)
            };
            q.Criteria.AddCondition("crfb7_evaluacion", ConditionOperator.Equal, evalId);

            var r = await _client.RetrieveMultipleAsync(q);
            return r.Entities.Select(e => new PlanAccion
            {
                Id               = e.Id,
                EvaluacionId     = evalId,
                ComportamientoNombre = e.GetAttributeValue<string>("crfb7_responsable"),
                DescripcionAccion = e.GetAttributeValue<string>("crfb7_descripciondelaaccion") ?? "",
                Responsable      = e.GetAttributeValue<string>("crfb7_responsable"),
                FechaCompromiso  = e.GetAttributeValue<DateTime?>("crfb7_fechacompromiso")
            }).ToList();
        }
    }
}
