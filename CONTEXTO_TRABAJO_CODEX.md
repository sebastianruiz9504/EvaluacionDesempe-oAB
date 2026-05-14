# Contexto de trabajo para Codex

Ultima actualizacion: 2026-05-14

Este documento resume el contexto funcional y tecnico reciente del proyecto
`EvaluacionDesempenoAB`. La idea es que un nuevo chat pueda leer este archivo
antes de tocar codigo y no tenga que redescubrir todo desde cero.

## Proyecto

Aplicacion ASP.NET Core MVC / .NET 8 para evaluacion de desempeno.

Repositorio local:

`C:\Users\SebastianRuiz\EvaluacionDesempenoAB`

App Service Azure:

`EvdesempenoAB`

Resource group:

`DigitalTechAppAI`

URL produccion:

`https://evdesempenoab.azurewebsites.net`

Backend principal:

Dataverse, con `MockRepository` para pruebas locales/en memoria.

## Regla central de roles

El permiso para diligenciar NO debe salir de `EsSuperAdministrador`.

La parte que puede contestar un usuario se resuelve solo por correo asignado:

- Evaluador normal: si el correo actual coincide con `EvaluadorNombre` o `CorreoEvaluador`.
- Evaluador SST: si el correo actual coincide con `CorreoEvaluadorSst`.
- Ambos bloques: solo si el mismo correo esta asignado en ambos lados.
- Superadministrador: puede consultar/administrar, pero no adquiere automaticamente preguntas normales + SST.

Archivo clave:

`Helpers/EvaluacionRolesHelper.cs`

Metodo clave:

`ResolveParte(usuarioObjetivo, correoActual)`

## Flujo funcional actual

1. El evaluador entra y selecciona un usuario evaluado.
2. Si es evaluador normal, solo ve competencias normales.
3. Si es evaluador SST, solo ve `CULTURA SST`.
4. Si el mismo correo esta asignado como normal y SST, ve ambos bloques.
5. Al guardar preguntas, se redirige a:

   `/Evaluaciones/PlanAccion/{evaluacionId}`

6. En plan de accion:
   - Si el evaluador no tiene firma guardada, debe subir una firma valida para guardar el plan.
   - Si ya tiene firma guardada y valida, puede guardar el plan sin volver a subirla.
   - Si quiere, puede actualizar la firma despues.
7. Cuando existe plan de accion + firma valida, el plan queda bloqueado para cambios.
8. El certificado solo puede emitirse cuando:
   - Evaluacion normal completa.
   - Evaluacion SST completa.
   - Firma valida del evaluador normal.
   - Firma valida del evaluador SST.

## Habilitacion de evaluacion

La regla unica para que un evaluador pueda iniciar o continuar una evaluacion es
la columna booleana de Dataverse `cr3d2_habilitado` en `crfb7_usuario`, mapeada
como `UsuarioEvaluado.Habilitado`.

Ya no se debe habilitar por ventanas naturales de 20/25 dias sobre fecha de
finalizacion de contrato o periodo de prueba. Esas fechas pueden mostrarse o
importarse, pero no deciden si se puede evaluar.

Cuando un usuario no esta habilitado, la pantalla `Views/Usuarios/Index.cshtml`
muestra `Solicitar aprobación`. Esa accion llama el flujo configurado en
`PowerAutomate:SolicitudActivacionEvaluacionUrl`. El flujo activo se llama
`App- Desempeño - Solicitud Aprobación Habilitado`, solicita aprobacion a
`jully.pinto@aguasdebogota.com.co` y, si se aprueba, cambia
`cr3d2_habilitado` a `true`.

Tambien existe `cr3d2_fechaactivacionprogramada`, mapeada como
`UsuarioEvaluado.FechaActivacionProgramada`. La importacion deja en `No` los
usuarios con fecha futura y en `Si` los que no tienen fecha futura. La app tiene
una salvaguarda que habilita filas vencidas al consultar o iniciar evaluaciones.
Se creo el flujo `App- Desempeño - Activación programada habilitado`, pero su
activacion puede requerir licencia Power Automate Premium para Dataverse.

Superadministradores (`crfb7_superadministrador`) ven en `Mis evaluaciones` la
descarga de plantilla e importacion Excel de usuarios. La importacion cruza por
`crfb7_cedula`; si existe actualiza columnas de la plantilla, si no existe crea
la fila.

Desde 2026-05-14 los mismos controles de usuarios tambien estan visibles en
`Modulo admin`, arriba de la tabla:

- `Descargar plantilla usuarios`
- `Subir usuarios`

En `Modulo admin` la tabla tiene columna `Habilitado` con boton por fila para
`Habilitar` o `Deshabilitar`, guardando directamente `cr3d2_habilitado`.

## Firmas

Tipos permitidos para firma:

- PNG real.
- JPG/JPEG real.

No basta con la extension. El sistema valida los bytes del archivo:

- PNG: firma binaria PNG.
- JPG/JPEG: firma binaria JPEG.

Esto evita aceptar un PDF, TXT u otro archivo renombrado como `.jpg`.

Metodos clave:

- `GetTipoContenidoFirma`
- `DetectarTipoContenidoFirma`
- `TieneFirmaValida`
- `ConvertirArchivoADataUrl`

Archivo:

`Controllers/EvaluacionesController.cs`

## Certificados

Accion:

`ImprimirResultados(Guid id)`

Accion auxiliar para UI:

`EstadoCertificado(Guid id)`

Reglas actuales:

- Bloquea si falta normal o SST.
- Bloquea si falta firma valida normal o SST.
- Retorna `ReporteImpresion` solo cuando todo esta completo.
- En `Mis evaluaciones`, antes de emitir se consulta `EstadoCertificado`.
- En `Mis evaluaciones` tambien existe el boton grande `Subir o actualizar
  firma`, como primera accion individual. Usa el mismo popup de firma, pero en
  modo guardar firma, sin emitir certificado.
- Si al evaluador actual le falta firma, se abre un popup para subir PNG/JPG.
- Si el evaluador actual ya tiene firma pero falta la del otro evaluador, se
  abre un popup informativo de firma pendiente.

Vista:

`Views/Evaluaciones/ReporteImpresion.cshtml`

Boton de emision:

`Views/Evaluaciones/Index.cshtml`

## Error 500 investigado

Error original visto en Azure:

`No ImageDescriptor records found for image attribute :cr3d2_firma`

Ocurria al intentar leer una firma vacia/inexistente desde Dataverse.

Correccion:

El repositorio ahora trata ese caso como "no hay archivo" en vez de dejar
explotar HTTP 500.

Archivo:

`Services/DataverseEvalautionRepository.cs`

Metodos afectados:

- `DownloadReporteFirmadoAsync`
- `DownloadFileColumnAsync`
- `IsFullImageNotAvailable`

## Error de carga de firma investigado

Error visto al guardar plan de accion y subir firma:

`crfb7_usuario.cr3d2_firma no es una columna de archivo valida.`

Causa:

`cr3d2_firma` puede ser una columna de imagen con imagen completa habilitada.
Dataverse permite subir estas imagenes con los mismos mensajes de bloques que
las columnas de archivo, pero el repositorio estaba validando el tamano con un
helper que aceptaba solo `FileAttributeMetadata`.

Correccion:

`UploadFileOrFullImageColumnAsync` ahora usa `GetMaxSizeInKb` con la metadata
real (`FileAttributeMetadata` o `ImageAttributeMetadata`) antes de subir por
bloques. Asi se conserva la validacion de tamano sin rechazar columnas de
imagen completas.

Ademas, para firmas no se acepta el modo de miniatura de Dataverse:

- Si `cr3d2_firma` es imagen, debe tener `CanStoreFullImage = true`.
- La carga se bloquea si la columna solo guarda miniatura, porque Dataverse
  recorta la miniatura a formato cuadrado.
- La descarga de firma en columna de imagen usa bloques para traer la imagen
  completa. Si no hay imagen completa disponible, se trata como firma faltante
  y no se usa la miniatura recortada.

Archivo:

`Services/DataverseEvalautionRepository.cs`

## UI del plan de accion

Vista:

`Views/Evaluaciones/Reporte.cshtml`

Cambios importantes:

- Hay modo explicito `PlanAccion`.
- Si falta firma inicial, el boton muestra flujo de guardar plan + subir firma.
- Si ya hay firma, el guardado del plan es directo.
- El boton de firma queda separado como `Actualizar firma` cuando corresponde.
- El modal de firma se abre automaticamente si hay error por firma faltante/invalida.

## Pruebas automatizadas de escenario

Se agrego un proyecto simple de pruebas por consola:

`ScenarioTests/ScenarioTests.csproj`

Ejecutar:

```powershell
dotnet run --project ScenarioTests\ScenarioTests.csproj
```

Salida esperada:

```text
OK - escenarios reales simulados en memoria completados.
```

Escenarios cubiertos:

- Evaluador normal no ve SST.
- Evaluador SST solo ve SST.
- Guardar plan sin firma inicial se bloquea.
- Archivo `.jpg` falso se rechaza.
- JPG valido se acepta aunque el `Content-Type` venga raro.
- PNG valido se acepta.
- Certificado se bloquea si falta firma SST.
- Estado de certificado devuelve `currentMissingSignature` para abrir popup de
  firma propia.
- El endpoint usado por el popup permite subir firma PNG/JPG del evaluador
  actual y revalidar el certificado.
- Estado de certificado devuelve `otherMissingSignature` si falta la firma del
  otro evaluador.
- Certificado final se genera cuando ambas firmas existen.
- Firma JPG puede actualizarse despues de plan firmado.
- Si el evaluador ya tiene firma previa, puede guardar plan sin subirla de nuevo.

El proyecto principal excluye `ScenarioTests\**` en `EvaluacionDesempenoAB.csproj`
para evitar que sus instrucciones de nivel superior se compilen dentro de la app web.

## Comandos utiles

Build rapido:

```powershell
dotnet build EvaluacionDesempenoAB.csproj --no-restore -v minimal
```

Pruebas de escenario:

```powershell
dotnet run --project ScenarioTests\ScenarioTests.csproj
```

Smoke Dataverse real para plantilla/importacion/evaluacion/certificado:

```powershell
dotnet run --project Tools\DataverseSmoke\DataverseSmoke.csproj
```

Ese smoke usa `digital@aguasdebogota.com.co` como evaluador/superadmin, descarga
la plantilla desde el controller, importa 5 usuarios `CODEXSMOKE20260514xx`,
evalua `CODEXSMOKE2026051401` como evaluador normal + SST, sube firma y valida
que `ImprimirResultados` emita `ReporteImpresion`.

Publicar:

```powershell
dotnet publish EvaluacionDesempenoAB.csproj -c Release --no-restore
```

Deploy usado:

```powershell
$zip = Join-Path $env:TEMP ('evdesempenoab-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.zip')
Compress-Archive -Path .\bin\Release\net8.0\publish\* -DestinationPath $zip -Force
az webapp deploy --resource-group DigitalTechAppAI --name EvdesempenoAB --src-path $zip --type zip --output json
```

Smoke test:

```powershell
Invoke-WebRequest -Uri 'https://evdesempenoab.azurewebsites.net/' -MaximumRedirection 0
```

Un `302` es esperado porque la app exige autenticacion.

## Ultimos despliegues relevantes

2026-05-12 / 2026-05-13:

- `905efb2aa7864c388e0726aa21ecd004`
- `0ec0d27880c04d8e880dbdf12a6d6c48`

El ultimo despliegue conocido exitoso fue:

`0ec0d27880c04d8e880dbdf12a6d6c48`

2026-05-14:

- Deploy zip exitoso a `EvdesempenoAB`.
- Deployment id Azure: `5fcc2ad8e7944b8dbfa5ab414ed4574d`.
- Incluye importacion Excel en `Mis evaluaciones` y `Modulo admin`, columna
  `Habilitado` con botones manuales en Admin, y regla unica por
  `cr3d2_habilitado`.

2026-05-14, ajuste certificado:

- Deploy zip exitoso a `EvdesempenoAB`.
- Deployment id Azure: `fda82cc858534e44b093a037fd401c0f`.
- Incluye popup de carga de firma al emitir certificado cuando falta la firma
  del evaluador actual, y popup informativo cuando falta la firma del otro
  evaluador.
- Deployment id Azure posterior: `90ee8e7268f443fbbbd91c2a7c4d7b97`.
- Incluye boton grande `Subir o actualizar firma` en `Mis evaluaciones`.

## Advertencias y cuidado

- No crear usuarios temporales en Dataverse de produccion sin plan de limpieza.
- Para pruebas funcionales automatizadas usar `MockRepository`.
- No volver a permitir certificado sin firmas validas.
- No volver a usar `EsSuperAdministrador` para decidir que preguntas se contestan.
- Revisar siempre si hay cambios pendientes antes de editar; el repo suele tener
  artefactos `bin/` y `obj/` modificados por builds.
- Hay secretos en `appsettings.json`; no documentarlos ni copiarlos en reportes.

## Archivos tocados recientemente

- `Controllers/EvaluacionesController.cs`
- `Controllers/AdminController.cs`
- `Controllers/UsuariosController.cs`
- `Helpers/EvaluacionRolesHelper.cs`
- `Services/DataverseEvalautionRepository.cs`
- `Services/IEvaluacionRepository.cs`
- `Services/MockRepository.cs`
- `Models/Usuario Evaluado.cs`
- `ViewModels/EvaluacionViewModels.cs`
- `ViewModels/UsuarioEvaluadoViewModel.cs`
- `Views/Evaluaciones/Reporte.cshtml`
- `Views/Evaluaciones/Index.cshtml`
- `Views/Admin/Index.cshtml`
- `Views/Usuarios/Index.cshtml`
- `EvaluacionDesempenoAB.csproj`
- `ScenarioTests/Program.cs`
- `ScenarioTests/ScenarioTests.csproj`
- `Tools/DataverseAdmin/Program.cs`
- `Tools/DataverseAdmin/DataverseAdmin.csproj`
- `Tools/DataverseSmoke/Program.cs`
- `Tools/DataverseSmoke/DataverseSmoke.csproj`

## Recomendacion para chats nuevos

Antes de modificar codigo, leer este archivo y luego revisar el diff actual:

```powershell
git status --short
git diff -- Controllers EvaluacionDesempenoAB.csproj Helpers Services ViewModels Views ScenarioTests
```

Si se cambia una regla de flujo, actualizar este documento en el mismo turno.
