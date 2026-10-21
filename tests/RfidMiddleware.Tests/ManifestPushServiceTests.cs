using Microsoft.Extensions.Logging.Abstractions;
using RfidMiddleware.Core;
using RfidMiddleware.Host;

namespace RfidMiddleware.Tests;

public sealed class ManifestPushServiceTests
{
    private const string Epc = "E28011700000000000000001";

    [Theory]
    [InlineData("Bearer correct-token", "correct-token", true)]
    [InlineData("bearer correct-token", "correct-token", true)]
    [InlineData("Bearer wrong-token", "correct-token", false)]
    [InlineData("", "correct-token", false)]
    [InlineData("Bearer correct-token", "", false)]
    public void PublicApiBearerAuthenticationIsValidated(string header, string configured, bool expected) =>
        Assert.Equal(expected, PublicApiSecurity.HasValidBearerToken(header, configured));

    [Fact]
    public async Task ActiveManifestIsAppliedAndImmediatelyVisible()
    {
        var repository = new MemoryManifestRepository();
        var validation = new ValidationEngine();
        var state = new RuntimeState();
        var service = Create(repository, validation, state);

        var result = await service.ApplyAsync(ActiveRequest(1), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.False(result.AlreadyApplied);
        Assert.Equal(AuthorizationState.Active, state.AuthorizationState);
        Assert.Equal(1, state.ActiveManifestVersion);
        Assert.Equal(ValidationOutcome.Authorized, validation.Validate(Epc, TestClock.Now).Outcome);
    }

    [Fact]
    public async Task ExactRetryIsIdempotentButChangedPayloadConflicts()
    {
        var repository = new MemoryManifestRepository();
        var service = Create(repository, new ValidationEngine(), new RuntimeState());
        var request = ActiveRequest(7);
        await service.ApplyAsync(request, CancellationToken.None);

        var replay = await service.ApplyAsync(request, CancellationToken.None);
        Assert.True(replay.AlreadyApplied);
        Assert.False(replay.Applied);

        var changed = request with
        {
            Items = [new ManifestPushItem("INV-2", "E28011700000000000000002")],
            Checksum = ManifestValidator.ComputeChecksum(["E28011700000000000000002"])
        };
        var exception = await Assert.ThrowsAsync<ManifestVersionConflictException>(
            () => service.ApplyAsync(changed, CancellationToken.None));
        Assert.Equal(7, exception.CurrentVersion);
    }

    [Fact]
    public async Task CompletedManifestClearsAuthorization()
    {
        var repository = new MemoryManifestRepository();
        var validation = new ValidationEngine();
        var state = new RuntimeState();
        var service = Create(repository, validation, state);
        await service.ApplyAsync(ActiveRequest(1), CancellationToken.None);

        var completed = new ManifestPushRequest("M-1", null, 2, "COMPLETED", 0, null, null, []);
        var result = await service.ApplyAsync(completed, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(AuthorizationState.Completed, state.AuthorizationState);
        Assert.Null(validation.Snapshot);
    }

    [Fact]
    public async Task CompletionForDifferentActiveManifestConflicts()
    {
        var repository = new MemoryManifestRepository();
        var service = Create(repository, new ValidationEngine(), new RuntimeState());
        await service.ApplyAsync(ActiveRequest(1), CancellationToken.None);
        var completed = new ManifestPushRequest("OTHER", null, 2, "COMPLETED", 0, null, null, []);

        await Assert.ThrowsAsync<ManifestVersionConflictException>(
            () => service.ApplyAsync(completed, CancellationToken.None));

        Assert.NotNull(repository.Active);
    }

    [Fact]
    public async Task InvalidChecksumIsRejectedWithoutChangingAuthorization()
    {
        var repository = new MemoryManifestRepository();
        var state = new RuntimeState();
        var service = Create(repository, new ValidationEngine(), state);
        var invalid = ActiveRequest(1) with { Checksum = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyAsync(invalid, CancellationToken.None));

        Assert.Equal(AuthorizationState.Uninitialized, state.AuthorizationState);
        Assert.Null(repository.Active);
    }

    private static ManifestPushService Create(
        IManifestRepository repository, ValidationEngine validation, RuntimeState state) =>
        new(new MiddlewareOptions { Id = "MW-1", ManifestMode = ManifestMode.Push }, repository, validation, state,
            new TestClock(), NullLogger<ManifestPushService>.Instance);

    private static ManifestPushRequest ActiveRequest(long version) =>
        new("M-1", "T-1", version, "ACTIVE", 1, ManifestValidator.ComputeChecksum([Epc]),
            TestClock.Now.AddHours(1), [new ManifestPushItem("INV-1", Epc)]);

    private sealed class TestClock : IClock
    {
        public static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MemoryManifestRepository : IManifestRepository
    {
        public ManifestSnapshot? Active { get; private set; }
        private AuthorizationStateSnapshot _authorization =
            new(AuthorizationState.Uninitialized, null, null, TestClock.Now);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ManifestSnapshot?> GetActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active);
        public Task<AuthorizationStateSnapshot> GetAuthorizationStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_authorization);

        public Task StageAndActivateAsync(ManifestDocument manifest, CancellationToken cancellationToken = default)
        {
            if (_authorization.Version is long version && manifest.Version <= version)
                throw new InvalidDataException("Version is not newer.");
            Active = ManifestSnapshot.Create(manifest, TestClock.Now);
            _authorization = new(AuthorizationState.Active, manifest.ManifestId, manifest.Version, TestClock.Now);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(ManifestCompletion completion, CancellationToken cancellationToken = default)
        {
            if (_authorization.Version is long version && completion.Version <= version)
                throw new InvalidDataException("Version is not newer.");
            Active = null;
            _authorization = new(AuthorizationState.Completed, completion.ManifestId, completion.Version, TestClock.Now);
            return Task.CompletedTask;
        }
    }
}
