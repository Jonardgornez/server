using LabGuard.Server.Auth;

namespace LabGuard.Server.Backgrounds;

// An uploaded image that has passed every check: its declared type, its size, its signature and its pixel size.
internal sealed record UploadedImage(string ContentType, byte[] Data, string Sha256);

// Reads an image sent as the raw request body and checks it. Screen backgrounds and PC wallpapers both use it, so they accept
// and refuse the same images with the same messages.
internal static class ImageUploadReader
{
    internal const string InvalidImageMessage = "Choose a PNG or JPEG image.";
    internal const string TooLargeMessage = "The image must be 5 MB or smaller.";
    internal const string TooManyPixelsMessage = "The image must be 7680 × 4320 pixels (8K) or smaller.";
    private const int ReadChunkBytes = 80 * 1024;

    // Runs onAccepted with the image once it passes every check. Otherwise the answer is the refusal, and onAccepted never runs.
    internal static async Task<IResult> ReadAsync(HttpRequest request, Func<UploadedImage, Task<IResult>> onAccepted)
    {
        var mediaType = BackgroundImageRules.MediaTypeOf(request.ContentType);
        if (!BackgroundImageRules.IsSupportedMediaType(mediaType))
        {
            return AuthEndpoints.BadRequest(InvalidImageMessage);
        }

        if (request.ContentLength is { } declaredBytes && !BackgroundImageRules.IsWithinSizeLimit(declaredBytes))
        {
            return TooLarge();
        }

        if (await ReadWithinLimitAsync(request.Body, request.HttpContext.RequestAborted) is not { } data)
        {
            return TooLarge();
        }

        if (!BackgroundImageRules.IsValidImage(mediaType, data)
            || !BackgroundImageRules.TryReadPixelSize(mediaType, data, out var width, out var height))
        {
            return AuthEndpoints.BadRequest(InvalidImageMessage);
        }

        if (!BackgroundImageRules.IsWithinPixelLimit(width, height))
        {
            return AuthEndpoints.BadRequest(TooManyPixelsMessage);
        }

        return await onAccepted(new UploadedImage(mediaType, data, BackgroundImageRules.Sha256Of(data)));
    }

    private static IResult TooLarge() =>
        Results.Json(new ErrorResponse(TooLargeMessage), statusCode: StatusCodes.Status413PayloadTooLarge);

    // Reads the whole body, but stops as soon as it would pass the limit, so an oversized upload never fills memory.
    // Null when the body is over the limit.
    private static async Task<byte[]?> ReadWithinLimitAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > BackgroundImageRules.MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
