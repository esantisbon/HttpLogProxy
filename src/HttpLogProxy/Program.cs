﻿using Azure.Storage.Blobs;

using ICSharpCode.SharpZipLib.Zip;

using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.IO;

using System.Text;
using System.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();
RecyclableMemoryStreamManager memoryStreamManager = new RecyclableMemoryStreamManager();

app.Map("/{**path}", async (HttpRequest request, IHttpClientFactory httpClientFactory, IConfiguration config) =>
{
    string storageCnnStr = config.GetConnectionString("Storage")
        ?? config.GetValue<string>("StorageConnectionString")
        ?? config.GetValue<string>("Storage-ConnectionString")
        ?? config.GetValue<string>("storage-connectionstring")
        ?? config.GetValue<string>("storageconnectionstring");
    if (string.IsNullOrEmpty(storageCnnStr))
    {
        return Results.BadRequest(error: "Storage not accesible!");
    }
    var httpClient = httpClientFactory.CreateClient();
    var ahora = DateTime.UtcNow;
    var targetUri = GetTargetUri(request);
    if (targetUri == null)
    {
        var qs = HttpUtility.ParseQueryString(request.QueryString.ToString());
        qs.Add("url", "www.example.com");
        var uriBuilder = new UriBuilder(request.GetEncodedUrl());
        uriBuilder.Query = qs.ToString();
        return Results.Text($"<h2>Usos:</h2><br/><p>{uriBuilder.Uri}</p><p>{request.GetEncodedUrl()}www.example.com/&lt;Target path and querystring&gt;</p>", "text/html");
    }
    var context = request.HttpContext;
    var sb = new StringBuilder();
    HttpRequestMessage requestMessage = new();
    HttpResponseMessage responseMessage;
    var msZip = memoryStreamManager.GetStream();
    var outStream = new ZipOutputStream(msZip);
    var msReqBody = memoryStreamManager.GetStream();
    await using (msReqBody.ConfigureAwait(false))
    {
        sb.Append(request.Method).Append(" ").Append(targetUri.AbsoluteUri).Append(" ").Append(request.Protocol).AppendLine();
        foreach (var head in request.Headers)
        {
            foreach (string headerValue in head.Value)
            {
                sb.Append(head.Key);
                sb.Append(": ");
                sb.AppendLine(headerValue);
            }
        }
        if (!HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsDelete(request.Method)
            && !HttpMethods.IsTrace(request.Method))
        {
            await using (request.Body)
            {
                await request.Body.CopyToAsync(msReqBody).ConfigureAwait(false);
            }
            if (IsTextContentType(request?.ContentType))
            {
                sb.AppendLine();
                sb.Append(Encoding.UTF8.GetString(msReqBody.ToArray()));
            }
            else
            {
                var zipEntryBinReq = new ZipEntry("request.bin")
                {
                    DateTime = ahora,
                    CompressionMethod = CompressionMethod.Deflated
                };
                await outStream.PutNextEntryAsync(zipEntryBinReq).ConfigureAwait(false);
                msReqBody.Position = 0;
                await msReqBody.CopyToAsync(outStream).ConfigureAwait(false);
                await outStream.FlushAsync().ConfigureAwait(false);
            }
            msReqBody.Position = 0;
            var streamContent = new StreamContent(msReqBody);
            requestMessage.Content = streamContent;
        }
        var zipEntryReq = new ZipEntry("request.txt")
        {
            DateTime = ahora,
            CompressionMethod = CompressionMethod.Deflated
        };
        await outStream.PutNextEntryAsync(zipEntryReq).ConfigureAwait(false);
        outStream.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        await outStream.FlushAsync().ConfigureAwait(false);
        sb.Clear();
        if (request != null)
        {
            foreach (var header in request.Headers)
            {
                requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            requestMessage.Method = GetMethod(request.Method);
        }
        requestMessage.RequestUri = targetUri;
        requestMessage.Headers.Host = targetUri.Host;
        responseMessage = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
    }
    var msRespBody = memoryStreamManager.GetStream();
    await using (msZip.ConfigureAwait(false))
    await using (outStream.ConfigureAwait(false))
    await using (msRespBody.ConfigureAwait(false))
    using (responseMessage)
    {

        foreach (var head in responseMessage.Headers)
        {
            foreach (string headerValue in head.Value)
            {
                sb.Append(head.Key);
                sb.Append(": ");
                sb.AppendLine(headerValue);
            }
        }
        context.Response.StatusCode = (int)responseMessage.StatusCode;
        var respStream = await responseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using (respStream.ConfigureAwait(false))
        {
            await respStream.CopyToAsync(msRespBody).ConfigureAwait(false);
        }
        string? contentType = responseMessage?.Content?.Headers?.ContentType?.MediaType;
        if (IsTextContentType(contentType))
        {
            sb.AppendLine();
            sb.Append(Encoding.UTF8.GetString(msRespBody.ToArray()));
        }
        else
        {
            ZipEntry? zipEntryBinResp = new ZipEntry("response.bin")
            {
                DateTime = ahora,
                CompressionMethod = CompressionMethod.Deflated
            };
            await outStream.PutNextEntryAsync(zipEntryBinResp).ConfigureAwait(false);
            msRespBody.Position = 0;
            await msRespBody.CopyToAsync(outStream).ConfigureAwait(false);
            await outStream.FlushAsync().ConfigureAwait(false);
        }
        var zipEntryResp = new ZipEntry("response.txt")
        {
            DateTime = ahora,
            CompressionMethod = CompressionMethod.Deflated
        };
        await outStream.PutNextEntryAsync(zipEntryResp).ConfigureAwait(false);
        outStream.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        sb.Clear();
        await outStream.FlushAsync().ConfigureAwait(false);
        await outStream.FinishAsync(default).ConfigureAwait(false);
        msZip.Position = 0;
        string container = NormalizaNombreContenedor(targetUri.Authority);
        var blobContainer = new BlobContainerClient(storageCnnStr, container);
        await blobContainer.CreateIfNotExistsAsync().ConfigureAwait(false);
        string blobPath = $"{targetUri.AbsolutePath.Substring(0, Math.Min(targetUri.AbsolutePath.Length, 1000))}/{ahora:yyyyMMddhhmmssffff}.zip";
        var blob = new BlobClient(storageCnnStr, container, blobPath);
        await blob.UploadAsync(msZip).ConfigureAwait(false);
        return Results.Bytes(msRespBody.ToArray(), contentType);
    }
});

app.Run();

static string NormalizaNombreContenedor(string nombre)
{
    var sb = new StringBuilder(nombre);
    sb.Replace(" ", "-");
    for (int i = sb.Length - 1; i >= 0; i--)
    {
        char c = sb[i];
        if (c != '-' && !char.IsDigit(c) && !char.IsLetter(c))
        {
            sb.Remove(i, 1);
        }
    }
    int length;
    do
    {
        length = sb.Length;
        sb.Replace("--", "-");
    } while (length != sb.Length);
    if (sb.Length > 0 && sb[0] == '-')
    {
        sb.Remove(0, 1);
    }
    return sb.ToString(0, Math.Min(sb.Length, 63));
}

static Uri? GetTargetUri(HttpRequest request)
{
    var paramToRemove = new HashSet<string>();
    string url = request.Query["url"];
    if (string.IsNullOrEmpty(url))
    {
        string b64url = request.Query["b64url"];
        if (!string.IsNullOrEmpty(b64url))
        {
            url = Encoding.UTF8.GetString(Convert.FromBase64String(b64url));
        }
        else
        {
            paramToRemove.Add("b64url");
        }
    }
    else
    {
        paramToRemove.Add("url");
    }
    string path = request.Path;
    if (string.IsNullOrEmpty(url)
        && !string.IsNullOrWhiteSpace(path)
        && !string.Equals(path, "/", StringComparison.Ordinal)
        && !string.Equals(path, "favicon.ico", StringComparison.Ordinal)
        && !string.Equals(path, "/favicon.ico", StringComparison.Ordinal))
    {
        var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        url = pathParts[0];
        path = string.Join('/', pathParts.Skip(1));
    }
    if (!string.IsNullOrEmpty(url)
        && !url.StartsWith("https://", StringComparison.Ordinal)
        && !url.StartsWith("http://", StringComparison.Ordinal))
    {
        string scheme = request.Query["scheme"];
        if (string.IsNullOrEmpty(scheme))
        {
            scheme = "https://";
        }
        else
        {
            paramToRemove.Add("scheme");
        }
        url = string.Concat(scheme, url);
    }
    if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
    {
        return null;
    }
    if (!string.IsNullOrEmpty(path))
    {
        targetUri = new Uri(targetUri, path);
    }
    if (request.Query.Any())
    {
        targetUri = new Uri(targetUri, request.QueryString.ToString());
        if (paramToRemove.Count > 0)
        {
            var qs = HttpUtility.ParseQueryString(request.QueryString.ToString());
            foreach (var p in paramToRemove)
            {
                qs.Remove(p);
            }
            var uriBuilder = new UriBuilder(targetUri);
            uriBuilder.Query = qs.ToString();
            targetUri = uriBuilder.Uri;
        }
    }
    return targetUri;
}

static HttpMethod GetMethod(string method)
{
    if (HttpMethods.IsDelete(method)) return HttpMethod.Delete;
    if (HttpMethods.IsGet(method)) return HttpMethod.Get;
    if (HttpMethods.IsHead(method)) return HttpMethod.Head;
    if (HttpMethods.IsOptions(method)) return HttpMethod.Options;
    if (HttpMethods.IsPost(method)) return HttpMethod.Post;
    if (HttpMethods.IsPut(method)) return HttpMethod.Put;
    if (HttpMethods.IsTrace(method)) return HttpMethod.Trace;
    return new HttpMethod(method);
}

static bool IsTextContentType(string? contentType)
    => contentType != null
    && (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase));
