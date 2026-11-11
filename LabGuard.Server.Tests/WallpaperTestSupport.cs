using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

// Helpers shared by the wallpaper tests: the administrator's uploads and assignments, and the PC's download of its own image.
internal static class WallpaperTestSupport
{
    internal const string UploadRoute = "/api/wallpapers";
    internal const string AssignPath = "/api/pcs/wallpaper";
    internal const string PcDownloadPath = "/api/client/wallpaper";
    internal const string NotFoundMessage = "Wallpaper not found.";
    internal const string NotSetMessage = "No wallpaper is set for this PC.";
    internal const string InvalidImageMessage = "Choose a PNG or JPEG image.";
    internal const string TooLargeMessage = "The image must be 5 MB or smaller.";
    internal const string TooManyPixelsMessage = "The image must be 7680 × 4320 pixels (8K) or smaller.";

    internal static ByteArrayContent ImageContent(byte[] bytes, string contentType = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    internal static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string contentType = "image/png") =>
        client.PostAsync(UploadRoute, ImageContent(bytes, contentType));

    // The id of a wallpaper the administrator has just uploaded.
    internal static async Task<int> UploadIdAsync(HttpClient admin, byte[] bytes, string contentType = "image/png")
    {
        var response = await UploadAsync(admin, bytes, contentType);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await PcShutdownEndpointTests.JsonOf(response)).GetProperty("id").GetInt32();
    }

    // Gives the listed PCs the wallpaper, or removes it when wallpaperId is null.
    internal static Task<HttpResponseMessage> AssignAsync(HttpClient admin, int? wallpaperId, params int[] pcIds) =>
        admin.PostAsJsonAsync(AssignPath, new { ids = pcIds, wallpaperId });

    internal static Task<HttpResponseMessage> PreviewAsync(HttpClient client, int wallpaperId) =>
        client.GetAsync($"{UploadRoute}/{wallpaperId}/image");

    // The PC's download. The token is sent as the heartbeat sends it; null sends no token.
    internal static async Task<HttpResponseMessage> PcDownloadAsync(HttpClient pc, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PcDownloadPath);
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return await pc.SendAsync(request);
    }

    internal static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static async Task<string> ErrorOfAsync(HttpResponseMessage response) =>
        (await PcShutdownEndpointTests.JsonOf(response)).GetProperty("error").GetString()!;

    internal static Task<bool> WallpaperStoredAsync(TestApp app, int wallpaperId) =>
        app.WithDbAsync(db => db.Wallpapers.AnyAsync(w => w.Id == wallpaperId));

    internal static Task<int> WallpaperCountAsync(TestApp app) =>
        app.WithDbAsync(db => db.Wallpapers.CountAsync());

    internal static async Task<int?> AssignedWallpaperIdAsync(TestApp app, int pcId) =>
        (await PcShutdownEndpointTests.StoredPcAsync(app, pcId)).WallpaperId;
}
