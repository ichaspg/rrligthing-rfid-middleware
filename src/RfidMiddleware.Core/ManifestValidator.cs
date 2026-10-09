using System.Security.Cryptography;
using System.Text;

namespace RfidMiddleware.Core;

public static class ManifestValidator
{
    public static void Validate(ManifestDocument manifest)
    {
        if (!string.Equals(manifest.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only ACTIVE manifests may be activated.");
        if (manifest.ItemCount != manifest.Items.Count)
            throw new InvalidDataException("Manifest item count does not match the payload.");
        if (manifest.Items.Count == 0)
            throw new InvalidDataException("An active manifest must contain at least one EPC.");

        var epcs = manifest.Items.Select(x => EpcNormalizer.Normalize(x.Epc)).ToArray();
        if (epcs.Distinct(StringComparer.Ordinal).Count() != epcs.Length)
            throw new InvalidDataException("Manifest contains duplicate EPCs.");

        if (!string.IsNullOrWhiteSpace(manifest.Checksum))
        {
            var actual = ComputeChecksum(epcs);
            if (!string.Equals(actual, manifest.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest checksum is invalid.");
        }
    }

    public static string ComputeChecksum(IEnumerable<string> epcs)
    {
        var canonical = string.Join('\n', epcs.Select(EpcNormalizer.Normalize).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

