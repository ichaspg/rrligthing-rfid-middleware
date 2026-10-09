using RfidMiddleware.Core;

namespace RfidMiddleware.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData(" e28011700000000000000001 ", "E28011700000000000000001")]
    [InlineData("AA", "AA")]
    public void NormalizesValidEpc(string input, string expected)
    {
        Assert.True(EpcNormalizer.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("XX")]
    public void RejectsInvalidEpc(string input) => Assert.False(EpcNormalizer.TryNormalize(input, out _));

    [Fact]
    public void FailsClosedWithoutManifestAndAfterExpiry()
    {
        var engine = new ValidationEngine();
        var now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        Assert.Equal(ValidationOutcome.NoActiveManifest, engine.Validate("AA", now).Outcome);
        var document = Document(now.AddMinutes(1));
        engine.Activate(ManifestSnapshot.Create(document, now));
        Assert.Equal(ValidationOutcome.Authorized, engine.Validate(document.Items[0].Epc, now).Outcome);
        Assert.Equal(ValidationOutcome.Unauthorized, engine.Validate("FFFFFFFF", now).Outcome);
        Assert.Equal(ValidationOutcome.StaleManifest, engine.Validate(document.Items[0].Epc, now.AddMinutes(2)).Outcome);
    }

    [Fact]
    public void AggregatesAcrossAntennasByEpcAndManifestVersion()
    {
        var start = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var aggregator = new ObservationAggregator(TimeSpan.FromSeconds(2));
        Assert.Empty(aggregator.Add(new("AA", "R1", 1, -60, start), 17));
        Assert.Empty(aggregator.Add(new("AA", "R1", 2, -40, start.AddSeconds(1)), 17));
        var result = aggregator.FlushExpired(start.AddSeconds(4));
        var observation = Assert.Single(result);
        Assert.Equal(2, observation.ReadCount);
        Assert.Equal(-40, observation.StrongestRssi);
        Assert.Equal([1, 2], observation.AntennaIds.Order());
    }

    [Fact]
    public void ValidationRetainsSnapshotUsedWhenObservationStarted()
    {
        var now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        var first = Document(now.AddMinutes(30));
        var replacementItems = new[] { new ManifestItem("INV-X", "FFFFFFFF") };
        var replacement = first with
        {
            Version = 18,
            Items = replacementItems,
            ItemCount = 1,
            Checksum = ManifestValidator.ComputeChecksum(replacementItems.Select(x => x.Epc))
        };
        var engine = new ValidationEngine();
        engine.Activate(ManifestSnapshot.Create(first, now));
        engine.Activate(ManifestSnapshot.Create(replacement, now.AddSeconds(1)));

        Assert.Equal(ValidationOutcome.Authorized, engine.ValidateForVersion(first.Items[0].Epc, now.AddSeconds(2), 17).Outcome);
        Assert.Equal(ValidationOutcome.Unauthorized, engine.ValidateForVersion(first.Items[0].Epc, now.AddSeconds(2), 18).Outcome);
        Assert.Equal(ValidationOutcome.NoActiveManifest, engine.ValidateForVersion(first.Items[0].Epc, now.AddSeconds(2), null).Outcome);
    }

    internal static ManifestDocument Document(DateTimeOffset expires, int count = 1)
    {
        var items = Enumerable.Range(1, count).Select(i => new ManifestItem($"INV{i}", $"E2801170{i:X16}")).ToArray();
        return new("TR-1", "TR-1", 17, "ACTIVE", items.Length,
            ManifestValidator.ComputeChecksum(items.Select(x => x.Epc)), "\"TR-1-17\"", expires.AddHours(-1), expires, items);
    }
}
