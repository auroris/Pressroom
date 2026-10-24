using System.Buffers;

namespace Pressroom.Server.Infrastructure;

public static class BoundedBodyReader
{
    public static async Task<MemoryStream> ReadAsync(Stream input, long limit, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    return output;
                if (output.Length + read > limit)
                    throw new BodyLimitExceededException();
                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            // The shared array has held caller data; clear it before returning it to the pool.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

public sealed class BodyLimitExceededException : Exception;
