using System.Security.Cryptography;

namespace RelayStack.Platform.Storage;

/// <summary>
/// An object key that is always inside one organization's prefix: <c>org/{organizationId}/...</c>.
/// Keys can only be built from identifiers, never from client-supplied path text, so traversal is impossible.
/// </summary>
public readonly record struct TenantObjectKey
{
    private TenantObjectKey(Guid organizationId, string value)
    {
        OrganizationId = organizationId;
        Value = value;
    }

    public Guid OrganizationId { get; }
    public string Value { get; }

    public static TenantObjectKey ForUpload(Guid organizationId, Guid projectId, Guid inboxId, Guid fileId) =>
        new(organizationId, $"org/{organizationId:N}/project/{projectId:N}/inbox/{inboxId:N}/{fileId:N}/original");

    /// <summary>Re-hydrates a stored key and verifies it belongs to the expected organization.</summary>
    public static TenantObjectKey Parse(Guid organizationId, string storedKey)
    {
        var expectedPrefix = $"org/{organizationId:N}/";
        if (!storedKey.StartsWith(expectedPrefix, StringComparison.Ordinal) || storedKey.Contains("..", StringComparison.Ordinal)
            || storedKey.Contains('\\') || storedKey.Any(char.IsControl))
        {
            throw new UnauthorizedAccessException("Object key does not belong to the organization.");
        }

        return new TenantObjectKey(organizationId, storedKey);
    }

    public override string ToString() => Value;
}

public sealed record StoredObject(long SizeBytes, byte[] Sha256);

public sealed class ObjectTooLargeException(long limit) : Exception($"Object exceeds the {limit} byte limit.")
{
    public long Limit { get; } = limit;
}

public interface IObjectStore
{
    /// <summary>Streams content to storage, enforcing <paramref name="maxBytes"/> and computing SHA-256. Partial objects are removed on failure.</summary>
    Task<StoredObject> PutAsync(TenantObjectKey key, Stream content, long maxBytes, CancellationToken ct);

    Task<Stream> OpenReadAsync(TenantObjectKey key, CancellationToken ct);

    Task DeleteAsync(TenantObjectKey key, CancellationToken ct);
}

/// <summary>
/// Local development object store backed by the filesystem. Writes to a temp file and renames on success,
/// so readers never observe a partially written object (the same guarantee S3 gives for completed PUTs).
/// </summary>
public sealed class FileSystemObjectStore : IObjectStore
{
    private readonly string _root;

    public FileSystemObjectStore(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredObject> PutAsync(TenantObjectKey key, Stream content, long maxBytes, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial-" + Guid.NewGuid().ToString("N");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new ObjectTooLargeException(maxBytes);
                    }

                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await output.FlushAsync(ct);
            }

            File.Move(temp, path, overwrite: true);
            return new StoredObject(total, sha.GetHashAndReset());
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(TenantObjectKey key, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Task DeleteAsync(TenantObjectKey key, CancellationToken ct)
    {
        TryDelete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(TenantObjectKey key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Value));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Object key escapes the storage root.");
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
