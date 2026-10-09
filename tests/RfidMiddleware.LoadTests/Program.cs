using System.Diagnostics;
using RfidMiddleware.Core;

var epcCount = args.Length > 0 ? int.Parse(args[0]) : 100_000;
var reads = args.Length > 1 ? int.Parse(args[1]) : 1_000_000;
var now = DateTimeOffset.UtcNow;
var items = Enumerable.Range(0, epcCount).Select(i => new ManifestItem($"INV{i:D6}", $"E2801170{i:X16}")).ToArray();
var document = new ManifestDocument("PERF-1", "PERF-1", 1, "ACTIVE", items.Length, null, null, now, now.AddHours(1), items);
var engine = new ValidationEngine();
var build = Stopwatch.StartNew();
engine.Activate(ManifestSnapshot.Create(document, now));
build.Stop();
var timings = new long[reads];
for (var i = 0; i < reads; i++)
{
    var started = Stopwatch.GetTimestamp();
    var result = engine.Validate($"E2801170{i % epcCount:X16}", now);
    timings[i] = Stopwatch.GetTimestamp() - started;
    if (result.Outcome != ValidationOutcome.Authorized) throw new InvalidOperationException("Unexpected validation result.");
}
Array.Sort(timings);
var p99 = TimeSpan.FromSeconds(timings[(int)(reads * 0.99)] / (double)Stopwatch.Frequency);
Console.WriteLine($"Manifest={epcCount} BuildMs={build.Elapsed.TotalMilliseconds:F2} Reads={reads} P99Ms={p99.TotalMilliseconds:F4} WorkingSet={Environment.WorkingSet}");
return p99 < TimeSpan.FromMilliseconds(2) ? 0 : 1;

