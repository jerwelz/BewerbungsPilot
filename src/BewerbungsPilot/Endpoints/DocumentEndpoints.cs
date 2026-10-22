using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace BewerbungsPilot.Endpoints;

// HTTP layer for documents. Files are ONLY reachable through these endpoints –
// storage/documents/ is outside wwwroot and never served as static files.
//
// NOTE: There is no login yet. This is a local, single-user application; these
// endpoints must not be exposed publicly until authentication exists.
public static class DocumentEndpoints
{
    // Slightly above 10 MB, so a file of 10–12 MB still gets the friendly German
    // message from ValidateUpload; anything larger is cut off by the server.
    private const long MaxRequestBytes = DocumentService.MaxFileSizeBytes + 2 * 1024 * 1024;

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/applications/{id:int}/documents", GetForApplication);
        app.MapPost("/api/applications/{id:int}/documents", Upload);
        app.MapMethods("/api/documents/{documentId:int}/preview", ["GET", "HEAD"], Preview);
        app.MapGet("/api/documents/{documentId:int}/download", Download);
        app.MapDelete("/api/documents/{documentId:int}", Delete);
    }

    // GET /api/applications/5/documents
    private static async Task<IResult> GetForApplication(int id, DocumentService documents, ApplicationService applications)
    {
        if (await applications.GetByIdAsync(id) is null)
            return ApplicationNotFound();
        var list = await documents.GetForApplicationAsync(id);
        return Results.Ok(list.Select(DocumentResponse.FromEntity));
    }

    // POST /api/applications/5/documents   (multipart/form-data: file, category)
    private static async Task<IResult> Upload(int id, HttpRequest request, DocumentService documents)
    {
        // Simple protection against other websites sending uploads to this local app:
        // browsers mark cross-site requests in the Sec-Fetch-Site header.
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite != "" && fetchSite != "same-origin" && fetchSite != "none")
            return Results.Problem(title: "Diese Anfrage ist nicht erlaubt.", statusCode: StatusCodes.Status403Forbidden);

        if (!request.HasFormContentType)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["file"] = ["Bitte wählen Sie eine Datei aus."] },
                title: "Bitte prüfen Sie Ihre Eingaben.");

        var sizeLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
            sizeLimit.MaxRequestBodySize = MaxRequestBytes;

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync();
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Problem(title: "Die Datei ist zu groß. Erlaubt sind höchstens 10 MB.", statusCode: 413);
        }

        var file = form.Files.GetFile("file");
        var category = form["category"].ToString();
        byte[]? content = null;
        if (file is not null && file.Length <= MaxRequestBytes)
        {
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory);
            content = memory.ToArray();
        }

        var errors = DocumentService.ValidateUpload(file?.FileName, content, category);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Das Dokument konnte nicht hochgeladen werden.");

        var document = await documents.UploadAsync(id, category, file!.FileName, content!);
        return document is null
            ? ApplicationNotFound()
            : Results.Created($"/api/documents/{document.Id}/download", DocumentResponse.FromEntity(document));
    }

    // GET /api/documents/7/preview → shown inside the browser's PDF viewer
    private static Task<IResult> Preview(int documentId, DocumentService documents, HttpResponse response) =>
        SendFile(documentId, inline: true, documents, response);

    // GET /api/documents/7/download → "Save as" with the original file name
    private static Task<IResult> Download(int documentId, DocumentService documents, HttpResponse response) =>
        SendFile(documentId, inline: false, documents, response);

    private static async Task<IResult> SendFile(int documentId, bool inline, DocumentService documents, HttpResponse response)
    {
        var document = await documents.GetAsync(documentId);
        if (document is null)
            return DocumentNotFound();

        var stream = documents.OpenFile(document);
        if (stream is null)
            return Results.Problem(title: "Die Datei wurde im Speicher nicht gefunden.", statusCode: StatusCodes.Status404NotFound);

        // Private files: never cache, never let the browser guess another content type.
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";

        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(document.OriginalFileName); // handles umlauts and spaces safely
        response.Headers.ContentDisposition = disposition.ToString();

        return Results.File(stream, document.ContentType, enableRangeProcessing: true);
    }

    // DELETE /api/documents/7
    private static async Task<IResult> Delete(int documentId, DocumentService documents)
    {
        return await documents.DeleteAsync(documentId) ? Results.NoContent() : DocumentNotFound();
    }

    private static IResult ApplicationNotFound() =>
        Results.Problem(title: "Diese Bewerbung wurde nicht gefunden. Möglicherweise wurde sie bereits gelöscht.",
            statusCode: StatusCodes.Status404NotFound);

    private static IResult DocumentNotFound() =>
        Results.Problem(title: "Dieses Dokument wurde nicht gefunden. Möglicherweise wurde es bereits gelöscht.",
            statusCode: StatusCodes.Status404NotFound);
}
